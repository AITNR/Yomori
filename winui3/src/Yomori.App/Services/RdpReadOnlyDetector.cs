// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
using Yomori.Models;

namespace Yomori.Services;

/// <summary>
/// RDP 解锁状态的本机只读检测。
/// </summary>
/// <remarks>
/// <para><b>只读边界（计划书 7.1）。</b>本类型只做四件事：读注册表值、读文件版本资源、
/// 读 INI 文本、查询服务状态与会话枚举。它不写注册表、不写文件、不启动或停止服务、
/// 不下载任何东西、不加载 Wrapper DLL、不申请调试权限。所有 P/Invoke 的入口名由
/// <see cref="ReadOnlyImports"/> 固定列出，冒烟工程断言这份清单之外没有别的导入 ——
/// 这条断言的意义是：以后有人往这个文件里塞一个 <c>RegSetValueEx</c>，
/// 测试会红，而不是靠人记得。
/// </para>
/// <para>
/// 本文件是**按公开的 Win32 文档与可观察行为重新实现**的，没有移植 CnC 的 Pascal 源码，
/// 因此不构成对上游代码的再分发。判定口径（ServiceDll 路径、QueryServiceStatusEx、
/// WinStation 枚举、FileVersion 匹配 INI）与上游对齐的部分写在各自方法上。
/// </para>
/// </remarks>
public static class RdpReadOnlyDetector
{
    /// <summary>远程桌面服务名（不是显示名 Remote Desktop Services）。</summary>
    public const string ServiceName = "TermService";

    /// <summary>监听器会话名。qwinsta 里那一行 <c>rdp-tcp ... 65536 Listen</c> 就是它。</summary>
    public const string ListenerStationName = "RDP-Tcp";

    private const string WrapperFileName = "rdpwrap.dll";
    private const string NativeDllFileName = "termsrv.dll";
    private const string IniFileName = "rdpwrap.ini";
    private const string ServiceKeyPath = @"SYSTEM\CurrentControlSet\Services\TermService";
    private const string ServiceParametersKeyPath = @"SYSTEM\CurrentControlSet\Services\TermService\Parameters";

    /// <summary>
    /// 本模块允许出现的原生导入。冒烟工程会断言实际导入集合是它的子集。
    /// 全部是查询类；出现任何写操作即刻说明越界。
    /// </summary>
    public static readonly IReadOnlyList<string> ReadOnlyImports = new[]
    {
        "OpenSCManagerW",
        "OpenServiceW",
        "QueryServiceStatusEx",
        "CloseServiceHandle",
        "WinStationEnumerateW",
        "WinStationFreeMemory",
        "GetFileVersionInfoSizeW",
        "GetFileVersionInfoW",
        "VerQueryValueW",
    };

    /// <summary>
    /// 跑一次完整检测。<b>不抛异常</b>：任何一项失败都变成该项的 Unknown 加一条错误记录，
    /// 因为调用方是界面上的「重新检测」，抛出去就是一次未观察异常。
    /// </summary>
    public static RdpUnlockSnapshot Detect()
    {
        var errors = new List<string>();
        var capturedAt = DateTimeOffset.Now;
        var elevated = SafeIsElevated();

        // ---- 1. Wrapper：只读 ImagePath / ServiceDll ----
        var imagePath = ReadHklmString(ServiceKeyPath, "ImagePath", errors);
        var serviceDllRaw = ReadHklmString(ServiceParametersKeyPath, "ServiceDll", errors);
        var classification = ClassifyServiceDllPath(serviceDllRaw);

        var installDir = classification.State is RdpWrapperState.Installed or RdpWrapperState.ThirdParty
            ? SafeDirectoryName(classification.NormalizedPath)
            : "";

        // Wrapper 未安装时也把「默认安装目录」算出来：那里面如果留着 rdpwrap.ini，
        // 是「组件被手工删过、但 INI 还在」的常见半残状态，值得显示出来。
        var iniPath = installDir.Length > 0
            ? Path.Combine(installDir, IniFileName)
            : Path.Combine(DefaultInstallDirectory(), IniFileName);

        var wrapperLevel = WrapperLevel(classification.State, classification.NormalizedPath, errors);
        var wrapperItem = new RdpProbeItem
        {
            Key = "wrapper",
            Title = "Wrapper",
            Level = wrapperLevel,
            StateText = RdpProbeText.WrapperLabel(classification.State),
            ValueText = DescribeWrapperValue(classification, serviceDllRaw, out var wrapperExpected),
            ExpectedText = wrapperExpected,
            ErrorCode = wrapperLevel == RdpProbeLevel.Unknown ? "读不到 ServiceDll" : "",
            Advice = WrapperAdvice(classification.State, wrapperLevel),
        };

        // ---- 2. Service：SCM 查询 ----
        var serviceProbe = QueryService(errors);
        var serviceLevel = ServiceLevel(serviceProbe.State);
        var serviceItem = new RdpProbeItem
        {
            Key = "service",
            Title = "Service",
            Level = serviceLevel,
            StateText = RdpProbeText.ServiceLabel(serviceProbe.State),
            ValueText = $"TermService 当前状态 0x{serviceProbe.CurrentState:X}（{RdpProbeText.ServiceLabel(serviceProbe.State)}）"
                        + (serviceProbe.ProcessId > 0 ? $"，宿主 PID {serviceProbe.ProcessId}" : ""),
            ExpectedText = "TermService 处于 RUNNING（4）",
            ErrorCode = serviceProbe.ErrorCode,
            Advice = ServiceAdvice(serviceProbe.State),
        };

        // ---- 3. Listener：WinStation 枚举 + TCP 补充 ----
        var listenerProbe = EnumerateSessions(errors);
        var tcp = ProbeTcp3389();
        var listenerLevel = ListenerLevel(listenerProbe.State);
        var listenerItem = new RdpProbeItem
        {
            Key = "listener",
            Title = "Listener",
            Level = listenerLevel,
            StateText = RdpProbeText.ListenerLabel(listenerProbe.State),
            ValueText = DescribeListenerValue(listenerProbe, tcp),
            ExpectedText = $"WinStation 里存在名为 {ListenerStationName} 的监听会话",
            ErrorCode = listenerProbe.ErrorCode,
            Advice = ListenerAdvice(listenerProbe.State),
        };

        // ---- 4. Support：termsrv.dll 版本 × 本机 INI ----
        var versions = ReadTermsrvVersion(errors);
        var version = versions.Primary;
        var ini = ReadAndAnalyzeIni(iniPath, versions.Candidates, errors);
        var supportLevel = SupportLevel(ini.State);
        var supportItem = new RdpProbeItem
        {
            Key = "support",
            Title = "Support",
            Level = supportLevel,
            StateText = RdpProbeText.SupportLabel(ini.State),
            ValueText = DescribeSupportValue(version, versions, ini),
            ExpectedText = "本机 rdpwrap.ini 中存在与该 termsrv.dll 版本一致的补丁段",
            ErrorCode = ini.Found ? "" : "未找到 rdpwrap.ini",
            Advice = SupportAdvice(ini.State),
        };

        return new RdpUnlockSnapshot
        {
            CapturedAt = capturedAt,
            IsElevated = elevated,
            WrapperState = classification.State,
            ImagePath = imagePath,
            ServiceDllPath = classification.NormalizedPath,
            InstallDirectory = installDir,
            IniPath = iniPath,
            ServiceState = serviceProbe.State,
            ServiceCurrentStateRaw = serviceProbe.CurrentState,
            ServiceWin32ExitCode = serviceProbe.Win32ExitCode,
            ServiceProcessId = serviceProbe.ProcessId,
            ListenerState = listenerProbe.State,
            ListenerSessionId = listenerProbe.ListenerSessionId,
            ListenerSessionState = listenerProbe.ListenerSessionState,
            SessionCount = listenerProbe.SessionCount,
            SessionSummary = listenerProbe.Summary,
            Tcp3389Listening = tcp.Listening,
            TcpDetail = tcp.Detail,
            SupportState = ini.State,
            TermsrvVersion = version,
            TermsrvVersionRaw = versions.Raw,
            TermsrvVersionText = string.Equals(versions.Text, version, StringComparison.OrdinalIgnoreCase)
                ? ""
                : versions.Text,
            IniUpdated = ini.Updated,
            IniLabelMatch = ini.LabelMatch,
            IniStrictSectionMatch = ini.StrictSectionMatch,
            IniMatchedVersion = ini.MatchedVersion,
            Items = new[] { wrapperItem, serviceItem, listenerItem, supportItem },
            Errors = errors,
        };
    }

