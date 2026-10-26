// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.Linq;

namespace Yomori.Services;

/// <summary>
/// 会话枚举结果的四种状态 —— 这是「同账户重复 RDP 会话」修复的地基。
///
/// <para>旧实现 <c>EnumerateSessions</c> 在 WTS 查询失败时返回<b>空列表</b>，
/// 于是「查询失败」和「这台机器上没有会话」在调用方看来完全一样：
/// 本该拦截的连接被当成「可以新建」，本该提示的故障被当成「一切正常」。
/// 本枚举把它们分开，且规定<b>任何一分失败都不许当作成功</b>。</para>
/// </summary>
public enum SessionQueryStatus
{
    /// <summary>查询成功，且有至少一条会话。</summary>
    Ok = 0,

    /// <summary>查询成功，确实一条会话都没有。</summary>
    Empty = 1,

    /// <summary>
    /// 会话列表拿到了，但至少有一条会话的身份信息（账户名 / 域 / SID）查询失败。
    /// 只拿到一部分身份就意味着唯一性无法证明 —— 与完全失败同等对待。
    /// </summary>
    PartialFailure = 2,

    /// <summary>枚举接口本身失败（无权限、服务未启动等），错误在 <see cref="SessionQueryResult.Error"/>。</summary>
    Failed = 3,
}

/// <summary>
/// 一条会话的完整身份快照。
///
/// <para>为什么不再用裸账户名当主键：截图里的「game1 / game2」只是账户名，
/// 同名账户可以在不同域存在，同一账户可以有多个 SessionId。
/// 本项目的所有判重、派发、注销都必须以
/// <c>主机 + 账户 SID + 真实 SessionId</c> 为准（见计划书 P0 第 2 条）。</para>
/// </summary>
public sealed class RdpSessionTarget
{
    /// <summary>目标主机（已归一，本机为 <c>localhost</c> 一类的规范值）。</summary>
    public string Host { get; set; } = string.Empty;

    /// <summary>WTS 报的域 / 机器名；本机账户可能为空。</summary>
    public string Domain { get; set; } = string.Empty;

    /// <summary>裸账户名（不含域）。</summary>
    public string UserName { get; set; } = string.Empty;

    /// <summary>账户 SID（<c>S-1-5-21-…</c>）。解析失败留空 —— 留空意味着<b>身份不可证明</b>。</summary>
    public string Sid { get; set; } = string.Empty;

    /// <summary>真实 SessionId（不是进程数，也不是截图里的行号）。</summary>
    public uint SessionId { get; set; }

    public SessionConnectState State { get; set; }

    /// <summary>采集时刻，用于审计时间线。</summary>
    public DateTimeOffset CapturedAt { get; set; } = DateTimeOffset.Now;

    /// <summary>
    /// 身份是否可证明。SID 解析不出来就不能拿它做唯一性判断 ——
    /// 认证域不同、账户同名时，仅凭用户名会把两个不同主体混成一个。
    /// </summary>
    public bool IdentityVerified => Sid.Length > 0;

    /// <summary>是否是可复用的交互式会话（Active / Connected）。</summary>
    public bool IsUsable => State is SessionConnectState.Active or SessionConnectState.Connected;

    /// <summary>
    /// 唯一身份键：SID 优先，退化为「域\账户」。
    /// 只用用户名的写法在这里是<b>不被允许</b>的。
    /// </summary>
    public string IdentityKey => Sid.Length > 0
        ? $"SID:{Sid}"
        : $"ACCOUNT:{(Domain.Length > 0 ? Domain + "\\" : string.Empty)}{UserName}";

    /// <summary>审计行：主机 + 域 + 账户 + SID + SessionId + 状态 + 时刻。</summary>
    public string Describe() =>
        $"{Host} | {Domain}\\{UserName} | {(Sid.Length > 0 ? Sid : "SID 未知")} | " +
        $"会话 {SessionId} | {StateText} | {CapturedAt:HH:mm:ss}";

