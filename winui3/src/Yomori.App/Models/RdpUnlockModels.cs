// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.Text;

namespace Yomori.Models;

/// <summary>
/// 单项检测的呈现等级：只决定画什么颜色、写哪个词，不参与判定。
/// </summary>
/// <remarks>
/// 状态与等级是两套东西。状态是事实（Wrapper 装了没有、服务在不在跑），等级是呈现（绿/黄/红/灰）。
/// 之所以不拿状态直接当颜色，是因为状态还要表达「读不出来」这种与好坏无关的结果 ——
/// 计划书 7.3 要求读取失败一律记 Unknown，既不能当「不支持」，也不能当「配置错误」。
///
/// 界面必须文字与颜色**同时**表达（计划书 3.1）：只给颜色时，色觉障碍用户拿不到任何信息。
/// </remarks>
public enum RdpProbeLevel
{
    Unknown = 0,
    Pass = 1,
    Warn = 2,
    Fail = 3,
}

/// <summary>Wrapper 项的四种结论。</summary>
public enum RdpWrapperState
{
    /// <summary>读不到 ServiceDll / ImagePath，或读取过程报错。</summary>
    Unknown = 0,

    /// <summary>ServiceDll 仍指向原版 termsrv.dll：没有装解锁组件。</summary>
    NotInstalled = 1,

    /// <summary>ServiceDll 指向 rdpwrap.dll：组件已接管。</summary>
    Installed = 2,

    /// <summary>ServiceDll 被改成了既不是原版也不是 rdpwrap.dll 的东西 —— 陌生接管，不自动覆盖。</summary>
    ThirdParty = 3,
}

/// <summary>TermService 的运行状态。</summary>
public enum RdpServiceState
{
    Unknown = 0,
    Running = 1,
    Stopped = 2,
    Pending = 3,
    Paused = 4,
}

/// <summary>RDP 监听器（WinStation 里的 RDP-Tcp）是否存在。</summary>
public enum RdpListenerState
{
    Unknown = 0,
    Listening = 1,
    Absent = 2,
}

/// <summary>本机 INI 是否覆盖当前 termsrv.dll 版本。</summary>
public enum RdpSupportState
{
    /// <summary>没读到 INI、没读到版本号，或解析过程报错。</summary>
    Unknown = 0,

    /// <summary>版本作为真正的 section 存在，且该 section 有补丁键。</summary>
    FullySupported = 1,

    /// <summary>版本没命中：当前 INI 里没有这个构建号。</summary>
    NotSupported = 2,

    /// <summary>版本号只出现在注释或说明文字里（上游子串匹配判真、严格 section 匹配判假）。</summary>
    Ambiguous = 3,
}

/// <summary>四项之外的补充证据：从 ServiceDll 路径推出 Wrapper 的安装位置。</summary>
public readonly record struct RdpServiceDllClassification(
    RdpWrapperState State,
    string NormalizedPath,
    string FileName);

/// <summary>
/// 本机 rdpwrap.ini 的解析结果。
/// </summary>
/// <remarks>
/// 同时保留两种判定：
/// <list type="bullet">
/// <item><see cref="LabelMatch"/>：上游 CnC 的做法 —— 在整份 INI 文本里找版本号子串。注释里提到也算命中。</item>
/// <item><see cref="StrictSectionMatch"/>：版本号是真正的 <c>[x.x.x.x]</c> section，且该 section 里有补丁键。</item>
/// </list>
/// 两者不一致时按 <see cref="RdpSupportState.Ambiguous"/> 呈现差异，不宣称逻辑与上游完全等价（计划书 7.2）。
/// </remarks>
public sealed class RdpIniAnalysis
{
    public bool Found { get; init; }
    public string Updated { get; init; } = "";
    public bool LabelMatch { get; init; }
    public bool StrictSectionMatch { get; init; }
    public int SectionCount { get; init; }
    public RdpSupportState State { get; init; } = RdpSupportState.Unknown;
    public string Detail { get; init; } = "";
}

