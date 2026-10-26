// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Yomori.Helpers;

namespace Yomori.Models;

/// <summary>
/// 远程桌面连接方式（计划书 §5.6 client_mode）：
/// mstsc = 系统自带远程桌面独立窗口（默认，永久回退路径）；
/// embedded = FreeRDP 内嵌画面（画面与输入嵌进Yomori窗口）。
/// </summary>
public static class ClientModes
{
    public const string Mstsc = "mstsc";
    public const string Embedded = "embedded";

    public static readonly IReadOnlyList<KeyValuePair<string, string>> Items =
        new List<KeyValuePair<string, string>>
        {
            new(Mstsc, "独立远程桌面窗口（mstsc，稳定）"),
            new(Embedded, "内嵌到Yomori窗口"),
        };

    public static string Normalize(string? value)
    {
        var text = (value ?? string.Empty).Trim().ToLowerInvariant();
        return text == Embedded ? Embedded : Mstsc;
    }
}

/// <summary>
/// 内嵌画面的渲染后端（计划书 §5.2 / §6 M4 的两种实现）：
/// auto = 默认 D3D11 SwapChainPanel，初始化失败自动回退 SoftwareBitmap；
/// software = 强制 SoftwareBitmap，完全不碰 D3D / 交换链 / GPU。
///
/// 为什么值得暴露给用户：D3D 路线在本机显卡 / 虚拟显示驱动组合异常时可能整窗假死
/// （D3D 调用与 GPU/合成器互锁，进程活着但消息泵停摆）。此时用户自己能切到软渲染
/// 就能立刻恢复可用，不必等新版本 —— 这也是 2026-10-12 那次卡死排查后补的逃生门。
/// </summary>
public static class RenderBackends
{
    public const string Auto = "auto";
    public const string Software = "software";

    public static readonly IReadOnlyList<KeyValuePair<string, string>> Items =
        new List<KeyValuePair<string, string>>
        {
            new(Auto, "D3D11 硬件加速（默认，失败自动回退软渲染）"),
            new(Software, "软渲染 SoftwareBitmap（不用 D3D/GPU，最稳但更吃 CPU）"),
        };

    public static string Normalize(string? value)
    {
        var text = (value ?? string.Empty).Trim().ToLowerInvariant();
        return text == Software ? Software : Auto;
    }

    public static string Label(string? value) => Normalize(value) switch
    {
        Software => "软渲染",
        _ => "D3D11",
    };
}

/// <summary>RDP 会话在任务全部结束后的处理方式。</summary>
public static class SessionFinishModes
{    public const string Keep = "keep";
    public const string Disconnect = "disconnect";
    public const string Logoff = "logoff";
    public static readonly IReadOnlyList<KeyValuePair<string, string>> Items =
        new List<KeyValuePair<string, string>>
        {
            new(Keep, "保持连接（方便随时切过去看结果）"),
            new(Disconnect, "断开会话（后台继续运行）"),
            new(Logoff, "注销目标用户（释放资源）"),
        };

    public static string Normalize(string? value)
    {
        var text = (value ?? string.Empty).Trim().ToLowerInvariant();
        return text switch
        {
            Disconnect => Disconnect,
            Logoff => Logoff,
            _ => Keep,
        };
    }

    public static string Label(string? value) => Normalize(value) switch
    {
        Disconnect => "断开会话",
        Logoff => "注销用户",
        _ => "保持连接",
    };
}

/// <summary>远程桌面分辨率档位。0 表示“自适应”，即按Yomori窗口里的画面区域大小来定。</summary>
public static class RdpResolutions
{
    public const int Auto = 0;

    public const int MinWidth = 640;
    public const int MaxWidth = 4096;
    public const int MinHeight = 480;
    public const int MaxHeight = 4096;

    /// <summary>
    /// 新建通道的默认窗口尺寸（1920 × 1080）。
    /// 不用「自适应」当默认：自适应会跟着画面区域变，同一套任务在不同显示器上
    /// 分辨率不同，脚本按坐标点击就会错位；固定 1080p 让每条通道的桌面尺寸可预期。
    /// </summary>
    public const int DefaultWidth = 1920;

    /// <inheritdoc cref="DefaultWidth"/>
    public const int DefaultHeight = 1080;

    public static readonly IReadOnlyList<KeyValuePair<string, string>> Items =
        new List<KeyValuePair<string, string>>
        {
            new("auto", "自动（由远程桌面客户端决定，通常全屏）"),
            new("1280x720", "1280 × 720"),
            new("1366x768", "1366 × 768"),
            new("1600x900", "1600 × 900"),
            new("1920x1080", "1920 × 1080"),
            new("2560x1440", "2560 × 1440"),
        };

    /// <summary>把 "1280x720" 之类的选择值拆成宽高；"auto" 或无法识别的一律返回 (0,0)。</summary>
    public static (int Width, int Height) Parse(string? value)
    {
        var text = (value ?? string.Empty).Trim().ToLowerInvariant();
        if (text.Length == 0 || text == "auto")
        {
            return (Auto, Auto);
        }

        var parts = text.Split('x', '*', '×');
        if (parts.Length != 2
            || !int.TryParse(parts[0], out var width)
            || !int.TryParse(parts[1], out var height))
        {
            return (Auto, Auto);
        }

        return (Clamp(width, MinWidth, MaxWidth), Clamp(height, MinHeight, MaxHeight));
    }

