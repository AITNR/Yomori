// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Yomori.Models;

/// <summary>
/// 注册表值的类型。<see cref="NotFound"/> 是一等公民 —— 计划书 5.2 要求
/// 「原值不存在」与「原值是空串」必须能区分：恢复时前者要删值，后者要写回空串。
/// </summary>
public enum RdpRegistryKind
{
    NotFound = 0,
    String = 1,
    ExpandString = 2,
    MultiString = 3,
    Dword = 4,
    Qword = 5,
    Binary = 6,
}

/// <summary>
/// 一个注册表值的无损快照。
/// </summary>
/// <remarks>
/// 为什么用一个「大而全」的类而不是 object 字段：快照要落 JSON，
/// 反序列化时 object 会变成 JsonElement，类型就丢了；而 EXPAND_SZ 必须保留**未展开**的原文
/// （计划书 5.2：显式 Registry64，EXPAND_SZ 保留未展开文本）。所以按类型分字段存，
/// <see cref="Kind"/> 决定读哪个字段。
/// </remarks>
public sealed class RdpRegistryValueState
{
    /// <summary>完整键路径，形如 <c>HKLM\SYSTEM\CurrentControlSet\Control\Terminal Server</c>。</summary>
    [JsonPropertyName("key_path")]
    public string KeyPath { get; init; } = "";

    /// <summary>值名。空串表示该键的默认值（本功能的白名单里没有默认值，但别让 API 假设）。</summary>
    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    [JsonPropertyName("kind")]
    public RdpRegistryKind Kind { get; init; } = RdpRegistryKind.NotFound;

    [JsonPropertyName("string_value")]
    public string StringValue { get; init; } = "";

    [JsonPropertyName("multi_values")]
    public IReadOnlyList<string> MultiValues { get; init; } = Array.Empty<string>();

    [JsonPropertyName("number_value")]
    public long NumberValue { get; init; }

    [JsonPropertyName("binary_base64")]
    public string BinaryBase64 { get; init; } = "";

    /// <summary>给界面/诊断看的一行摘要（不含敏感内容 —— 白名单里的值都不是敏感信息）。</summary>
    public string ToDisplay() => Kind switch
    {
        RdpRegistryKind.NotFound => "（不存在）",
        RdpRegistryKind.Dword => $"DWORD 0x{NumberValue:X}",
        RdpRegistryKind.Qword => $"QWORD 0x{NumberValue:X}",
        RdpRegistryKind.ExpandString => $"EXPAND_SZ {StringValue}",
        RdpRegistryKind.String => $"SZ {StringValue}",
        RdpRegistryKind.MultiString => $"MULTI_SZ [{string.Join(", ", MultiValues)}]",
        RdpRegistryKind.Binary => $"BINARY {BinaryBase64.Length} 字符 base64",
        _ => "（未知类型）",
    };
}

/// <summary>一个键值的「改动前后」对照。</summary>
public sealed class RdpRegistryDelta
{
    [JsonPropertyName("before")]
    public RdpRegistryValueState Before { get; init; } = new();

    [JsonPropertyName("after")]
    public RdpRegistryValueState After { get; set; } = new();

    [JsonPropertyName("applied")]
    public bool Applied { get; set; }

    [JsonPropertyName("verified")]
    public bool Verified { get; set; }

    [JsonPropertyName("conflict")]
    public bool Conflict { get; set; }

    [JsonPropertyName("note")]
    public string Note { get; set; } = "";

    public string Label => $"{Before.KeyPath}\\{Before.Name}";
}

/// <summary>事务种类。三件事各自独立记事务，绝不互相偷调（计划书 10）。</summary>
public enum RdpTransactionKind
{
    Unlock = 0,
    Uninstall = 1,
    RegistryRestore = 2,
}

/// <summary>
/// 事务状态。
/// </summary>
/// <remarks>
/// <see cref="Running"/> 留在盘上就是「崩溃或强杀留下的半截事务」——
/// 下次启动检测到它时不能直接覆盖，也不能同时回滚和新建操作（计划书 5.2 最后一条）。
/// </remarks>
public enum RdpTransactionStatus
{
    Running = 0,
    Completed = 1,
    Failed = 2,
    Compensated = 3,
    Abandoned = 4,
}

