// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;

namespace Yomori.Models;

/// <summary>
/// Windows 版本的粗分类。<b>只用于诊断展示</b>：既不参与账户授权决策，也不用于拦截任何连接。
/// </summary>
public enum RdpWindowsEdition
{
    Unknown = 0,
    Home = 1,
    Professional = 2,
    Enterprise = 3,
    Education = 4,
    Server = 5,
}

/// <summary>
/// 版本判定与本地组 SID。
/// </summary>
/// <remarks>
/// 判定必须用 <c>EditionID</c> 而不是 <c>ProductName</c>：后者是本地化字符串
/// （中文系统上是「Windows 11 家庭中文版」），拿它做英文关键字匹配在中文/其他语言系统上直接失效。
/// <c>EditionID</c> 是语言中立的（家庭版一律 <c>Core</c>，不管界面语言）。
/// <para>为什么坚持按 SID 找组：组名会被本地化（「Remote Desktop Users」在中文系统上是
/// 「Remote Desktop Users」还是「远程桌面用户」取决于系统），只有 <c>S-1-5-32-555</c> 是稳定的。</para>
/// </remarks>
public static class RdpEditions
{
    /// <summary>Remote Desktop Users（内置组）。</summary>
    public const string RemoteDesktopUsersSid = "S-1-5-32-555";

    /// <summary>Administrators（内置组）。</summary>
    public const string AdministratorsSid = "S-1-5-32-544";

    /// <summary>
    /// 由 <c>EditionID</c> 与 <c>ProductName</c> 判版本。<c>EditionID</c> 优先；两者都说不出所以然就是 Unknown。
    /// </summary>
    public static RdpWindowsEdition Classify(string? editionId, string? productName)
    {
        var id = (editionId ?? string.Empty).Trim();
        var name = (productName ?? string.Empty).Trim();

        // ---- EditionID（语言中立，最可靠） ----
        if (id.Length > 0)
        {
            if (id.Equals("Core", StringComparison.OrdinalIgnoreCase)
                || id.Equals("CoreSingleLanguage", StringComparison.OrdinalIgnoreCase)
                || id.Equals("CoreCountrySpecific", StringComparison.OrdinalIgnoreCase)
                || id.Equals("CoreN", StringComparison.OrdinalIgnoreCase))
            {
                return RdpWindowsEdition.Home;
            }

            if (id.Equals("Professional", StringComparison.OrdinalIgnoreCase)
                || id.Equals("ProfessionalN", StringComparison.OrdinalIgnoreCase)
                || id.Equals("ProfessionalWorkstation", StringComparison.OrdinalIgnoreCase)
                || id.Equals("ProfessionalWorkstationN", StringComparison.OrdinalIgnoreCase))
            {
                return RdpWindowsEdition.Professional;
            }

            if (id.Equals("Enterprise", StringComparison.OrdinalIgnoreCase)
                || id.Equals("EnterpriseN", StringComparison.OrdinalIgnoreCase)
                || id.Equals("EnterpriseS", StringComparison.OrdinalIgnoreCase)
                || id.Equals("IoTEnterprise", StringComparison.OrdinalIgnoreCase))
            {
                return RdpWindowsEdition.Enterprise;
            }

            if (id.Equals("Education", StringComparison.OrdinalIgnoreCase)
                || id.Equals("EducationN", StringComparison.OrdinalIgnoreCase))
            {
                return RdpWindowsEdition.Education;
            }

            if (id.StartsWith("Server", StringComparison.OrdinalIgnoreCase))
            {
                return RdpWindowsEdition.Server;
            }
        }

        // ---- ProductName（兜底：EditionID 被精简系统抹掉时） ----
        // ⚠️ 关键字必须同时覆盖中文本地化写法：中文家庭版的 ProductName 是
        // 「Windows 11 家庭中文版」，里面没有 "Home" —— 只按英文匹配等于在中文系统上失效。
        if (name.Length > 0)
        {
            if (name.Contains("Home", StringComparison.OrdinalIgnoreCase)
                || name.Contains("家庭", StringComparison.Ordinal))
            {
                return RdpWindowsEdition.Home;
            }

            if (name.Contains("Server", StringComparison.OrdinalIgnoreCase))
            {
                return RdpWindowsEdition.Server;
            }

            if (name.Contains("Professional", StringComparison.OrdinalIgnoreCase)
                || name.Contains("Pro", StringComparison.OrdinalIgnoreCase)
                || name.Contains("专业", StringComparison.Ordinal))
            {
                return RdpWindowsEdition.Professional;
            }

            if (name.Contains("Enterprise", StringComparison.OrdinalIgnoreCase)
                || name.Contains("企业", StringComparison.Ordinal))
            {
                return RdpWindowsEdition.Enterprise;
            }

            if (name.Contains("Education", StringComparison.OrdinalIgnoreCase)
                || name.Contains("教育", StringComparison.Ordinal))
            {
                return RdpWindowsEdition.Education;
            }
        }

        return RdpWindowsEdition.Unknown;
    }

