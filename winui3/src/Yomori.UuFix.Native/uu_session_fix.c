/*
 * uu_session_fix.c — UU 远程会话修复的注入载荷
 *
 * 背景：本机 wtsapi32.dll 是多会话补丁的兼容层，缺少 WTSGetActiveConsoleSessionId 导出。
 *       UU 的 GameViewerService 按名字查这个函数拿到 NULL，无法得知控制台会话，
 *       于是退化成「随便挑一个活动会话」，导致远控进入通道账户的桌面而不是主控桌面。
 *
 * 做法：hook 目标进程里的 wtsapi32!WTSEnumerateSessionsW，在返回后把
 *       「非 console 且 Active」的会话标记为 Disconnected。控制台身份按
 *       WinStationName == "console" 判定，不依赖那个缺失的 API。
 *
 * 边界：只改内存里返回结构体的 State 字段，不动任何文件、不增删数组元素
 *      （数组由 WTS 分配、由 WTSFreeMemory 释放，改长度会破坏释放语义）。
 *      进程重启即恢复，卸载导出也能恢复。
 *
 * 编译：cmake -S . -B build -G "Visual Studio 17 2022" -A x64
 *       cmake --build build --config Release
 */

#include <windows.h>

/* 自行定义布局，避免不同 SDK 版本的头文件差异；x64 下为 24 字节 */
typedef struct
{
    DWORD SessionId;
    LPWSTR pWinStationName;
    DWORD State;
} UUFIX_SESSION_INFO;

typedef BOOL(WINAPI *PFN_WTS_ENUM)(HANDLE, DWORD, DWORD, UUFIX_SESSION_INFO **, DWORD *);

#define WTS_ACTIVE 0
#define WTS_DISCONNECTED 4

static PFN_WTS_ENUM g_original = NULL;
static ULONG_PTR *g_iatSlot = NULL;
static ULONG_PTR g_iatOriginal = 0;
static volatile LONG g_installed = 0;
static volatile LONG g_calls = 0;
static volatile LONG g_rewritten = 0;

static BOOL WINAPI Hook_WTSEnumerateSessionsW(HANDLE hServer, DWORD reserved, DWORD version,
                                              UUFIX_SESSION_INFO **ppSessionInfo, DWORD *pCount)
{
    BOOL ok;
    DWORD consoleId = (DWORD)-1;
    DWORD i;

    if (g_original == NULL)
    {
        return FALSE;
    }

    ok = g_original(hServer, reserved, version, ppSessionInfo, pCount);
    if (!ok || ppSessionInfo == NULL || *ppSessionInfo == NULL || pCount == NULL)
    {
        return ok;
    }

    if (*pCount == 0 || *pCount > 512)
    {
        return ok;
    }

    InterlockedIncrement(&g_calls);

    /* 先找 console：WinStationName 为 "console" 的那一项就是物理控制台 */
    for (i = 0; i < *pCount; i++)
    {
        LPWSTR name = (*ppSessionInfo)[i].pWinStationName;
        if (name != NULL && _wcsicmp(name, L"console") == 0)
        {
            consoleId = (*ppSessionInfo)[i].SessionId;
            break;
        }
    }

    if (consoleId == (DWORD)-1)
    {
        /* 控制台未附着：不做任何改写，避免把全部会话标成断开 */
        return ok;
    }

    for (i = 0; i < *pCount; i++)
    {
        UUFIX_SESSION_INFO *item = &(*ppSessionInfo)[i];
        if (item->SessionId != consoleId && item->State == WTS_ACTIVE)
        {
            item->State = WTS_DISCONNECTED;
            InterlockedIncrement(&g_rewritten);
        }
    }

    return ok;
}

/* 在模块导入表里定位 wtsapi32!WTSEnumerateSessionsW 的槽位
 *
 * 注意目标进程是 delay-load 的：GameViewerService.exe 的普通导入表里没有 wtsapi32，
 * 它在 .didat（延迟导入）段。只查普通导入表既找不到、又会因为 OriginalFirstThunk
 * 为空而拿着野指针当名字表遍历 —— 那就是 0xC0000005 的来源。
 */
