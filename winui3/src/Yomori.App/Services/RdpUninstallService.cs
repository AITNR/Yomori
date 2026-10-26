// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Yomori.Models;

namespace Yomori.Services;

/// <summary>卸载请求。</summary>
/// <remarks>
/// <see cref="AntivirusConfirmed"/> 与解锁同义：只有需要**重新下载**卸载器时才起作用，
/// 而且是计划书要求的同一个门禁（下载前人工确认，不检测杀软状态）。
/// </remarks>
public sealed class RdpUninstallRequest
{
    public bool AntivirusConfirmed { get; init; }

    /// <summary>
    /// 允许使用与当前安装版本不一致的卸载器。默认 false ——
    /// 计划书 6.1 要求「最新版卸载器与旧安装兼容性不明时停止并提示，不盲目混配」。
    /// </summary>
    public bool AllowMismatchedUninstaller { get; init; }
}

/// <summary>
/// 「卸载 RDP 解锁」（计划书 §6.1）。
/// </summary>
/// <remarks>
/// <para>与「还原注册表」的分界线：卸载只调用官方卸载器、验证接管是否解除；
/// 它**不**还原注册表值、不删 Windows 用户、不删通道凭据、不动组成员。
/// 卸载完成后会明确提示"注册表快照仍保留，需要时点还原注册表"。</para>
/// <para>验证判据刻意不用"3389 有没有监听"：专业版卸载后原生 RDP 照样监听 3389，
/// 家庭版卸载后没有监听器也是正常的（计划书 6.1 第 5 条）。
/// 唯一可靠的判据是 ServiceDll 是否还指向 rdpwrap.dll。</para>
/// </remarks>
public static class RdpUninstallService
{
    private static readonly TimeSpan ToolTimeout = TimeSpan.FromSeconds(180);
    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(2);
    private const int TotalSteps = 6;

    public static Task<RdpOperationResult> UninstallAsync(
        RdpUninstallRequest request,
        UpdateNetworkSettings network,
        IProgress<RdpProgressInfo>? progress = null,
        CancellationToken cancellationToken = default) =>
        Task.Run(() => UninstallCoreAsync(request, network, progress, cancellationToken), cancellationToken);

