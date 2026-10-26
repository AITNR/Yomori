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

    /// <summary>
    /// INI 里有这个构建号的补丁段，但运行期根本没起来（没有 RDP-Tcp 监听）。
    /// </summary>
    /// <remarks>
    /// 这个状态存在的理由：计划书里的 Support 是「本机 INI 是否覆盖当前版本」（静态），
    /// 而 RDPConf 的 <c>Support level</c> 是**运行期**结论（hook 有没有真的挂上）。
    /// 两者语义不同 —— 只报「已覆盖」会让用户拿着我们的界面去对 RDPConf 时看到相反的结论。
    /// 所以监听缺失时单独列一档：INI 覆盖了，但没生效，通常要重启电脑。
    /// </remarks>
    RuntimeNotApplied = 4,

    /// <summary>
    /// INI 静态上覆盖了当前构建号，但运行期状态读不出来（WinStation 枚举失败 / 状态值异常）。
    /// </summary>
    /// <remarks>
    /// <para>为什么单列一档而不是直接报 <see cref="FullySupported"/>：枚举会话失败时我们**没有**
    /// 运行期证据，报「通过」等于把「不知道」写成「没问题」（2026-10-26 检查记录 P2）。</para>
    /// <para>为什么也不直接报 <see cref="Unknown"/>：INI 那份静态证据是真实存在的，
    /// 抹掉它会让用户以为连 INI 都不对。这一档的文案必须同时写出「INI 已覆盖」和「运行期未知」。</para>
    /// </remarks>
    RuntimeUnknown = 5,
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

    /// <summary>
    /// 实际命中的那个候选版本号（<see cref="State"/> 为已覆盖时才有值）。
    ///
    /// <para>为什么需要它：termsrv.dll 的「固定文件信息」与「版本字符串」可能不一致
    /// （Windows 的 QS 更新只改前者），两段都要拿去比对。命中哪个必须记下来，
    /// 否则界面无法解释「为什么显示的版本号与文件属性里的不一样」。</para>
    /// </summary>
    public string MatchedVersion { get; init; } = "";
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
    /// <summary>
    /// <b>静态</b>结论：本机 rdpwrap.ini 是否覆盖当前 termsrv.dll 版本。只看文件，不看监听器。
    /// </summary>
    /// <remarks>
    /// <para>为什么要和 <see cref="SupportState"/> 分开存：决定「要不要再跑一次
    /// <c>RDPWInst -w</c>」只能依据静态事实。混进运行期监听会出两种相反的错
    /// （2026-10-26 检查记录 P1）：监听暂时缺失 → 已覆盖的 INI 被判成「要更新」，
    /// 白跑一次联网；监听读不出来 → 仍显示已覆盖 → 跳过更新。</para>
    /// <para>界面仍显示合并后的 <see cref="SupportState"/>，这个字段只服务流程判据与诊断。</para>
    /// </remarks>
    public RdpSupportState IniState { get; init; } = RdpSupportState.Unknown;

    /// <summary>静态结论 × 运行期监听合并后的展示结论。</summary>
    public RdpSupportState SupportState { get; init; } = RdpSupportState.Unknown;

    /// <summary>比对用的版本号：取 DLL 的**固定文件信息**（与 RDPWrap 一致）。</summary>
    public string TermsrvVersion { get; init; } = "";

    /// <summary><c>FileVersion</c> 的原始文本，含 <c> (WinBuild.160101.0800)</c> 这类后缀。</summary>
    public string TermsrvVersionRaw { get; init; } = "";

    /// <summary>
    /// 版本字符串里的四段数字。<see cref="TermsrvVersion"/> 与它不同时才有值 ——
    /// Windows 的 QS 更新只改固定文件信息，字符串会滞后，两者不一致是正常现象。
    /// </summary>
    public string TermsrvVersionText { get; init; } = "";

    public string IniUpdated { get; init; } = "";
    public bool IniLabelMatch { get; init; }
    public bool IniStrictSectionMatch { get; init; }

    /// <summary>INI 里实际命中的版本段（多候选比对时才知道是哪一个）。</summary>
    public string IniMatchedVersion { get; init; } = "";

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

