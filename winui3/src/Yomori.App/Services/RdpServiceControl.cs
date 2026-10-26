// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Yomori.Models;

namespace Yomori.Services;

/// <summary>
/// TermService 的停止/启动/重启（属于「修改模块」，检测不走这里）。
/// </summary>
/// <remarks>
/// <para>为什么不用 <c>net stop termservice</c>：命令行要过 cmd 一层，退出码语义被包了一层，
/// 而且拿不到「服务到底停在哪个状态」。这里直接用 SCM API，每一步都能回读实际状态。</para>
/// <para>为什么必须管依赖服务：停 TermService 会连带停掉 <c>UmRdpService</c>
/// （Remote Desktop Services UserMode Port Redirector）等依赖它的服务。
/// 计划书 5.4 要求「服务状态只还原本次改变的项；原本停止的依赖服务不无条件启动」，
/// 所以先记下「谁本来在跑」，重启后只把这些拉起来。</para>
/// </remarks>
public static class RdpServiceControl
{
    public const string TermServiceName = "TermService";

    /// <summary>计划书 6.2 的实施初值：每段服务等待 45 秒，经 VM 实测后调整。</summary>
    public static readonly TimeSpan DefaultWait = TimeSpan.FromSeconds(45);

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>查当前状态。<c>Error</c> 非空时 <c>State</c> 为 Unknown。</summary>
    public static (RdpServiceState State, uint Raw, string Error) Query(string serviceName)
    {
        var scm = Native.OpenSCManagerW(null, null, Native.SC_MANAGER_CONNECT);
        if (scm == IntPtr.Zero)
        {
            return (RdpServiceState.Unknown, 0, $"OpenSCManager 失败：Win32 {Marshal.GetLastWin32Error()}");
        }

        try
        {
            var service = Native.OpenServiceW(scm, serviceName, Native.SERVICE_QUERY_STATUS);
            if (service == IntPtr.Zero)
            {
                return (RdpServiceState.Unknown, 0, $"OpenService 失败：Win32 {Marshal.GetLastWin32Error()}");
            }

            try
            {
                if (!Native.QueryServiceStatusEx(
                        service,
                        Native.SC_STATUS_PROCESS_INFO,
                        out var status,
                        (uint)Marshal.SizeOf<Native.SERVICE_STATUS_PROCESS>(),
                        out _))
                {
                    return (RdpServiceState.Unknown, 0, $"QueryServiceStatusEx 失败：Win32 {Marshal.GetLastWin32Error()}");
                }

                return (RdpReadOnlyDetector.MapServiceState(status.dwCurrentState), status.dwCurrentState, "");
            }
            finally
            {
                Native.CloseServiceHandle(service);
            }
        }
        finally
        {
            Native.CloseServiceHandle(scm);
        }
    }

    /// <summary>
    /// 列出依赖 <paramref name="serviceName"/> 的服务里**当前正在运行**的那些。
    /// </summary>
    public static IReadOnlyList<string> RunningDependents(string serviceName) =>
        QueryRunningDependents(serviceName).Names;