    /// <summary>是否属于「没有 RDP 服务端」的家庭版一支。</summary>
    public static bool IsHomeFamily(RdpWindowsEdition edition) => edition == RdpWindowsEdition.Home;

    public static string Label(RdpWindowsEdition edition) => edition switch
    {
        RdpWindowsEdition.Home => "家庭版",
        RdpWindowsEdition.Professional => "专业版",
        RdpWindowsEdition.Enterprise => "企业版",
        RdpWindowsEdition.Education => "教育版",
        RdpWindowsEdition.Server => "Server",
        _ => "未知版本",
    };
}

/// <summary>本机账户授权的结果种类。</summary>
public enum RdpGrantOutcome
{
    /// <summary>目标不是本机 —— 不对远端授权，本机同名账户也不碰。</summary>
    SkippedRemote = 0,

    /// <summary>已经是管理员，幂等成功。</summary>
    AlreadyMember = 1,

    /// <summary>
    /// 历史值：旧策略的「本轮新加入 Remote Desktop Users」。
    /// <b>当前策略不再产出这个结果</b>，保留数值只为让旧记录与旧日志仍能按原义读出。
    /// </summary>
    AddedRemoteDesktopUsers = 2,

    /// <summary>本轮新加入 Administrators（授予本机管理员权限）。</summary>
    AddedAdministrators = 3,

    /// <summary>按规则拒绝（账户边界不清、管理员组不可用等），未做任何写入。</summary>
    Refused = 4,

    /// <summary>执行失败（API 报错）。</summary>
    Failed = 5,

    /// <summary>
    /// 已发出加入请求，但回读确认不了成员资格。<b>结果未确定</b>：
    /// 既不能当失败宣称「没有改动」，也不能当成功宣称「已授权」。
    /// </summary>
    Unconfirmed = 6,
}

/// <summary>一次本机账户授权的完整交代。</summary>
public sealed class RdpGrantResult
{
    public RdpGrantOutcome Outcome { get; init; }

    /// <summary>已通过身份边界校验的本机账户名（不含机器/域前缀）。</summary>
    public string AccountName { get; init; } = "";

    public string GroupName { get; init; } = "";
    public string GroupSid { get; init; } = "";
    public string AccountSid { get; init; } = "";

    /// <summary>加入请求是否被 API 接受（<c>1378</c> 也算接受：并发场景下已在组内）。</summary>
    public bool RequestAccepted { get; init; }

    /// <summary>本次加入请求的 Win32/NERR 码；<c>0</c> 表示无错误。</summary>
    public int RequestCode { get; init; }

    /// <summary>
    /// 加入后回读的成员关系：<c>true</c>=确认在组内，<c>false</c>=确认不在组内，
    /// <c>null</c>=读不出来（结果未确定）。
    /// </summary>
    public bool? ReadbackMember { get; init; }

    /// <summary>给用户看的一句话。</summary>
    public string Message { get; init; } = "";

    /// <summary>给日志/诊断看的细节（执行前的成员关系、回读结果等）。</summary>
    public string Detail { get; init; } = "";

