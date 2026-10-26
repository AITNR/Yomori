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
    /// <remarks>
    /// ACL 收紧的结果记进账本而不是丢掉（2026-10-26 检查记录 P2）：
    /// 由调用方在**第一次系统修改之前**决定要不要继续 ——
    /// 「备份放在谁都能改的目录里」不能靠一句注释就算合规。
    /// </remarks>
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
        manifest.BackupAclVerified = TryHardenDirectory(dir);

        Save(manifest);
        return manifest;
    }

    /// <summary>
    /// 恢复性写入：**失败必须让调用方知道**。
    /// </summary>
    /// <remarks>
    /// <para>与 <see cref="AppendStep"/> 的差别就是「诊断日志」与「恢复证据」的差别
    /// （2026-10-26 检查记录 P0）：追加一行说明写不进去可以忍，但「目标值已写入」
    /// 「这一步已验证」这类信息写不进去，崩溃后就再也没人知道系统被改成了什么样。</para>
    /// <para>失败时同时把标记打进内存里的 manifest：即使盘上一片空白，
    /// 本次操作的结果也能如实告诉用户「系统可能已动过、账本不完整」。</para>
    /// </remarks>
    public static bool TrySaveCritical(RdpTransactionManifest manifest, out string error)
    {
        try
        {
            Save(manifest);
            error = "";
            return true;
        }
        catch (Exception ex)
        {
            error = $"{ex.GetType().Name}: {ex.Message}";
            MarkLedgerIncomplete(manifest, error);
            return false;
        }
    }

    /// <summary>在内存里记下「账本已不完整」，并尽力再写一次（可能同样失败）。</summary>
    private static void MarkLedgerIncomplete(RdpTransactionManifest manifest, string error)
    {
        manifest.LedgerIncomplete = true;
        manifest.LedgerFailure = error;

        try
        {
            Save(manifest);
        }
        catch
        {
            // 连标记都写不下去：只能靠内存里的状态把话说给用户听
        }
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
    /// <para><b>结论：遗留事务一律放行并就地结案，只有工具进程真的还在跑时才拦。</b>
    /// 早先的实现还要求「当前状态已达成该事务的目标状态」才放行，结果是装失败一次就永久卡死：
    /// 用户既不能重装（解锁拦）、也不能卸载（卸载也调这里）、连还原注册表都找不到基线 ——
    /// 三条路全堵，唯一出口是手工删 <c>%ProgramData%</c>。这违背了「失败要能退出」的基本要求。</para>
    /// <para>放行的安全性来自两件事：① 账本与快照从不删除，历史基线一直在；
    /// ② 每一步系统变更都写账本，失败会走补偿。所以「结案」只是停止把它当未完成，不是抹掉证据。</para>
    /// </remarks>
    /// <returns>
    /// <c>Blocked</c> 为真时 <c>Message</c> 说明为什么不能开始；否则 <c>Message</c> 非空表示
    /// 已经替用户结掉了一些遗留事务，调用方应把它作为一条「注意」级步骤展示出来。
    /// </returns>
    public static (bool Blocked, string Message) ResolveStale(RdpUnlockSnapshot current)
    {
        var incomplete = RdpTransactionStore.ListIncomplete();
        if (incomplete.Count == 0)
        {
            return (false, "");
        }

        // 唯一的硬拦：安装器/卸载器进程还活着，此时任何回滚都可能与它抢文件（计划书 5.2）。
        // 判据先按**账本记过的 PID + 启动时间**走（能覆盖被改名的副本与子进程），
        // 再退回按固定进程名扫一遍兜底（2026-10-26 检查记录 P0）。
        var recordedAlive = AnyRecordedToolAlive(incomplete, out var recordedDetail);
        var toolRunning = recordedAlive || RdpToolRunner.IsToolRunning();
        var gate = DecideStale(RdpTransactionKind.Unlock, current, toolRunning);
        if (gate.Blocked)
        {
            return (true, recordedAlive ? gate.BlockReason + "　正在运行：" + recordedDetail : gate.BlockReason);
        }

        var notes = new List<string>();

        foreach (var info in incomplete)
        {
            var manifest = RdpTransactionStore.Load(info.Id);
            if (manifest is null)
            {
                // 账本读不出来也要放行：拦住用户对他没有任何帮助，而快照文件本身还在
                notes.Add($"事务 {info.Id} 的账本读不出来（文件损坏），已忽略；快照仍在 {RootDir} 下。");
                continue;
            }

            // 账本本身不完整（写盘失败过）：知道系统被改过，却不知道改到哪一步。
            // 这种事务不自动恢复、也不重复拦人，只如实结案并留下原因（检查记录 P0）。
            if (manifest.LedgerIncomplete)
            {
                var reason = $"账本记录不完整（{manifest.LedgerFailure}），未做任何自动恢复";
                RdpTransactionStore.Finish(manifest, RdpTransactionStatus.Failed, reason);
                notes.Add($"{info.Id}（{info.KindLabel}）{reason}；系统可能已被修改过一部分，请按诊断人工核对");
                continue;
            }

            var decision = DecideStale(manifest.Kind, current, toolRunning);
            if (decision.Blocked)
            {
                return (true, decision.BlockReason);
            }

            RdpTransactionStore.AppendStep(manifest, "自动结案：" + decision.Note);
            RdpTransactionStore.Finish(manifest, decision.CloseAs, decision.Note);

            notes.Add($"{info.Id}（{info.KindLabel}）{decision.Note}");
        }

        return (false, notes.Count == 0
            ? ""
            : "已收尾上一次未完成的操作：" + string.Join("；", notes) + "。");
    }

    /// <summary>
    /// 未完成事务里记录的**阻塞型**工具进程是否还有活着的。读不出账本的一律不计（放行优先）。
    /// </summary>
    private static bool AnyRecordedToolAlive(IReadOnlyList<RdpTransactionInfo> incomplete, out string detail)
    {
        var alive = new List<string>();

        foreach (var info in incomplete)
        {
            var manifest = RdpTransactionStore.Load(info.Id);
            if (manifest is null)
            {
                continue;
            }

            if (RdpToolProcessTree.AnyBlockingAlive(manifest.ToolProcesses, out var text) && text.Length > 0)
            {
                alive.Add($"事务 {info.Id}：{text}");
            }
        }

        detail = string.Join("；", alive);
        return detail.Length > 0;
    }

    /// <summary>
    /// 一条没走完的事务该怎么收尾 —— 纯判据，不读盘、不写盘。
    /// </summary>
    /// <remarks>
    /// <para>只有一种情况允许拦截：工具进程还在跑（可能与回滚抢文件）。其余一律放行并结案。</para>
    /// <para>目标状态是否达成只影响<strong>记成完成还是失败</strong>，不影响<strong>放不放行</strong>。
    /// 早先的实现把这两件事混在一起，于是「装失败」= 「以后永远不能再操作」——
    /// 2026-10-26 真机就是这样把解锁、卸载、还原三条路一起堵死的。</para>
    /// </remarks>
    public static RdpStaleDecision DecideStale(
        RdpTransactionKind kind,
        RdpUnlockSnapshot current,
        bool toolRunning)
    {
        if (toolRunning)
        {
            return RdpStaleDecision.Halt(
                "上一次的安装器/卸载器进程还在运行（RDPWInst.exe 或 RDPW_Installer.exe）。"
                + "请等它自行结束；如果它明显卡住不动，重启电脑是最稳妥的办法"
                + "（不要手工结束它 —— 它正在往系统目录写文件）。");
        }

        var reachedTarget = kind switch
        {
            RdpTransactionKind.Unlock => current.WrapperState == RdpWrapperState.Installed,
            // 卸载：只有**明确**回到「未安装」才算达成。Unknown/ThirdParty 都不算 ——
            // 读不出 ServiceDll 与「已经解除接管」是两件事（2026-10-26 检查记录 P1）。
            RdpTransactionKind.Uninstall => current.WrapperState == RdpWrapperState.NotInstalled,
            // 还原注册表：没有「目标状态」可对照，按「没有可确认的残留」结案
            _ => true,
        };

        return reachedTarget
            ? RdpStaleDecision.Close(
                RdpTransactionStatus.Completed,
                "执行被中断，但本机状态已是该事务的目标状态，记为完成")
            : RdpStaleDecision.Close(
                RdpTransactionStatus.Failed,
                "执行被中断且未达成目标状态，记为失败（快照保留，可用于还原注册表）");
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
    /// 读事务里的 INI 备份正文（只读，不改动）。空串表示没有备份或读不出来。
    /// </summary>
    /// <remarks>
    /// 用途：安装器会用它自带的 INI 覆盖现有文件，覆盖之后需要判断
    /// 「新的这份是不是比备份更差」—— 那就得把备份内容读出来比对。
    /// </remarks>
    public static string ReadIniBackup(RdpTransactionManifest manifest)
    {
        if (manifest.IniBackup.Length == 0)
        {
            return "";
        }

        try
        {
            var backup = Path.Combine(DirectoryFor(manifest.Id), Path.GetFileName(manifest.IniBackup));
            return File.Exists(backup) ? File.ReadAllText(backup) : "";
        }
        catch
        {
            return "";
        }
    }

    /// <summary>
    /// 在所有历史事务里找一份**含指定构建段**的 INI 备份，返回其完整路径（找不到返回空串）。
    /// </summary>
    /// <remarks>
    /// <para>为什么要在历史事务里找：安装器会用自带的残本 INI 覆盖磁盘上那份更好的，
    /// 而「本轮快照」备份的可能只是刚被覆盖前的那份 —— 也就是好的那份只在**上一轮**事务里。</para>
    /// <para>2026-10-26 真机：17:45 那轮留下的好 INI 被 18:04 那轮盖成了 67 段的残本，
    /// 于是「本轮备份」也是残本，只看本轮就救不回来。</para>
    /// <para>按目录名倒序（目录名就是 <c>yyyyMMdd-HHmmss-xxxx</c>），取最新的一份。</para>
    /// </remarks>
    public static string FindIniBackupCovering(string version)
    {
        if (version.Length == 0 || !Directory.Exists(RootDir))
        {
            return "";
        }

        try
        {
            foreach (var dir in Directory.GetDirectories(RootDir).OrderByDescending(d => d))
            {
                var backup = Path.Combine(dir, IniBackupFileName);
                if (!File.Exists(backup))
                {
                    continue;
                }

                try
                {
                    if (File.ReadAllText(backup).Contains($"[{version}]", StringComparison.Ordinal))
                    {
                        return backup;
                    }
                }
                catch
                {
                    // 单份读不出来不影响继续找
                }
            }
        }
        catch
        {
            // 目录都列不出来就当作没有
        }

        return "";
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
    /// <para>这里**不抛**写盘异常：账本是诊断记录，写到一半时磁盘满了不该把一次已经动了注册表的
    /// 操作中途打断 —— 那会跳过补偿，留下比"少一行日志"严重得多的后果。
    /// 与之相对，流程开始前的 <see cref="Begin"/> 必须严格失败（备份失败不得开始系统变更）。</para>
    /// <para>但「不抛」不等于「不管」：失败会打在 manifest 上
    /// （<see cref="RdpTransactionManifest.LedgerIncomplete"/>），由调用方在下一步系统修改之前
    /// 用 <see cref="TrySaveCritical"/> 判定到底能不能继续（2026-10-26 检查记录 P0）。</para>
    /// </remarks>
    public static void AppendStep(RdpTransactionManifest manifest, string line)
    {
        manifest.Steps.Add($"{DateTimeOffset.Now:HH:mm:ss} {line}");

        try
        {
            Save(manifest);
        }
        catch (Exception ex)
        {
            // 见上面的说明：账本写不下去也得让流程继续，否则补偿链会被一个 IO 错误掐断
            MarkLedgerIncomplete(manifest, $"{ex.GetType().Name}: {ex.Message}");
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
        catch (Exception ex)
        {
            // 同上
            MarkLedgerIncomplete(manifest, $"{ex.GetType().Name}: {ex.Message}");
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
    /// 目录权限收紧到「只有 SYSTEM 与 Administrators 可写」（计划书 5.2），并**回读校验**。
    /// </summary>
    /// <remarks>
    /// <para>返回值不是「SetAccessControl 有没有抛异常」，而是「这个目录现在是不是只有管理员能写」。
    /// 两件事不一样：ProgramData 下新建的子目录本来就不继承「创建文件夹」这类写字权限，
    /// 这时收紧动作失败也照样是安全的；反过来收紧成功也可能还有别的 Allow 规则在放权
    /// （2026-10-26 检查记录 P2）。</para>
    /// <para>判据只读有效的 Allow 规则：只要有任何非 SYSTEM / 非 Administrators 的主体
    /// 拿到写入类权限，就返回 false —— 由调用方在第一次系统修改之前停下。</para>
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
        }
        catch
        {
            // 收紧动作本身失败不要紧，下面按「实际 ACL」判定
        }

        return DirectoryIsRestricted(dir);
    }

    /// <summary>只读判定：这个目录是不是「只有 SYSTEM / Administrators 能写」。</summary>
    public static bool DirectoryIsRestricted(string dir)
    {
        try
        {
            const FileSystemRights writeLike =
                FileSystemRights.Write
                | FileSystemRights.WriteData
                | FileSystemRights.AppendData
                | FileSystemRights.Delete
                | FileSystemRights.DeleteSubdirectoriesAndFiles
                | FileSystemRights.ChangePermissions
                | FileSystemRights.TakeOwnership;

            var security = new DirectoryInfo(dir).GetAccessControl(AccessControlSections.Access);

            foreach (FileSystemAccessRule rule in security.GetAccessRules(
                         includeExplicit: true, includeInherited: true, targetType: typeof(SecurityIdentifier)))
            {
                if (rule.AccessControlType != AccessControlType.Allow)
                {
                    continue;
                }

                if (rule.IdentityReference is not SecurityIdentifier sid)
                {
                    continue;
                }

                if (sid.IsWellKnown(WellKnownSidType.LocalSystemSid)
                    || sid.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid))
                {
                    continue;
                }

                if ((rule.FileSystemRights & writeLike) != 0)
                {
                    return false;
                }
            }

            return true;
        }
        catch
        {
            // 连 ACL 都读不出来：不能证明「只有管理员能写」，按未达标处理
            return false;
        }
    }
}