    /// <summary>把宽高还原成下拉框的选择值。</summary>
    public static string ToValue(int width, int height)
    {
        if (width <= 0 || height <= 0)
        {
            return "auto";
        }

        var key = $"{width}x{height}";
        return Items.Any(i => i.Key == key) ? key : "auto";
    }

    /// <summary>给配置里存的宽高做范围收敛（0 表示自适应，保持不变）。</summary>
    public static int Normalize(int value, int min, int max)
    {
        if (value <= 0)
        {
            return Auto;
        }

        return Math.Clamp(value, min, max);
    }

    /// <summary>
    /// 自适应档位下按画面区域算分辨率。
    /// 传入的是 XAML 的有效像素，要乘上缩放比才是物理像素；结果取偶数并落在合法区间内。
    /// </summary>
    public static (int Width, int Height) FitToArea(double widthDip, double heightDip, double scale)
    {
        var width = (int)Math.Round(widthDip * scale);
        var height = (int)Math.Round(heightDip * scale);

        // RDP 对奇数尺寸容易出黑边，统一向下取偶数
        width -= width % 2;
        height -= height % 2;

        if (width < MinWidth || height < MinHeight)
        {
            return (MinWidth, MinHeight);
        }

        return (Math.Min(width, MaxWidth), Math.Min(height, MaxHeight));
    }

    public static string Label(int width, int height) =>
        width <= 0 || height <= 0 ? "自适应" : $"{width} × {height}";

    private static int Clamp(int value, int min, int max)
    {
        if (value <= 0)
        {
            return Auto;
        }

        return Math.Clamp(value, min, max);
    }
}

/// <summary>
/// 远程桌面目标主机的写法约定与归一化。
/// 既支持本机环回（127.0.0.1，切本机另一个账户），也支持局域网 / 公网 IP 或主机名。
/// </summary>
public static class RdpTargets
{
    /// <summary>默认值：本机环回。</summary>
    public const string DefaultHost = "127.0.0.1";

    /// <summary>Agent 模式的命令行开关。</summary>
    public const string AgentArgument = "--rdp-agent";

    /// <summary>指定指令桥目录的命令行前缀，例如 --bridge:\\192.168.1.20\YomoriBridge</summary>
    public const string BridgeArgumentPrefix = "--bridge:";

