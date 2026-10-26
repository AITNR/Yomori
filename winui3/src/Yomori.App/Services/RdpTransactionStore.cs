// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using Yomori.Models;

namespace Yomori.Services;

/// <summary>
/// 事务账本的落盘与读取。
/// </summary>
/// <remarks>
/// <para>目录约定（计划书 5.2）：<c>%ProgramData%\Yomori\rdp\transactions\&lt;GUID&gt;\</c>，
/// 内含 <c>manifest.json</c>、INI 备份与（后续里程碑的）日志。</para>
/// <para>为什么每一步都要落盘而不是最后统一写：崩溃/断电之后要能判断「写到哪了」。
/// 只写最终状态的账本，一旦中途崩溃就只剩一份「未开始」的记录，等于什么都没记。</para>
/// <para>为什么 <see cref="RdpTransactionStatus.Running"/> 会长期留在盘上：那是上一次没走完的
/// 半截事务。它既不能当成功，也不能直接删 —— 下次操作前要能看见它并提示，
/// 否则「同时回滚和新建操作」这种最容易把系统搞坏的动作就会被顺手做出来。</para>
/// </remarks>
public static class RdpTransactionStore
{
    public const string ManifestFileName = "manifest.json";
    public const string IniBackupFileName = "rdpwrap.ini.bak";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        // 中文备注与失败原因要能直接读，不要被转成 \uXXXX
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>事务根目录。<c>%ProgramData%\Yomori\rdp\transactions</c>。</summary>
    public static string RootDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "Yomori", "rdp", "transactions");

    public static string ToolsDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "Yomori", "rdp", "tools");

    /// <summary>新建一个事务并立刻落盘（状态 Running）。</summary>
    public static RdpTransactionManifest Begin(RdpTransactionKind kind)
    {
        var manifest = new RdpTransactionManifest
        {
            Id = DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N").Substring(0, 8),
            Kind = kind,
            Status = RdpTransactionStatus.Running,
            StartedAt = DateTimeOffset.Now,
        };

        var dir = DirectoryFor(manifest.Id);
        Directory.CreateDirectory(dir);
        TryHardenDirectory(dir);

        Save(manifest);
        return manifest;
    }

    /// <summary>
    /// 把账本写到盘上。先写临时文件再替换 —— 半写的 manifest.json 比没有更难查。
    /// </summary>
    public static void Save(RdpTransactionManifest manifest)
    {
        var dir = DirectoryFor(manifest.Id);
        Directory.CreateDirectory(dir);

        var path = Path.Combine(dir, ManifestFileName);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(manifest, JsonOptions));

        if (File.Exists(path))
        {
            File.Replace(temp, path, null);
        }
        else
        {
            File.Move(temp, path);
        }
    }

    public static RdpTransactionManifest? Load(string id)
    {
        if (!IsValidId(id))
        {
            // 拒绝路径穿越：id 只允许本类自己生成的那种形状
            return null;
        }

        var path = Path.Combine(DirectoryFor(id), ManifestFileName);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<RdpTransactionManifest>(json, JsonOptions);
        }
        catch
        {
            // 账本损坏：当作不存在，但目录留着（里面可能还有 INI 备份）
            return null;
        }
    }

    /// <summary>所有事务，新的在前。</summary>
    public static IReadOnlyList<RdpTransactionInfo> List()
    {
        var result = new List<RdpTransactionInfo>();

        if (!Directory.Exists(RootDir))
        {
            return result;
        }

        foreach (var dir in Directory.GetDirectories(RootDir))
        {
            var manifest = Load(Path.GetFileName(dir));
            if (manifest is null)
            {
                continue;
            }

            result.Add(ToInfo(manifest));
        }

        result.Sort((a, b) => b.StartedAt.CompareTo(a.StartedAt));
        return result;
    }

    /// <summary>上一次没走完的事务（崩溃/强杀留下的）。空表示干净。</summary>
    public static IReadOnlyList<RdpTransactionInfo> ListIncomplete()
    {
        var result = new List<RdpTransactionInfo>();
        foreach (var info in List())
        {
            if (info.Status == RdpTransactionStatus.Running)
            {
                result.Add(info);
            }
        }

        return result;
    }

    /// <summary>
    /// 默认要还原的那份基线：最早的、含注册表快照的解锁事务。
    /// </summary>
    /// <remarks>
    /// 为什么取最早：计划书 5.2 明确「首次基线不得被第二次解锁后的状态覆盖」。
    /// 第二次解锁记下的是「第一次解锁后」的状态，拿它还原等于把系统退回到「已解锁」，
    /// 用户会以为自己点了还原却什么都没变。
    /// </remarks>
    public static RdpTransactionInfo? FindFirstBaseline()
    {
        RdpTransactionInfo? found = null;

        if (!Directory.Exists(RootDir))
        {
            return null;
        }

        foreach (var dir in Directory.GetDirectories(RootDir))
        {
            var manifest = Load(Path.GetFileName(dir));
            if (manifest is null || !manifest.IsBaselineCandidate || manifest.Registry.Count == 0)
            {
                continue;
            }

            if (found is null || manifest.StartedAt < found.StartedAt)
            {
                found = ToInfo(manifest);
            }
        }

        return found;
    }

    /// <summary>
    /// 处理上一次没走完的事务（崩溃 / 超时 / 强杀留下的 <see cref="RdpTransactionStatus.Running"/>）。
    /// </summary>
    /// <remarks>
    /// <para>计划书 5.2 的原意是「子进程尚在运行时禁止同时回滚和新建操作」—— 那是真风险，必须拦住。
    /// 但 2026-10-26 真机出现过另一种：安装器其实已经装成功，只是没退出，于是事务停在 Running，
    /// 用户再点一次就被「上一次操作没有收尾」挡死，而系统其实已经是好的。</para>
    /// <para>所以这里分两档：工具还在跑 → 拦；工具不在了、且**当前状态已经就是那笔事务的目标状态** →
    /// 自动结案（标记完成并写明原因）后继续。目标状态对不上、或者账本损坏、或者是注册表还原事务
    /// （中途崩了看不出做到哪）→ 一律拦，交给人看诊断。</para>
    /// </remarks>
    public static (bool Blocked, string Message) ResolveStale(RdpUnlockSnapshot current)
    {
        var incomplete = RdpTransactionStore.ListIncomplete();
        if (incomplete.Count == 0)
        {
            return (false, "");
        }

        if (RdpToolRunner.IsToolRunning())
        {
            // 真正的危险场景：安装器/卸载器进程还活着，此时任何回滚都可能与它抢文件
            return (true, "上一次的安装器/卸载器进程还在运行。请等它自己结束（不要去结束它），然后再试。");
        }

        var closed = new List<string>();

        foreach (var info in incomplete)
        {
            var manifest = RdpTransactionStore.Load(info.Id);
            if (manifest is null)
            {
                return (true, $"事务 {info.Id} 的账本读不出来（文件损坏），无法判断它做到了哪一步。");
            }

            var reachedTarget = manifest.Kind switch
            {
                RdpTransactionKind.Unlock => current.WrapperState == RdpWrapperState.Installed,
                RdpTransactionKind.Uninstall => current.WrapperState != RdpWrapperState.Installed,
                // 还原注册表：没有明确的"目标状态"可对照，保持拦截
                _ => false,
            };

            if (!reachedTarget)
            {
                return (true, $"{info.SummaryText}（id={info.Id}）没有收尾，且当前状态不是它的目标状态。"
                              + "请点「查看诊断」确认它做到哪一步，再决定是重试还是手工清理。");
            }

            RdpTransactionStore.AppendStep(manifest, "自动结案：执行被中断，但本机状态已是该事务的目标状态");
            RdpTransactionStore.Finish(
                manifest,
                RdpTransactionStatus.Completed,
                "自动结案：执行中断（超时/崩溃），但目标状态已达成");

            closed.Add($"{info.Id}（{info.KindLabel}）");
        }

        return closed.Count == 0
            ? (false, "")
            : (false, "已自动结案：" + string.Join("、", closed) + "（执行被中断，但状态已达成目标）");
    }

    /// <summary>把 INI 备份进事务目录。返回备份文件名（不含目录）；源文件不存在返回空串。</summary>
    public static string BackupIni(string id, string iniPath)
    {
        if (!IsValidId(id) || string.IsNullOrWhiteSpace(iniPath) || !File.Exists(iniPath))
        {
            return "";
        }

        try
        {
            var target = Path.Combine(DirectoryFor(id), IniBackupFileName);
            File.Copy(iniPath, target, overwrite: true);
            return IniBackupFileName;
        }
        catch
        {
            return "";
        }
    }

    /// <summary>
    /// 把事务里的 INI 备份写回原处。返回 (是否成功, 说明)。
    /// </summary>
    public static (bool Ok, string Message) RestoreIni(RdpTransactionManifest manifest)
    {
        if (manifest.IniBackup.Length == 0 || manifest.IniSourcePath.Length == 0)
        {
            return (false, "该事务没有 INI 备份");
        }

        var backup = Path.Combine(DirectoryFor(manifest.Id), Path.GetFileName(manifest.IniBackup));
        if (!File.Exists(backup))
        {
            return (false, $"备份文件不存在：{backup}");
        }

        try
        {
            var target = manifest.IniSourcePath;

            // 目标目录可能整个都没了（用户手工删了安装目录）：这时候不该凭空造一个
            var directory = Path.GetDirectoryName(target);
            if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
            {
                return (false, $"INI 原目录不存在，未写回：{directory}");
            }

            File.Copy(backup, target, overwrite: true);

            // 回读比对：写进去的和备份不是同一份就别说恢复成功
            var expected = Sha256File(backup);
            var actual = Sha256File(target);
            return expected.Length > 0 && expected == actual
                ? (true, $"已写回 {target}")
                : (false, "写回后哈希不一致");
        }
        catch (Exception ex)
        {
            return (false, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// 追加一步到账本并立刻落盘。
    /// </summary>
    /// <remarks>
    /// 这里**吞掉**写盘异常：账本是诊断记录，写到一半时磁盘满了不该把一次已经动了注册表的
    /// 操作中途打断 —— 那会跳过补偿，留下比"少一行日志"严重得多的后果。
    /// 与之相对，流程开始前的 <see cref="Begin"/> 必须严格失败（备份失败不得开始系统变更）。
    /// </remarks>
    public static void AppendStep(RdpTransactionManifest manifest, string line)
    {
        manifest.Steps.Add($"{DateTimeOffset.Now:HH:mm:ss} {line}");

        try
        {
            Save(manifest);
        }
        catch
        {
            // 见上面的说明：账本写不下去也得让流程继续，否则补偿链会被一个 IO 错误掐断
        }
    }

    /// <summary>收尾：写最终状态、结束时间与失败原因。写盘同样是尽力而为。</summary>
    public static void Finish(RdpTransactionManifest manifest, RdpTransactionStatus status, string failure = "")
    {
        manifest.Status = status;
        manifest.FinishedAt = DateTimeOffset.Now;
        manifest.Failure = failure;

        try
        {
            Save(manifest);
        }
        catch
        {
            // 同上
        }
    }

    public static string DirectoryFor(string id) => Path.Combine(RootDir, id);

    /// <summary>
    /// 事务 id 的合法性。只允许「数字 + 字母 + 连字符」，且不含路径分隔符与 <c>..</c>。
    /// </summary>
    /// <remarks>
    /// 账本目录名来自 id，而 id 会被界面/命令行带回来。不校验就等于把
    /// <c>..\..\Windows\System32</c> 这类路径交给 <see cref="DirectoryFor"/>（计划书 5.2 拒绝路径穿越）。
    /// </remarks>
    public static bool IsValidId(string? id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 64 || id.Contains(".."))
        {
            return false;
        }

        foreach (var ch in id)
        {
            if (!char.IsLetterOrDigit(ch) && ch != '-')
            {
                return false;
            }
        }

        return true;
    }

    public static string Sha256File(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        }
        catch
        {
            return "";
        }
    }

    private static RdpTransactionInfo ToInfo(RdpTransactionManifest manifest) => new()
    {
        Id = manifest.Id,
        Kind = manifest.Kind,
        Status = manifest.Status,
        StartedAt = manifest.StartedAt,
        FinishedAt = manifest.FinishedAt,
        ReleaseTag = manifest.ReleaseTag,
        RegistryCount = manifest.Registry.Count,
        HasIniBackup = manifest.IniBackup.Length > 0
                       && File.Exists(Path.Combine(DirectoryFor(manifest.Id), IniBackupFileName)),
        Failure = manifest.Failure,
    };

    /// <summary>
    /// 目录权限收紧到「只有 SYSTEM 与 Administrators 可写」（计划书 5.2）。
    /// </summary>
    /// <remarks>
    /// 尽力而为：ProgramData 下的子目录默认就只有管理员能写，这里把继承断掉再显式给两条规则，
    /// 是为了防止将来有人把 Yomori 目录的 ACL 放宽之后备份跟着裸奔。
    /// 失败不抛 —— 权限收紧失败不该阻断「先备份再改系统」这件更重要的事，但要留痕（见返回值调用方的日志）。
    /// </remarks>
    public static bool TryHardenDirectory(string dir)
    {
        try
        {
            var info = new DirectoryInfo(dir);
            var security = info.GetAccessControl();

            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

            const InheritanceFlags inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;

            security.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));

            security.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
                FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));

            info.SetAccessControl(security);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