    // ==================== 纯逻辑（可单独断言） ====================

    /// <summary>
    /// 由 <c>Parameters\ServiceDll</c> 的原始值判定 Wrapper 状态。
    /// </summary>
    /// <remarks>
    /// 上游的做法是读 TermService 的 ImagePath 与 Parameters\ServiceDll，而不是看 DLL 文件在不在
    /// （计划书 2.1）—— 只看文件存在会把「装过又卸载、文件残留」判成已安装。
    /// 这里按文件名判定：<c>termsrv.dll</c> 是原版，<c>rdpwrap.dll</c> 是解锁组件，
    /// 其余一律「第三方接管」，不自动覆盖（计划书 7.2 第 1 条）。
    /// </remarks>
    public static RdpServiceDllClassification ClassifyServiceDllPath(string? raw)
    {
        var normalized = NormalizeServiceDllPath(raw);
        if (normalized.Length == 0)
        {
            return new RdpServiceDllClassification(RdpWrapperState.Unknown, "", "");
        }

        var fileName = Path.GetFileName(normalized);
        if (fileName.Equals(WrapperFileName, StringComparison.OrdinalIgnoreCase))
        {
            return new RdpServiceDllClassification(RdpWrapperState.Installed, normalized, fileName);
        }

        if (fileName.Equals(NativeDllFileName, StringComparison.OrdinalIgnoreCase))
        {
            return new RdpServiceDllClassification(RdpWrapperState.NotInstalled, normalized, fileName);
        }

        return new RdpServiceDllClassification(RdpWrapperState.ThirdParty, normalized, fileName);
    }

    /// <summary>
    /// 把 ServiceDll 的原始值规范化成绝对路径。
    /// </summary>
    /// <remarks>
    /// <para>两种形态都要照顾：REG_EXPAND_SZ 的 <c>%ProgramFiles%\RDP Wrapper\rdpwrap.dll</c>，
    /// 以及历史安装器留下的裸文件名 <c>termsrv.dll</c>（相对于 System32 解析）。</para>
    /// <para>⚠️ 不能只靠 <see cref="Environment.ExpandEnvironmentVariables"/>：它只认**本进程**
    /// 环境块里的变量。本机实测踩过 —— 从被裁剪过环境的父进程启动时 <c>%ProgramFiles%</c>
    /// 展开失败，于是路径被当成相对路径拼到 System32 下，得到一个
    /// <c>C:\WINDOWS\System32\%ProgramFiles%\RDP Wrapper\rdpwrap.dll</c> 的假路径，
    /// 再被判成「组件装了但文件不在」。所以常见安装目录变量一律按已知值兜底替换；
    /// 替换后仍残留 <c>%</c> 的，直接判为不可解析，不猜。</para>
    /// </remarks>
    public static string NormalizeServiceDllPath(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return "";
        }

        var text = raw.Trim().Trim('"');
        if (text.Length == 0)
        {
            return "";
        }

        text = Environment.ExpandEnvironmentVariables(text);
        text = ExpandKnownVariables(text);

        if (text.Contains('%'))
        {
            return "";
        }

