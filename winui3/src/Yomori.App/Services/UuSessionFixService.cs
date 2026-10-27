// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace Yomori.Services;

/// <summary>UU 远程会话修复的结果分类。</summary>
internal enum UuFixOutcome
{
    /// <summary>注入成功，且投屏进程已落到控制台会话。</summary>
    Success,

    /// <summary>该进程里已经装过，未重复注入。</summary>
    AlreadyInstalled,

    /// <summary>UU 服务没在运行。</summary>
    ServiceNotRunning,

    /// <summary>载荷文件不在程序目录。</summary>
    PayloadMissing,

    /// <summary>打不开目标进程（通常是权限不足）。</summary>
    AccessDenied,

    /// <summary>远程线程未能加载载荷。</summary>
    InjectFailed,

    /// <summary>载荷里没找到 wtsapi32!WTSEnumerateSessionsW 的导入项。</summary>
    HookFailed,

    /// <summary>注入成功但投屏进程没回到控制台会话。</summary>
    NotEffective,

    /// <summary>重启 UU 服务成功，已恢复原行为。</summary>
    Restored,

    /// <summary>重启 UU 服务失败。</summary>
    RestoreFailed,

    /// <summary>其他未归类错误。</summary>
    Unknown,
}

internal sealed record UuFixResult
{
    public UuFixOutcome Outcome { get; init; } = UuFixOutcome.Unknown;

    public string Detail { get; init; } = string.Empty;

    public int ServicePid { get; init; }

    public int? ServerSessionId { get; init; }

    public int? ConsoleSessionId { get; init; }

    public bool IsSuccess => Outcome is UuFixOutcome.Success or UuFixOutcome.AlreadyInstalled;
}

/// <summary>
/// 把 UU 远程的投屏进程拉回主控所在的会话。
///
/// <para>问题链路：本机 <c>wtsapi32.dll</c> 是多会话补丁的兼容层，缺
/// <c>WTSGetActiveConsoleSessionId</c> 导出；UU 的 GameViewerService 取不到控制台身份，
/// 退化成挑一个活动会话，于是远控进到通道账户的桌面。</para>
///
/// <para>对策：向该服务注入 <c>uu_session_fix.dll</c>，它在进程内 hook
/// <c>WTSEnumerateSessionsW</c>，把「非 console 且 Active」的会话标记为断开。
/// 只改返回结构体，不动任何文件；服务重启即失效。</para>
///
/// <para>本类不做权限提升 —— 主控清单已是 requireAdministrator，缺少权限时如实回报。</para>
/// </summary>
internal static class UuSessionFixService
{
    public const string ServiceName = "GameViewerService";
    public const string ServiceProcessName = "GameViewerService";
    public const string ServerProcessName = "GameViewerServer";
    public const string PayloadFileName = "uu_session_fix.dll";

    private const string ConsoleWinStation = "console";

    private const uint PROCESS_ALL_ACCESS = 0x001F0FFF;
    private const uint MEM_COMMIT = 0x1000;
    private const uint MEM_RESERVE = 0x2000;
    private const uint MEM_RELEASE = 0x8000;
    private const uint PAGE_READWRITE = 0x04;
    private const uint WAIT_OBJECT_0 = 0;
    private const uint TOKEN_ADJUST_PRIVILEGES = 0x0020;
    private const uint TOKEN_QUERY = 0x0008;
    private const int SE_PRIVILEGE_ENABLED = 0x0002;

    public static string PayloadPath => Path.Combine(AppContext.BaseDirectory, PayloadFileName);

    /// <summary>载荷是否随程序一起发布。</summary>
    public static bool PayloadExists => File.Exists(PayloadPath);