    /// <summary>状态的中文说法（与界面一致）。</summary>
    public string StateText => State switch
    {
        SessionConnectState.Active => "运行中",
        SessionConnectState.Connected => "已连接",
        SessionConnectState.ConnectQuery => "正在连接",
        SessionConnectState.Disconnected => "已断开",
        SessionConnectState.Idle => "空闲",
        SessionConnectState.Listen => "监听中",
        SessionConnectState.Reset => "正在重置",
        SessionConnectState.Down => "已关闭",
        _ => State.ToString(),
    };
}

/// <summary>一次会话枚举的完整结果（含状态与错误，不再把失败折叠成空列表）。</summary>
public sealed class SessionQueryResult
{
    public string Host { get; set; } = string.Empty;

    public SessionQueryStatus Status { get; set; } = SessionQueryStatus.Failed;

    /// <summary>失败原因（仅 <see cref="SessionQueryStatus.Failed"/> / 部分失败时有值）。</summary>
    public string Error { get; set; } = string.Empty;

    public List<RdpSessionTarget> Sessions { get; set; } = new();

    /// <summary>身份查询失败的会话条数（&gt; 0 即视为部分失败）。</summary>
    public int PartialFailures { get; set; }

    public DateTimeOffset At { get; set; } = DateTimeOffset.Now;

    /// <summary>结果能不能用来做准入判断。只有 Ok / Empty 可以。</summary>
    public bool Usable => Status is SessionQueryStatus.Ok or SessionQueryStatus.Empty;

    /// <summary>某账户名下的全部会话（不做「取第一条」这种裁剪）。</summary>
    public List<RdpSessionTarget> ForUser(string? userName)
    {
        var bare = BareName(userName);
        if (bare.Length == 0)
        {
            return new List<RdpSessionTarget>();
        }

        return Sessions
            .Where(s => string.Equals(s.UserName, bare, StringComparison.OrdinalIgnoreCase))
            .OrderBy(s => s.SessionId)
            .ToList();
    }

    /// <summary>审计用的多行快照。</summary>
    public List<string> DescribeAll()
    {
        var lines = new List<string>
        {
            $"会话快照：主机 {Host}，状态 {Status}，共 {Sessions.Count} 条，身份查询失败 {PartialFailures} 条，采集于 {At:HH:mm:ss}",
        };

        if (Error.Length > 0)
        {
            lines.Add($"错误：{Error}");
        }

        foreach (var session in Sessions.OrderBy(s => s.SessionId))
        {
            lines.Add("  · " + session.Describe());
        }

        return lines;
    }

    internal static string BareName(string? userName)
    {
        var text = (userName ?? string.Empty).Trim();
        return text.Contains('\\') ? text[(text.LastIndexOf('\\') + 1)..] : text;
    }
}

/// <summary>准入结论。</summary>
public enum RdpSessionAdmission
{
    /// <summary>目标账户没有任何会话 —— 需要新建连接。</summary>
    AllowCreate = 0,

    /// <summary>已有一条 Active / Connected 会话，直接复用，不新建连接。</summary>
    AllowReuse = 1,

    /// <summary>
    /// 只有一条已断开的会话。按「重连」处理：mstsc / FreeRDP 重连会回到该会话，
    /// 前提是服务端允许复用（单账户单会话策略生效）。策略未证实时会附一条强警告，
    /// 并由连接后复核兜底（真的多出一条就断开本次客户端）。
    /// </summary>
    AllowReconnect = 2,

    /// <summary>同账户出现多条会话 —— 硬阻断。</summary>
    BlockMultiSession = 3,

    /// <summary>会话枚举失败或部分失败 —— 硬阻断（不把未知当安全）。</summary>
    BlockQueryFailed = 4,

    /// <summary>目标账户 SID 无法确认 —— 硬阻断（仅凭用户名不能证明唯一性）。</summary>
    BlockIdentityUnknown = 5,

    /// <summary>单条会话处于无法判定的异常状态（Reset / Down 等）—— 阻断。</summary>
    BlockUnusableState = 6,
}

/// <summary>准入判定的完整结论，含证据（可审计清单）与后续动作要求。</summary>
public sealed class RdpSessionAdmissionResult
{
    public RdpSessionAdmission Admission { get; set; } = RdpSessionAdmission.BlockQueryFailed;