    /// <summary>命令行里是否带了 --rdp-agent。</summary>
    public static bool IsAgentInvocation(IReadOnlyList<string> args) =>
        args.Any(a => string.Equals(a, AgentArgument, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 从命令行里取出 --bridge: 后面的桥目录；没带则返回 null（用默认 ProgramData 目录）。
    /// 远程目标场景下，部署 Agent 时会把这个参数写进快捷方式，
    /// 让对方机器的 Agent 与主控端读写同一个共享位置。
    /// </summary>
    public static string? ExtractBridgePath(IReadOnlyList<string> args)
    {
        foreach (var argument in args)
        {
            if (argument.StartsWith(BridgeArgumentPrefix, StringComparison.OrdinalIgnoreCase))
            {
                var value = argument[BridgeArgumentPrefix.Length..].Trim().Trim('"');
                return value.Length == 0 ? null : value;
            }
        }

        return null;
    }

    /// <summary>生成 Agent 快捷方式的命令行参数（带桥目录时一并带上）。</summary>
    public static string BuildAgentArguments(string? bridgePath)
    {
        var text = (bridgePath ?? string.Empty).Trim();
        return text.Length == 0 ? AgentArgument : $"{AgentArgument} {BridgeArgumentPrefix}{text}";
    }

    /// <summary>
    /// 归一化用户填写的目标主机：去空白、去 rdp:// 前缀、去结尾斜杠；
    /// 空值回落到本机环回。保留端口（例如 192.168.1.20:3390）。
    /// </summary>
    public static string Normalize(string? value)
    {
        var text = (value ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            return DefaultHost;
        }

        if (text.StartsWith("rdp://", StringComparison.OrdinalIgnoreCase))
        {
            text = text["rdp://".Length..];
        }

        text = text.Trim().TrimEnd('/', '\\').Trim();
        return text.Length == 0 ? DefaultHost : text;
    }

    /// <summary>目标是否指向本机（决定预检能不能查账户 / 会话 / Agent）。</summary>
    public static bool IsLocal(string? host) =>
        IsLocal(host, Environment.MachineName, EnumerateLocalAddresses());

    /// <summary>
    /// 纯逻辑判定：把 host 与本机名、本机 IP 列表比对。
    /// 抽成可注入参数的形式，方便不联网、不依赖注册表地做单元测试。
    /// </summary>
    public static bool IsLocal(string? host, string machineName, IReadOnlyCollection<string> localAddresses)
    {
        var text = Normalize(host);
        if (text.Length == 0)
        {
            return true;
        }

        // 去掉端口再比对，端口不影响"是不是本机"。
        // 注意裸 IPv6（::1、fe80::1）本身含多个冒号，不能被当成 "IP:端口" 拆开；
        // 只有方括号包裹的写法（[::1]:3390）才带端口。
        var hostPart = text;
        if (text.StartsWith('['))
        {
            var close = text.IndexOf(']');
            hostPart = close > 0 ? text[1..close] : text.TrimStart('[');
        }
        else
        {
            var firstColon = text.IndexOf(':');
            if (firstColon > 0 && firstColon == text.LastIndexOf(':'))
            {
                hostPart = text[..firstColon];
            }
        }

        hostPart = hostPart.Trim('[', ']').Trim();

        if (hostPart.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
            hostPart.Equals(".", StringComparison.Ordinal) ||
            hostPart.Equals("::1", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (hostPart.StartsWith("127.", StringComparison.Ordinal))
        {
            return true;
        }

        if (hostPart.Equals(machineName, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        foreach (var address in localAddresses)
        {
            if (hostPart.Equals(address, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>本机所有 IPv4 / IPv6 地址（含环回），用于判断目标是否本机。</summary>
    public static List<string> EnumerateLocalAddresses()
    {
        var result = new List<string>();
        try
        {
            foreach (var entry in System.Net.Dns.GetHostAddresses(System.Net.Dns.GetHostName()))
            {
                result.Add(entry.ToString());
            }
        }
        catch
        {
            // 拿不到就算了，退化为只认 127.* / localhost / 机器名
        }

        return result;
    }
}

/// <summary>
/// 会话代理的心跳判定。
///
/// 为什么需要它：Agent 和它启动的脚本都活在目标会话里，会话被注销时会被系统连带终止，
/// 于是 status.json 永远停在最后一帧、Phase 一直是 running。
/// 只看"运行中"三个字，根本分不清任务是在跑还是代理已经死了 ——
/// 所以主控端必须靠"多久没更新"来判断存活。
/// 抽成纯函数是为了能脱离 WinUI 做确定性单测。
/// </summary>
public static class RdpHeartbeat
{
    /// <summary>
    /// 多久没收到心跳就判定失联（秒）。
    /// Agent 正常每秒重写一次 status.json，给 20 秒是为了留足磁盘抖动余量、避免误报。
    /// </summary>
    public const int TimeoutSeconds = 20;

    /// <summary>
    /// 从「下发指令」到「代理真正接管」的心跳启动宽限期（秒）。
    ///
    /// 为什么必须有：代理不是立刻就能回传心跳的 ——
    ///   mstsc 打开远程桌面并完成登录 + 公共启动目录拉起代理 + WinUI 3 初始化，
    ///   现场实测整体约 25 秒（21:33:43 下发指令 → 21:34:02 代理才起来）。
    /// 这段时间里 status.json 要么还是上一轮指令写下的残留、要么根本没有本指令的状态，
    /// 若只看时间戳就会把「代理还没接管」误判成「代理已失联」。
    /// 取 90 秒：远大于实测的 25 秒（覆盖慢机器 / 慢网络），又短到不会把真失联长期掩盖。
    /// </summary>
    public const int StartupGraceSeconds = 90;

    /// <summary>
    /// 启动宽限期的下限（秒）。即便用户把连接超时调得很短，宽限期也不低于这个值，
    /// 否则会短于实测的代理接管耗时（约 25 秒），又变成误报。
    /// </summary>
    public const int MinStartupGraceSeconds = 30;

    /// <summary>心跳判定结果。</summary>
    /// <param name="Parsed">UpdatedAt 能否解析（解析不出来说明状态文件损坏或字段缺失）。</param>
    /// <param name="IsStale">是否已失联。</param>
    /// <param name="Text">给界面用的描述文本。</param>
    /// <param name="AgeSeconds">距最后一次心跳的秒数。</param>
    /// <param name="UpdatedAt">最后一次心跳的时间点。</param>
    /// <param name="AwaitingAgent">
    /// 桥里的状态不属于当前指令（归属不匹配 / 还没有本指令的状态），
    /// 处于「等待目标会话接管」的过渡态 —— 此时绝不报失联。
    /// </param>
    /// <param name="InGracePeriod">是否落在「下发指令后的启动宽限期」内。</param>
    public readonly record struct Result(
        bool Parsed, bool IsStale, string Text, long AgeSeconds, DateTimeOffset UpdatedAt,
        bool AwaitingAgent = false, bool InGracePeriod = false);

    /// <summary>
    /// 把连接超时（秒）折算成启动宽限期：夹在 [MinStartupGraceSeconds, StartupGraceSeconds] 之间。
    /// 上限保证真失联不会被长期掩盖，下限保证代理接管耗时被覆盖。
    /// </summary>
    public static int ResolveGraceSeconds(int connectTimeoutSeconds) =>
        Math.Clamp(connectTimeoutSeconds, MinStartupGraceSeconds, StartupGraceSeconds);

    /// <summary>
    /// 依据 status.json 的 UpdatedAt 与 Phase 判定心跳健康度。
    /// now 由调用方注入，便于测试。
    ///
    /// 传入 <paramref name="expectedCommandId"/> / <paramref name="statusCommandId"/> /
    /// <paramref name="issuedAt"/> 后，判定会先做「指令归属」校验：
    ///   归属不匹配（或状态还是空的）→ 视为「等待目标会话接管」，绝不报失联。
    /// 这几个参数都可省略 —— 省掉时退化为「只看时间戳」的纯时间判定，
    /// 方便脱离主控端对时间逻辑做确定性单测。
    /// </summary>
    public static Result Evaluate(
        string? updatedAt,
        string? phase,
        DateTimeOffset now,
        string? expectedCommandId = null,
        string? statusCommandId = null,
        DateTimeOffset? issuedAt = null,
        int graceSeconds = 0)
    {
        // ① 归属校验：桥里的状态不是当前指令写的（残留着上一轮指令的 status.json，
        //    或者本指令的状态还没落地）。这是「等待目标会话接管」的正常过渡态，
        //    无论时间戳多旧都【绝不】报失联，否则就会出现"日志说断开、其实脚本在跑"的假阳性。
        if (!string.IsNullOrEmpty(expectedCommandId)
            && !string.Equals(expectedCommandId, statusCommandId, StringComparison.Ordinal))
        {
            return new Result(false, false, "等待目标会话接管…", 0, default, AwaitingAgent: true);
        }

        if (!DateTimeOffset.TryParse(updatedAt, out var updated))
        {
            return new Result(false, false, "未知", 0, default);
        }

        var age = now - updated;
        if (age < TimeSpan.Zero)
        {
            // 两端时钟有偏差时不报负数
            age = TimeSpan.Zero;
        }

        var seconds = (long)age.TotalSeconds;

        // ② 启动宽限期：从下发指令时刻算起，N 秒内一律不报失联
        //    （此时代理可能还在登录 / 初始化，状态本身就是陈旧的）。
        if (issuedAt is { } issued && graceSeconds > 0)
        {
            var sinceIssued = now - issued;
            if (sinceIssued < TimeSpan.Zero)
            {
                sinceIssued = TimeSpan.Zero;
            }

            // 临界点（恰好等于 N 秒）仍算在宽限期内，与 TimeoutSeconds 的边界口径保持一致
            if (sinceIssued.TotalSeconds <= graceSeconds)
            {
                var text = seconds < 1 ? "等待目标会话接管…" : $"{FormatHelper.FormatSeconds(seconds)}前（启动中）";
                return new Result(true, false, text, seconds, updated, InGracePeriod: true);
            }
        }

        // ③ 还在按秒写，或者刚写完 → 正常
        if (age.TotalSeconds <= TimeoutSeconds)
        {
            return new Result(true, false, seconds < 1 ? "刚刚" : $"{FormatHelper.FormatSeconds(seconds)}前",
                seconds, updated);
        }

        // ④ 已经收尾的终态不需要再报失联
        var active = phase is "running" or "idle";
        if (!active)
        {
            return new Result(true, false, $"{FormatHelper.FormatSeconds(seconds)}前（已结束）", seconds, updated);
        }

        return new Result(true, true, $"已失联 {FormatHelper.FormatSeconds(seconds)}", seconds, updated);
    }
}

/// <summary>
/// RDP 相关设置。注意：目标账户密码不会写入 config.json，
/// 只通过 Windows 凭据管理器（cmdkey）保存，配置里仅记录"是否已保存"。
/// </summary>
public sealed class RdpConfig : ObservableObject, ICloneable
{
    private bool _enabled = true;
    private string _targetHost = RdpTargets.DefaultHost;
    private string _bridgePath = string.Empty;
    private string _targetUser = string.Empty;
    private string _sessionFinish = SessionFinishModes.Keep;
    private bool _notifyOnTaskDone = true;
    private bool _notifyOnChannelDone = true;
    private bool _notifyOnAllDone = true;
    private bool _notifyLocalTaskDone = true;
    private int _connectTimeoutSeconds = 120;
    private bool _credentialSaved;
    private bool _embedRemoteDesktop;
    private string _clientMode = ClientModes.Embedded;
    private bool _audioEnabled = true;
    private bool _gfxEnabled = true;
    private string _renderBackend = RenderBackends.Auto;
    private int _desktopWidth = RdpResolutions.DefaultWidth;
    private int _desktopHeight = RdpResolutions.DefaultHeight;
    private List<RdpChannel> _channels = new();

    /// <summary>
    /// 是否在目标账户的会话里执行任务。
    ///
    /// <para><b>2026-10-26 起恒为 <c>true</c></b>：通道管理页上的总开关去掉了，有通道就按通道下发，
    /// 没配通道的任务照旧跑本地。因此 <see cref="Sanitize"/> 会把任何来源的 <c>false</c>（旧 config.json
    /// 里存过这个键）一律改写回 <c>true</c> —— 否则升级用户的配置会永远把自己钉在「全部本地执行」，
    /// 而界面上再没有回头路。</para>
    /// <para>字段与 JSON 键保留有两个原因：旧配置里的 <c>enabled</c> 键不会掉进 ExtensionData；
    /// 下游的 <c>RdpChannelPlanner</c> / <c>TaskScopePlanner</c> / <c>StatsSourcePlanner</c>
    /// 仍把它当入参传（纯函数保留这条分支，冒烟可继续验证"总开关关掉时全走本地"）。</para>
    /// </summary>
    [JsonPropertyName("enabled")]
    public bool Enabled
    {
        get => _enabled;
        set => SetProperty(ref _enabled, value);
    }

    /// <summary>
    /// 远程桌面目标主机：IP、主机名，可带端口（例如 192.168.1.20:3390）。
    /// 默认 127.0.0.1（本机环回，用来切本机另一个账户）。
    /// </summary>
    [JsonPropertyName("target_host")]
    public string TargetHost
    {
        get => _targetHost;
        set => SetProperty(ref _targetHost, RdpTargets.Normalize(value));
    }

    /// <summary>
    /// 指令桥目录。留空表示用本机 ProgramData（只适用于目标是本机另一个账户的情况）。
    /// 目标为远程主机时必须填一个两端都能读写的位置，例如 \\192.168.1.20\YomoriBridge 或 Z:\YomoriBridge。
    /// </summary>
    [JsonPropertyName("bridge_path")]
    public string BridgePath
    {
        get => _bridgePath;
        set => SetProperty(ref _bridgePath, (value ?? string.Empty).Trim());
    }

    /// <summary>目标 Windows 账户名，例如 "GamePC\Player2" 或 "Player2"。</summary>
    [JsonPropertyName("target_user")]
    public string TargetUser
    {
        get => _targetUser;
        set => SetProperty(ref _targetUser, value);
    }

    /// <summary>凭据是否已存入 Windows 凭据管理器。</summary>
    [JsonPropertyName("credential_saved")]
    public bool CredentialSaved
    {
        get => _credentialSaved;
        set => SetProperty(ref _credentialSaved, value);
    }

    /// <summary>
    /// <b>仅用于导出</b>：旧版单通道布局（顶层 <c>target_user</c>，无 <c>channels</c>）的明文密码。
    /// 语义与 <see cref="RdpChannel.ExportPassword"/> 一致，见那里的说明。
    /// </summary>
    [JsonPropertyName("password")]
    public string ExportPassword { get; set; } = string.Empty;

    [JsonPropertyName("session_finish")]
    public string SessionFinish
    {
        get => _sessionFinish;
        set => SetProperty(ref _sessionFinish, SessionFinishModes.Normalize(value));
    }

    /// <summary>每完成一个任务都弹系统通知。</summary>
    [JsonPropertyName("notify_on_task_done")]
    public bool NotifyOnTaskDone
    {
        get => _notifyOnTaskDone;
        set => SetProperty(ref _notifyOnTaskDone, value);
    }

    /// <summary>某条通道（一整条会话通道）跑完后，在主控端弹一条该通道的收尾通知。</summary>
    [JsonPropertyName("notify_on_channel_done")]
    public bool NotifyOnChannelDone
    {
        get => _notifyOnChannelDone;
        set => SetProperty(ref _notifyOnChannelDone, value);
    }

    /// <summary>全部通道都跑完后，在主控端弹一条整轮汇总通知。</summary>
    [JsonPropertyName("notify_on_all_done")]
    public bool NotifyOnAllDone
    {
        get => _notifyOnAllDone;
        set => SetProperty(ref _notifyOnAllDone, value);
    }

    /// <summary>本地（非 RDP）执行时，每完成一个任务也弹通知。</summary>
    [JsonPropertyName("notify_local_task_done")]
    public bool NotifyLocalTaskDone
    {
        get => _notifyLocalTaskDone;
        set => SetProperty(ref _notifyLocalTaskDone, value);
    }

    /// <summary>等待目标会话建立的超时时间（秒）。</summary>
    [JsonPropertyName("connect_timeout_seconds")]
    public int ConnectTimeoutSeconds
    {
        get => _connectTimeoutSeconds;
        set => SetProperty(ref _connectTimeoutSeconds, Math.Clamp(value, 15, 600));
    }

    /// <summary>
    /// 已废弃：保留键位只是为了兼容旧配置文件。
    /// 现在统一使用系统自带的远程桌面（独立 mstsc 窗口），不再往Yomori窗口里嵌画面。
    /// </summary>
    [JsonPropertyName("embed_remote_desktop")]
    public bool EmbedRemoteDesktop
    {
        get => _embedRemoteDesktop;
        set => SetProperty(ref _embedRemoteDesktop, value);
    }

    /// <summary>连接方式：内嵌 FreeRDP 画面（默认）或 mstsc 独立窗口（client_mode，计划书 §5.6）。</summary>
    [JsonPropertyName("client_mode")]
    public string ClientMode
    {
        get => _clientMode;
        set => SetProperty(ref _clientMode, ClientModes.Normalize(value));
    }

    /// <summary>内嵌连接时是否在本机播放远端声音（audio_enabled，仅 embedded 生效）。</summary>
    [JsonPropertyName("audio_enabled")]
    public bool AudioEnabled
    {
        get => _audioEnabled;
        set => SetProperty(ref _audioEnabled, value);
    }

    /// <summary>内嵌 GFX 全帧管线（gfx_enabled；撕裂根治，自动协商 progressive）。默认开启。</summary>
    [JsonPropertyName("gfx_enabled")]
    public bool GfxEnabled
    {
        get => _gfxEnabled;
        set => SetProperty(ref _gfxEnabled, value);
    }

    /// <summary>
    /// 内嵌画面渲染后端：auto = D3D11（失败自动回退软渲染）；software = 强制软渲染。
    ///
    /// 存在的原因：D3D 路线在某些显卡 / 虚拟显示驱动组合下会把 UI 线程与 GPU 互锁，
    /// 表现为整窗假死。给用户一个不用等新版本就能自救的开关。
    /// 命令行 <c>--embed-nod3d</c> 仍然有效，且优先级更高（等价于 software）。
    /// </summary>
    [JsonPropertyName("render_backend")]
    public string RenderBackend
    {
        get => _renderBackend;
        set => SetProperty(ref _renderBackend, RenderBackends.Normalize(value));
    }

    /// <summary>远程桌面宽度，0 表示自适应（跟随画面区域）。</summary>
    [JsonPropertyName("desktop_width")]
    public int DesktopWidth
    {
        get => _desktopWidth;
        set => SetProperty(ref _desktopWidth, RdpResolutions.Normalize(value, RdpResolutions.MinWidth, RdpResolutions.MaxWidth));
    }

    /// <summary>远程桌面高度，0 表示自适应（跟随画面区域）。</summary>
    [JsonPropertyName("desktop_height")]
    public int DesktopHeight
    {
        get => _desktopHeight;
        set => SetProperty(ref _desktopHeight, RdpResolutions.Normalize(value, RdpResolutions.MinHeight, RdpResolutions.MaxHeight));
    }

    /// <summary>
    /// 会话通道列表：每个通道 = 主机 + 账户 + 桥目录 + 收尾策略，任务用 channel_id 指过来。
    /// 上面的 target_* 一组是「旧配置迁移源 + 新建通道时的默认值」，
    /// 单独跑一路的时代过去后它们不再是执行依据（Planner 只看通道）。
    /// </summary>
    [JsonPropertyName("channels")]
    public List<RdpChannel> Channels
    {
        get => _channels;
        set => SetProperty(ref _channels, value ?? new List<RdpChannel>());
    }

    /// <summary>
    /// 早期版本把这一节写成了 PascalCase（"TargetUser"、"SessionFinish"…）。
    /// 与项目其余配置的 snake_case 风格不一致，这里兜住旧键做一次性迁移。
    /// </summary>
    [JsonExtensionData]
    public IDictionary<string, JsonElement>? ExtensionData { get; set; }

    public void Sanitize()
    {
        MigrateLegacyKeys();

        // 总开关已从界面移除，行为恒为开启（见 Enabled 的说明）。旧配置里存过 false 的也要拨回来，
        // 所以放在 MigrateLegacyKeys 之后 —— 那一步可能刚把旧键的 false 读进来。
        Enabled = true;

        TargetHost = RdpTargets.Normalize(TargetHost);
        BridgePath = (BridgePath ?? string.Empty).Trim();
        TargetUser = (TargetUser ?? string.Empty).Trim();
        SessionFinish = SessionFinishModes.Normalize(SessionFinish);
        ClientMode = ClientModes.Normalize(ClientMode);
        RenderBackend = RenderBackends.Normalize(RenderBackend);
        ConnectTimeoutSeconds = Math.Clamp(ConnectTimeoutSeconds, 15, 600);
        DesktopWidth = RdpResolutions.Normalize(DesktopWidth, RdpResolutions.MinWidth, RdpResolutions.MaxWidth);
        DesktopHeight = RdpResolutions.Normalize(DesktopHeight, RdpResolutions.MinHeight, RdpResolutions.MaxHeight);

        Channels ??= new List<RdpChannel>();
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < Channels.Count; i++)
        {
            var channel = Channels[i] ??= new RdpChannel();
            channel.Sanitize(i + 1);

            // id 撞车（手改配置 / 复制粘贴整段）→ 换一个，否则任务指向哪个通道就有歧义了
            while (!seenIds.Add(channel.Id))
            {
                channel.Id = RdpChannel.NewId();
            }
        }
    }

    /// <summary>
    /// 把早期 PascalCase 写法（"TargetUser"、"SessionFinish"…）的键搬过来。
    /// 只有反序列化时没匹配上的键才会进 ExtensionData，所以遇到就覆盖写回。
    /// 迁移完清空，下次保存时就会以 snake_case 落盘。
    /// </summary>
    private void MigrateLegacyKeys()
    {
        if (ExtensionData is null || ExtensionData.Count == 0)
        {
            return;
        }

        foreach (var (key, element) in ExtensionData)
        {
            // 去掉下划线与大小写差异后再比对，"TargetUser" / "target_user" 都能命中
            switch (key.Replace("_", string.Empty).ToLowerInvariant())
            {
                case "enabled":
                    Enabled = element.ValueKind == JsonValueKind.True;
                    break;
                case "targethost":
                    if (element.ValueKind == JsonValueKind.String)
                    {
                        TargetHost = element.GetString() ?? string.Empty;
                    }

                    break;
                case "bridgepath":
                    if (element.ValueKind == JsonValueKind.String)
                    {
                        BridgePath = element.GetString() ?? string.Empty;
                    }

                    break;
                case "targetuser":
                    if (element.ValueKind == JsonValueKind.String)
                    {
                        TargetUser = element.GetString() ?? string.Empty;
                    }

                    break;
                case "credentialsaved":
                    CredentialSaved = element.ValueKind == JsonValueKind.True;
                    break;
                case "sessionfinish":
                    if (element.ValueKind == JsonValueKind.String)
                    {
                        SessionFinish = element.GetString() ?? SessionFinishModes.Keep;
                    }

                    break;
                case "notifyontaskdone":
                    NotifyOnTaskDone = element.ValueKind == JsonValueKind.True;
                    break;
                case "notifyonchanneldone":
                    NotifyOnChannelDone = element.ValueKind == JsonValueKind.True;
                    break;
                case "notifyonalldone":
                    NotifyOnAllDone = element.ValueKind == JsonValueKind.True;
                    break;
                case "notifylocaltaskdone":
                    NotifyLocalTaskDone = element.ValueKind == JsonValueKind.True;
                    break;
                case "connecttimeoutseconds":
                    if (element.ValueKind == JsonValueKind.Number)
                    {
                        ConnectTimeoutSeconds = element.GetInt32();
                    }

                    break;
                case "embedremotedesktop":
                    EmbedRemoteDesktop = element.ValueKind != JsonValueKind.False;
                    break;
                case "desktopwidth":
                    if (element.ValueKind == JsonValueKind.Number)
                    {
                        DesktopWidth = element.GetInt32();
                    }

                    break;
                case "desktopheight":
                    if (element.ValueKind == JsonValueKind.Number)
                    {
                        DesktopHeight = element.GetInt32();
                    }

                    break;
            }
        }

        ExtensionData = null;
    }

    public object Clone()
    {
        var copy = (RdpConfig)MemberwiseClone();
        // 通道是可变对象，浅拷贝会让两份配置共享同一条通道 —— 必须逐个复制
        copy._channels = _channels.Select(c => c.Clone()).ToList();
        // 同 RdpChannel.Clone：明文密码只属于导出产物，运行期的副本一律清空，
        // 免得被 SaveConfig 写进 config.json
        copy.ExportPassword = string.Empty;
        return copy;
    }
}

/// <summary>主控端下发给目标会话 Agent 的执行指令。</summary>
public sealed class RdpCommand
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];
    public string CreatedAt { get; set; } = DateTimeOffset.Now.ToString("o");
    public string ControllerUser { get; set; } = Environment.UserName;

    // ---------------- 归属信息（计划书 P1：命令必须携带目标身份） ----------------
    //
    // 为什么要写进指令文件而不是靠"目录/用户名"隐式约定：
    //   同账户可能出现多个会话，多个代理会抢读同一个 command.json。
    //   代理侧只有拿到「这条指令是发给哪个 SID、哪个 SessionId」才能判断自己该不该执行，
    //   否则"同名账户"和"同账户多会话"两种串台都拦不住。

    /// <summary>目标账户 SID（主控端算出来的那份）。空串 = 主控端拿不到身份，代理应按旧行为放行。</summary>
    public string TargetSid { get; set; } = string.Empty;

    /// <summary>目标会话 id。0 = 不限（老版本主控端不写这个字段）。</summary>
    public uint TargetSessionId { get; set; }

    /// <summary>指令协议版本，见 <see cref="RdpCommandProtocol"/>。</summary>
    public int ProtocolVersion { get; set; } = RdpCommandProtocol.Current;

    public List<TaskConfig> Tasks { get; set; } = new();

    public bool ShutdownAfterDone { get; set; }
    public int ShutdownDelaySeconds { get; set; } = 60;
    public bool EnableTimeoutScreenshot { get; set; } = true;

    /// <summary>任务全部结束后的会话处理方式，见 <see cref="SessionFinishModes"/>。</summary>
    public string SessionFinish { get; set; } = SessionFinishModes.Keep;

    /// <summary>
    /// 已废弃：通知一律在主控端发，目标会话里的代理不再弹任何 Toast。
    ///
    /// 这两个键保留下来只为<b>让已经部署在目标账户里的旧代理闭嘴</b> —— 旧版代理会读它们
    /// 并在本会话弹通知，而那种通知在主控端桌面上根本看不见，纯属噪音。主控端现在一律发
    /// <c>false</c>，于是旧代理装了也不再弹；新版代理里压根没有发通知的代码。
    /// </summary>
    public bool NotifyOnTaskDone { get; set; }

    /// <inheritdoc cref="NotifyOnTaskDone"/>
    public bool NotifyOnAllDone { get; set; }
}

/// <summary>
/// 主控端写进 stop.json 的停止请求（Agent 每秒检查一次）。
///
/// 为什么需要它：主控端点「停止执行」时，任务其实跑在目标会话的代理进程里，
/// 两个会话之间没有别的通信手段 —— 只能靠桥文件把"停"这个意图传过去。
/// 带 CommandId 是为了归属校验：桥里可能残留上一轮的请求，不校验会把新指令一起停掉。
/// </summary>
public sealed class RdpStopRequest
{
    public string CommandId { get; set; } = string.Empty;

    /// <summary>true = 紧急停止（直接杀进程），false = 常规停止（收尾后退出）。</summary>
    public bool Emergency { get; set; }

    public string RequestedAt { get; set; } = DateTimeOffset.Now.ToString("o");
}

/// <summary>主控与代理共同使用的实时日志协议版本。</summary>
public static class RdpLiveLogProtocol
{
    public const int Current = 1;
}

/// <summary>
/// 指令协议的版本号。
///
/// <para>2 = 起加入了 <c>TargetSid</c> / <c>TargetSessionId</c> 归属信息，以及代理侧的原子认领。
/// 旧代理读到新指令不会看这些字段（JSON 反序列化忽略未知键），行为与以前一致 ——
/// 这正是安全的降级方向：<b>旧代理照常跑，新代理额外校验</b>，不会因为升级顺序把人卡死。</para>
/// </summary>
public static class RdpCommandProtocol
{
    public const int Legacy = 1;
    public const int Current = 2;
}

/// <summary>独立 NDJSON 流中的一条外部脚本日志。</summary>
public sealed class RdpLiveLog
{
    public string CommandId { get; set; } = string.Empty;
    public long Seq { get; set; }
    public string Time { get; set; } = DateTime.Now.ToString("HH:mm:ss");
    public string TaskName { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string Level { get; set; } = "info";
    public bool IsAbnormal { get; set; }
}

/// <summary>Agent 回写给主控端的一条事件（序号单调递增，主控端按序号去重）。</summary>
public sealed class RdpTaskEvent
{
    public long Seq { get; set; }
    public string Kind { get; set; } = "log";
    public string Time { get; set; } = DateTime.Now.ToString("HH:mm:ss");
    public string TaskName { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public int Index { get; set; }
    public int Total { get; set; }
    public int ElapsedSeconds { get; set; }
    public bool IsAbnormal { get; set; }

    [JsonIgnore]
    public string Headline => Kind switch
    {
        RdpEventKinds.TaskDone => $"已完成：{TaskName}",
        RdpEventKinds.TaskStarted => $"开始：{TaskName}",
        RdpEventKinds.Finished => "全部任务结束",
        RdpEventKinds.ExternalLog => $"日志：{Message}",
        _ => Message,
    };
}

public static class RdpEventKinds
{
    public const string TaskStarted = "task_started";
    public const string TaskDone = "task_done";
    public const string Finished = "finished";

    /// <summary>内部结构化日志（保留给调度器自己用）。</summary>
    public const string Log = "log";

    /// <summary>
    /// 外部脚本实时日志的一行（BetterGI / March7thAssistant / MaaEnd 等）。
    /// 与 <see cref="Log"/> 分开：Log 会被停止回执那套文本匹配消费，
    /// 混在一起会污染既有判定。
    /// </summary>
    public const string ExternalLog = "ext_log";
}

/// <summary>Agent 的运行状态快照。</summary>
public sealed class RdpStatus
{
    public string CommandId { get; set; } = string.Empty;
    public string AgentUser { get; set; } = string.Empty;
    public string AgentVersion { get; set; } = "v2.0 WinUI";

    // ---------------- 归属信息（计划书 P1：心跳必须带实例与会话条件） ----------------
    //
    // 同账户多代理时，几个代理会往同一份 status.json 里写。
    // 没有这三项的话，"A 会话在跑任务"可以被"B 会话的 idle 心跳"直接覆写成空闲，
    // 主控端就会误判任务已结束/未开始。写入方必须带上自己是谁。

    /// <summary>代理实例标识（进程启动时生成，进程内不变）。</summary>
    public string AgentInstanceId { get; set; } = string.Empty;

    /// <summary>代理所在的 WTS 会话 id。</summary>
    public uint AgentSessionId { get; set; }

    /// <summary>代理所在账户的 SID。</summary>
    public string AgentSid { get; set; } = string.Empty;

    /// <summary>idle / running / done / stopped / error / rejected</summary>
    public string Phase { get; set; } = "idle";

    public string StatusText { get; set; } = string.Empty;
    public string CurrentTask { get; set; } = string.Empty;
    public int Progress { get; set; }
    public int Total { get; set; }
    public int ElapsedSeconds { get; set; }
    public string UpdatedAt { get; set; } = DateTimeOffset.Now.ToString("o");
    public string Machine { get; set; } = Environment.MachineName;
    public List<RdpTaskEvent> Events { get; set; } = new();
    public int LiveLogProtocol { get; set; }
    public string LiveLogError { get; set; } = string.Empty;

    /// <summary>
    /// 代理是否<b>正在执行任务</b>。
    ///
    /// <para>⚠️ 2026-10-26 收紧：以前是 <c>Phase is "running" or "idle"</c> ——
    /// 把 idle 也算成"在跑"。这让「空闲心跳是否会覆盖别人的 running」在语义上无法判定
    /// （两边都叫 IsRunning），也让调用方没法用它判断"能不能接管"。
    /// 现在只认 running；想要「代理在线」请判断 <see cref="Phase"/> 是否为 idle/running。</para>
    /// </summary>
    [JsonIgnore]
    public bool IsRunning => string.Equals(Phase, "running", StringComparison.OrdinalIgnoreCase);
}