    /// <summary>
    /// 同 <see cref="RunningDependents"/>，但把失败原因带出来。
    /// </summary>
    /// <remarks>
    /// 枚举失败**不能**当成「没有依赖服务」：那会让调用方以为不用恢复任何东西，
    /// 而实际上可能漏掉了 UmRdpService 这类必须跟着起来的服务。
    /// </remarks>
    public static (IReadOnlyList<string> Names, string Error) QueryRunningDependents(string serviceName)
    {
        var result = new List<string>();
        var scm = Native.OpenSCManagerW(null, null, Native.SC_MANAGER_CONNECT);
        if (scm == IntPtr.Zero)
        {
            return (result, $"OpenSCManager 失败：Win32 {Marshal.GetLastWin32Error()}");
        }

        try
        {
            var service = Native.OpenServiceW(
                scm, serviceName, Native.SERVICE_QUERY_STATUS | Native.SERVICE_ENUMERATE_DEPENDENTS);
            if (service == IntPtr.Zero)
            {
                return (result, $"OpenService 失败：Win32 {Marshal.GetLastWin32Error()}");
            }

            try
            {
                // 先问需要多大缓冲，再按需分配 —— 依赖服务的数量不确定
                // 首次调用必然失败并要求给缓冲区；只有结果真的是「没有依赖」时 needed 才是 0
                if (Native.EnumDependentServicesW(
                        service, Native.SERVICE_STATE_ALL, IntPtr.Zero, 0, out var needed, out _))
                {
                    return (result, "");
                }

                var probe = Marshal.GetLastWin32Error();
                if (needed == 0)
                {
                    // 122 = ERROR_INSUFFICIENT_BUFFER 才是正常路径；别的错误码说明这次查询不可信
                    return probe == 122
                        ? (result, "")
                        : (result, $"{serviceName} 的依赖服务枚举失败：Win32 {probe}");
                }

                var buffer = Marshal.AllocHGlobal((int)needed);
                try
                {
                    if (!Native.EnumDependentServicesW(
                            service, Native.SERVICE_STATE_ALL, buffer, needed, out _, out var count))
                    {
                        return (result, $"{serviceName} 的依赖服务读取失败：Win32 {Marshal.GetLastWin32Error()}");
                    }

                    var size = Marshal.SizeOf<Native.ENUM_SERVICE_STATUSW>();
                    for (var i = 0; i < count; i++)
                    {
                        var item = Marshal.PtrToStructure<Native.ENUM_SERVICE_STATUSW>(buffer + (i * size));
                        var name = item.lpServiceName ?? "";
                        if (name.Length == 0)
                        {
                            continue;
                        }

                        // 只记「本来在跑」的，停止的原样保持停止
                        if (item.ServiceStatus.dwCurrentState == 4)
                        {
                            result.Add(name);
                        }
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }
            finally
            {
                Native.CloseServiceHandle(service);
            }
        }
        finally
        {
            Native.CloseServiceHandle(scm);
        }

        return (result, "");
    }

    /// <summary>
    /// 有没有权限停这个服务（**只查权限，不动它**）。
    /// </summary>
    /// <remarks>
    /// 用在自检里：停 TermService 之前要先停依赖服务（UmRdpService），
    /// 如果对依赖服务没有 SERVICE_STOP 权限，整套「先停依赖」的方案就落不了地 ——
    /// 这件事必须能在不真的停服务的前提下验证。
    /// </remarks>
    public static bool CanStop(string serviceName)
    {
        var scm = Native.OpenSCManagerW(null, null, Native.SC_MANAGER_CONNECT);
        if (scm == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            var service = Native.OpenServiceW(
                scm, serviceName, Native.SERVICE_STOP | Native.SERVICE_QUERY_STATUS);
            if (service == IntPtr.Zero)
            {
                return false;
            }

            Native.CloseServiceHandle(service);
            return true;
        }
        finally
        {
            Native.CloseServiceHandle(scm);
        }
    }

    /// <summary>停止服务并等它真的停下。</summary>
    public static (bool Ok, string Message) Stop(string serviceName, TimeSpan? wait = null)
    {
        var timeout = wait ?? DefaultWait;
        var (state, _, error) = Query(serviceName);

        if (error.Length > 0)
        {
            return (false, error);
        }

        if (state == RdpServiceState.Stopped)
        {
            return (true, $"{serviceName} 本来就是停止状态");
        }

        var scm = Native.OpenSCManagerW(null, null, Native.SC_MANAGER_CONNECT);
        if (scm == IntPtr.Zero)
        {
            return (false, $"OpenSCManager 失败：Win32 {Marshal.GetLastWin32Error()}");
        }

        try
        {
            var service = Native.OpenServiceW(
                scm, serviceName, Native.SERVICE_STOP | Native.SERVICE_QUERY_STATUS);
            if (service == IntPtr.Zero)
            {
                return (false, $"OpenService(停止) 失败：Win32 {Marshal.GetLastWin32Error()}");
            }

            try
            {
                if (!Native.ControlService(service, Native.SERVICE_CONTROL_STOP, out var status))
                {
                    var code = Marshal.GetLastWin32Error();

                    // 1062 = 服务没在运行；1061 = 当前不接受控制命令（正在切换中）
                    // 1051 = 还有依赖服务在跑，系统不允许单独停它
                    if (code == 1062)
                    {
                        return (true, $"{serviceName} 已经停止");
                    }

                    if (code == 1051)
                    {
                        return (false,
                            $"{serviceName} 有依赖服务正在运行，系统不允许单独停止它（Win32 1051）");
                    }

                    return (false, $"ControlService(STOP) 失败：Win32 {code}");
                }
            }
            finally
            {
                Native.CloseServiceHandle(service);
            }
        }
        finally
        {
            Native.CloseServiceHandle(scm);
        }

        var stopped = WaitForState(serviceName, RdpServiceState.Stopped, timeout);
        return stopped
            ? (true, $"{serviceName} 已停止")
            : (false, $"{serviceName} 在 {timeout.TotalSeconds:0} 秒内没有停下");
    }

    /// <summary>启动服务并等它真的跑起来。</summary>
    public static (bool Ok, string Message) Start(string serviceName, TimeSpan? wait = null)
    {
        var timeout = wait ?? DefaultWait;
        var (state, _, error) = Query(serviceName);

        if (error.Length > 0)
        {
            return (false, error);
        }

        if (state == RdpServiceState.Running)
        {
            return (true, $"{serviceName} 已在运行");
        }

        var scm = Native.OpenSCManagerW(null, null, Native.SC_MANAGER_CONNECT);
        if (scm == IntPtr.Zero)
        {
            return (false, $"OpenSCManager 失败：Win32 {Marshal.GetLastWin32Error()}");
        }

        try
        {
            var service = Native.OpenServiceW(
                scm, serviceName, Native.SERVICE_START | Native.SERVICE_QUERY_STATUS);
            if (service == IntPtr.Zero)
            {
                return (false, $"OpenService(启动) 失败：Win32 {Marshal.GetLastWin32Error()}");
            }

            try
            {
                if (!Native.StartServiceW(service, 0, IntPtr.Zero))
                {
                    var code = Marshal.GetLastWin32Error();

                    // 1056 = 已在运行
                    if (code != 1056)
                    {
                        return (false, $"StartService 失败：Win32 {code}");
                    }
                }
            }
            finally
            {
                Native.CloseServiceHandle(service);
            }
        }
        finally
        {
            Native.CloseServiceHandle(scm);
        }

        var running = WaitForState(serviceName, RdpServiceState.Running, timeout);
        return running
            ? (true, $"{serviceName} 已启动")
            : (false, $"{serviceName} 在 {timeout.TotalSeconds:0} 秒内没有进入运行状态");
    }

    /// <summary>
    /// 停 + 起一轮，并恢复本轮被连带停掉、原本在运行的依赖服务。
    /// </summary>
    /// <remarks>
    /// 返回的说明会逐条列出「停过谁 / 起了谁」，因为计划书 5.4 要求
    /// 「服务状态只还原本次改变的项」，用户要能看到到底动了什么。
    /// </remarks>
    public static (bool Ok, string Message, IReadOnlyList<string> Touched) Restart(
        string serviceName, TimeSpan? wait = null)
    {
        var touched = new List<string>();
        var notes = new List<string>();

        var (dependents, dependentError) = QueryRunningDependents(serviceName);
        if (dependentError.Length > 0)
        {
            // 查不出依赖时继续停启（用户要的是把服务拉回来），但必须留下这句，
            // 否则「依赖没恢复」会变成事后无从解释的谜
            notes.Add($"依赖服务没查出来：{dependentError}");
        }

        foreach (var dependent in dependents)
        {
            touched.Add(dependent);
        }

        // ⚠️ **必须先停依赖服务，再停宿主。**
        // ControlService(STOP) 对 TermService 只会返回 Win32 1051
        // （ERROR_DEPENDENT_SERVICES_RUNNING）—— 它不会替你停依赖，只有 `net stop` 会。
        // 2026-10-26 真机就卡在这里：UmRdpService 正在运行，TermService 停不下去，
        // 于是整轮被判失败、刚装好的组件又被补偿卸掉。
        var stoppedDependents = new List<string>();
        foreach (var dependent in dependents)
        {
            var (depStopOk, depStopMessage) = Stop(dependent, wait);
            notes.Add(depStopOk ? $"已停依赖服务 {dependent}" : $"依赖服务 {dependent} 停不下来：{depStopMessage}");

            if (depStopOk)
            {
                stoppedDependents.Add(dependent);
            }
        }

        var (stopOk, stopMessage) = Stop(serviceName, wait);
        notes.Add(stopMessage);
        if (!stopOk)
        {
            // 依赖没停干净时，把「是谁挡着」写出来，比只报 1051 有用
            if (stopMessage.Contains("1051", StringComparison.Ordinal) && dependents.Count > 0)
            {
                notes.Add($"仍被依赖服务挡着：{string.Join("、", dependents)}");
            }

            return (false, string.Join("；", notes), touched);
        }

        var (startOk, startMessage) = Start(serviceName, wait);
        notes.Add(startMessage);
        if (!startOk)
        {
            return (false, string.Join("；", notes), touched);
        }

        // 依赖服务要宿主先起来才能起，所以放在 TermService 之后
        foreach (var dependent in dependents)
        {
            var (depOk, depMessage) = Start(dependent, wait);
            notes.Add(depOk ? $"已恢复依赖服务 {dependent}" : $"依赖服务 {dependent} 恢复失败：{depMessage}");
        }

        _ = stoppedDependents;
        return (true, string.Join("；", notes), touched);
    }

    /// <summary>轮询直到进入目标状态或超时。不强制杀进程（计划书 6.2：不直接强杀正在写文件的工具）。</summary>
    public static bool WaitForState(string serviceName, RdpServiceState target, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.Now + timeout;

        while (DateTimeOffset.Now < deadline)
        {
            var (state, _, error) = Query(serviceName);
            if (error.Length == 0 && state == target)
            {
                return true;
            }

            System.Threading.Thread.Sleep(PollInterval);
        }

        return false;
    }

    private static class Native
    {
        public const uint SC_MANAGER_CONNECT = 0x0001;
        public const uint SERVICE_QUERY_STATUS = 0x0004;
        public const uint SERVICE_START = 0x0010;
        public const uint SERVICE_STOP = 0x0020;
        /// <summary>
        /// 0x0008。这个值踩过两次：写过 0x0001（SERVICE_QUERY_CONFIG）、也写过 0x0080
        /// （SERVICE_INTERROGATE），两次都让 EnumDependentServices 以 Win32 5（拒绝访问）失败，
        /// 而代码把「调用失败」当成「没有依赖服务」，于是补偿永远不会恢复 UmRdpService。
        /// 2026-10-26 实测：TermService 确实有依赖 UmRdpService（运行中），枚举却返回 0 个。
        /// </summary>
        public const uint SERVICE_ENUMERATE_DEPENDENTS = 0x0008;
        public const uint SERVICE_CONTROL_STOP = 0x00000001;
        public const uint SERVICE_STATE_ALL = 0x00000003;
        public const int SC_STATUS_PROCESS_INFO = 0;

        [StructLayout(LayoutKind.Sequential)]
        public struct SERVICE_STATUS
        {
            public uint dwServiceType;
            public uint dwCurrentState;
            public uint dwControlsAccepted;
            public uint dwWin32ExitCode;
            public uint dwServiceSpecificExitCode;
            public uint dwCheckPoint;
            public uint dwWaitHint;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct SERVICE_STATUS_PROCESS
        {
            public uint dwServiceType;
            public uint dwCurrentState;
            public uint dwControlsAccepted;
            public uint dwWin32ExitCode;
            public uint dwServiceSpecificExitCode;
            public uint dwCheckPoint;
            public uint dwWaitHint;
            public uint dwProcessId;
            public uint dwServiceFlags;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct ENUM_SERVICE_STATUSW
        {
            public string? lpServiceName;
            public string? lpDisplayName;
            public SERVICE_STATUS ServiceStatus;
        }

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr OpenSCManagerW(string? machineName, string? databaseName, uint access);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr OpenServiceW(IntPtr scManager, string serviceName, uint access);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CloseServiceHandle(IntPtr handle);

        /// <summary>
        /// 查服务状态。
        /// </summary>
        /// <remarks>
        /// ⚠️ <c>QueryServiceStatusEx</c> 是 **5 个参数**，最后一个 <c>pcbBytesNeeded</c>
        /// （<c>LPDWORD</c>）不能省：原生实现会通过它写回实际字节数。
        /// 少声明这一个参数时，它写的是栈上一个随机值当指针 —— 进程就地
        /// <c>AccessViolationException</c>，而且是**第一次调用就炸**，
        /// 不是「跑几次才坏」那种堆损坏。2026-10-26 真机点解锁即死就是这个原因。
        /// </remarks>
        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool QueryServiceStatusEx(
            IntPtr service,
            int infoLevel,
            out SERVICE_STATUS_PROCESS buffer,
            uint bufferSize,
            out uint bytesNeeded);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool ControlService(IntPtr service, uint control, out SERVICE_STATUS status);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool StartServiceW(IntPtr service, int argCount, IntPtr arguments);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool EnumDependentServicesW(
            IntPtr service,
            uint serviceState,
            IntPtr services,
            uint bufferSize,
            out uint bytesNeeded,
            out uint servicesReturned);
    }
}
