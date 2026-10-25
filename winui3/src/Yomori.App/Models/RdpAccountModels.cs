// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;

namespace Yomori.Models;

/// <summary>Windows 版本的粗分类。<b>只用于选择授权组</b>，不用于拦截任何连接。</summary>
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
    /// <summary>目标不是本机 —— 按计划书 §8 不对远端授权。</summary>
    SkippedRemote = 0,

    /// <summary>已经是成员，幂等成功。</summary>
    AlreadyMember = 1,

    /// <summary>本轮新加入 Remote Desktop Users。</summary>
    AddedRemoteDesktopUsers = 2,

    /// <summary>本轮新加入 Administrators（仅家庭版且缺 RDP 组时）。</summary>
    AddedAdministrators = 3,

    /// <summary>按规则拒绝（SKU 未知、账户边界不清、组缺失等）。</summary>
    Refused = 4,

    /// <summary>执行失败（API 报错）。</summary>
    Failed = 5,
}

/// <summary>一次本机账户授权的完整交代。</summary>
public sealed class RdpGrantResult
{
    public RdpGrantOutcome Outcome { get; init; }
    public string GroupName { get; init; } = "";
    public string GroupSid { get; init; } = "";
    public string AccountSid { get; init; } = "";

    /// <summary>给用户看的一句话。</summary>
    public string Message { get; init; } = "";

    /// <summary>给日志/诊断看的细节（执行前的成员关系、回读结果等）。</summary>
    public string Detail { get; init; } = "";

    public bool WasAddedThisRound { get; init; }

    /// <summary>这条规则是否需要在界面上显式提醒「授予管理员权限」。</summary>
    public bool RequiresAdminWarning => Outcome == RdpGrantOutcome.AddedAdministrators;

    public bool IsSuccess =>
        Outcome is RdpGrantOutcome.AlreadyMember
            or RdpGrantOutcome.AddedRemoteDesktopUsers
            or RdpGrantOutcome.AddedAdministrators;

    public static RdpGrantResult Skipped(string message, string detail = "") => new()
    {
        Outcome = RdpGrantOutcome.SkippedRemote, Message = message, Detail = detail,
    };

    public static RdpGrantResult Refused(string message, string detail = "") => new()
    {
        Outcome = RdpGrantOutcome.Refused, Message = message, Detail = detail,
    };

    public static RdpGrantResult Failed(string message, string detail = "") => new()
    {
        Outcome = RdpGrantOutcome.Failed, Message = message, Detail = detail,
    };
}

/// <summary>授权决策的结果：该加哪个组，或者为什么不该动。</summary>
public sealed class RdpGrantPlan
{
    public bool Allowed { get; init; }
    public string GroupSid { get; init; } = "";
    public bool NeedsAdminWarning { get; init; }
    public string Reason { get; init; } = "";
}

/// <summary>
/// 本机账户该进哪个组的**纯逻辑**（计划书 §8）。
/// </summary>
/// <remarks>
/// <para>抽成纯函数是为了能离线断言：这些规则错一条的后果都很重 ——
/// 把域账户当本机账户加组、在专业版上改授管理员、把 <c>DOMAIN\user</c> 截成 <c>user</c>
/// 后加到本机同名账户上。它们全都不报错，只会"权限给错了人"。</para>
/// <para>规则的来源是计划书 §8 的逐条要求，不是自己发明的策略。</para>
/// </remarks>
public static class RdpGrantPlanner
{
    /// <summary>
    /// 决定给这个账户加哪个组。
    /// </summary>
    /// <param name="accountName">通道里填的账户名，可能是 <c>user</c> / <c>MACHINE\user</c> / <c>DOMAIN\user</c> / UPN。</param>
    /// <param name="machineName">本机名。</param>
    /// <param name="edition">本机版本。</param>
    /// <param name="rdpGroupExists">本机是否存在 <c>S-1-5-32-555</c> 这个组。</param>
    /// <param name="adminGroupExists">本机是否存在 <c>S-1-5-32-544</c>。</param>
    public static RdpGrantPlan Plan(
        string? accountName,
        string machineName,
        RdpWindowsEdition edition,
        bool rdpGroupExists,
        bool adminGroupExists)
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
                return Deny($"「{prefix}」不是本机名，按计划书不对其它机器/域的账户做本机授权。");
            }
        }

        if (edition == RdpWindowsEdition.Unknown)
        {
            return Deny("读不出本机版本，按计划书停止自动授权（不改授管理员）。");
        }

        if (!rdpGroupExists && !RdpEditions.IsHomeFamily(edition))
        {
            // 专业版/企业版的 RDP 组是系统自带的；缺了说明系统被改过，报告异常比改授管理员安全
            return Deny("本机缺少 Remote Desktop Users 组（S-1-5-32-555）。这属于系统异常，"
                        + "按计划书不改授管理员权限。");
        }

        if (rdpGroupExists)
        {
            // 有 RDP 组就用它 —— 家庭版也一样（有些家庭版镜像保留了该组）
            return new RdpGrantPlan
            {
                Allowed = true,
                GroupSid = RdpEditions.RemoteDesktopUsersSid,
                Reason = $"加入 Remote Desktop Users（{bare}）",
            };
        }

        // 到这儿只剩「家庭版 + 缺 RDP 组」
        if (!adminGroupExists)
        {
            return Deny("家庭版本机既没有 Remote Desktop Users 也没有 Administrators 组，属系统异常。");
        }

        return new RdpGrantPlan
        {
            Allowed = true,
            GroupSid = RdpEditions.AdministratorsSid,
            NeedsAdminWarning = true,
            Reason = $"家庭版缺少远程桌面用户组，将把 {bare} 加入 Administrators（授予管理员权限）",
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
}