/// <summary>
/// 一行检测结果：标题、等级、实测值、期望值、错误码、建议。
/// </summary>
/// <remarks>
/// 计划书 3.4 要求异常时「列出具体项目、期望值、实测值、错误码和建议」，
/// 所以这四项是结构字段而不是拼好的一句话 —— 拼句子的话，诊断详情和界面上的短标签
/// 就只能二选一，而两者都需要。
/// </remarks>
public sealed class RdpProbeItem
{
    public string Key { get; init; } = "";
    public string Title { get; init; } = "";
    public RdpProbeLevel Level { get; init; }
    public string StateText { get; init; } = "";
    public string ValueText { get; init; } = "";
    public string ExpectedText { get; init; } = "";
    public string ErrorCode { get; init; } = "";
    public string Advice { get; init; } = "";
}

/// <summary>
/// 一次只读检测的完整快照。字段全部来自读操作，不含任何写操作的结果。
/// </summary>
/// <remarks>
/// 之所以做成不可变快照而不是「检测器对象 + 一堆属性」，是为了让界面与诊断详情
/// 拿的是同一份数据：检测跑在后台线程，界面在 UI 线程渲染，中间不能有第三次读取 ——
/// 否则界面显示的和用户复制出来的诊断会不一致。
/// </remarks>
public sealed class RdpUnlockSnapshot
{
    public DateTimeOffset CapturedAt { get; init; } = DateTimeOffset.Now;
    public bool IsElevated { get; init; }

    // ---- Wrapper ----
    public RdpWrapperState WrapperState { get; init; } = RdpWrapperState.Unknown;
    public string ImagePath { get; init; } = "";
    public string ServiceDllPath { get; init; } = "";
    public string InstallDirectory { get; init; } = "";
    public string IniPath { get; init; } = "";

    // ---- Service ----
    public RdpServiceState ServiceState { get; init; } = RdpServiceState.Unknown;
    public uint ServiceCurrentStateRaw { get; init; }
    public uint ServiceWin32ExitCode { get; init; }
    public uint ServiceProcessId { get; init; }

    // ---- Listener ----
    public RdpListenerState ListenerState { get; init; } = RdpListenerState.Unknown;
    public int ListenerSessionId { get; init; }

    /// <summary>RDP-Tcp 会话的 WINSTATIONSTATECLASS 原值（6 = State_Listen）。</summary>
    public int ListenerSessionState { get; init; }
    public int SessionCount { get; init; }
    public string SessionSummary { get; init; } = "";
    public bool Tcp3389Listening { get; init; }
    public string TcpDetail { get; init; } = "";

    // ---- Support ----
    public RdpSupportState SupportState { get; init; } = RdpSupportState.Unknown;
    public string TermsrvVersion { get; init; } = "";
    public string TermsrvVersionRaw { get; init; } = "";
    public string IniUpdated { get; init; } = "";
    public bool IniLabelMatch { get; init; }
    public bool IniStrictSectionMatch { get; init; }

    // ---- 汇总 ----
    public IReadOnlyList<RdpProbeItem> Items { get; init; } = Array.Empty<RdpProbeItem>();
    public IReadOnlyList<string> Errors { get; init; } = Array.Empty<string>();

    /// <summary>四项是否全部达成「通过」—— 注意这不等于账户一定能远程登录（计划书 1.3）。</summary>
    public bool AllPass =>
        WrapperState == RdpWrapperState.Installed
        && ServiceState == RdpServiceState.Running
        && ListenerState == RdpListenerState.Listening
        && SupportState == RdpSupportState.FullySupported;

    public int PassCount
    {
        get
        {
            var count = 0;
            foreach (var item in Items)
            {
                if (item.Level == RdpProbeLevel.Pass)
                {
                    count++;
                }
            }

            return count;
        }
    }

    /// <summary>四项里是否有任何一项没定论（Unknown）—— 用来区分「确定不行」和「读不出来」。</summary>
    public bool HasUnknown
    {
        get
        {
            foreach (var item in Items)
            {
                if (item.Level == RdpProbeLevel.Unknown)
                {
                    return true;
                }
            }

            return false;
        }
    }
}