    private static async Task<RdpOperationResult> UninstallCoreAsync(
        RdpUninstallRequest request,
        UpdateNetworkSettings network,
        IProgress<RdpProgressInfo>? progress,
        CancellationToken cancellationToken)
    {
        var steps = new List<RdpStepRecord>();

        if (!RdpOperationLock.TryAcquire(LockTimeout, out var lease, out var lockReason))
        {
            return Fail(steps, "无法开始：", lockReason);
        }

        using (lease)
        {
            // ---- 1. 只读检测：到底有没有东西可卸 ----
            Report(progress, 1, "检查当前安装状态");
            var before = RdpReadOnlyDetector.Detect();

            // 半截事务：能结案就结案（原因见 RdpUnlockService.ResolveStaleTransactions）
            var stale = RdpTransactionStore.ResolveStale(before);
            if (stale.Blocked)
            {
                return Fail(steps, "上一次操作没有收尾，先处理它：", stale.Message, before);
            }

            if (stale.Message.Length > 0)
            {
                steps.Add(new RdpStepRecord
                {
                    Key = "stale", Title = "收尾上一次未完成的事务",
                    Outcome = RdpStepOutcome.Warn, Detail = stale.Message,
                });
            }

            if (before.WrapperState != RdpWrapperState.Installed)
            {
                steps.Add(new RdpStepRecord
                {
                    Key = "precheck",
                    Title = "检查当前安装状态",
                    Outcome = RdpStepOutcome.Skipped,
                    Detail = $"Wrapper={RdpProbeText.WrapperLabel(before.WrapperState)}，本机没有可卸载的解锁组件。",
                });

                return new RdpOperationResult
                {
                    Success = true,
                    Summary = "未安装 RDP 解锁组件，无需卸载。其它远程桌面设置没有被改动。",
                    Detail = RdpUnlockDiagnostics.FormatDetail(before),
                    Steps = steps,
                    Final = before,
                };
            }

            steps.Add(new RdpStepRecord
            {
                Key = "precheck",
                Title = "检查当前安装状态",
                Outcome = RdpStepOutcome.Ok,
                Detail = $"ServiceDll={before.ServiceDllPath}；TermService={RdpProbeText.ServiceLabel(before.ServiceState)}；"
                         + $"当前 {before.SessionCount} 个会话。",
            });

            // ---- 2. 事务 + 卸载前快照 ----
            Report(progress, 2, "创建卸载前快照");
            RdpTransactionManifest manifest;

            try
            {
                manifest = RdpTransactionStore.Begin(RdpTransactionKind.Uninstall);
            }
            catch (Exception ex)
            {
                return Fail(steps, "无法创建事务账本，未做任何修改：",
                    $"{ex.GetType().Name}: {ex.Message}", before);
            }

            foreach (var (keyPath, name) in RdpRegistryService.Whitelist)
            {
                try
                {
                    var state = RdpRegistryService.Read(keyPath, name);
                    manifest.Registry.Add(new RdpRegistryDelta { Before = state, After = state });
                }
                catch
                {
                    // 卸载不需要注册表基线也能进行：这里只做记录，失败不阻断
                }
            }

            if (File.Exists(before.IniPath))
            {
                manifest.IniSourcePath = before.IniPath;
                manifest.IniBackup = RdpTransactionStore.BackupIni(manifest.Id, before.IniPath);
            }

            RdpTransactionStore.AppendStep(manifest,
                $"卸载前快照：ServiceDll={before.ServiceDllPath}，{manifest.Registry.Count} 个键值");

            steps.Add(new RdpStepRecord
            {
                Key = "snapshot",
                Title = "创建卸载前快照",
                Outcome = RdpStepOutcome.Ok,
                Detail = $"事务 {manifest.Id}；记录 ServiceDll 与 {manifest.Registry.Count} 个键值"
                         + (manifest.IniBackup.Length > 0 ? "、INI 备份" : ""),
            });

            // ---- 3. 找卸载器 ----
            Report(progress, 3, "定位配套卸载器");
            var lookup = await FindUninstallerAsync(request, network, before.InstallDirectory, cancellationToken)
                .ConfigureAwait(false);

            if (lookup.Tool is null)
            {
                RdpTransactionStore.Finish(manifest, RdpTransactionStatus.Failed, lookup.Error);
                return Fail(steps, "拿不到可用的卸载器：", lookup.Error, before, manifest.Id);
            }

            var tool = lookup.Tool;
            manifest.UninstallerPath = tool.LocalPath;
            manifest.UninstallerSha256 = tool.Sha256;
            manifest.ReleaseId = lookup.ReleaseId;
            manifest.ReleaseTag = lookup.ReleaseTag;

            steps.Add(new RdpStepRecord
            {
                Key = "tool",
                Title = "定位配套卸载器",
                Outcome = lookup.FromRecord ? RdpStepOutcome.Ok : RdpStepOutcome.Warn,
                Detail = lookup.Detail,
            });

            // ---- 4. 运行卸载器 ----
            Report(progress, 4, "运行官方卸载器");
            var verify = RdpToolDownloader.VerifyExisting(tool.LocalPath, tool.Sha256, tool.SizeBytes);
            if (!verify.Ok)
            {
                RdpTransactionStore.Finish(manifest, RdpTransactionStatus.Failed, "卸载器二次核对失败：" + verify.Message);
                return Fail(steps, "卸载器在下载后被改动过，已停止：", verify.Message, before, manifest.Id);
            }

            // ⚠️ 必须带 -u：无参运行是交互式菜单，会一直挂着不退出（与安装器同一个坑）
            var run = await RdpToolRunner
                .RunAsync(tool.LocalPath, RdpToolRunner.UninstallArguments, ToolTimeout, cancellationToken)
                .ConfigureAwait(false);

            RdpTransactionStore.AppendStep(
                manifest, $"卸载器（{RdpToolRunner.UninstallArguments}）退出码 {run.ExitCode}：{run.OutputTail}");

            // 退出码只作参考，最终认 ServiceDll
            var after = RdpReadOnlyDetector.Detect();
            var uninstalledNow = after.WrapperState != RdpWrapperState.Installed;

            if (run.TimedOut && !uninstalledNow)
            {
                // 不杀进程、不补偿、事务保持未完成，交给下次识别（计划书 6.2）
                steps.Add(new RdpStepRecord
                {
                    Key = "run",
                    Title = "运行官方卸载器",
                    Outcome = RdpStepOutcome.Fail,
                    Detail = $"卸载器 {ToolTimeout.TotalSeconds:0} 秒未结束，且 ServiceDll 仍为 {after.ServiceDllPath}。",
                });

                return Fail(steps, "卸载器超时，结果不明：",
                    "没有强制结束卸载器。请等它自行结束后重新检测。", before, manifest.Id);
            }

            steps.Add(new RdpStepRecord
            {
                Key = "run",
                Title = "运行官方卸载器",
                Outcome = uninstalledNow
                    ? (run.TimedOut ? RdpStepOutcome.Warn : RdpStepOutcome.Ok)
                    : RdpStepOutcome.Warn,
                Detail = run.TimedOut
                    ? $"卸载器 {ToolTimeout.TotalSeconds:0} 秒未退出，但 ServiceDll 已回到 {after.ServiceDllPath} —— 按状态判定为已卸载。"
                    : $"退出码 {run.ExitCode}。{run.OutputTail}",
            });

            // ---- 5. 回读：只看 ServiceDll ----
            Report(progress, 5, "验证接管已解除");
            var uninstalled = after.WrapperState != RdpWrapperState.Installed;

            // 专业版卸载后原生 RDP 仍在监听 3389，这不算失败；家庭版没有监听器也是正常的
            var listenerNote = after.Tcp3389Listening
                ? "3389 仍在监听（专业版卸载后属原生 RDP，正常）"
                : "3389 无监听（家庭版卸载后属正常）";

            steps.Add(new RdpStepRecord
            {
                Key = "verify",
                Title = "验证接管已解除",
                Outcome = uninstalled ? RdpStepOutcome.Ok : RdpStepOutcome.Fail,
                Detail = uninstalled
                    ? $"ServiceDll 已回到 {after.ServiceDllPath}；{listenerNote}。"
                    : $"ServiceDll 仍指向 {after.ServiceDllPath}，卸载未生效。",
            });

            if (!uninstalled)
            {
                RdpTransactionStore.Finish(manifest, RdpTransactionStatus.Failed, "卸载后 ServiceDll 仍指向 Wrapper");
                return Fail(steps, "卸载没有生效：",
                    "ServiceDll 仍指向 rdpwrap.dll。常见原因：文件被占用（有远程会话正在用）、"
                    + "或安全软件拦截了卸载器。注销所有远程会话后重试；必要时重启再试。", after, manifest.Id);
            }

            // ---- 6. 收尾 ----
            Report(progress, 6, "完成");
            var handMade = !WasInstalledByUs();
            if (handMade)
            {
                steps.Add(new RdpStepRecord
                {
                    Key = "legacy",
                    Title = "遗留安装说明",
                    Outcome = RdpStepOutcome.Warn,
                    Detail = "本机这份解锁组件不是由 Yomori 安装的，没有安装前基线："
                             + "本次只做官方卸载与现状验证，无法保证重建安装前的历史状态。",
                });
            }

            RdpTransactionStore.Finish(manifest, RdpTransactionStatus.Completed, "");

            return new RdpOperationResult
            {
                Success = true,
                Summary = "已卸载 RDP 解锁组件。Windows 用户、Yomori 通道与凭据都保留着。"
                          + "注册表配置快照仍保留在本机，需要恢复解锁前的注册表请点「还原注册表」。",
                Detail = RdpUnlockDiagnostics.FormatDetail(after),
                Steps = steps,
                Final = after,
                TransactionId = manifest.Id,
            };
        }
    }