    /// <summary>结论原因，直接进日志与对话框。</summary>
    public string Reason { get; set; } = string.Empty;

    /// <summary>本账户命中的全部会话（不裁剪）。</summary>
    public List<RdpSessionTarget> Candidates { get; set; } = new();

    /// <summary>选定要复用 / 重连的那一条（仅 AllowReuse / AllowReconnect 有值）。</summary>
    public RdpSessionTarget? Target { get; set; }

    /// <summary>可用但需要提示的事项（不阻断）。</summary>
    public List<string> Warnings { get; } = new();

    /// <summary>可审计的证据行：快照 + 判定 + 警告。</summary>
    public List<string> AuditLines { get; } = new();

    /// <summary>准入快照（连接后复核要用它比对）。</summary>
    public SessionQueryResult? Snapshot { get; set; }

    public bool Allowed => Admission is RdpSessionAdmission.AllowCreate
        or RdpSessionAdmission.AllowReuse
        or RdpSessionAdmission.AllowReconnect;

    /// <summary>连接后是否应当出现一条新会话（只有 AllowCreate 允许）。</summary>
    public bool ExpectsNewSession => Admission == RdpSessionAdmission.AllowCreate;

    /// <summary>连接后现有会话集合必须原样不变（复用 / 重连）。</summary>
    public bool ExpectsSameSessions => Admission is RdpSessionAdmission.AllowReuse
        or RdpSessionAdmission.AllowReconnect;

    public string Code => Admission switch
    {
        RdpSessionAdmission.AllowCreate => "allow-create",
        RdpSessionAdmission.AllowReuse => "allow-reuse",
        RdpSessionAdmission.AllowReconnect => "allow-reconnect",
        RdpSessionAdmission.BlockMultiSession => "block-multi-session",
        RdpSessionAdmission.BlockQueryFailed => "block-query-failed",
        RdpSessionAdmission.BlockIdentityUnknown => "block-identity-unknown",
        _ => "block-unusable-state",
    };
}

/// <summary>连接后复核的结论。</summary>
public enum RdpSessionVerification
{
    /// <summary>会话集合符合预期。</summary>
    Ok = 0,

    /// <summary>出现了预期外的会话（本次连接把会话数量搞多了）。</summary>
    Duplicated = 1,

    /// <summary>复核时查不到会话（无法证明，按不安全处理）。</summary>
    QueryFailed = 2,

    /// <summary>目标会话消失了（登录失败 / 已被注销）。</summary>
    TargetMissing = 3,
}

public sealed class RdpSessionVerificationResult
{
    public RdpSessionVerification Verdict { get; set; } = RdpSessionVerification.QueryFailed;

    public string Reason { get; set; } = string.Empty;

    /// <summary>基线里没有、复核时冒出来的会话 id。</summary>
    public List<uint> NewSessionIds { get; } = new();

    /// <summary>复核时的实际会话（审计用）。</summary>
    public List<RdpSessionTarget> Observed { get; set; } = new();

    public bool Ok => Verdict == RdpSessionVerification.Ok;
}

/// <summary>
/// 准入判定的纯函数实现（不碰注册表 / WTS，可在冒烟里直接断言）。
///
/// <para>规则来自计划书 P0：</para>
/// <list type="number">
/// <item>查询失败 / 部分失败 → 阻断。未知不等于安全。</item>
/// <item>同账户 ≥ 2 条会话 → 阻断，并把完整清单交给用户。</item>
/// <item>只有 1 条且 Active / Connected → 复用。</item>
/// <item>只有 1 条且 Disconnected → 允许重连，但必须提示「服务端须允许复用」，并由连接后复核兜底。</item>
/// <item>身份（SID）无法确认 → 阻断，不允许只凭用户名放行。</item>
/// </list>
/// </summary>
public static class RdpSessionAdmissionPlanner
{
    /// <summary>阻断时给用户的统一修正方向。</summary>
    public const string SingleSessionPolicyHint =
        "请到「设置 → 远程桌面」用「单账户单会话策略」显式开启并回读确认，或先注销多余的历史会话。";