/// <summary>状态与等级的中文文案。界面与诊断详情共用，避免两处各写一套。</summary>
public static class RdpProbeText
{
    public static string LevelLabel(RdpProbeLevel level) => level switch
    {
        RdpProbeLevel.Pass => "通过",
        RdpProbeLevel.Warn => "注意",
        RdpProbeLevel.Fail => "异常",
        _ => "未知",
    };

    public static string WrapperLabel(RdpWrapperState state) => state switch
    {
        RdpWrapperState.Installed => "已安装",
        RdpWrapperState.NotInstalled => "未安装",
        RdpWrapperState.ThirdParty => "第三方接管",
        _ => "未知",
    };

    public static string ServiceLabel(RdpServiceState state) => state switch
    {
        RdpServiceState.Running => "正在运行",
        RdpServiceState.Stopped => "已停止",
        RdpServiceState.Pending => "正在切换",
        RdpServiceState.Paused => "已暂停",
        _ => "未知",
    };

    public static string ListenerLabel(RdpListenerState state) => state switch
    {
        RdpListenerState.Listening => "正在监听",
        RdpListenerState.Absent => "无监听",
        _ => "未知",
    };

    public static string SupportLabel(RdpSupportState state) => state switch
    {
        RdpSupportState.FullySupported => "已覆盖当前版本",
        RdpSupportState.NotSupported => "未覆盖当前版本",
        RdpSupportState.Ambiguous => "存疑",
        _ => "未知",
    };

    /// <summary>颜色键：界面把它映射成画刷，冒烟断言四个等级都有值（别出现空白色块）。</summary>
    public static string LevelColorKey(RdpProbeLevel level) => level switch
    {
        RdpProbeLevel.Pass => "pass",
        RdpProbeLevel.Warn => "warn",
        RdpProbeLevel.Fail => "fail",
        _ => "unknown",
    };

    /// <summary>
    /// WinStation 会话状态（<c>WINSTATIONSTATECLASS</c>）的中文/英文对照。
    /// 计划书只要求看「有没有 RDP-Tcp」，但把状态原样说出来，排查时能省一轮追问。
    /// </summary>
    public static string SessionStateLabel(int state) => state switch
    {
        0 => "Active",
        1 => "Connected",
        2 => "ConnectQuery",
        3 => "Shadow",
        4 => "Disconnected",
        5 => "Idle",
        6 => "Listen",
        7 => "Reset",
        8 => "Down",
        9 => "Init",
        _ => $"状态 {state}",
    };
}

/// <summary>
/// 把快照摊平成可复制、可读的诊断文本。
/// </summary>
/// <remarks>
/// 计划书 7.3 要求「所有结果均在 Yomori 显示」并保留复制脱敏诊断的能力，
/// 所以这里只输出检测证据本身：不含密码、令牌、通道凭据，也不含与本次无关的个人路径。
/// </remarks>
public static class RdpUnlockDiagnostics
{
    public static string FormatSummaryLine(RdpUnlockSnapshot snapshot)
    {
        var total = snapshot.Items.Count;
        if (total == 0)
        {
            return "尚未检测";
        }

        if (snapshot.AllPass)
        {
            return $"四项全部通过（{snapshot.CapturedAt:HH:mm:ss}）";
        }

        return snapshot.HasUnknown
            ? $"{total} 项中 {snapshot.PassCount} 项通过，含无法读取的项（{snapshot.CapturedAt:HH:mm:ss}）"
            : $"{total} 项中 {snapshot.PassCount} 项通过（{snapshot.CapturedAt:HH:mm:ss}）";
    }

    public static string FormatDetail(RdpUnlockSnapshot snapshot)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"RDP 解锁诊断 · {snapshot.CapturedAt:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"权限：{(snapshot.IsElevated ? "管理员" : "非管理员（部分键可能读不到）")}");
        sb.AppendLine($"总体：{FormatSummaryLine(snapshot)}");
        sb.AppendLine();

