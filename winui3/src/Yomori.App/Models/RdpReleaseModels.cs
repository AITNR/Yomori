// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Serialization;

namespace Yomori.Models;

/// <summary>上游 Releases 里的一个附件。</summary>
public sealed class RdpAsset
{
    [JsonPropertyName("id")]
    public long Id { get; init; }

    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    [JsonPropertyName("size")]
    public long Size { get; init; }

    /// <summary>官方给的 digest，形如 <c>sha256:abc...</c>；没给就是空串（不能假装校验过）。</summary>
    [JsonPropertyName("digest")]
    public string Digest { get; init; } = "";

    [JsonPropertyName("browser_download_url")]
    public string DownloadUrl { get; init; } = "";

    public string Sha256FromDigest
    {
        get
        {
            var text = (Digest ?? string.Empty).Trim();
            var index = text.IndexOf(':');
            if (index >= 0)
            {
                var algorithm = text.Substring(0, index).Trim();
                if (!algorithm.Equals("sha256", StringComparison.OrdinalIgnoreCase))
                {
                    return "";
                }

                text = text.Substring(index + 1).Trim();
            }

            return text.Length == 64 ? text.ToLowerInvariant() : "";
        }
    }
}

/// <summary>
/// 一份上游发布 + 从里面挑出来的工具。
/// </summary>
/// <remarks>
/// 计划书 4.1 要求「一轮操作锁定同一 release ID、tag、资产 ID 和链接」，
/// 所以这里把发布与选中的资产放在同一个对象里一起传，不给调用方「再查一次」的机会。
/// </remarks>
public sealed class RdpReleaseInfo
{
    [JsonPropertyName("id")]
    public long Id { get; init; }

    [JsonPropertyName("tag_name")]
    public string Tag { get; init; } = "";

    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    [JsonPropertyName("html_url")]
    public string PageUrl { get; init; } = "";

    [JsonPropertyName("draft")]
    public bool IsDraft { get; init; }

    [JsonPropertyName("prerelease")]
    public bool IsPrerelease { get; init; }

    [JsonPropertyName("published_at")]
    public DateTimeOffset? PublishedAt { get; init; }

    [JsonPropertyName("assets")]
    public IReadOnlyList<RdpAsset> Assets { get; init; } = Array.Empty<RdpAsset>();

    /// <summary>按官方资产名挑出来的安装器；挑不出来为 null。</summary>
    public RdpAsset? Installer => PickSingle(a => IsInstallerName(a.Name));

    /// <summary>按官方资产名挑出来的配套卸载器；挑不出来为 null。</summary>
    public RdpAsset? Uninstaller => PickSingle(a => IsUninstallerName(a.Name));

    /// <summary>
    /// 资产挑选是否可用。不可用时 <see cref="SelectionError"/> 说明原因（缺资产 / 多个无法区分）。
    /// </summary>
    /// <remarks>
    /// 不猜名字是硬要求：计划书 4.1 明确「缺失或出现多个无法区分的资产则失败并展示官方发布页，
    /// 不猜名称或执行其他附件」。所以这里一旦有歧义就整体失败，不做「凑一个最像的」。
    /// </remarks>
    public bool TrySelect(out RdpAsset installer, out RdpAsset uninstaller, out string error)
    {
        installer = Installer!;
        uninstaller = Uninstaller!;
        error = "";

        if (IsDraft || IsPrerelease)
        {
            error = "该发布是草稿或预发布，不能作为安装来源";
            return false;
        }

        if (Assets.Count == 0)
        {
            error = "该发布没有任何附件";
            return false;
        }

        var installers = Assets.Where(a => IsInstallerName(a.Name)).ToList();
        var uninstallers = Assets.Where(a => IsUninstallerName(a.Name)).ToList();

        if (installers.Count == 0)
        {
            error = "找不到安装器附件（RDPW_Installer.exe）";
            return false;
        }

        if (installers.Count > 1)
        {
            error = "安装器附件有多个且无法区分：" + string.Join(", ", installers.Select(a => a.Name));
            return false;
        }

        if (uninstallers.Count == 0)
        {
            error = "找不到配套卸载器附件（RDPW_Uninstaller.exe）；按计划书 4.1 不混配其他版本";
            return false;
        }

        if (uninstallers.Count > 1)
        {
            error = "卸载器附件有多个且无法区分：" + string.Join(", ", uninstallers.Select(a => a.Name));
            return false;
        }

        installer = installers[0];
        uninstaller = uninstallers[0];

        if (installer.DownloadUrl.Length == 0)
        {
            error = "安装器附件没有下载地址";
            return false;
        }

        if (uninstaller.DownloadUrl.Length == 0)
        {
            error = "卸载器附件没有下载地址";
            return false;
        }

        return true;
    }