/// <summary>
/// 把「INI 静态覆盖」与「运行时监听」合成 Support 的最终结论。
/// </summary>
/// <remarks>
/// <para>抽成纯函数是为了能离线断言这条规则：它是「我们显示已覆盖、RDPConf 显示 not supported」
/// 这个矛盾的唯一出口。规则很简单 —— INI 说覆盖、但拿不到运行期证据，就不能算通过。</para>
/// <para>2026-10-26 检查记录 P2：只有「明确没有监听」才降为
/// <see cref="RdpSupportState.RuntimeNotApplied"/>；监听**读不出来**时不能保持
/// <see cref="RdpSupportState.FullySupported"/>（那会让精简系统/权限不足的机器显示「通过」），
/// 而是降到 <see cref="RdpSupportState.RuntimeUnknown"/>。</para>
/// </remarks>
public static class RdpSupportVerdict
{
    public static RdpSupportState Combine(RdpSupportState iniState, RdpListenerState listener)
    {
        // INI 本身没覆盖（或读不到）：运行期怎样都改变不了这个结论
        if (iniState != RdpSupportState.FullySupported)
        {
            return iniState;
        }

        return listener switch
        {
            RdpListenerState.Listening => RdpSupportState.FullySupported,
            RdpListenerState.Absent => RdpSupportState.RuntimeNotApplied,
            _ => RdpSupportState.RuntimeUnknown,
        };
    }
}

/// <summary>
/// 「要不要再跑一次在线更新 INI」的判据。
/// </summary>
/// <remarks>
/// 只接受静态结论：运行期监听与这件事无关 —— <c>-w</c> 改的是文件内容，
/// 监听器缺失是有没有生效的问题，不是文件缺不缺段的问题。
/// </remarks>
public static class RdpIniUpdatePolicy
{
    /// <summary>静态分析已覆盖当前构建号时，不需要联网更新。</summary>
    public static bool NeedsOnlineUpdate(RdpSupportState iniState) =>
        iniState != RdpSupportState.FullySupported;
}

/// <summary>
/// 监听器判定的纯规则：名字命中 <b>且</b> 会话状态是 Listen（6）才算正在监听。
/// </summary>
/// <remarks>
/// <para>2026-10-26 检查记录 P2：原实现只要在 WinStation 里找到同名条目就判「正在监听」，
/// 完全没用已经读到的 <c>State</c>。同名但处于 Active/Disconnected 的条目会把它顶成通过。</para>
/// <para>同名但状态不是 6 时返回 <see cref="RdpListenerState.Unknown"/> 而不是「无监听」：
/// 条目存在这件事本身说明我们看见了一个真实存在的会话，只是它不是监听态 ——
/// 报 Unknown 才是「看见了但看不懂」，报 Absent 会在诊断里丢掉这个线索。</para>
/// </remarks>
public static class RdpListenerVerdict
{
    /// <summary><c>WINSTATIONSTATECLASS.State_Listen</c>。</summary>
    public const int ListenState = 6;

    public static RdpListenerState Classify(bool found, int sessionState)
    {
        if (!found)
        {
            return RdpListenerState.Absent;
        }

        return sessionState == ListenState
            ? RdpListenerState.Listening
            : RdpListenerState.Unknown;
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
        RdpSupportState.RuntimeNotApplied => "INI 已覆盖 · 运行期未生效",
        RdpSupportState.RuntimeUnknown => "INI 已覆盖 · 运行期未知",
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
        sb.AppendLine($"termsrv.dll 版本：{Blank(snapshot.TermsrvVersion)}（取自固定文件信息，与 RDPWrap 同源）");
        if (snapshot.TermsrvVersionText.Length > 0)
        {
            sb.AppendLine($"版本字符串：{snapshot.TermsrvVersionText}（StringFileInfo 里的 FileVersion，QS 更新后可能滞后）");
        }

        if (snapshot.TermsrvVersionRaw.Length > 0)
        {
            sb.AppendLine($"版本原始文本：{snapshot.TermsrvVersionRaw}");
        }

        if (snapshot.IniMatchedVersion.Length > 0)
        {
            sb.AppendLine($"INI 命中的段：[{snapshot.IniMatchedVersion}]");
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