    public bool WasAddedThisRound { get; init; }

    /// <summary>
    /// 授权记录是否已落盘。为 <c>false</c> 且真的改了权限时，界面必须讲
    /// 「管理员已授予，记录保存失败」——不能说成"没有改动"（计划书 §6）。
    /// </summary>
    public bool RecordSaved { get; init; } = true;

    /// <summary>这条规则是否需要在界面上显式提醒「授予管理员权限」。</summary>
    public bool RequiresAdminWarning => Outcome == RdpGrantOutcome.AddedAdministrators;

    public bool IsSuccess =>
        Outcome is RdpGrantOutcome.AlreadyMember
            or RdpGrantOutcome.AddedAdministrators
            // 历史值：旧记录按原义就是成功；新策略不再产出它
            or RdpGrantOutcome.AddedRemoteDesktopUsers;

    public static RdpGrantResult Skipped(string message, string detail = "") => new()
    {
        Outcome = RdpGrantOutcome.SkippedRemote, Message = message, Detail = detail,
    };

    public static RdpGrantResult Refused(string message, string detail = "") => new()
    {
        Outcome = RdpGrantOutcome.Refused, Message = message, Detail = detail,
    };

    public static RdpGrantResult Failed(string message, string detail = "", int code = 0) => new()
    {
        Outcome = RdpGrantOutcome.Failed, Message = message, Detail = detail, RequestCode = code,
    };

    public static RdpGrantResult Unconfirmed(string message, string detail = "", int code = 0) => new()
    {
        Outcome = RdpGrantOutcome.Unconfirmed,
        Message = message,
        Detail = detail,
        RequestAccepted = true,
        RequestCode = code,
        WasAddedThisRound = true,
    };
}

/// <summary>授权决策的结果：该加哪个组，或者为什么不该动。</summary>
public sealed class RdpGrantPlan
{
    public bool Allowed { get; init; }

    /// <summary>已通过身份边界校验的本机账户名（不含机器/域前缀）。</summary>
    public string AccountName { get; init; } = "";

    public string GroupSid { get; init; } = "";
    public bool NeedsAdminWarning { get; init; }
    public string Reason { get; init; } = "";
}

/// <summary>
/// 授权前的只读核对结果：能不能授权、要不要弹确认框、确认时锁的是哪个 SID。
/// </summary>
/// <remarks>
/// 与决策器分开是因为"要不要弹框"依赖实测的成员关系，而决策器必须保持纯函数。
/// 这个对象只在内存里传，不落盘。
/// </remarks>
public sealed class RdpGrantPrecheck
{
    public RdpGrantPlan Plan { get; init; } = new();

    /// <summary>Administrators 的本地化组名；组不存在时为空串。</summary>
    public string GroupName { get; init; } = "";

    /// <summary>确认对话框要锁定的账户 SID。授权时回传，防止同名账户被删除重建后授给另一个 SID。</summary>
    public string AccountSid { get; init; } = "";

    /// <summary><c>true</c>=已在管理员组；<c>false</c>=不在；<c>null</c>=读不出来。</summary>
    public bool? IsAlreadyMember { get; init; }

    /// <summary>非空表示不能继续（拒绝或查不出来），内容就是该展示给用户的原因。</summary>
    public string Failure { get; init; } = "";

    /// <summary>需要向用户确认后才能执行。</summary>
    public bool NeedsConfirmation =>
        Failure.Length == 0 && Plan.Allowed && IsAlreadyMember == false && AccountSid.Length > 0;

    /// <summary>已经是管理员，无需任何修改。</summary>
    public bool AlreadyAdministrator => Failure.Length == 0 && Plan.Allowed && IsAlreadyMember == true;
}