typedef struct
{
    DWORD Attributes;
    DWORD DllNameRVA;
    DWORD ModuleHandleRVA;
    DWORD ImportAddressTableRVA;
    DWORD ImportNameTableRVA;
    DWORD BoundImportAddressTableRVA;
    DWORD UnloadInformationTableRVA;
    DWORD TimeDateStamp;
} UUFIX_DELAY_DESC;

#define UUFIX_MAX_THUNKS 4096

static BOOL MatchThunkTable(BYTE *base, IMAGE_THUNK_DATA *iat, IMAGE_THUNK_DATA *names,
                            ULONG_PTR **slot, ULONG_PTR *originalValue)
{
    int guard = 0;

    if (iat == NULL || names == NULL)
    {
        return FALSE;
    }

    for (; iat->u1.Function != 0; iat++, names++)
    {
        IMAGE_IMPORT_BY_NAME *hint;

        if (++guard > UUFIX_MAX_THUNKS)
        {
            return FALSE;
        }

        if (names->u1.AddressOfData == 0)
        {
            continue;
        }

        /* 序号导入没有名字，跳过 */
        if (IMAGE_SNAP_BY_ORDINAL(names->u1.Ordinal))
        {
            continue;
        }

        hint = (IMAGE_IMPORT_BY_NAME *)(base + (DWORD)names->u1.AddressOfData);
        if (strcmp((const char *)hint->Name, "WTSEnumerateSessionsW") != 0)
        {
            continue;
        }

        *slot = (ULONG_PTR *)&iat->u1.Function;
        *originalValue = iat->u1.Function;
        return TRUE;
    }

    return FALSE;
}

static BOOL LocateIatSlot(HMODULE module, ULONG_PTR **slot, ULONG_PTR *originalValue)
{
    BYTE *base;
    IMAGE_DOS_HEADER *dos;
    IMAGE_NT_HEADERS *nt;
    IMAGE_DATA_DIRECTORY normal;
    IMAGE_DATA_DIRECTORY delayed;

    if (module == NULL)
    {
        return FALSE;
    }

    base = (BYTE *)module;
    dos = (IMAGE_DOS_HEADER *)base;
    if (dos->e_magic != IMAGE_DOS_SIGNATURE)
    {
        return FALSE;
    }

    nt = (IMAGE_NT_HEADERS *)(base + dos->e_lfanew);
    if (nt->Signature != IMAGE_NT_SIGNATURE)
    {
        return FALSE;
    }

    normal = nt->OptionalHeader.DataDirectory[IMAGE_DIRECTORY_ENTRY_IMPORT];
    delayed = nt->OptionalHeader.DataDirectory[IMAGE_DIRECTORY_ENTRY_DELAY_IMPORT];

    /* 1) 普通导入表 */
    if (normal.VirtualAddress != 0)
    {
        IMAGE_IMPORT_DESCRIPTOR *desc = (IMAGE_IMPORT_DESCRIPTOR *)(base + normal.VirtualAddress);
        int guard = 0;

        for (; desc->Name != 0 && guard < 256; desc++, guard++)
        {
            const char *dllName = (const char *)(base + desc->Name);

            if (_stricmp(dllName, "wtsapi32.dll") != 0)
            {
                continue;
            }

            /* OriginalFirstThunk 为 0 时没有名字表：此时按名遍历会把数据段当 thunk 读，
               必须放弃，交给下面的 delay-load 分支 */
            if (desc->OriginalFirstThunk == 0)
            {
                continue;
            }

            if (MatchThunkTable(base,
                                (IMAGE_THUNK_DATA *)(base + desc->FirstThunk),
                                (IMAGE_THUNK_DATA *)(base + desc->OriginalFirstThunk),
                                slot, originalValue))
            {
                return TRUE;
            }
        }
    }

    /* 2) 延迟导入表（目标进程走的是这一条） */
    if (delayed.VirtualAddress != 0)
    {
        UUFIX_DELAY_DESC *desc = (UUFIX_DELAY_DESC *)(base + delayed.VirtualAddress);
        int guard = 0;

        for (; desc->DllNameRVA != 0 && guard < 256; desc++, guard++)
        {
            const char *dllName = (const char *)(base + desc->DllNameRVA);

            if (_stricmp(dllName, "wtsapi32.dll") != 0)
            {
                continue;
            }

            if (desc->ImportAddressTableRVA == 0 || desc->ImportNameTableRVA == 0)
            {
                continue;
            }

            if (MatchThunkTable(base,
                                (IMAGE_THUNK_DATA *)(base + desc->ImportAddressTableRVA),
                                (IMAGE_THUNK_DATA *)(base + desc->ImportNameTableRVA),
                                slot, originalValue))
            {
                return TRUE;
            }
        }
    }

    return FALSE;
}