/// <summary>
/// 一次系统修改的完整账本。落在 <c>%ProgramData%\Yomori\rdp\transactions\&lt;id&gt;\manifest.json</c>。
/// </summary>
public sealed class RdpTransactionManifest
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("kind")]
    public RdpTransactionKind Kind { get; set; }

    [JsonPropertyName("status")]
    public RdpTransactionStatus Status { get; set; }

    [JsonPropertyName("started_at")]
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.Now;

    [JsonPropertyName("finished_at")]
    public DateTimeOffset? FinishedAt { get; set; }

    /// <summary>本轮锁定的 release 身份（计划书 4.1：一轮操作锁定同一份发布）。</summary>
    [JsonPropertyName("release_id")]
    public long ReleaseId { get; set; }

    [JsonPropertyName("release_tag")]
    public string ReleaseTag { get; set; } = "";

    [JsonPropertyName("installer_path")]
    public string InstallerPath { get; set; } = "";

    [JsonPropertyName("installer_sha256")]
    public string InstallerSha256 { get; set; } = "";

    [JsonPropertyName("uninstaller_path")]
    public string UninstallerPath { get; set; } = "";

    [JsonPropertyName("uninstaller_sha256")]
    public string UninstallerSha256 { get; set; } = "";

    /// <summary>安装前 rdpwrap.ini 的备份文件名（在事务目录内）。</summary>
    [JsonPropertyName("ini_backup")]
    public string IniBackup { get; set; } = "";

    /// <summary>安装前 INI 的绝对路径，恢复时写回原处。</summary>
    [JsonPropertyName("ini_source_path")]
    public string IniSourcePath { get; set; } = "";

    /// <summary>本次事务是否由 Yomori 自己装上了 Wrapper（决定失败时能不能做卸载补偿）。</summary>
    [JsonPropertyName("installed_by_us")]
    public bool InstalledByUs { get; set; }

    [JsonPropertyName("registry")]
    public List<RdpRegistryDelta> Registry { get; set; } = new();

    /// <summary>被本轮停止、需要恢复的服务名（原本在跑的那些）。</summary>
    [JsonPropertyName("services_touched")]
    public List<string> ServicesTouched { get; set; } = new();

    [JsonPropertyName("steps")]
    public List<string> Steps { get; set; } = new();

    [JsonPropertyName("failure")]
    public string Failure { get; set; } = "";

    /// <summary>首次基线不得被第二次操作覆盖（计划书 5.2）：还原时优先选最早的那份 Unlock 事务。</summary>
    public bool IsBaselineCandidate =>
        Kind == RdpTransactionKind.Unlock
        && Status is RdpTransactionStatus.Completed or RdpTransactionStatus.Failed or RdpTransactionStatus.Compensated;
}

/// <summary>事务目录里的一份备份（用于界面的「历史备份」列表）。</summary>
public sealed class RdpTransactionInfo
{
    public string Id { get; init; } = "";
    public RdpTransactionKind Kind { get; init; }
    public RdpTransactionStatus Status { get; init; }
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? FinishedAt { get; init; }
    public string ReleaseTag { get; init; } = "";
    public int RegistryCount { get; init; }
    public bool HasIniBackup { get; init; }
    public string Failure { get; init; } = "";

    public string KindLabel => Kind switch
    {
        RdpTransactionKind.Unlock => "解锁",
        RdpTransactionKind.Uninstall => "卸载",
        RdpTransactionKind.RegistryRestore => "还原注册表",
        _ => "未知",
    };

    public string StatusLabel => Status switch
    {
        RdpTransactionStatus.Running => "未完成（可能崩溃）",
        RdpTransactionStatus.Completed => "已完成",
        RdpTransactionStatus.Failed => "失败",
        RdpTransactionStatus.Compensated => "已补偿",
        RdpTransactionStatus.Abandoned => "已放弃",
        _ => "未知",
    };

    public string SummaryText =>
        $"{StartedAt:yyyy-MM-dd HH:mm}　{KindLabel}　{StatusLabel}"
        + (RegistryCount > 0 ? $"　{RegistryCount} 个键值" : "")
        + (HasIniBackup ? "　含 INI 备份" : "");

    public override string ToString() => SummaryText;
}

/// <summary>修改操作共用的一把锁的持有凭据。Dispose 即释放。</summary>
public sealed class RdpOperationLease : IDisposable
{
    private readonly Action _release;

    public RdpOperationLease(string name, Action release)
    {
        Name = name;
        _release = release;
    }

    public string Name { get; }

    public void Dispose() => _release();
}