    /// <summary>UU 服务是否在运行；返回其 PID。</summary>
    public static bool TryGetServicePid(out int pid)
    {
        pid = 0;
        try
        {
            var process = Process.GetProcessesByName(ServiceProcessName).FirstOrDefault();
            if (process is null)
            {
                return false;
            }

            pid = process.Id;
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// 本机控制台会话 ID。不使用 WTSGetActiveConsoleSessionId（本机该导出不存在），
    /// 而是枚举后按窗口站名判定。
    /// </summary>
    public static bool TryGetConsoleSessionId(out int sessionId)
    {
        sessionId = -1;
        try
        {
            foreach (var session in RdpSessionService.EnumerateSessions())
            {
                if (string.Equals(session.WinStationName, ConsoleWinStation, StringComparison.OrdinalIgnoreCase))
                {
                    sessionId = unchecked((int)session.Id);
                    return true;
                }
            }
        }
        catch (Exception)
        {
            // 枚举失败按「未知」处理，调用方不得据此做判断
        }

        return false;
    }

    /// <summary>当前投屏进程所在会话；没有进程时返回 null。</summary>
    public static int? TryGetServerSessionId()
    {
        try
        {
            var process = Process.GetProcessesByName(ServerProcessName).FirstOrDefault();
            if (process is null)
            {
                return null;
            }

            using (process)
            {
                return process.SessionId;
            }
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// 只注入、不碰投屏进程。
    ///
    /// <para>开关一打开就要调这个：hook 装上后，UU 后续枚举就看不到其它会话，
    /// 不会再跑去占通道会话。而 18 秒的延迟只用于「把已经连错的投屏进程迁回来」，
    /// 不适合用来决定要不要装 hook —— 那个窗口正好够 UU 把会话抢走。</para>
    /// </summary>
    public static async Task<UuFixResult> InjectOnlyAsync(CancellationToken cancellationToken)
    {
        if (!PayloadExists)
        {
            return new UuFixResult
            {
                Outcome = UuFixOutcome.PayloadMissing,
                Detail = "程序目录里没有 " + PayloadFileName + "。",
            };
        }

        if (!TryGetServicePid(out var pid))
        {
            return new UuFixResult
            {
                Outcome = UuFixOutcome.ServiceNotRunning,
                Detail = "UU 服务未运行，等它启动后连接通道时会自动补装。",
            };
        }

        var result = await Task.Run(() => Inject(pid, cancellationToken), cancellationToken).ConfigureAwait(false);
        return result with { ServicePid = pid };
    }

    /// <summary>
    /// 执行修复：注入载荷 → 结束当前投屏进程，让服务按修正后的枚举结果重新拉起。
    /// </summary>
    public static async Task<UuFixResult> ApplyAsync(CancellationToken cancellationToken)
    {
        if (!PayloadExists)
        {
            return new UuFixResult
            {
                Outcome = UuFixOutcome.PayloadMissing,
                Detail = "程序目录里没有 " + PayloadFileName + "，请重新安装或先构建 Yomori.UuFix.Native。",
            };
        }

        if (!TryGetServicePid(out var pid))
        {
            return new UuFixResult
            {
                Outcome = UuFixOutcome.ServiceNotRunning,
                Detail = "没有找到 " + ServiceName + " 进程（UU 远程未安装或服务未启动）。",
            };
        }

        var consoleKnown = TryGetConsoleSessionId(out var consoleId);

        var inject = await Task.Run(() => Inject(pid, cancellationToken), cancellationToken).ConfigureAwait(false);
        if (inject.Outcome != UuFixOutcome.Success && inject.Outcome != UuFixOutcome.AlreadyInstalled)
        {
            return inject with { ServicePid = pid, ConsoleSessionId = consoleKnown ? consoleId : null };
        }

        // 注入只改变量：现有投屏进程仍绑在旧会话上，必须让它重启一次。
        // 但若它本来就在控制台会话，重启只会白断一次远控 —— 此时保留进程，只装上 hook 即可。
        var currentServerSession = TryGetServerSessionId();
        if (consoleKnown && currentServerSession == consoleId)
        {
            return new UuFixResult
            {
                Outcome = UuFixOutcome.Success,
                ServicePid = pid,
                ServerSessionId = currentServerSession,
                ConsoleSessionId = consoleId,
                Detail = "已启用：投屏进程本来就在主控会话，未打断当前连接。",
            };
        }

        KillServers();
        var serverSession = await WaitForServerSessionAsync(cancellationToken).ConfigureAwait(false);

        // 投屏进程是**按需拉起**的：没有连接请求时它根本不常驻。
        // 杀完等不到新进程，只说明「现在没人连」，不代表修正失效 ——
        // hook 已经装进服务进程，下次 UU 连接时会按修正后的结果选会话。
        if (serverSession is null)
        {
            return new UuFixResult
            {
                Outcome = UuFixOutcome.Success,
                ServicePid = pid,
                ServerSessionId = null,
                ConsoleSessionId = consoleKnown ? consoleId : null,
                Detail = "已启用：修正已装载。投屏进程当前未运行，下次 UU 连接时会直接进入主控桌面。",
            };
        }

        var effective = consoleKnown && serverSession == consoleId;

        return new UuFixResult
        {
            Outcome = effective ? UuFixOutcome.Success : UuFixOutcome.NotEffective,
            ServicePid = pid,
            ServerSessionId = serverSession,
            ConsoleSessionId = consoleKnown ? consoleId : null,
            Detail = effective
                ? $"已修复：投屏进程 ${serverSession} 落回控制台会话。UU 重新连接后画面即为主控桌面。"
                : consoleKnown
                    ? $"注入成功，但投屏进程在会话 {(serverSession?.ToString() ?? "未知")}，控制台会话是 {consoleId}。"
                    : $"注入成功，但未能确认控制台会话；投屏进程在会话 {(serverSession?.ToString() ?? "未知")}。",
        };
    }

    /// <summary>撤销：重启 UU 服务，进程内 hook 随进程消失。</summary>
    public static UuFixResult Restore()
    {
        try
        {
            var result = RdpServiceControl.Restart(ServiceName);
            return new UuFixResult
            {
                Outcome = result.CoreOk ? UuFixOutcome.Restored : UuFixOutcome.RestoreFailed,
                Detail = result.CoreOk
                    ? "已重启 UU 服务，会话修复失效，行为恢复原状。"
                    : "重启 UU 服务失败：" + result.Message,
            };
        }
        catch (Exception ex)
        {
            return new UuFixResult
            {
                Outcome = UuFixOutcome.RestoreFailed,
                Detail = "重启 UU 服务异常：" + (ex.Message.Length == 0 ? ex.GetType().Name : ex.Message),
            };
        }
    }

    // ---------------- 注入 ----------------

    private static UuFixResult Inject(int pid, CancellationToken cancellationToken)
    {
        if (!EnableDebugPrivilege())
        {
            // 非致命：管理员令牌通常已足够，失败继续尝试
        }

        IntPtr hProcess = OpenProcess(PROCESS_ALL_ACCESS, false, pid);
        if (hProcess == IntPtr.Zero)
        {
            return new UuFixResult
            {
                Outcome = UuFixOutcome.AccessDenied,
                Detail = $"打不开目标进程（Win32 {Marshal.GetLastWin32Error()}）。本程序需以管理员身份运行。",
            };
        }

        try
        {
            var path = PayloadPath;
            var pathBytes = System.Text.Encoding.Unicode.GetBytes(path + "\0");

            IntPtr remotePath = VirtualAllocEx(hProcess, IntPtr.Zero, (IntPtr)pathBytes.Length,
                MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
            if (remotePath == IntPtr.Zero)
            {
                return Fail(UuFixOutcome.InjectFailed, "远程分配路径缓冲区失败");
            }

            try
            {
                if (!WriteProcessMemory(hProcess, remotePath, pathBytes, (IntPtr)pathBytes.Length, out _))
                {
                    return Fail(UuFixOutcome.InjectFailed, "写入远程路径失败");
                }

                IntPtr kernel32 = GetModuleHandleW("kernel32.dll");
                if (kernel32 == IntPtr.Zero)
                {
                    return Fail(UuFixOutcome.InjectFailed, "取不到 kernel32 模块句柄");
                }

                IntPtr loadLibrary = GetProcAddress(kernel32, "LoadLibraryW");
                if (loadLibrary == IntPtr.Zero)
                {
                    return Fail(UuFixOutcome.InjectFailed, "在当前进程里解析不到 LoadLibraryW");
                }

                if (!RunRemote(hProcess, loadLibrary, remotePath, cancellationToken, out _))
                {
                    return Fail(UuFixOutcome.InjectFailed, "远程 LoadLibrary 超时或失败");
                }

                // 关键：GetExitCodeThread 只有 32 位，装不下 x64 的 HMODULE。
                // 早期版本直接拿退出码当模块基址，高位被截断 → 算出的 Install 地址是野指针
                // → 远程线程一跳就 0xC0000005。这里改从目标进程的模块表取完整地址。
                var remoteModule = FindRemoteModule(pid, PayloadFileName);
                if (remoteModule == IntPtr.Zero)
                {
                    return Fail(UuFixOutcome.InjectFailed, "载荷没出现在目标进程的模块表里（加载被拒）");
                }

                // 远端导出地址 = 远端基址 + (本地区性偏移)
                IntPtr localModule = LoadLibraryW(path);
                if (localModule == IntPtr.Zero)
                {
                    return Fail(UuFixOutcome.InjectFailed, "本地加载载荷失败，无法定位导出");
                }

                try
                {
                    IntPtr localInstall = GetProcAddress(localModule, "UuFixInstall");
                    if (localInstall == IntPtr.Zero)
                    {
                        return Fail(UuFixOutcome.InjectFailed, "载荷缺少 UuFixInstall 导出");
                    }

                    var offset = localInstall.ToInt64() - localModule.ToInt64();
                    var remoteInstall = (IntPtr)(remoteModule.ToInt64() + offset);

                    if (!RunRemote(hProcess, remoteInstall, IntPtr.Zero, cancellationToken, out var code))
                    {
                        return Fail(UuFixOutcome.InjectFailed, "调用远程安装函数超时");
                    }

                    return (int)code switch
                    {
                        1 => new UuFixResult { Outcome = UuFixOutcome.Success, Detail = "载荷已安装。" },
                        2 => new UuFixResult { Outcome = UuFixOutcome.AlreadyInstalled, Detail = "该进程里已安装过，未重复注入。" },
                        0 => new UuFixResult
                        {
                            Outcome = UuFixOutcome.HookFailed,
                            Detail = "载荷没找到 wtsapi32!WTSEnumerateSessionsW 的导入项 —— 该版本可能改成动态获取，此修复不适用。",
                        },
                        -3 => new UuFixResult
                        {
                            Outcome = UuFixOutcome.HookFailed,
                            Detail = "载荷在目标进程里发生访问冲突（0xC0000005）并已自行中止，服务未受影响。"
                                     + "该版本的导入表结构可能不标准（加壳 / 重定向），此修复不适用。",
                        },
                        _ => new UuFixResult { Outcome = UuFixOutcome.HookFailed, Detail = "安装函数返回 " + (int)code + "（改写导入表失败）。" },
                    };
                }
                finally
                {
                    FreeLibrary(localModule);
                }
            }
            finally
            {
                VirtualFreeEx(hProcess, remotePath, IntPtr.Zero, MEM_RELEASE);
            }
        }
        catch (Exception ex)
        {
            return Fail(UuFixOutcome.Unknown, ex.Message.Length == 0 ? ex.GetType().Name : ex.Message);
        }
        finally
        {
            CloseHandle(hProcess);
        }
    }

    private static bool RunRemote(IntPtr hProcess, IntPtr start, IntPtr parameter,
        CancellationToken cancellationToken, out uint result)
    {
        result = 0;

        var thread = CreateRemoteThread(hProcess, IntPtr.Zero, IntPtr.Zero, start, parameter, 0, out _);
        if (thread == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            // 等 15 秒：加载 DLL 一般远快于此，超时按失败处理但不重试
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (DateTime.UtcNow < deadline)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return false;
                }

                if (WaitForSingleObject(thread, 250) == WAIT_OBJECT_0)
                {
                    GetExitCodeThread(thread, out result);
                    return true;
                }
            }

            return false;
        }
        finally
        {
            CloseHandle(thread);
        }
    }

    private static UuFixResult Fail(UuFixOutcome outcome, string detail)
    {
        var suffix = Marshal.GetLastWin32Error() is var err && err != 0 ? $"（Win32 {err}）" : string.Empty;
        return new UuFixResult { Outcome = outcome, Detail = detail + suffix };
    }

    /// <summary>
    /// 在目标进程的模块表里找载荷基址。
    ///
    /// 不能用 <c>GetExitCodeThread</c> 接 <c>LoadLibraryW</c> 的返回值：那个 API 只给 32 位，
    /// 而 x64 的 HMODULE 是 64 位 —— 高位被截断后算出的导出地址必然是野指针。
    /// </summary>
    private static IntPtr FindRemoteModule(int pid, string moduleName)
    {
        const uint TH32CS_SNAPMODULE = 0x00000008;
        const uint TH32CS_SNAPMODULE32 = 0x00000010;
        var invalid = new IntPtr(-1);

        var snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPMODULE | TH32CS_SNAPMODULE32, pid);
        if (snapshot == invalid || snapshot == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        try
        {
            var entry = new MODULEENTRY32W { dwSize = (uint)Marshal.SizeOf<MODULEENTRY32W>() };
            if (!Module32FirstW(snapshot, ref entry))
            {
                return IntPtr.Zero;
            }

            do
            {
                if (string.Equals(entry.szModule, moduleName, StringComparison.OrdinalIgnoreCase))
                {
                    return entry.modBaseAddr;
                }
            }
            while (Module32NextW(snapshot, ref entry));
        }
        finally
        {
            CloseHandle(snapshot);
        }

        return IntPtr.Zero;
    }

    private static void KillServers()
    {
        try
        {
            foreach (var process in Process.GetProcessesByName(ServerProcessName))
            {
                try
                {
                    process.Kill();
                }
                catch (Exception)
                {
                    // 单个失败不影响后续判断：真正的判据是等不到新进程
                }
                finally
                {
                    process.Dispose();
                }
            }
        }
        catch (Exception)
        {
            // 枚举失败按「没有可结束的」处理
        }
    }

    /// <summary>等服务重新拉起投屏进程，并返回它所在的会话 ID。</summary>
    private static async Task<int?> WaitForServerSessionAsync(CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);

        while (DateTime.UtcNow < deadline && !cancellationToken.IsCancellationRequested)
        {
            try
            {
                var process = Process.GetProcessesByName(ServerProcessName).FirstOrDefault();
                if (process is not null)
                {
                    using (process)
                    {
                        // 给它一点时间完成会话附着，避免读到刚创建时的临时状态
                        await Task.Delay(700, cancellationToken).ConfigureAwait(false);
                        return process.SessionId;
                    }
                }
            }
            catch (Exception)
            {
                // 进程在枚举与取属性之间退出：继续等
            }

            try
            {
                await Task.Delay(500, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return null;
            }
        }

        return null;
    }

    private static bool EnableDebugPrivilege()
    {
        if (!OpenProcessToken(Process.GetCurrentProcess().Handle, TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, out var token))
        {
            return false;
        }

        try
        {
            if (!LookupPrivilegeValueW(null, "SeDebugPrivilege", out var luid))
            {
                return false;
            }

            var state = new TOKEN_PRIVILEGES { Count = 1, Luid = luid, Attributes = SE_PRIVILEGE_ENABLED };
            return AdjustTokenPrivileges(token, false, ref state, 0, IntPtr.Zero, IntPtr.Zero);
        }
        finally
        {
            CloseHandle(token);
        }
    }

    // ---------------- Win32 ----------------

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MODULEENTRY32W
    {
        public uint dwSize;
        public uint th32ModuleID;
        public uint th32ProcessID;
        public uint GlblcntUsage;
        public uint ProccntUsage;
        public IntPtr modBaseAddr;
        public uint modBaseSize;
        public IntPtr hModule;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szModule;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExePath;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, int processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Module32FirstW(IntPtr snapshot, ref MODULEENTRY32W entry);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Module32NextW(IntPtr snapshot, ref MODULEENTRY32W entry);

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct TOKEN_PRIVILEGES
    {
        public int Count;
        public long Luid;
        public int Attributes;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr VirtualAllocEx(IntPtr process, IntPtr address, IntPtr size, uint type, uint protect);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool VirtualFreeEx(IntPtr process, IntPtr address, IntPtr size, uint type);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WriteProcessMemory(IntPtr process, IntPtr address, byte[] buffer, IntPtr size, out IntPtr written);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateRemoteThread(IntPtr process, IntPtr attributes, IntPtr stackSize,
        IntPtr start, IntPtr parameter, uint flags, out IntPtr threadId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetExitCodeThread(IntPtr thread, out uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    // GetProcAddress 只接受 ANSI 函数名（LPCSTR），这里必须是 CharSet.Ansi + ExactSpelling。
    // 之前写成 Unicode，它把 "LoadLibraryW" 当 UTF-16 读成 "L"，返回 0 且 GetLastError=127。
    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true, ExactSpelling = true)]
    private static extern IntPtr GetProcAddress(IntPtr module, string name);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern IntPtr GetModuleHandleW(string name);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern IntPtr LoadLibraryW(string path);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FreeLibrary(IntPtr module);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LookupPrivilegeValueW(string? system, string name, out long luid);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AdjustTokenPrivileges(IntPtr token, [MarshalAs(UnmanagedType.Bool)] bool disableAll,
        ref TOKEN_PRIVILEGES newState, int length, IntPtr previous, IntPtr returnLength);
}
