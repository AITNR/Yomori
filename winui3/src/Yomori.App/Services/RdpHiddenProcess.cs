// -*- coding: utf-8 -*-
using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Yomori.Models;

namespace Yomori.Services;

/// <summary>
/// 在一个**隐藏的桌面（Desktop）**上启动进程 —— 窗口照建，但用户看不到。
/// </summary>
/// <remarks>
/// <para>为什么需要这个：上游 <c>RDPW_Installer.exe</c> 是个一键图形安装器，装完会
/// （照官方 <c>install.bat</c> 的做法）把 <c>RDP_CnC.exe</c> 拉起来当状态窗口。
/// 那个窗口是常驻 GUI，既挡屏幕，又让我们永远等不到"安装器退出"。</para>
/// <para>为什么不换工具：Release 里只有这一个安装器，官方 <c>install.bat</c> 也是这么用的；
/// 我们不打包、不重分发、也不去拆它的内部文件（那会把校验链断掉）。</para>
/// <para>为什么不上"关掉它的窗口"：计划书 §1.3 明确不做窗口消息模拟。换个桌面启动是
/// 我们的进程属性，不动第三方程序的界面逻辑，也不隐藏任何安全提示。</para>
/// <para>为什么 <c>bInheritHandles=false</c> 很重要：上一版重定向管道时，安装器拉起的
/// <c>netsh</c>/<c>reg.exe</c>/被重启的 svchost 会继承写端，导致我们永远读不到 EOF。
/// 不继承句柄就从根上没了这个坑。</para>
/// </remarks>
public static class RdpHiddenProcess
{
    /// <summary>
    /// 在隐藏桌面上启动一个进程，**不等待它退出**。
    /// </summary>
    /// <remarks>
    /// 调用方要按"状态是否达成"来判定结果（安装器可能因为等待用户按键/查看状态而长期不退）。
    /// 桌面句柄在子进程退出后自动关闭；子进程要是长期不退，句柄随进程一起被系统回收。
    /// </remarks>
    public static (bool Ok, int ProcessId, string Error) Start(string path, string arguments)
    {
        var (record, error) = StartTracked(path, arguments);
        return record is null ? (false, 0, error) : (true, record.Pid, "");
    }

    /// <summary>
    /// 同 <see cref="Start"/>，但返回可写进事务账本的进程记录（PID + 启动时间）。
    /// </summary>
    /// <remarks>
    /// 2026-10-26 检查记录 P0：只回一个 PID 不够 —— 调用方超时后没有任何办法判断
    /// 「它还在不在写文件」，于是要么盲目继续、要么永远拦着用户。启动时间是为了防 PID 复用。
    /// </remarks>
    public static (RdpToolProcessRecord? Record, string Error) StartTracked(
        string path, string arguments, string role = RdpToolProcessRecord.RoleBlocking)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return (null, $"文件不存在：{path}");
        }

        var desktopName = "YomoriRdp-" + Guid.NewGuid().ToString("N").Substring(0, 12);
        var desktop = Native.CreateDesktopW(desktopName, IntPtr.Zero, IntPtr.Zero, 0, Native.GENERIC_ALL, IntPtr.Zero);

        if (desktop == IntPtr.Zero)
        {
            return (null, $"CreateDesktop 失败：Win32 {Marshal.GetLastWin32Error()}");
        }

        try
        {
            var startInfo = new Native.STARTUPINFO
            {
                cb = Marshal.SizeOf<Native.STARTUPINFO>(),
                lpDesktop = desktopName,
                dwFlags = Native.STARTF_USESHOWWINDOW,
                wShowWindow = Native.SW_HIDE,
            };

            var commandLine = arguments.Length > 0 ? $"\"{path}\" {arguments}" : $"\"{path}\"";

            var started = Native.CreateProcessW(
                path,
                commandLine,
                IntPtr.Zero,
                IntPtr.Zero,
                bInheritHandles: false,
                dwCreationFlags: 0,
                lpEnvironment: IntPtr.Zero,
                currentDirectory: Path.GetDirectoryName(path),
                ref startInfo,
                out var processInfo);

            if (!started)
            {
                var code = Marshal.GetLastWin32Error();
                Native.CloseDesktop(desktop);
                return (null, $"CreateProcess 失败：Win32 {code}");
            }

            Native.CloseHandle(processInfo.hThread);
            Native.CloseHandle(processInfo.hProcess);

            var pid = processInfo.dwProcessId;
            WatchAndCloseDesktop(pid, desktop);

            var record = RdpToolProcessTree.Capture(pid, role, path);
            return record is not null
                ? (record, "")
                : (new RdpToolProcessRecord { Pid = pid, Name = Path.GetFileName(path), Path = path, Role = role }, "");
        }
        catch (Exception ex)
        {
            Native.CloseDesktop(desktop);
            return (null, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>子进程退出后关掉桌面句柄；退出前别关（会让它的窗口立刻失效）。</summary>
    private static void WatchAndCloseDesktop(int pid, IntPtr desktop)
    {
        _ = Task.Run(() =>
        {
            try
            {
                using var process = Process.GetProcessById(pid);
                process.WaitForExit();
            }
            catch
            {
                // 进程已退出 / 拿不到句柄：直接往下走
            }

            try
            {
                Native.CloseDesktop(desktop);
            }
            catch
            {
                // 忽略
            }
        });
    }

    private static class Native
    {
        public const uint GENERIC_ALL = 0x10000000;
        public const int STARTF_USESHOWWINDOW = 0x00000001;
        public const short SW_HIDE = 0;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct STARTUPINFO
        {
            public int cb;
            public IntPtr lpReserved;
            public string? lpDesktop;
            public string? lpTitle;
            public int dwX;
            public int dwY;
            public int dwXSize;
            public int dwYSize;
            public int dwXCountChars;
            public int dwYCountChars;
            public int dwFillAttribute;
            public int dwFlags;
            public short wShowWindow;
            public short cbReserved2;
            public IntPtr lpReserved2;
            public IntPtr hStdInput;
            public IntPtr hStdOutput;
            public IntPtr hStdError;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct PROCESS_INFORMATION
        {
            public IntPtr hProcess;
            public IntPtr hThread;
            public int dwProcessId;
            public int dwThreadId;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr CreateDesktopW(
            string desktopName,
            IntPtr device,
            IntPtr deviceMode,
            uint flags,
            uint desiredAccess,
            IntPtr securityAttributes);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CloseDesktop(IntPtr desktop);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CreateProcessW(
            string? applicationName,
            string commandLine,
            IntPtr processAttributes,
            IntPtr threadAttributes,
            bool bInheritHandles,
            uint dwCreationFlags,
            IntPtr lpEnvironment,
            string? currentDirectory,
            ref STARTUPINFO startupInfo,
            out PROCESS_INFORMATION processInformation);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CloseHandle(IntPtr handle);
    }
}