    private readonly record struct UninstallerLookup(
        RdpDownloadedTool? Tool,
        bool FromRecord,
        string Detail,
        string Error,
        long ReleaseId,
        string ReleaseTag);

    /// <summary>
    /// 找卸载器，优先级：本项目记录的那一份 → staging 目录里已有的 → 重新下载（需人工确认）。
    /// </summary>
    private static async Task<UninstallerLookup> FindUninstallerAsync(
        RdpUninstallRequest request,
        UpdateNetworkSettings network,
        string installDirectory,
        CancellationToken cancellationToken)
    {
        // 0) 安装目录里的控制台工具 —— 最优解：它就是官方 install.bat 用的那个 RDPWInst.exe，
        //    跑起来没有任何窗口，不需要下载，也与当前已装版本天然同源。
        var localTool = FindLocalConsoleTool(installDirectory);
        if (localTool is not null)
        {
            return new UninstallerLookup(
                localTool,
                FromRecord: true,
                Detail: $"使用安装目录里的控制台工具：{localTool.LocalPath}（不会弹出任何界面）",
                Error: "",
                ReleaseId: 0,
                ReleaseTag: "");
        }

        var recordedTag = "";
        var recordedReleaseId = 0L;

        // 1) 账本里记录过的（与当初安装用的同一份发布，天然版本匹配）
        foreach (var info in RdpTransactionStore.List())
        {
            if (info.Kind != RdpTransactionKind.Unlock)
            {
                continue;
            }

            var manifest = RdpTransactionStore.Load(info.Id);
            if (manifest is null)
            {
                continue;
            }

            recordedTag = manifest.ReleaseTag;
            recordedReleaseId = manifest.ReleaseId;

            if (manifest.UninstallerPath.Length == 0 || !File.Exists(manifest.UninstallerPath))
            {
                continue;
            }

            var tool = new RdpDownloadedTool
            {
                Asset = new RdpAsset { Name = Path.GetFileName(manifest.UninstallerPath) },
                LocalPath = manifest.UninstallerPath,
                Sha256 = manifest.UninstallerSha256,
                SizeBytes = new FileInfo(manifest.UninstallerPath).Length,
            };

            return new UninstallerLookup(
                tool,
                FromRecord: true,
                Detail: $"使用本项目记录的配套卸载器（发布 {manifest.ReleaseTag}）：{manifest.UninstallerPath}",
                Error: "",
                ReleaseId: manifest.ReleaseId,
                ReleaseTag: manifest.ReleaseTag);
        }

        // 2) staging 目录里已经下过的那一份
        var staged = FindStagedUninstaller(recordedReleaseId);
        if (staged is not null)
        {
            return new UninstallerLookup(
                staged,
                FromRecord: false,
                Detail: $"使用已下载的卸载器：{staged.LocalPath}",
                Error: "",
                ReleaseId: recordedReleaseId,
                ReleaseTag: recordedTag);
        }

        // 3) 只能重新下载
        if (!request.AntivirusConfirmed)
        {
            return new UninstallerLookup(null, false, "",
                "本机没有可用的卸载器，需要重新下载。下载前请先确认已关闭所有杀毒软件。",
                0, recordedTag);
        }

        var (release, resolveError) = await RdpReleaseResolver
            .ResolveLatestAsync(network, cancellationToken)
            .ConfigureAwait(false);

        if (release is null)
        {
            return new UninstallerLookup(null, false, "", resolveError, 0, recordedTag);
        }

        // 版本混配检查：记录过安装版本、而最新版换了 tag → 不盲目混配
        if (recordedTag.Length > 0
            && !string.Equals(recordedTag, release.Tag, StringComparison.OrdinalIgnoreCase)
            && !request.AllowMismatchedUninstaller)
        {
            return new UninstallerLookup(null, false, "",
                $"本机安装的是 {recordedTag}，而官方最新是 {release.Tag}。"
                + "两者兼容性不明，按计划书不混配。请先用同一版本重装后再卸载，"
                + "或到发布页自行确认：" + RdpReleaseResolver.ReleasesPageUrl,
                release.Id, recordedTag);
        }

        if (!release.TrySelect(out _, out var uninstallerAsset, out var selectError))
        {
            return new UninstallerLookup(null, false, "", selectError, release.Id, release.Tag);
        }

        var (downloaded, downloadError) = await RdpToolDownloader
            .DownloadAsync(uninstallerAsset, release.Id, network, progress: null, cancellationToken)
            .ConfigureAwait(false);

        if (downloaded is null)
        {
            return new UninstallerLookup(null, false, "", downloadError, release.Id, release.Tag);
        }

        return new UninstallerLookup(
            downloaded,
            FromRecord: false,
            Detail: $"已下载本次所需卸载器（发布 {release.Tag}）：{downloaded.LocalPath}",
            Error: "",
            ReleaseId: release.Id,
            ReleaseTag: release.Tag);
    }

