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

    /// <summary>
    /// 写入后回读到的实际值（序列化文本）。空串表示没回读或没写入。
    /// </summary>
    /// <remarks>
    /// 为什么把它放进账本而不是只留在内存里：崩溃恢复时要能看出「这个值到底变成什么了」。
    /// 只有 <see cref="Verified"/> 一个布尔时，恢复端只知道「不符」，不知道不符成了什么，
    /// 于是既不敢覆盖也没法解释（2026-10-26 检查记录 P1）。
    /// </remarks>
    [JsonPropertyName("read_back")]
    public string ReadBack { get; set; } = "";

    public string Label => $"{Before.KeyPath}\\{Before.Name}";
}

/// <summary>
/// 由本程序启动的上游工具进程（安装器 / 卸载器 / RDPWInst），用于超时后判断「它还在不在写文件」。
/// </summary>
/// <remarks>
/// <para>为什么必须记进账本而不是只在内存里留一个 PID：超时后进程会活过本次调用，
/// 甚至活过本进程（用户关掉 Yomori 再开）。下一次操作要么放行、要么拦住，
/// 只能靠账本里的记录判断 —— 按固定进程名全局扫描既会误拦同名进程，
/// 也覆盖不到被改名的副本与子进程（2026-10-26 检查记录 P0）。</para>
/// <para><see cref="StartTimeUtcTicks"/> 是防 PID 复用的：同一个 PID 被系统重新分配给别人时，
/// 启动时间对不上，就不算「我们那个工具还活着」。</para>
/// </remarks>
public sealed class RdpToolProcessRecord
{
    [JsonPropertyName("pid")]
    public int Pid { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("path")]
    public string Path { get; set; } = "";

    [JsonPropertyName("start_utc_ticks")]
    public long StartTimeUtcTicks { get; set; }

    /// <summary>
    /// 这条记录的角色。<see cref="RoleBlocking"/> 的进程还在跑时禁止新的系统修改；
    /// <see cref="RoleResidue"/> 只作报告（上游安装器拉起的 <c>RDP_CnC.exe</c> 会常驻）。
    /// </summary>
    [JsonPropertyName("role")]
    public string Role { get; set; } = RoleBlocking;

    /// <summary>会写系统文件的工具（阻塞后续操作）。</summary>
    public const string RoleBlocking = "blocking";

    /// <summary>不该拦流程、但要如实报告的残留（第三方界面进程）。</summary>
    public const string RoleResidue = "residue";
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

    /// <summary>
    /// 走了补偿，但补偿**没能证明**系统已回到安装前状态（卸载器退出码非零、或回读仍是 Wrapper）。
    /// </summary>
    /// <remarks>
    /// 2026-10-26 检查记录 P1：原实现只要补偿代码跑过就记 <see cref="Compensated"/>，
    /// 哪怕卸载器被安全软件拦下、ServiceDll 仍指向 rdpwrap.dll。界面于是显示「已补偿」，
    /// 用户以为干净了，下一步操作踩在半截状态上。
    /// </remarks>
    CompensationFailed = 5,
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

    /// <summary>
    /// 本轮有没有**真的动过** TermService（停或启）。
    /// </summary>
    /// <remarks>
    /// 补偿流程原先是无条件 <c>Restart("TermService")</c>：即使失败发生在「更新 INI」这一步、
    /// 服务根本没被碰过，也会停一次再起一次。后果不只是多余：停服务是有破坏性的动作
    /// （会打断远程会话、清掉监听），而且它正是 2026-10-26 那次崩溃的入口。
    /// 所以判据改成「动过才恢复」。
    /// </remarks>
    [JsonPropertyName("service_touched")]
    public bool ServiceTouched { get; set; }

    /// <summary>本轮由本程序启动的上游工具进程（见 <see cref="RdpToolProcessRecord"/>）。</summary>
    [JsonPropertyName("tool_processes")]
    public List<RdpToolProcessRecord> ToolProcesses { get; set; } = new();

    /// <summary>
    /// 事务目录的 ACL 是否已确认收紧到「只有 SYSTEM 与 Administrators 可写」。
    /// </summary>
    /// <remarks>
    /// 2026-10-26 检查记录 P2：原实现调用收紧后把返回值丢掉。做不到「备份只有管理员能写」
    /// 却照样改系统，等于把恢复材料放在一个别人能改的地方。这里记下来，
    /// 由流程在**第一次系统修改之前**决定是否继续。
    /// </remarks>
    [JsonPropertyName("backup_acl_verified")]
    public bool BackupAclVerified { get; set; }

    /// <summary>
    /// 恢复性账本写入是否失败过。为真时这次操作是「系统可能已动过、账本不完整」，
    /// 不允许自动恢复覆盖，必须在界面上显式说明。
    /// </summary>
    /// <remarks>内存标记优先；能落盘时一并写进 JSON，供下次启动识别。</remarks>
    [JsonPropertyName("ledger_incomplete")]
    public bool LedgerIncomplete { get; set; }

    [JsonPropertyName("ledger_failure")]
    public string LedgerFailure { get; set; } = "";

    [JsonPropertyName("steps")]
    public List<string> Steps { get; set; } = new();

    [JsonPropertyName("failure")]
    public string Failure { get; set; } = "";

    /// <summary>
    /// 这份事务能不能当「还原注册表」的基线。
    /// </summary>
    /// <remarks>
    /// <para>判据只看两件事：是不是解锁事务、有没有记到注册表原值。<b>不看最终状态</b>。</para>
    /// <para>理由：快照是在任何写入<b>之前</b>记下的，事务后来走完没走完、有没有补偿，都不改变
    /// 「这一刻注册表长什么样」这个事实。恰恰相反 —— <see cref="RdpTransactionStatus.Running"/>
    /// （装到一半崩了/超时）那份才是用户最需要拿来还原的：那时候系统已经被改过一部分了。</para>
    /// <para>2026-10-26 真机事故：只把 Completed/Failed/Compensated 当候选，导致唯一那份
    /// Running 事务既挡住重装、又挡住卸载、还不能当还原基线 —— 三条路全堵死，
    /// 用户除了手工删 <c>%ProgramData%</c> 没有任何出口。</para>
    /// </remarks>
    public bool IsBaselineCandidate =>
        Kind == RdpTransactionKind.Unlock && Registry.Count > 0;
}

/// <summary>
/// 对一条「没走完」的事务该怎么处理的结论。纯数据，不碰磁盘。
/// </summary>
/// <remarks>
/// 抽成纯函数是为了能被断言：这段判据 2026-10-26 真机把三条出路全堵死过一次
/// （解锁拦、卸载拦、还原找不到基线），而它出错时<strong>没有任何异常</strong>，
/// 只是界面上一句「上一次操作没有收尾」。
/// </remarks>
public readonly record struct RdpStaleDecision(
    bool Blocked,
    string BlockReason,
    RdpTransactionStatus CloseAs,
    string Note)
{
    public static RdpStaleDecision Halt(string reason) =>
        new(true, reason, RdpTransactionStatus.Running, "");

    public static RdpStaleDecision Close(RdpTransactionStatus status, string note) =>
        new(false, "", status, note);
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
        RdpTransactionStatus.CompensationFailed => "补偿未完成（需人工处理）",
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