    public static RdpSessionAdmissionResult Plan(
        SessionQueryResult query, string? targetHost, string? targetUser, string? targetSid = null)
    {
        var host = (targetHost ?? string.Empty).Trim();
        var bare = SessionQueryResult.BareName(targetUser);
        var result = new RdpSessionAdmissionResult { Snapshot = query };

        result.AuditLines.AddRange(query.DescribeAll());
        var wantedSidText = (targetSid ?? string.Empty).Trim();
        result.AuditLines.Add(
            $"目标：主机 {host}，账户 {bare}，期望 SID {(wantedSidText.Length > 0 ? wantedSidText : "未指定")}");

        if (!query.Usable)
        {
            result.Admission = RdpSessionAdmission.BlockQueryFailed;
            result.Reason = query.Status == SessionQueryStatus.PartialFailure
                ? $"会话枚举只成功了一部分（{query.PartialFailures} 条会话的身份查不出来），无法证明同账户唯一性。"
                : $"会话枚举失败：{(query.Error.Length > 0 ? query.Error : "原因未知")}。";
            result.AuditLines.Add("判定：阻断（查询不可用）。");
            return result;
        }

        if (bare.Length == 0)
        {
            result.Admission = RdpSessionAdmission.BlockIdentityUnknown;
            result.Reason = "没有指定目标账户，无法建立会话身份。";
            result.AuditLines.Add("判定：阻断（目标账户为空）。");
            return result;
        }

        var candidates = query.ForUser(bare);
        result.Candidates = candidates;

        // 域过滤：目标账户写成 "DOMAIN\user" 时，域必须对得上（防止同名不同域串台）
        var targetDomain = (targetUser ?? string.Empty).Trim();
        var slash = targetDomain.IndexOf('\\');
        if (slash > 0)
        {
            var wanted = targetDomain[..slash];
            var beforeCount = candidates.Count;
            candidates = candidates
                .Where(s => s.Domain.Length == 0 || string.Equals(s.Domain, wanted, StringComparison.OrdinalIgnoreCase))
                .ToList();
            result.Candidates = candidates;
            if (candidates.Count != beforeCount)
            {
                result.AuditLines.Add($"按域「{wanted}」过滤掉 {beforeCount - candidates.Count} 条同名但不同域的会话。");
            }
        }

        var wantedSid = (targetSid ?? string.Empty).Trim();
        if (wantedSid.Length > 0)
        {
            var beforeCount = candidates.Count;
            candidates = candidates
                .Where(s => s.Sid.Length == 0 || string.Equals(s.Sid, wantedSid, StringComparison.OrdinalIgnoreCase))
                .ToList();
            result.Candidates = candidates;
            if (candidates.Count != beforeCount)
            {
                result.AuditLines.Add($"按 SID 过滤掉 {beforeCount - candidates.Count} 条同名不同主体的会话。");
            }
        }

        if (candidates.Count == 0)
        {
            result.Admission = RdpSessionAdmission.AllowCreate;
            result.Reason = $"账户「{bare}」当前没有会话，将新建一条。";
            result.AuditLines.Add("判定：允许新建（0 条会话）。");
            return result;
        }

        if (candidates.Count > 1)
        {
            result.Admission = RdpSessionAdmission.BlockMultiSession;
            result.Reason =
                $"账户「{bare}」在主机上有 {candidates.Count} 条会话（会话号 " +
                string.Join("、", candidates.Select(c => c.SessionId)) +
                "）。同账户多会话会让任务派发无法确定归属，本次连接已停止。";
            result.AuditLines.Add("判定：阻断（同账户多会话）。" + SingleSessionPolicyHint);
            return result;
        }

        var only = candidates[0];
        if (!only.IdentityVerified)
        {
            result.Admission = RdpSessionAdmission.BlockIdentityUnknown;
            result.Reason = $"账户「{bare}」的会话 {only.SessionId} 无法解析出账户 SID，仅凭用户名不能证明身份唯一。";
            result.AuditLines.Add("判定：阻断（SID 未知）。");
            return result;
        }

        if (only.IsUsable)
        {
            result.Admission = RdpSessionAdmission.AllowReuse;
            result.Target = only;
            result.Reason = $"复用已有会话 {only.SessionId}（{only.StateText}）。";
            result.AuditLines.Add($"判定：允许复用（会话 {only.SessionId}）。");
            return result;
        }

        if (only.State == SessionConnectState.Disconnected)
        {
            result.Admission = RdpSessionAdmission.AllowReconnect;
            result.Target = only;
            result.Reason = $"账户「{bare}」只有一条已断开的会话 {only.SessionId}，按重连处理。";
            result.Warnings.Add(
                "该会话处于「已断开」：重连能否回到同一条会话，取决于服务端的单账户单会话策略。" +
                SingleSessionPolicyHint);
            result.AuditLines.Add($"判定：允许重连（会话 {only.SessionId}，已断开）。");
            return result;
        }

        result.Admission = RdpSessionAdmission.BlockUnusableState;
        result.Reason = $"账户「{bare}」的会话 {only.SessionId} 处于「{only.StateText}」，此刻连接既不能复用也无法确认会落到哪条会话。";
        result.AuditLines.Add("判定：阻断（会话状态不可用）。");
        return result;
    }