    /// <summary>
    /// 本机是否已经有可用的卸载器 —— 界面据此决定要不要弹「关闭杀软」的下载确认。
    /// </summary>
    /// <remarks>
    /// 有本地卸载器却不弹确认、没有才弹，这条顺序是计划书 6.1 第 3 条：
    /// 只在"缺失卸载器需要重新下载"时才需要那道门禁。
    /// </remarks>
    public static bool HasLocalUninstaller(out string detail)
    {
        foreach (var info in RdpTransactionStore.List())
        {
            if (info.Kind != RdpTransactionKind.Unlock)
            {
                continue;
            }

            var manifest = RdpTransactionStore.Load(info.Id);
            if (manifest is null || manifest.UninstallerPath.Length == 0)
            {
                continue;
            }

            if (File.Exists(manifest.UninstallerPath))
            {
                detail = $"账本记录的卸载器：{manifest.UninstallerPath}（发布 {manifest.ReleaseTag}）";
                return true;
            }
        }

        var staged = FindStagedUninstaller(0);
        if (staged is not null)
        {
            detail = $"已下载的卸载器：{staged.LocalPath}";
            return true;
        }

        detail = "本机没有卸载器，需要从官方 Releases 重新下载";
        return false;
    }

    /// <summary>
    /// 安装目录里的控制台卸载工具（<c>RDPWInst.exe</c>）。没有就返回 null。
    /// </summary>
    /// <remarks>
    /// 它和那个 2.5MB 的图形"安装器/卸载器"是同一套工具链里的控制台版，
    /// 官方 <c>uninstall.bat</c> 就是 <c>RDPWInst.exe -u</c>。用它卸载不会有任何窗口。
    /// </remarks>
    private static RdpDownloadedTool? FindLocalConsoleTool(string? installDirectory)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(installDirectory))
            {
                return null;
            }

            var path = Path.Combine(installDirectory, "RDPWInst.exe");
            if (!File.Exists(path))
            {
                return null;
            }

            return new RdpDownloadedTool
            {
                Asset = new RdpAsset { Name = "RDPWInst.exe" },
                LocalPath = path,
                Sha256 = RdpTransactionStore.Sha256File(path),
                SizeBytes = new FileInfo(path).Length,
            };
        }
        catch
        {
            return null;
        }
    }

    private static RdpDownloadedTool? FindStagedUninstaller(long preferredReleaseId)
    {
        try
        {
            var root = RdpTransactionStore.ToolsDir;
            if (!Directory.Exists(root))
            {
                return null;
            }

            var candidates = new List<string>();

            foreach (var directory in Directory.GetDirectories(root))
            {
                foreach (var file in Directory.GetFiles(directory, "*.exe"))
                {
                    if (RdpReleaseInfo.IsUninstallerName(Path.GetFileName(file)))
                    {
                        candidates.Add(file);
                    }
                }
            }

            if (candidates.Count == 0)
            {
                return null;
            }

            // 优先取与记录版本一致的那一份
            string chosen;
            if (preferredReleaseId > 0)
            {
                var preferred = Path.Combine(root, preferredReleaseId.ToString());
                chosen = candidates.Find(c => c.StartsWith(preferred, StringComparison.OrdinalIgnoreCase)) ?? candidates[0];
            }
            else
            {
                chosen = candidates[0];
            }

            return new RdpDownloadedTool
            {
                Asset = new RdpAsset { Name = Path.GetFileName(chosen) },
                LocalPath = chosen,
                Sha256 = RdpTransactionStore.Sha256File(chosen),
                SizeBytes = new FileInfo(chosen).Length,
            };
        }
        catch
        {
            return null;
        }
    }

    /// <summary>本机这份组件是不是 Yomori 装的（有 Unlock 事务且标记了 InstalledByUs）。</summary>
    private static bool WasInstalledByUs()
    {
        foreach (var info in RdpTransactionStore.List())
        {
            if (info.Kind != RdpTransactionKind.Unlock)
            {
                continue;
            }

            var manifest = RdpTransactionStore.Load(info.Id);
            if (manifest is { InstalledByUs: true })
            {
                return true;
            }
        }

        return false;
    }

    private static void Report(IProgress<RdpProgressInfo>? progress, int step, string text) =>
        progress?.Report(new RdpProgressInfo(step, TotalSteps, text));

    private static RdpOperationResult Fail(
        List<RdpStepRecord> steps,
        string summaryPrefix,
        string reason,
        RdpUnlockSnapshot? snapshot = null,
        string transactionId = "")
    {
        return new RdpOperationResult
        {
            Success = false,
            Summary = summaryPrefix + reason,
            Detail = snapshot is null ? "" : RdpUnlockDiagnostics.FormatDetail(snapshot),
            Steps = new List<RdpStepRecord>(steps),
            Final = snapshot,
            TransactionId = transactionId,
        };
    }
}