        try
        {
            if (!Path.IsPathRooted(text))
            {
                var system32 = Path.Combine(SystemRoot(), "System32");
                text = Path.Combine(system32, text);
            }

            return Path.GetFullPath(text);
        }
        catch
        {
            // 非法字符、超长路径：都当读不出来，别抛
            return "";
        }
    }

    /// <summary>按已知的系统目录替换常见环境变量 —— 不依赖父进程是否传了这些变量。</summary>
    private static string ExpandKnownVariables(string text)
    {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var systemDrive = Path.GetPathRoot(SystemRoot()) ?? @"C:\";

        text = ReplaceToken(text, "%SystemRoot%", SystemRoot());
        text = ReplaceToken(text, "%windir%", SystemRoot());
        text = ReplaceToken(text, "%SystemDrive%", systemDrive);
        text = ReplaceToken(text, "%ProgramW6432%", programFiles);
        text = ReplaceToken(text, "%ProgramFiles%", programFiles);
        text = ReplaceToken(text, "%ProgramFiles(x86)%", programFilesX86);
        return text;
    }

    private static string ReplaceToken(string text, string token, string? value) =>
        string.IsNullOrEmpty(value) ? text : text.Replace(token, value, StringComparison.OrdinalIgnoreCase);

    /// <summary>SCM 的 <c>dwCurrentState</c> → 本工程的四态。<c>Pending</c> 覆盖四种 pending。</summary>
    public static RdpServiceState MapServiceState(uint currentState) => currentState switch
    {
        1 => RdpServiceState.Stopped,
        4 => RdpServiceState.Running,
        7 => RdpServiceState.Paused,
        2 or 3 or 5 or 6 => RdpServiceState.Pending,
        _ => RdpServiceState.Unknown,
    };

    /// <summary>
    /// 解析 rdpwrap.ini 文本，判定当前 termsrv.dll 版本是否被覆盖。
    /// </summary>
    /// <remarks>
    /// <para>两种判定都要算，理由见 <see cref="RdpIniAnalysis"/> 的说明：上游是「整份文本找版本号子串」，
    /// 注释里提一句「本站已覆盖 10.0.26100.1」也会被判命中；严格 section 解析不会。
    /// 两者不一致时报 <see cref="RdpSupportState.Ambiguous"/>，把差异摆出来，而不是替用户下结论。</para>
    /// <para>「该 section 有效」的判据是里面存在以 <c>.x64</c>（或 32 位进程下的 <c>.x86</c>）
    /// 结尾的键 —— 补丁偏移都按架构后缀给出。只看 section 名不看键，会把
    /// 只写了 <c>LocalOnlyTransports=1</c> 的空壳段当成已支持。</para>
    /// </remarks>
    public static RdpIniAnalysis AnalyzeIni(string? iniText, string fourPartVersion, bool is64BitProcess = true)
        => AnalyzeIni(iniText, new[] { fourPartVersion }, is64BitProcess);

    /// <summary>
    /// 同一份 INI 按**多个候选版本**比对，任一命中即算覆盖。
    /// </summary>
    /// <remarks>
    /// <para>为什么要多候选：termsrv.dll 有两套版本号，<c>VS_FIXEDFILEINFO</c> 的数值字段与
    /// StringFileInfo 的 <c>FileVersion</c> 字符串。Windows 的 QS 更新只刷新前者，
    /// 于是会出现「固定信息 10.0.26100.9549 / 字符串 10.0.26100.8875」这种情况。
    /// RDPWrap 自己（以及社区 INI 的段名）用的是固定信息，本机 INI 里也只写了前者；
    /// 但历史上确有按字符串写的 INI，两段都比才不会被某一种起源的 INI 判死。</para>
    /// <para>顺序即优先级：前面的候选先命中，命中哪个记在
    /// <see cref="RdpIniAnalysis.MatchedVersion"/> 里。</para>
    /// </remarks>
    public static RdpIniAnalysis AnalyzeIni(string? iniText, IReadOnlyList<string> candidates,
        bool is64BitProcess = true)
    {
        var archSuffix = is64BitProcess ? ".x64" : ".x86";

        if (string.IsNullOrWhiteSpace(iniText))
        {
            return new RdpIniAnalysis
            {
                Found = false,
                State = RdpSupportState.Unknown,
                Detail = "未找到或读不出 rdpwrap.ini",
            };
        }

        var sections = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var updated = "";
        var current = "";

        foreach (var rawLine in iniText.Split('\n'))
        {
            var line = rawLine.Trim().TrimEnd('\r');
            if (line.Length == 0 || line.StartsWith(';') || line.StartsWith('#'))
            {
                continue;
            }

            if (line.StartsWith('['))
            {
                var close = line.IndexOf(']');
                current = close > 1 ? line.Substring(1, close - 1).Trim() : "";
                if (current.Length > 0 && !sections.ContainsKey(current))
                {
                    sections[current] = new List<string>();
                }

                continue;
            }

            var eq = line.IndexOf('=');
            if (eq <= 0)
            {
                continue;
            }

            var key = line.Substring(0, eq).Trim();
            if (current.Length == 0)
            {
                continue;
            }

            sections[current].Add(key);

            if (updated.Length == 0 && current.Equals("main", StringComparison.OrdinalIgnoreCase)
                && key.Equals("Updated", StringComparison.OrdinalIgnoreCase))
            {
                updated = line.Substring(eq + 1).Trim();
            }
        }

        // 候选去空去重，保持传入顺序（顺序即优先级）
        var versions = new List<string>();
        foreach (var candidate in candidates ?? Array.Empty<string>())
        {
            var version = (candidate ?? string.Empty).Trim();
            if (version.Length > 0 && !versions.Contains(version, StringComparer.OrdinalIgnoreCase))
            {
                versions.Add(version);
            }
        }

        if (versions.Count == 0)
        {
            return new RdpIniAnalysis
            {
                Found = true,
                Updated = updated,
                SectionCount = sections.Count,
                State = RdpSupportState.Unknown,
                Detail = "rdpwrap.ini 可读，但读不到 termsrv.dll 版本，无法比对",
            };
        }

        // 严格命中：逐个候选找同名 section，且该 section 里有本架构的补丁键
        foreach (var version in versions)
        {
            if (!sections.TryGetValue(version, out var keys))
            {
                continue;
            }

            var patchKeyCount = 0;
            foreach (var key in keys)
            {
                if (key.EndsWith(archSuffix, StringComparison.OrdinalIgnoreCase))
                {
                    patchKeyCount++;
                }
            }

            if (patchKeyCount > 0)
            {
                return new RdpIniAnalysis
                {
                    Found = true,
                    Updated = updated,
                    LabelMatch = true,
                    StrictSectionMatch = true,
                    SectionCount = sections.Count,
                    State = RdpSupportState.FullySupported,
                    MatchedVersion = version,
                    Detail = $"[{version}] 段存在，含 {patchKeyCount} 个 {archSuffix} 补丁键",
                };
            }
        }

        // 宽松命中：整份文本里出现过某个候选，但没有可用的补丁段（多半只是注释）
        var labelHit = "";
        foreach (var version in versions)
        {
            if (iniText.IndexOf(version, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                labelHit = version;
                break;
            }
        }

        if (labelHit.Length > 0)
        {
            return new RdpIniAnalysis
            {
                Found = true,
                Updated = updated,
                LabelMatch = true,
                StrictSectionMatch = false,
                SectionCount = sections.Count,
                State = RdpSupportState.Ambiguous,
                MatchedVersion = labelHit,
                Detail = $"INI 文本里出现过 {labelHit}，但没有带 {archSuffix} 补丁键的同名段"
                         + "（常见于注释或说明文字里提到该版本）",
            };
        }

        return new RdpIniAnalysis
        {
            Found = true,
            Updated = updated,
            LabelMatch = false,
            StrictSectionMatch = false,
            SectionCount = sections.Count,
            State = RdpSupportState.NotSupported,
            Detail = $"INI 里没有 {string.Join(" 或 ", versions)} 这个构建号（共 {sections.Count} 个段）",
        };
    }

    // ==================== 具体读数 ====================

    private readonly record struct ServiceProbe(
        RdpServiceState State,
        uint CurrentState,
        uint Win32ExitCode,
        uint ProcessId,
        string ErrorCode);

    private readonly record struct SessionProbe(
        RdpListenerState State,
        int ListenerSessionId,
        int ListenerSessionState,
        int SessionCount,
        string Summary,
        string ErrorCode);

    private readonly record struct TcpProbe(bool Listening, string Detail);

    private static ServiceProbe QueryService(List<string> errors)
    {
        var handle = Native.OpenSCManagerW(null, null, Native.SC_MANAGER_CONNECT);
        if (handle == IntPtr.Zero)
        {
            var code = Marshal.GetLastWin32Error();
            errors.Add($"OpenSCManager 失败：Win32 {code}");
            return new ServiceProbe(RdpServiceState.Unknown, 0, 0, 0, $"Win32 {code}");
        }

        try
        {
            var service = Native.OpenServiceW(handle, ServiceName, Native.SERVICE_QUERY_STATUS);
            if (service == IntPtr.Zero)
            {
                var code = Marshal.GetLastWin32Error();
                errors.Add($"OpenService({ServiceName}) 失败：Win32 {code}");
                return new ServiceProbe(RdpServiceState.Unknown, 0, 0, 0, $"Win32 {code}");
            }

            try
            {
                var buffer = new byte[Marshal.SizeOf<Native.SERVICE_STATUS_PROCESS>()];
                if (!Native.QueryServiceStatusEx(service, Native.SC_STATUS_PROCESS_INFO, buffer, (uint)buffer.Length, out _))
                {
                    var code = Marshal.GetLastWin32Error();
                    errors.Add($"QueryServiceStatusEx({ServiceName}) 失败：Win32 {code}");
                    return new ServiceProbe(RdpServiceState.Unknown, 0, 0, 0, $"Win32 {code}");
                }

                var status = MemoryMarshal.Read<Native.SERVICE_STATUS_PROCESS>(buffer);
                return new ServiceProbe(
                    MapServiceState(status.dwCurrentState),
                    status.dwCurrentState,
                    status.dwWin32ExitCode,
                    status.dwProcessId,
                    "");
            }
            finally
            {
                Native.CloseServiceHandle(service);
            }
        }
        catch (Exception ex)
        {
            errors.Add($"查询服务状态异常：{ex.GetType().Name}: {ex.Message}");
            return new ServiceProbe(RdpServiceState.Unknown, 0, 0, 0, ex.GetType().Name);
        }
        finally
        {
            Native.CloseServiceHandle(handle);
        }
    }

    /// <summary>
    /// 用 <c>WinStationEnumerateW</c> 枚举会话并查找 <c>RDP-Tcp</c>。
    /// </summary>
    /// <remarks>
    /// 不能只看 3389 有没有 TCP 监听：专业版即使没装 Wrapper 也照样监听 3389，
    /// 所以 TCP 只作补充（计划书 7.2 第 3 条）。监听器在 WinStation 里是一条名为
    /// <c>RDP-Tcp</c>、状态 Listen、会话 ID 通常为 65536 的条目。
    /// </remarks>
    private static SessionProbe EnumerateSessions(List<string> errors)
    {
        var pointer = IntPtr.Zero;
        try
        {
            if (!Native.WinStationEnumerateW(IntPtr.Zero, out pointer, out var count))
            {
                var code = Marshal.GetLastWin32Error();
                errors.Add($"WinStationEnumerateW 失败：Win32 {code}");
                return new SessionProbe(RdpListenerState.Unknown, 0, 0, 0, "", $"Win32 {code}");
            }

            var size = Marshal.SizeOf<Native.SESSIONIDW>();
            var listenerId = 0;
            var listenerState = 0;
            var entries = new List<string>();

            for (var i = 0; i < count && i < 4096; i++)
            {
                var item = Marshal.PtrToStructure<Native.SESSIONIDW>(pointer + (i * size));
                var name = (item.WinStationName ?? "").Trim('\0', ' ');
                if (name.Length == 0)
                {
                    name = "(无名)";
                }

                entries.Add($"{name}({item.SessionId}/{item.State})");

                if (listenerId == 0 && name.Equals(ListenerStationName, StringComparison.OrdinalIgnoreCase))
                {
                    listenerId = (int)item.SessionId;
                    listenerState = item.State;
                }
            }

            var summary = entries.Count == 0 ? "" : string.Join(", ", entries);
            return new SessionProbe(
                listenerId > 0 ? RdpListenerState.Listening : RdpListenerState.Absent,
                listenerId,
                listenerState,
                (int)count,
                summary,
                "");
        }
        catch (Exception ex)
        {
            errors.Add($"枚举会话异常：{ex.GetType().Name}: {ex.Message}");
            return new SessionProbe(RdpListenerState.Unknown, 0, 0, 0, "", ex.GetType().Name);
        }
        finally
        {
            if (pointer != IntPtr.Zero)
            {
                // WinStation 层分配的内存必须用它的对手释放；漏掉就是一次原生泄漏
                _ = Native.WinStationFreeMemory(pointer);
            }
        }
    }

    private static TcpProbe ProbeTcp3389()
    {
        try
        {
            var endpoints = new List<string>();
            foreach (var endpoint in IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners())
            {
                if (endpoint.Port == 3389)
                {
                    endpoints.Add(endpoint.ToString());
                }
            }

            return endpoints.Count == 0
                ? new TcpProbe(false, "仅作补充：本机没有进程监听 3389")
                : new TcpProbe(true, string.Join(", ", endpoints));
        }
        catch (Exception ex)
        {
            return new TcpProbe(false, $"仅作补充：TCP 表读取失败 {ex.GetType().Name}");
        }
    }

    /// <summary>
    /// termsrv.dll 的两套版本号。
    /// </summary>
    /// <remarks>
    /// <para>同一份 DLL 里有**两个来源**，Windows 的 QS 更新只刷新数值、不刷新字符串：</para>
    /// <list type="bullet">
    /// <item><see cref="File"/> / <see cref="Product"/>：<c>VS_FIXEDFILEINFO</c> 里的数值字段，
    /// 直接从文件字节读出。RDPWrap CnC 显示、社区 INI 的段名用的都是这一套。</item>
    /// <item><see cref="Text"/>：StringFileInfo 里的 <c>FileVersion</c> 字符串。
    /// 可能长期滞后 —— 本机实测数值是 10.0.26100.9549，字符串仍写 10.0.26100.8875。</item>
    /// </list>
    /// <para>2026-10-25 连栽两次，两次的结论相反、都记在这里：</para>
    /// <list type="number">
    /// <item>按字符串比对 → INI 里只有 <c>[10.0.26100.9549]</c> 段，「已覆盖」被报成「未覆盖（特例）」；</item>
    /// <item>改读 .NET 的 <c>FileMajorPart</c> → 得到 <c>6.2.26100.9549</c>；
    /// 直接 <c>VerQueryValue</c> 同样是 6.2，而磁盘上写的是 10.0 ——
    /// 兼容层把 file version 字段改写成模拟系统版本，product 字段不动。</item>
    /// </list>
    /// <para>所以现在**不信 API、只信文件字节**：file 优先，product 次之，字符串兜底。</para>
    /// </remarks>
    private readonly record struct TermsrvVersions(string File, string Product, string Text, string Raw)
    {
        /// <summary>界面与比对用的主版本号。</summary>
        public string Primary
        {
            get
            {
                if (File.Length > 0)
                {
                    return File;
                }

                if (Product.Length > 0)
                {
                    return Product;
                }

                return Text;
            }
        }

        /// <summary>比对用的候选，主版本在前（顺序即优先级）。</summary>
        public IReadOnlyList<string> Candidates
        {
            get
            {
                var list = new List<string>(3);
                foreach (var candidate in new[] { Primary, Product, Text })
                {
                    if (candidate.Length > 0 && !list.Contains(candidate, StringComparer.OrdinalIgnoreCase))
                    {
                        list.Add(candidate);
                    }
                }

                return list;
            }
        }
    }

    private static TermsrvVersions ReadTermsrvVersion(List<string> errors)
    {
        try
        {
            // 永远读原版 termsrv.dll：Wrapper 装了之后 ServiceDll 指向 rdpwrap.dll，
            // 但版本比对的依据仍然是真正的服务实现文件。
            var path = Path.Combine(SystemRoot(), "System32", NativeDllFileName);
            if (!File.Exists(path))
            {
                errors.Add($"找不到 {path}");
                return default;
            }

            var info = FileVersionInfo.GetVersionInfo(path);
            var raw = info.FileVersion ?? "";
            var textVersion = TryParseFourPartVersion(raw, out var parsed) ? parsed : "";

            // ① 以**磁盘上的** VS_FIXEDFILEINFO 为准。
            //    本机实测踩过：同一份文件，.NET 进程里 version.dll 的 VerQueryValue 给出
            //    dwFileVersionMS = 0x00060002（6.2），而文件字节里是 0x000A0000（10.0）；
            //    dwProductVersion 没被改写。同一份文件用 python 进程读却是 10.0。
            //    成因未定位（疑与宿主进程的兼容层有关），结论很明确：
            //    不能拿 API 的 file version 字段当判据，否则「已覆盖」会被报成「未覆盖」。
            if (TryReadFixedInfoFromDisk(path, out var diskFile, out var diskProduct))
            {
                if (TryReadFixedFileInfo(path, out var api) &&
                    api.FileVersionMs != 0 && diskFile.Length > 0)
                {
                    var apiFile = FourPart(api.FileVersionMs, api.FileVersionLs);
                    if (!string.Equals(apiFile, diskFile, StringComparison.OrdinalIgnoreCase))
                    {
                        // 不打断检测，只留证：将来别人再看到版本号对不上时，这里能直接指向根因
                        errors.Add($"[环境提示] 版本 API 与文件内容不一致：API 报 {apiFile}，"
                                   + $"文件里写的是 {diskFile}；比对以文件内容为准。"
                                   + "（同一份文件在不同宿主进程里读出的 file version 可能不同，成因未定位，"
                                   + "因此这里不采用该字段。）");
                    }
                }

                return new TermsrvVersions(diskFile, diskProduct, textVersion, raw);
            }

            // ② 读不出磁盘结构时才退回 version.dll（正常环境两者一致）
            if (TryReadFixedFileInfo(path, out var fixedInfo))
            {
                return new TermsrvVersions(
                    FourPart(fixedInfo.FileVersionMs, fixedInfo.FileVersionLs),
                    FourPart(fixedInfo.ProductVersionMs, fixedInfo.ProductVersionLs),
                    textVersion,
                    raw);
            }

            // ③ 最后才用框架的数值字段 —— 它在某些文件上等于资源里的值，但不能当主判据
            var product =
                $"{info.ProductMajorPart}.{info.ProductMinorPart}.{info.ProductBuildPart}.{info.ProductPrivatePart}";
            return new TermsrvVersions("", product, textVersion, raw);
        }
        catch (Exception ex)
        {
            errors.Add($"读取 termsrv.dll 版本失败：{ex.GetType().Name}: {ex.Message}");
            return default;
        }
    }

    /// <summary>
    /// 直接在文件字节里找出 <c>VS_FIXEDFILEINFO</c> 并读它的两套版本号。
    /// </summary>
    /// <remarks>
    /// <para>为什么不走 <c>version.dll</c>：本机实测同一份文件，<c>VerQueryValue</c> 返回的
    /// <c>dwFileVersionMS</c> 是 0x00060002（6.2），而文件字节里写的是 0x000A0000（10.0）。
    /// 自己读字节绕开这一层。</para>
    /// <para>匹配条件两个都要满足：签名 0xFEEF04BD、紧跟的 <c>dwStrucVersion</c> 为 0x00010000。
    /// 只认签名会在二进制里撞上偶然相同的四字节，而撞上之后得到的「版本号」看起来完全正常 ——
    /// 那是最坏的一种错。</para>
    /// </remarks>
    private static bool TryReadFixedInfoFromDisk(string path, out string fileVersion, out string productVersion)
    {
        fileVersion = "";
        productVersion = "";

        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(path);
        }
        catch
        {
            return false;
        }

        const uint signature = 0xFEEF04BD;
        const uint expectedStruc = 0x00010000;

        for (var i = 0; i + 24 <= bytes.Length; i++)
        {
            if (BitConverter.ToUInt32(bytes, i) != signature)
            {
                continue;
            }

            if (BitConverter.ToUInt32(bytes, i + 4) != expectedStruc)
            {
                continue;
            }

            fileVersion = FourPart(BitConverter.ToUInt32(bytes, i + 8), BitConverter.ToUInt32(bytes, i + 12));
            productVersion = FourPart(BitConverter.ToUInt32(bytes, i + 16), BitConverter.ToUInt32(bytes, i + 20));
            return fileVersion.Length > 0;
        }

        return false;
    }

    /// <summary>
    /// 读 <c>VS_FIXEDFILEINFO</c>（RT_VERSION 里的数值块）。
    /// </summary>
    /// <remarks>
    /// <para><c>dwSignature</c> 不等于 0xFEEF04BD 一律视为失败 —— 这种块的其余字段全是垃圾，
    /// 但会被格式化成一个「看起来很正常」的版本号，是最坏的一种错。</para>
    /// <para>⚠️ 缓冲区必须自己 pin 住再把地址交给 <c>VerQueryValueW</c>。把
    /// <c>lpBlock</c> 声明成 <c>byte[]</c> 时封送会复制一份、返回的指针指向那块临时内存，
    /// 取结构时可能已经失效 —— 实测读出 <c>6.2.26100.9549</c>（资源里是 10.0.26100.9549），
    /// 一个不报错、还能通过 signature 校验的假版本号。</para>
    /// </remarks>
    private static bool TryReadFixedFileInfo(string path, out Native.VS_FIXEDFILEINFO info)
    {
        info = default;
        try
        {
            var size = Native.GetFileVersionInfoSizeW(path, out _);
            if (size <= 0)
            {
                return false;
            }

            var data = new byte[size];
            if (!Native.GetFileVersionInfoW(path, 0, size, data))
            {
                return false;
            }

            var handle = GCHandle.Alloc(data, GCHandleType.Pinned);
            try
            {
                var block = handle.AddrOfPinnedObject();
                if (!Native.VerQueryValueW(block, @"\", out var buffer, out var length)
                    || buffer == IntPtr.Zero
                    || length < (uint)Marshal.SizeOf<Native.VS_FIXEDFILEINFO>())
                {
                    return false;
                }

                info = Marshal.PtrToStructure<Native.VS_FIXEDFILEINFO>(buffer);
            }
            finally
            {
                handle.Free();
            }

            return info.Signature == 0xFEEF04BD;
        }
        catch
        {
            return false;
        }
    }

    private static string FourPart(uint versionMs, uint versionLs) =>
        $"{(versionMs >> 16) & 0xFFFF}.{versionMs & 0xFFFF}.{(versionLs >> 16) & 0xFFFF}.{versionLs & 0xFFFF}";

    /// <summary>
    /// 从 <c>FileVersion</c> 原文里取四段数字版本（丢掉 " (WinBuild.160101.0800)" 这类后缀）。
    /// </summary>
    /// <remarks>
    /// 公开是为了让冒烟能单独断言：这是纯字符串处理，而它的错法（多带一个括号、少一段、
    /// 把 8875 取成 9549）都不会报错，只会让 Support 一项永远判错。
    /// </remarks>
    public static bool TryParseFourPartVersion(string? fileVersion, out string fourPart)
    {
        fourPart = "";

        if (string.IsNullOrWhiteSpace(fileVersion))
        {
            return false;
        }

        var text = fileVersion.Trim();

        // 只取第一个空白/括号之前的部分
        var cut = text.Length;
        for (var i = 0; i < text.Length; i++)
        {
            if (char.IsWhiteSpace(text[i]) || text[i] == '(')
            {
                cut = i;
                break;
            }
        }

        var token = text.Substring(0, cut);
        var parts = token.Split('.');
        if (parts.Length != 4)
        {
            return false;
        }

        foreach (var part in parts)
        {
            if (part.Length == 0 || !int.TryParse(part, out _))
            {
                return false;
            }
        }

        fourPart = token;
        return true;
    }

    private static RdpIniAnalysis ReadAndAnalyzeIni(string path, IReadOnlyList<string> versions, List<string> errors)
    {
        try
        {
            if (!File.Exists(path))
            {
                return new RdpIniAnalysis { Found = false, State = RdpSupportState.Unknown, Detail = "未找到 rdpwrap.ini" };
            }

            // 社区版 rdpwrap.ini 常见 UTF-8（含或不含 BOM）；ASCII 兼容的 ANSI 也读得动。
            var text = File.ReadAllText(path, Encoding.UTF8);
            return AnalyzeIni(text, versions, Environment.Is64BitProcess);
        }
        catch (Exception ex)
        {
            errors.Add($"读取 rdpwrap.ini 失败：{ex.GetType().Name}: {ex.Message}");
            return new RdpIniAnalysis { Found = false, State = RdpSupportState.Unknown, Detail = "rdpwrap.ini 读取失败" };
        }
    }

    private static string ReadHklmString(string subKey, string valueName, List<string> errors)
    {
        try
        {
            // 显式 Registry64：x64 系统上 32 位视图会把 SYSTEM 键重定向到 Wow6432Node
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = baseKey.OpenSubKey(subKey, writable: false);
            if (key is null)
            {
                errors.Add($"注册表键不存在：HKLM\\{subKey}");
                return "";
            }

            // REG_EXPAND_SZ 取未展开文本，展开动作自己做 —— 与计划书 5.2 的口径一致
            return key.GetValue(valueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string ?? "";
        }
        catch (Exception ex)
        {
            errors.Add($"读取 HKLM\\{subKey}\\{valueName} 失败：{ex.GetType().Name}: {ex.Message}");
            return "";
        }
    }

    // ==================== 等级与文案 ====================

    private static RdpProbeLevel WrapperLevel(RdpWrapperState state, string normalizedPath, List<string> errors)
    {
        switch (state)
        {
            case RdpWrapperState.Installed:
                // ServiceDll 指向 rdpwrap.dll 但文件不在：典型的「卸载不干净」。
                // 这种情况服务下次启动就会失败，所以不能报「通过」。
                if (!SafeFileExists(normalizedPath))
                {
                    errors.Add($"ServiceDll 指向的 {normalizedPath} 不存在");
                    return RdpProbeLevel.Warn;
                }

                return RdpProbeLevel.Pass;
            case RdpWrapperState.NotInstalled:
                return RdpProbeLevel.Fail;
            case RdpWrapperState.ThirdParty:
                return RdpProbeLevel.Warn;
            default:
                return RdpProbeLevel.Unknown;
        }
    }

    private static RdpProbeLevel ServiceLevel(RdpServiceState state) => state switch
    {
        RdpServiceState.Running => RdpProbeLevel.Pass,
        RdpServiceState.Stopped => RdpProbeLevel.Fail,
        // 正在切换时既不能说好也不能说坏：计划书 3.1 明确「不能在修改中把瞬时服务停止误报为最终失败」
        RdpServiceState.Pending => RdpProbeLevel.Warn,
        RdpServiceState.Paused => RdpProbeLevel.Warn,
        _ => RdpProbeLevel.Unknown,
    };

    private static RdpProbeLevel ListenerLevel(RdpListenerState state) => state switch
    {
        RdpListenerState.Listening => RdpProbeLevel.Pass,
        RdpListenerState.Absent => RdpProbeLevel.Fail,
        _ => RdpProbeLevel.Unknown,
    };

    private static RdpProbeLevel SupportLevel(RdpSupportState state) => state switch
    {
        RdpSupportState.FullySupported => RdpProbeLevel.Pass,
        RdpSupportState.NotSupported => RdpProbeLevel.Fail,
        RdpSupportState.Ambiguous => RdpProbeLevel.Warn,
        _ => RdpProbeLevel.Unknown,
    };

    private static string DescribeWrapperValue(
        RdpServiceDllClassification classification,
        string rawValue,
        out string expected)
    {
        expected = @"ServiceDll 指向 %SystemRoot%\system32\termsrv.dll（原版）或 rdpwrap.dll（已接管）";
        return classification.State switch
        {
            // 判不准时把注册表原文贴出来：用户/我们都要看得到「到底写的是什么」，
            // 而不是一句「读不到」—— 原值里带未解析变量是最常见的一种
            RdpWrapperState.Unknown when !string.IsNullOrWhiteSpace(rawValue) =>
                $"注册表原值 {rawValue.Trim()}（路径无法解析，或读取被拒绝）",
            RdpWrapperState.Unknown =>
                @"读不到 HKLM\SYSTEM\CurrentControlSet\Services\TermService\Parameters\ServiceDll",
            RdpWrapperState.NotInstalled => classification.NormalizedPath,
            RdpWrapperState.Installed => classification.NormalizedPath,
            _ => $"{classification.NormalizedPath}（既不是原版也不是 rdpwrap.dll）",
        };
    }

    private static string WrapperAdvice(RdpWrapperState state, RdpProbeLevel level) => state switch
    {
        RdpWrapperState.NotInstalled => "本机没有解锁组件。点击「解锁 RDP」可按官方最新正式版安装（需先确认关闭安全软件）。",
        RdpWrapperState.ThirdParty => "ServiceDll 被第三方程序接管。Yomori 不会自动覆盖别人的接管，请先确认那是什么。",
        RdpWrapperState.Installed when level == RdpProbeLevel.Warn => "ServiceDll 指向的 rdpwrap.dll 已不存在，服务下次启动会失败；建议重新安装或卸载后还原。",
        RdpWrapperState.Installed => "",
        _ => "以管理员身份重新检测；若仍读不到，检查 TermService 的 Parameters 键是否被策略限制。",
    };

    private static string ServiceAdvice(RdpServiceState state) => state switch
    {
        RdpServiceState.Stopped => "TermService 处于停止状态：先看它为什么停下（事件查看器 → 系统 → Service Control Manager），不要直接重启了当修好。",
        RdpServiceState.Pending => "服务正在切换状态，等它落定后再点「重新检测」。",
        RdpServiceState.Paused => "服务被暂停，远程桌面此时不可用。",
        RdpServiceState.Unknown => "读不到服务状态。若本进程不是管理员，服务查询可能被拒。",
        _ => "",
    };

    private static string ListenerAdvice(RdpListenerState state) => state switch
    {
        RdpListenerState.Absent => "WinStation 里没有 RDP-Tcp 监听：家庭版未解锁时属预期；专业版出现这一项要查 TermService 与 termsrv.dll 是否被替换。",
        RdpListenerState.Unknown => "会话枚举失败。该 API 在部分精简系统上会被裁掉，属无法判定，不等于「不支持」。",
        _ => "",
    };

    private static string SupportAdvice(RdpSupportState state) => state switch
    {
        RdpSupportState.NotSupported => "本机 INI 没有当前构建号。可先更新 INI（RDPWInst -w）；更新后仍不命中说明社区尚未覆盖该版本。",
        RdpSupportState.Ambiguous => "INI 文本里提到该版本但没有对应的补丁段，多半是注释。请以 INI 的 Updated 日期与上游仓库为准。",
        RdpSupportState.Unknown => "读不到 INI 或版本号，无法比对 —— 这是「不知道」，不是「不支持」。",
        _ => "",
    };

    private static string DescribeSupportValue(string version, TermsrvVersions versions, RdpIniAnalysis ini)
    {
        if (version.Length == 0)
        {
            return "读不到 termsrv.dll 版本";
        }

        var updated = ini.Updated.Length > 0 ? $"，INI Updated={ini.Updated}" : "";
        var text = $"termsrv.dll {version}{updated}；{ini.Detail}";

        // 两套版本号不一致时必须解释一句：否则用户看到界面写 9549、文件属性里写 8875，
        // 第一反应是「读错了」，然后就不再信这一项的结论。
        if (versions.Text.Length > 0 &&
            !string.Equals(versions.Text, version, StringComparison.OrdinalIgnoreCase))
        {
            text += $"（文件属性里的版本字符串仍写作 {versions.Text} —— Windows 的 QS 更新"
                    + "只刷新固定文件信息，比对以固定信息为准，与 RDPWrap 一致）";
        }

        return text;
    }

    private static string DescribeListenerValue(SessionProbe probe, TcpProbe tcp)
    {
        var listener = probe.ListenerSessionId > 0
            ? $"找到 {ListenerStationName}，会话 ID {probe.ListenerSessionId}"
              + $"（{RdpProbeText.SessionStateLabel(probe.ListenerSessionState)}）"
            : $"WinStation 里没有 {ListenerStationName}";

        var count = probe.SessionCount > 0 ? $"，共 {probe.SessionCount} 个会话" : "";
        var tcpText = tcp.Listening ? "；TCP 3389 有监听（仅补充）" : "；TCP 3389 无监听（仅补充）";
        return listener + count + tcpText;
    }

    /// <summary>
    /// 本进程是否以管理员身份运行。
    /// </summary>
    /// <remarks>
    /// 自己算而不是借 <c>RdpSessionService.IsElevated</c>：那个服务里带着
    /// <c>EnableRemoteDesktop</c> 之类的写操作，只读模块依赖它，等于把「检测」和「修改」
    /// 焊在了同一条调用链上（计划书 7.1 要求两者分离）。
    /// </remarks>
    private static bool SafeIsElevated()
    {
        try
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            return new System.Security.Principal.WindowsPrincipal(identity)
                .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    private static string SafeDirectoryName(string path)
    {
        try
        {
            return path.Length == 0 ? "" : Path.GetDirectoryName(path) ?? "";
        }
        catch
        {
            return "";
        }
    }

    private static bool SafeFileExists(string path)
    {
        try
        {
            return path.Length > 0 && File.Exists(path);
        }
        catch
        {
            return false;
        }
    }

    private static string SystemRoot()
    {
        var root = Environment.GetEnvironmentVariable("SystemRoot");
        return string.IsNullOrEmpty(root) ? @"C:\Windows" : root;
    }

    private static string DefaultInstallDirectory() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "RDP Wrapper");

    // ==================== 原生导入 ====================

    /// <summary>
    /// 本模块的全部 P/Invoke。**只放查询类导入**，写操作一律不进这里（见 <see cref="ReadOnlyImports"/>）。
    /// </summary>
    private static class Native
    {
        public const uint SC_MANAGER_CONNECT = 0x0001;
        public const uint SERVICE_QUERY_STATUS = 0x0004;
        public const int SC_STATUS_PROCESS_INFO = 0;

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

        /// <summary>
        /// winsta.h 的 <c>SESSIONIDW</c>：<c>{ ULONG SessionId; WCHAR WinStationName[33]; WINSTATIONSTATECLASS State; }</c>。
        /// </summary>
        /// <remarks>
        /// 布局必须与头文件一致 —— 字段顺序或长度错一位，读出来的会话名就是垃圾，
        /// 而且**不会报错**（只是永远找不到 RDP-Tcp）。所以冒烟工程用
        /// <see cref="Layout"/> 把手算的偏移锁死，改结构会立刻红。
        /// <para>⚠️ <c>CharSet = Unicode</c> 不能省：<c>ByValTStr</c> 默认按 ANSI 封送，
        /// 33 个 wchar 会被算成 33 字节，整个结构缩到 44 字节、State 偏移掉到 40 ——
        /// 编译不报错、运行不报错，只是每次都用错位的指针读会话名（首次实测就是这么红的）。</para>
        /// </remarks>
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct SESSIONIDW
        {
            public uint SessionId;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 33)]
            public string WinStationName;

            public int State;
        }

        /// <summary>
        /// <c>VS_FIXEDFILEINFO</c>：版本资源里的数值块，紧跟在 RT_VERSION 的头部之后。
        /// </summary>
        /// <remarks>
        /// <para>13 个 DWORD，<c>dwSignature</c> 必须是 0xFEEF04BD。四个版本段的位置是
        /// 8/12（file）与 16/20（product），偏移错一位读出来的就是「看起来像版本号」的垃圾。</para>
        /// <para>⚠️ 本机 termsrv.dll 的 file version = product version = 10.0.26100.9549，
        /// 而 StringFileInfo 里的 <c>FileVersion</c> 字符串仍写 10.0.26100.8875 ——
        /// 字符串与数值可以差一个 QS 更新，比对只能认数值。</para>
        /// </remarks>
        [StructLayout(LayoutKind.Sequential)]
        public struct VS_FIXEDFILEINFO
        {
            public uint Signature;
            public uint StrucVersion;

            /// <summary>高 16 位主版本、低 16 位次版本。</summary>
            public uint FileVersionMs;

            /// <summary>高 16 位构建号、低 16 位修订号。</summary>
            public uint FileVersionLs;
            public uint ProductVersionMs;
            public uint ProductVersionLs;
            public uint FileFlagsMask;
            public uint FileFlags;
            public uint FileOs;
            public uint FileType;
            public uint FileSubtype;
            public uint FileDateMs;
            public uint FileDateLs;
        }

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr OpenSCManagerW(string? machineName, string? databaseName, uint access);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr OpenServiceW(IntPtr scManager, string serviceName, uint access);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool QueryServiceStatusEx(
            IntPtr service,
            int infoLevel,
            byte[] buffer,
            uint bufferSize,
            out uint bytesNeeded);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CloseServiceHandle(IntPtr handle);

        // 返回的是 Windows BOOLEAN（1 字节），不是 BOOL（4 字节）：
        // 写成默认的 bool 会被按 4 字节 BOOL 封送，返回值高位是垃圾。
        [DllImport("winsta.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.U1)]
        public static extern bool WinStationEnumerateW(IntPtr server, out IntPtr sessionIds, out int count);

        [DllImport("winsta.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.U1)]
        public static extern bool WinStationFreeMemory(IntPtr buffer);

        // ---- 版本资源（version.dll）----
        // 只读：读 VS_FIXEDFILEINFO 的数值字段。为什么不直接用 .NET 的 FileVersionInfo：
        // 本机 termsrv.dll 上它的 FileMajorPart/MinorPart 给出 6.2（而不是资源里写的 10.0），
        // Product* 才给出 10.0.26100.9549 —— 与 RDPWrap CnC 显示的一致。数值取自原始结构
        // 才不依赖框架内部对 File/Product 两套字段的取舍，也能同时把两套都留作诊断。

        [DllImport("version.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern int GetFileVersionInfoSizeW(string fileName, out int handle);

        [DllImport("version.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetFileVersionInfoW(string fileName, int handle, int length, byte[] data);

        [DllImport("version.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool VerQueryValueW(IntPtr block, string subBlock, out IntPtr buffer, out uint length);
    }

    /// <summary>
    /// 结构布局的公开视图，专供冒烟断言（原生结构不允许靠肉眼核对）。
    /// </summary>
    public static class Layout
    {
        public static int SessionIdSize => Marshal.SizeOf<Native.SESSIONIDW>();

        public static int SessionNameOffset => (int)Marshal.OffsetOf<Native.SESSIONIDW>(nameof(Native.SESSIONIDW.WinStationName));

        public static int SessionStateOffset => (int)Marshal.OffsetOf<Native.SESSIONIDW>(nameof(Native.SESSIONIDW.State));

        public static int ServiceStatusSize => Marshal.SizeOf<Native.SERVICE_STATUS_PROCESS>();
    }
}
