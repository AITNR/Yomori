// -*- coding: utf-8 -*-
using System;
using System.Runtime.InteropServices;
using System.Text;

namespace Yomori.Services;

/// <summary>调度器需要的 Windows API。仅 P/Invoke 声明，不含业务逻辑。</summary>
internal static class NativeMethods
{
    public const uint WaitObject0 = 0x00000000;
    public const uint WaitTimeout = 0x00000102;
    public const uint Infinite = 0xFFFFFFFF;

    public const uint SeeMaskNoCloseProcess = 0x00000040;
    public const int SwShowNormal = 1;

    public const byte VkReturn = 0x0D;
    public const uint KeyEventFKeyUp = 0x0002;

    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetWindowTextLengthW(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetWindowTextW(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    /// <summary>WM_CLOSE：请求窗口按"用户点了关闭"的正常流程退出。</summary>
    public const uint WmClose = 0x0010;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool PostMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    [DllImport("kernel32.dll")]
    public static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetExitCodeProcess(IntPtr hProcess, out uint lpExitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool TerminateProcess(IntPtr hProcess, uint uExitCode);

    [DllImport("kernel32.dll")]
    public static extern uint GetProcessId(IntPtr hProcess);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct ShellExecuteInfoW
    {
        public int cbSize;
        public uint fMask;
        public IntPtr hwnd;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpVerb;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpFile;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpParameters;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpDirectory;
        public int nShow;
        public IntPtr hInstApp;
        public IntPtr lpIDList;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpClass;
        public IntPtr hkeyClass;
        public uint dwHotKey;
        public IntPtr hIcon;
        public IntPtr hProcess;
    }

    [DllImport("shell32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ShellExecuteExW(ref ShellExecuteInfoW lpExecInfo);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr CommandLineToArgvW(
        [MarshalAs(UnmanagedType.LPWStr)] string lpCmdLine,
        out int pNumArgs);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr LocalFree(IntPtr hMem);

    [DllImport("user32.dll")]
    public static extern int GetSystemMetrics(int nIndex);

    public const int SmXVirtualScreen = 76;
    public const int SmYVirtualScreen = 77;
    public const int SmCxVirtualScreen = 78;
    public const int SmCyVirtualScreen = 79;

    // ---------------- 终端服务（WTS）：查询 / 断开 / 注销会话 ----------------
    // 相比解析 query session 的文本输出，WTS API 不受系统语言和列名影响。

    public const int WtsUserName = 5;
    public const int WtsDomainName = 7;
    public const int WtsConnectState = 8;

    public static readonly IntPtr WtsCurrentServerHandle = IntPtr.Zero;

    [StructLayout(LayoutKind.Sequential)]
    public struct WtsSessionInfo
    {
        public uint SessionId;

        public IntPtr WinStationName;

        public uint State;
    }

    [DllImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool WTSEnumerateSessionsW(
        IntPtr hServer,
        int reserved,
        int version,
        out IntPtr ppSessionInfo,
        out uint pCount);

    [DllImport("wtsapi32.dll")]
    public static extern void WTSFreeMemory(IntPtr pMemory);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool WTSQuerySessionInformationW(
        IntPtr hServer,
        uint sessionId,
        int wtsInfoClass,
        out IntPtr ppBuffer,
        out uint pBytesReturned);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool WTSLogoffSession(IntPtr hServer, uint sessionId, [MarshalAs(UnmanagedType.Bool)] bool bWait);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool WTSDisconnectSession(
        IntPtr hServer,
        uint sessionId,
        [MarshalAs(UnmanagedType.Bool)] bool bWait);

    /// <summary>尚未附着任何控制台会话时的返回值（微软文档：0xFFFFFFFF）。</summary>
    public const uint WtsNoActiveConsoleSession = 0xFFFFFFFF;

    /// <summary>
    /// 取当前附着在物理控制台（显示器/键盘/鼠标）上的会话 ID。
    /// 无控制台附着时返回 <see cref="WtsNoActiveConsoleSession"/>，调用方不得把它当成有效目标。
    /// </summary>
    [DllImport("wtsapi32.dll")]
    public static extern uint WTSGetActiveConsoleSessionId();


    // ---------------- Toolhelp32：一次快照拿全系统的 PID / 父 PID / 镜像名 ----------------
    //
    // 为什么必须自己拿父子关系：.NET 的 Process.Kill(entireProcessTree: true) **实测会漏掉后代** ——
    // 2026-09-27 真机：`March7th Launcher.exe` 派生 `March7th Assistant.exe`，停止时托管 API
    // 回报"结束成功"，子进程却活得好好的，随后自己把游戏重新拉起来继续跑（用户看到"停了又自己跑"）。
    // 所以"结束整棵树"这件事不能交给它：得自己按 PPID 列后代、自己逐个结束、再自己复查。
    //
    // 选 Toolhelp32 而不是 WMI / PowerShell：一次调用、不受系统语言与代码页影响、
    // 不需要管理员权限，也**不受会话限制**（能列到 RDP 目标会话里的进程）。

    public const uint Th32csSnapProcess = 0x00000002;

    public static readonly IntPtr InvalidHandleValue = new(-1);

    /// <summary>对应 PROCESSENTRY32W。x64 下 Marshal.SizeOf = 564（与 Windows 期望的 dwSize 一致）。</summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct ProcessEntry32W
    {
        public int dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool Process32FirstW(IntPtr hSnapshot, ref ProcessEntry32W lppe);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool Process32NextW(IntPtr hSnapshot, ref ProcessEntry32W lppe);

    // 消息循环那几个 P/Invoke（PeekMessageW / TranslateMessage / DispatchMessageW /
    // PostQuitMessage / Message 结构）已随代理宿主一起删除：代理是纯后台进程，
    // 不开窗、不收窗口消息，历史上那套消息泵只是为了让"隐藏的 XAML 窗口"有个替代品，
    // 且 DispatcherQueueController 在真实代理里 100% 建不起来（0x80040111）。
    // 进程存活现在靠主线程同步等常驻循环（见 Services/AgentHost.cs）。
}