        foreach (var item in snapshot.Items)
        {
            sb.AppendLine($"■ {item.Title} —— {item.StateText}（{RdpProbeText.LevelLabel(item.Level)}）");
            sb.AppendLine($"   实测：{Blank(item.ValueText)}");
            sb.AppendLine($"   期望：{Blank(item.ExpectedText)}");
            if (item.ErrorCode.Length > 0)
            {
                sb.AppendLine($"   错误码：{item.ErrorCode}");
            }

            if (item.Advice.Length > 0)
            {
                sb.AppendLine($"   建议：{item.Advice}");
            }

            sb.AppendLine();
        }

        sb.AppendLine("--- 补充信息 ---");
        sb.AppendLine($"ImagePath：{Blank(snapshot.ImagePath)}");
        sb.AppendLine($"ServiceDll：{Blank(snapshot.ServiceDllPath)}");
        sb.AppendLine($"安装目录：{Blank(snapshot.InstallDirectory)}");
        sb.AppendLine($"INI 路径：{Blank(snapshot.IniPath)}");
        sb.AppendLine($"INI Updated：{Blank(snapshot.IniUpdated)}");
        sb.AppendLine($"termsrv.dll 版本：{Blank(snapshot.TermsrvVersion)}");
        if (snapshot.TermsrvVersionRaw.Length > 0)
        {
            sb.AppendLine($"版本原始文本：{snapshot.TermsrvVersionRaw}");
        }

        sb.AppendLine($"INI 标签匹配：{YesNo(snapshot.IniLabelMatch)}　严格 section 匹配：{YesNo(snapshot.IniStrictSectionMatch)}");
        sb.AppendLine($"服务状态原始值：{snapshot.ServiceCurrentStateRaw}　退出码：0x{snapshot.ServiceWin32ExitCode:X8}　PID：{snapshot.ServiceProcessId}");
        sb.AppendLine($"会话枚举：共 {snapshot.SessionCount} 个{FormatSessions(snapshot.SessionSummary)}");
        sb.AppendLine(snapshot.ListenerSessionId > 0
            ? $"RDP-Tcp 会话 ID：{snapshot.ListenerSessionId}（{RdpProbeText.SessionStateLabel(snapshot.ListenerSessionState)}）"
            : "RDP-Tcp 会话 ID：—");
        sb.AppendLine($"TCP 3389：{(snapshot.Tcp3389Listening ? "有监听" : "无监听")}{FormatSuffix(snapshot.TcpDetail)}");

        if (snapshot.Errors.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("--- 读取过程中的错误 ---");
            foreach (var error in snapshot.Errors)
            {
                sb.AppendLine(error);
            }
        }

        sb.AppendLine();
        sb.AppendLine("说明：以上只说明本机解锁组件的状态，不代表账户一定有远程登录权限，也不代表许可要求已经满足。");
        return sb.ToString();
    }

    /// <summary>写日志用的一行摘要。</summary>
    public static string FormatLogLine(RdpUnlockSnapshot snapshot) =>
        $"[RDP 解锁] 检测完成：Wrapper={RdpProbeText.WrapperLabel(snapshot.WrapperState)}，"
        + $"Service={RdpProbeText.ServiceLabel(snapshot.ServiceState)}，"
        + $"Listener={RdpProbeText.ListenerLabel(snapshot.ListenerState)}，"
        + $"Support={RdpProbeText.SupportLabel(snapshot.SupportState)}"
        + $"（{snapshot.PassCount}/{snapshot.Items.Count} 项通过）";

    private static string Blank(string text) => string.IsNullOrWhiteSpace(text) ? "—" : text;

    private static string YesNo(bool value) => value ? "是" : "否";

    private static string FormatSuffix(string text) => string.IsNullOrWhiteSpace(text) ? "" : $"（{text}）";

    private static string FormatSessions(string summary) => string.IsNullOrWhiteSpace(summary) ? "" : $"：{summary}";
}