    /// <summary>
    /// 连接后复核：把「准入时的快照」和「连接建立后的实际会话」比一比。
    ///
    /// <para>这是计划书 P0 第 4 条的后半句 —— 「连接后必须复核会话身份」。
    /// 只做准入不做复核的话，服务端策略、Wrapper、客户端差异任何一处让我们算错，
    /// 都会静默多出一条会话，而这正是原问题的形态。</para>
    /// </summary>
    public static RdpSessionVerificationResult VerifyAfterConnect(
        RdpSessionAdmissionResult admission, SessionQueryResult after, string? targetUser)
    {
        var bare = SessionQueryResult.BareName(targetUser);
        var verification = new RdpSessionVerificationResult();

        if (!after.Usable)
        {
            verification.Verdict = RdpSessionVerification.QueryFailed;
            verification.Reason = after.Status == SessionQueryStatus.PartialFailure
                ? "复核时会话身份查询部分失败，无法证明唯一性。"
                : $"复核时会话枚举失败：{(after.Error.Length > 0 ? after.Error : "原因未知")}。";
            return verification;
        }

        var current = after.ForUser(bare);
        verification.Observed = current;

        if (admission.Snapshot is null)
        {
            verification.Verdict = RdpSessionVerification.QueryFailed;
            verification.Reason = "没有准入快照，无法复核。";
            return verification;
        }

        var baseline = admission.Snapshot.ForUser(bare).Select(s => s.SessionId).ToHashSet();
        var now = current.Select(s => s.SessionId).ToHashSet();

        foreach (var id in now.Where(id => !baseline.Contains(id)))
        {
            verification.NewSessionIds.Add(id);
        }

        if (admission.ExpectsSameSessions && verification.NewSessionIds.Count > 0)
        {
            verification.Verdict = RdpSessionVerification.Duplicated;
            verification.Reason =
                $"本次连接在预期之外新增了会话 {string.Join("、", verification.NewSessionIds)}；" +
                "准入时要求保持原有会话集合不变。";
            return verification;
        }

        if (admission.ExpectsNewSession && verification.NewSessionIds.Count > 1)
        {
            verification.Verdict = RdpSessionVerification.Duplicated;
            verification.Reason =
                $"本次连接一次性新增了 {verification.NewSessionIds.Count} 条会话（{string.Join("、", verification.NewSessionIds)}），只应有一条。";
            return verification;
        }

        if (admission.ExpectsNewSession && verification.NewSessionIds.Count == 0)
        {
            // 新建路径下会话还没出现：既可能仍在登录，也可能落进了旧会话。
            // 交给调用方按超时处理，这里判为缺失而不是重复。
            verification.Verdict = RdpSessionVerification.TargetMissing;
            verification.Reason = "新建连接后没有观察到新会话（可能仍在登录，或落回了已有会话）。";
            return verification;
        }

        verification.Verdict = RdpSessionVerification.Ok;
        verification.Reason = $"会话集合符合预期（当前 {now.Count} 条：{string.Join("、", now)}）。";
        return verification;
    }
}