/// <summary>
/// 本机账户授权的**纯逻辑**决策器。
/// </summary>
/// <remarks>
/// <para>统一策略（2026-10-26 增量计划书 §2）：<b>本机被控账户一律设为管理员</b>，
/// 目标组恒为 Administrators（<c>S-1-5-32-544</c>）。不再按家庭版/非家庭版分流，
/// 不再因为 Remote Desktop Users 组存不存在而改变目标组。</para>
/// <para>抽成纯函数是为了能离线断言：这些规则错一条的后果都很重 ——
/// 把域账户当本机账户加组、把 <c>DOMAIN\user</c> 截成 <c>user</c> 后加到本机同名账户上、
/// 在管理员组不可用时"降级"去加 RDP 组。它们全都不报错，只会"权限给错了人"。</para>
/// </remarks>
public static class RdpGrantPlanner
{
    /// <summary>策略版本，随授权记录落盘，便于日后区分"按哪一版规则授的权"。</summary>
    public const string PolicyVersion = "local-admin-only/1";

    /// <summary>
    /// 决定该不该给这个账户授权。允许时目标组恒为 Administrators。
    /// </summary>
    /// <param name="accountName">通道里填的账户名，可能是 <c>user</c> / <c>MACHINE\user</c> / <c>DOMAIN\user</c> / UPN。</param>
    /// <param name="machineName">本机名。</param>
    /// <param name="adminGroupExists">本机是否存在 <c>S-1-5-32-544</c>。</param>
    public static RdpGrantPlan Plan(string? accountName, string machineName, bool adminGroupExists)
    {
        var raw = (accountName ?? string.Empty).Trim();
        if (raw.Length == 0)
        {
            return Deny("账户名为空。");
        }

        // UPN（user@contoso.com）通常是 Microsoft 账户或域账户：身份边界不确定，不做本机授权
        if (raw.Contains('@'))
        {
            return Deny("账户名像 Microsoft 账户或域账户（含 @），身份边界不确定，不做本机授权。");
        }

        var bare = raw;
        var slash = raw.IndexOf('\\');
        if (slash > 0)
        {
            var prefix = raw.Substring(0, slash).Trim();
            bare = raw.Substring(slash + 1).Trim();

            if (bare.Length == 0)
            {
                return Deny("账户名只有域/机器前缀，没有实际账户。");
            }

            // 关键：不把 DOMAIN\user 简单截成 user。前缀不是本机名时一律不动
            if (!prefix.Equals((machineName ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase)
                && !prefix.Equals(".", StringComparison.Ordinal))
            {
                return Deny($"「{prefix}」不是本机名，不对其它机器/域的账户做本机授权。");
            }
        }

        // 到这儿只剩"账户边界清楚的本机账户"：只剩管理员组能不能用这一件事
        if (!adminGroupExists)
        {
            // 不创建组、也不"降级"去加 RDP 组 —— 目标账户拿不到管理员身份就是失败
            return Deny("本机没有 Administrators 组（S-1-5-32-544），属系统异常，未做任何修改。");
        }

        var qualified = QualifiedName(machineName, bare);
        return new RdpGrantPlan
        {
            Allowed = true,
            AccountName = bare,
            GroupSid = RdpEditions.AdministratorsSid,
            NeedsAdminWarning = true,
            Reason = $"将把此计算机上的账户「{qualified}」设为管理员（加入 Administrators）。",
        };

        static RdpGrantPlan Deny(string reason) => new() { Allowed = false, Reason = reason };
    }

    /// <summary>从 <c>MACHINE\user</c> 或 <c>user</c> 里取本机账户名（前缀已由 <see cref="Plan"/> 校验过）。</summary>
    public static string BareAccountName(string? accountName)
    {
        var raw = (accountName ?? string.Empty).Trim();
        var slash = raw.IndexOf('\\');
        return slash > 0 ? raw.Substring(slash + 1).Trim() : raw;
    }

    /// <summary>对话框与结果文案里的账户全名：<c>计算机名\账户名</c>。</summary>
    public static string QualifiedName(string? machineName, string? accountName)
    {
        var machine = (machineName ?? string.Empty).Trim();
        var bare = BareAccountName(accountName);
        return bare.Length == 0
            ? machine.Length == 0 ? "（未填写）" : machine
            : machine.Length == 0 ? bare : $"{machine}\\{bare}";
    }
}