    /// <summary>
    /// 资产名判定：先排除卸载器再认安装器 —— <c>RDPW_Uninstaller.exe</c> 里也含 "installer"，
    /// 顺序写反会把卸载器当安装器下载执行。
    /// </summary>
    public static bool IsInstallerName(string? name)
    {
        var text = (name ?? "").Trim();
        return text.Length > 0
               && text.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
               && text.Contains("install", StringComparison.OrdinalIgnoreCase)
               && !text.Contains("uninstall", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsUninstallerName(string? name)
    {
        var text = (name ?? "").Trim();
        return text.Length > 0
               && text.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
               && text.Contains("uninstall", StringComparison.OrdinalIgnoreCase);
    }

    private RdpAsset? PickSingle(Func<RdpAsset, bool> predicate)
    {
        RdpAsset? found = null;
        foreach (var asset in Assets)
        {
            if (!predicate(asset))
            {
                continue;
            }

            if (found is not null)
            {
                return null;   // 多个候选：宁可没有，也不随便挑一个
            }

            found = asset;
        }

        return found;
    }
}

/// <summary>下载 + 校验之后得到的本地工具。</summary>
public sealed class RdpDownloadedTool
{
    public required RdpAsset Asset { get; init; }
    public required string LocalPath { get; init; }
    public required string Sha256 { get; init; }

    public long SizeBytes { get; init; }

    /// <summary>官方是否给了可用的 sha256 digest —— 决定结果里能不能说「已按官方摘要校验」。</summary>
    public bool VerifiedAgainstOfficialDigest { get; init; }

    /// <summary>校验能力的局限说明（无 digest / 无签名时如实写出来）。</summary>
    public string Limits { get; init; } = "";
}

/// <summary>单个步骤的结果。</summary>
public enum RdpStepOutcome
{
    Pending = 0,
    Ok = 1,
    Warn = 2,
    Fail = 3,
    Skipped = 4,
}

/// <summary>步骤记录（界面逐行显示，也进诊断文本）。</summary>
public sealed class RdpStepRecord
{
    public string Key { get; init; } = "";
    public string Title { get; init; } = "";
    public RdpStepOutcome Outcome { get; init; }
    public string Detail { get; init; } = "";
    public string ErrorCode { get; init; } = "";
    public DateTimeOffset At { get; init; } = DateTimeOffset.Now;

    public string ToLine()
    {
        var mark = Outcome switch
        {
            RdpStepOutcome.Ok => "通过",
            RdpStepOutcome.Warn => "注意",
            RdpStepOutcome.Fail => "失败",
            RdpStepOutcome.Skipped => "跳过",
            _ => "未执行",
        };

        var sb = new StringBuilder($"[{mark}] {Title}");
        if (Detail.Length > 0)
        {
            sb.Append(" —— ").Append(Detail);
        }

        if (ErrorCode.Length > 0)
        {
            sb.Append("（").Append(ErrorCode).Append('）');
        }

        return sb.ToString();
    }
}

/// <summary>给界面用的进度。</summary>
public readonly record struct RdpProgressInfo(int Step, int Total, string Text);

/// <summary>
/// 一次修改操作的请求。
/// </summary>
/// <remarks>
/// <see cref="AntivirusConfirmed"/> 是「人工确认门禁」的唯一凭据：为 false 时
/// 服务层**不允许**发起任何 metadata 请求或下载（计划书 3.2 最后一条）。
/// 由界面在用户勾选并点确定之后置 true —— 服务层不检测、不验证杀软状态。
/// </remarks>
public sealed class RdpUnlockRequest
{
    public required bool AntivirusConfirmed { get; init; }

    /// <summary>
    /// 用户手动指定的安装包（本机路径）。空串表示按官方发布下载。
    /// </summary>
    /// <remarks>
    /// 存在的理由：GitHub 的 Release 附件在国内网络下经常拿不到完整文件
    /// （2026-10-26 真机报「不是有效的 PE 文件」，实际是被拦成了网页）。
    /// 这时让用户用浏览器自己下一个，再指给程序，比反复重试有意义。
    /// 手动指定的文件仍然要做 PE 校验与哈希记录，只是不能和官方 digest 比对。
    /// </remarks>
    public string ManualInstallerPath { get; init; } = "";

    /// <summary>是否在安装后更新 INI（<c>RDPWInst -w</c>）。</summary>
    public bool UpdateIni { get; init; } = true;

    /// <summary>是否在 INI 更新后显式重启 TermService（计划书 6.2 的既定要求）。</summary>
    public bool RestartService { get; init; } = true;
}

/// <summary>一次修改操作的结果。</summary>
public sealed class RdpOperationResult
{
    public bool Success { get; init; }
    public string Summary { get; init; } = "";
    public string Detail { get; init; } = "";
    public IReadOnlyList<RdpStepRecord> Steps { get; init; } = Array.Empty<RdpStepRecord>();
    public RdpUnlockSnapshot? Final { get; init; }
    public string TransactionId { get; init; } = "";

    /// <summary>本轮曾人工确认下载 → 结果里必须提醒重新开启安全防护（不检测是否已开启）。</summary>
    public bool NeedsProtectionReminder { get; init; }

    public static RdpOperationResult Fail(string summary, IEnumerable<RdpStepRecord>? steps = null, string detail = "") =>
        new()
        {
            Success = false,
            Summary = summary,
            Detail = detail,
            Steps = steps?.ToList() ?? new List<RdpStepRecord>(),
        };

    /// <summary>长文本诊断：逐步列出，末尾给最终检测。可能为空（例如在预检就失败）。</summary>
    public string FormatReport()
    {
        var sb = new StringBuilder();
        sb.AppendLine(Summary);

        if (TransactionId.Length > 0)
        {
            sb.AppendLine($"事务：{TransactionId}");
        }

        if (Steps.Count > 0)
        {
            sb.AppendLine();
            foreach (var step in Steps)
            {
                sb.AppendLine(step.ToLine());
            }
        }

        if (Detail.Length > 0)
        {
            sb.AppendLine();
            sb.AppendLine(Detail);
        }

        return sb.ToString();
    }
}