/* 返回值：1 = 安装成功，2 = 已安装，0 = 未找到导入项，-1 = 保护失败，-3 = 访问冲突 */
static int InstallCore(void)
{
    DWORD oldProtect = 0;
    void *target = NULL;

    if (InterlockedCompareExchange(&g_installed, 1, 0) != 0)
    {
        return 2;
    }

    if (!LocateIatSlot(GetModuleHandleW(NULL), &g_iatSlot, &g_iatOriginal))
    {
        InterlockedExchange(&g_installed, 0);
        return 0;
    }

    g_original = (PFN_WTS_ENUM)g_iatOriginal;
    target = (void *)Hook_WTSEnumerateSessionsW;

    if (!VirtualProtect(g_iatSlot, sizeof(void *), PAGE_READWRITE, &oldProtect))
    {
        g_original = NULL;
        InterlockedExchange(&g_installed, 0);
        return -1;
    }

    *g_iatSlot = (ULONG_PTR)target;
    VirtualProtect(g_iatSlot, sizeof(void *), oldProtect, &oldProtect);
    FlushInstructionCache(GetCurrentProcess(), g_iatSlot, sizeof(void *));

    return 1;
}

__declspec(dllexport) int WINAPI UuFixInstall(void)
{
    /* 整段套 SEH：载荷跑在 UU 的 SYSTEM 服务里，任何一次越界读都会把远控服务掀掉。
       宁可返回错误码、由主控如实提示，也绝不能让目标进程因为我们的探测而死。 */
    __try
    {
        return InstallCore();
    }
    __except (EXCEPTION_EXECUTE_HANDLER)
    {
        g_original = NULL;
        g_iatSlot = NULL;
        g_iatOriginal = 0;
        InterlockedExchange(&g_installed, 0);
        return -3;
    }
}

/* 恢复原导入项；返回 1 = 已恢复，0 = 本来没装 */
__declspec(dllexport) int WINAPI UuFixUninstall(void)
{
    DWORD oldProtect = 0;

    if (g_iatSlot == NULL || g_iatOriginal == 0)
    {
        return 0;
    }

    if (!VirtualProtect(g_iatSlot, sizeof(void *), PAGE_READWRITE, &oldProtect))
    {
        return 0;
    }

    *g_iatSlot = g_iatOriginal;
    VirtualProtect(g_iatSlot, sizeof(void *), oldProtect, &oldProtect);
    FlushInstructionCache(GetCurrentProcess(), g_iatSlot, sizeof(void *));

    g_original = NULL;
    g_iatSlot = NULL;
    g_iatOriginal = 0;
    InterlockedExchange(&g_installed, 0);
    return 1;
}

/* 诊断：高 32 位 = 枚举次数，低 32 位 = 改写条数 */
__declspec(dllexport) long long WINAPI UuFixStats(void)
{
    return ((long long)g_calls << 32) | (DWORD)g_rewritten;
}

BOOL WINAPI DllMain(HINSTANCE instance, DWORD reason, LPVOID reserved)
{
    (void)reserved;

    if (reason == DLL_PROCESS_ATTACH)
    {
        DisableThreadLibraryCalls(instance);
    }

    return TRUE;
}
