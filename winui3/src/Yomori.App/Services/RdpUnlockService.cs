// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Yomori.Models;

namespace Yomori.Services;

/// <summary>
/// 「解锁 RDP」「还原注册表」的编排（计划书 3.3 的十步 + 5.4 的补偿）。
/// </summary>
/// <remarks>
/// <para><b>顺序不能换</b>，每一步都在计划书里对应一句话：</para>
/// <list type="number">
/// <item>本机只读预检、权限与活动会话影响确认</item>
/// <item>缺少组件时取得人工杀软确认（确认前不查 metadata、不下载）</item>
/// <item>解析最新正式版，下载安装器及配套卸载器</item>
/// <item>校验资产并锁定本轮 release ID，创建安装前快照</item>
/// <item>调用外部安装器，等待结束并回读实际安装状态</item>
/// <item>持久化注册表变更日志，逐项应用及回读</item>
/// <item>备份 INI，调用 <c>RDPWInst.exe -w</c></item>
/// <item>正常结束后显式执行一轮 TermService 重启</item>
/// <item>内置检测收敛四项状态</item>
/// <item>结果中提醒用户重新开启安全防护</item>
/// </list>
/// <para><b>为什么每一步都落账本</b>：中途失败时要能按逆序补偿；崩溃之后要能看出做到哪了。
/// <see cref="RdpTransactionStatus.Running"/> 留在盘上就是"没走完"的证据。</para>
/// </remarks>
public static class RdpUnlockService
{
    /// <summary>安装器/卸载器的等待上限。工具是控制台程序，正常几秒内结束。</summary>
    private static readonly TimeSpan ToolTimeout = TimeSpan.FromSeconds(180);

    /// <summary>INI 更新（<c>RDPWInst -w</c>）的等待上限，计划书 6.2 给的初值是 120 秒。</summary>
    private static readonly TimeSpan IniTimeout = TimeSpan.FromSeconds(120);

    /// <summary>操作锁的等待时间：连点两次时第二次直接告诉用户"正在跑"，不要排长队。</summary>
    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(2);

    private const int TotalSteps = 10;

    /// <summary>
    /// 解锁流程。
    /// </summary>
    /// <param name="request">必须带 <see cref="RdpUnlockRequest.AntivirusConfirmed"/> 才能下载。</param>
    /// <param name="network">下载与查发布用的代理设置。</param>
    public static async Task<RdpOperationResult> UnlockAsync(
        RdpUnlockRequest request,
        UpdateNetworkSettings network,
        IProgress<RdpProgressInfo>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var steps = new List<RdpStepRecord>();

        // ---- 互斥锁：三个修改动作共享 ----
        if (!RdpOperationLock.TryAcquire(LockTimeout, out var lease, out var lockReason))
        {
            return Fail(steps, "无法开始：", lockReason);
        }

        using (lease)
        {
            // 半截事务：既不能当成功，也不能直接覆盖（计划书 5.2）
            var incomplete = RdpTransactionStore.ListIncomplete();
            if (incomplete.Count > 0)
            {
                return Fail(steps, "上一次操作没有收尾，先处理它：",
                    $"{incomplete[0].SummaryText}。请点「查看诊断」看它做到哪一步，必要时手工收尾后再来一次。");
            }

            // ---- 1. 只读预检 ----
            Report(progress, 1, "本机只读预检");
            var before = RdpReadOnlyDetector.Detect();
            Add(steps, "precheck", "只读预检", before.AllPass ? RdpStepOutcome.Ok : RdpStepOutcome.Warn,
                $"Wrapper={RdpProbeText.WrapperLabel(before.WrapperState)}，"
                + $"Service={RdpProbeText.ServiceLabel(before.ServiceState)}，"
                + $"Listener={RdpProbeText.ListenerLabel(before.ListenerState)}，"
                + $"Support={RdpProbeText.SupportLabel(before.SupportState)}"
                + (before.SessionCount > 0 ? $"；当前 {before.SessionCount} 个会话（{before.SessionSummary}）" : ""));

            if (!before.IsElevated)
            {
                return Fail(steps, "需要管理员权限：", "本进程不是管理员。重启程序并通过 UAC 后再试。", before);
            }

            // ---- 2 / 3 / 4：需要下载时才有这两步 ----
            RdpReleaseInfo? release = null;
            RdpDownloadedTool? installer = null;
            RdpDownloadedTool? uninstaller = null;
            var needsDownload = before.WrapperState != RdpWrapperState.Installed;

            if (needsDownload)
            {
                if (!request.AntivirusConfirmed)
                {
                    // 硬门禁：没有人工确认，连 metadata 都不查（计划书 3.2）
                    return Fail(steps, "缺少解锁组件，需要先确认：",
                        "本机没有安装 RDP Wrapper 解锁组件。下载任何东西之前，请先确认已关闭所有杀毒软件，"
                        + "然后在确认窗口里勾选并点「确定并开始下载」。", before);
                }

                Report(progress, 2, "解析官方最新正式版");
                var (resolved, resolveError) = await RdpReleaseResolver
                    .ResolveLatestAsync(network, cancellationToken)
                    .ConfigureAwait(false);

                if (resolved is null)
                {
                    return Fail(steps, "拿不到官方发布信息：", resolveError, before);
                }

                if (!resolved.TrySelect(out var installerAsset, out var uninstallerAsset, out var selectError))
                {
                    return Fail(steps, "官方这份发布的附件不可用：",
                        selectError + "。请到发布页确认：" + RdpReleaseResolver.ReleasesPageUrl, before);
                }

                release = resolved;
                Add(steps, "release", "锁定发布", RdpStepOutcome.Ok,
                    $"{resolved.Tag}（release {resolved.Id}，安装器 {installerAsset.Name} / 卸载器 {uninstallerAsset.Name}）");

                Report(progress, 3, "下载安装器与卸载器");
                var (downloadedInstaller, installerError) = await RdpToolDownloader
                    .DownloadAsync(installerAsset, resolved.Id, network, progress: null, cancellationToken)
                    .ConfigureAwait(false);

                if (downloadedInstaller is null)
                {
                    return Fail(steps, "下载安装器失败：", installerError, before);
                }

                installer = downloadedInstaller;

                var (downloadedUninstaller, uninstallerError) = await RdpToolDownloader
                    .DownloadAsync(uninstallerAsset, resolved.Id, network, progress: null, cancellationToken)
                    .ConfigureAwait(false);

                if (downloadedUninstaller is null)
                {
                    // 卸载器拿不到就别装了：装完无法按计划书卸载，等于留一个卸不掉的东西
                    return Fail(steps, "下载配套卸载器失败：", uninstallerError + "（没有卸载器就不安装）", before);
                }

                uninstaller = downloadedUninstaller;

                Add(steps, "download", "下载与校验", RdpStepOutcome.Ok,
                    $"{installer.Asset.Name} {installer.SizeBytes} 字节 sha256={Short(installer.Sha256)}；"
                    + $"{uninstaller.Asset.Name} sha256={Short(uninstaller.Sha256)}。" + installer.Limits);
            }
            else
            {
                Add(steps, "reuse", "复用已有安装", RdpStepOutcome.Ok,
                    $"本机已有解锁组件（{before.ServiceDllPath}），本轮只更新配置，不重装。");
            }

            // ---- 建事务 + 安装前快照 ----
            Report(progress, 4, "创建安装前快照");
            RdpTransactionManifest manifest;

            try
            {
                manifest = RdpTransactionStore.Begin(RdpTransactionKind.Unlock);
            }
            catch (Exception ex)
            {
                // 计划书 §0：备份失败不得开始系统变更
                return Fail(steps, "无法创建事务账本，未做任何修改：",
                    $"{ex.GetType().Name}: {ex.Message}（{RdpTransactionStore.RootDir}）", before);
            }

            manifest.InstalledByUs = needsDownload;
            if (release is not null)
            {
                manifest.ReleaseId = release.Id;
                manifest.ReleaseTag = release.Tag;
            }

            if (installer is not null)
            {
                manifest.InstallerPath = installer.LocalPath;
                manifest.InstallerSha256 = installer.Sha256;
            }

            if (uninstaller is not null)
            {
                manifest.UninstallerPath = uninstaller.LocalPath;
                manifest.UninstallerSha256 = uninstaller.Sha256;
            }

            // 注册表基线：白名单里每个值的现状。原值不存在也是一个状态
            foreach (var (keyPath, name) in RdpRegistryService.Whitelist)
            {
                RdpRegistryValueState state;
                try
                {
                    state = RdpRegistryService.Read(keyPath, name);
                }
                catch (Exception ex)
                {
                    Add(steps, "snapshot", "注册表快照", RdpStepOutcome.Fail, $"{keyPath}\\{name}：{ex.Message}");
                    RdpTransactionStore.Finish(manifest, RdpTransactionStatus.Failed, "注册表快照读取失败");
                    return Fail(steps, "读取注册表失败，未做任何修改：", $"{keyPath}\\{name}：{ex.Message}", before);
                }

                manifest.Registry.Add(new RdpRegistryDelta
                {
                    Before = state,
                    After = state,
                });
            }

            // INI 备份：安装前的那份（可能根本不存在 —— 那就记录"不存在"）
            var iniPath = before.IniPath;
            if (File.Exists(iniPath))
            {
                manifest.IniSourcePath = iniPath;
                manifest.IniBackup = RdpTransactionStore.BackupIni(manifest.Id, iniPath);
            }

            RdpTransactionStore.AppendStep(manifest,
                $"快照完成：{manifest.Registry.Count} 个键值，INI{(manifest.IniBackup.Length > 0 ? "已备份" : "不存在")}");

            Add(steps, "snapshot", "安装前快照", RdpStepOutcome.Ok,
                $"事务 {manifest.Id}；{manifest.Registry.Count} 个键值；"
                + (manifest.IniBackup.Length > 0 ? $"INI 已备份（{manifest.IniBackup}）" : "INI 尚不存在"));

            // ---- 5. 调安装器 ----
            var installDir = before.InstallDirectory;

            if (needsDownload && installer is not null)
            {
                Report(progress, 5, "运行官方安装器");
                var verify = RdpToolDownloader.VerifyExisting(installer.LocalPath, installer.Sha256, installer.SizeBytes);
                if (!verify.Ok)
                {
                    RdpTransactionStore.Finish(manifest, RdpTransactionStatus.Failed, "安装器二次核对失败：" + verify.Message);
                    return Fail(steps, "安装器在下载后被改动过，已停止：", verify.Message, before);
                }

                var run = await RunToolAsync(installer.LocalPath, "", ToolTimeout, cancellationToken).ConfigureAwait(false);
                RdpTransactionStore.AppendStep(manifest, $"安装器退出码 {run.ExitCode}：{run.OutputTail}");

                if (run.TimedOut)
                {
                    // 结果不明：不杀进程、不补偿、事务保持 Running 供下次识别（计划书 6.2）
                    Add(steps, "install", "运行官方安装器", RdpStepOutcome.Fail,
                        $"安装器 {ToolTimeout.TotalSeconds:0} 秒未结束，结果不明。事务保持未完成状态，下次操作前会先提示。");
                    return Fail(steps, "安装器超时，结果不明：",
                        "没有强制结束安装器。请等它自行结束后重新检测；若一直不结束，重启电脑后再来。", before,
                        manifest.Id);
                }

                if (run.ExitCode != 0)
                {
                    var compensated = await CompensateAsync(manifest, uninstaller, steps, cancellationToken)
                        .ConfigureAwait(false);
                    return Fail(steps, "安装器返回失败：", $"退出码 {run.ExitCode}。{run.OutputTail}", before, manifest.Id,
                        compensated);
                }

                // 回读真实安装状态：不认退出码，认 ServiceDll
                var afterInstall = RdpReadOnlyDetector.Detect();
                installDir = afterInstall.InstallDirectory;

                if (afterInstall.WrapperState != RdpWrapperState.Installed)
                {
                    var compensated = await CompensateAsync(manifest, uninstaller, steps, cancellationToken)
                        .ConfigureAwait(false);
                    return Fail(steps, "安装器报告成功，但本机没有接管：",
                        $"ServiceDll 仍为 {afterInstall.ServiceDllPath}。可能是安全软件拦截，或系统版本不被该版本支持。",
                        afterInstall, manifest.Id, compensated);
                }

                Add(steps, "install", "运行官方安装器", RdpStepOutcome.Ok,
                    $"退出码 0；ServiceDll 已指向 {afterInstall.ServiceDllPath}");
            }
            else
            {
                Report(progress, 5, "跳过安装（已有安装）");
                Add(steps, "install", "运行官方安装器", RdpStepOutcome.Skipped, "本机已有有效安装，按计划书 4.1 不强制重装。");
            }

            // ---- 6. 注册表事务 ----
            Report(progress, 6, "应用注册表变更");
            var registryOk = ApplyRegistry(manifest, steps, out var registryMessage);
            Add(steps, "registry", "注册表变更", registryOk ? RdpStepOutcome.Ok : RdpStepOutcome.Fail, registryMessage);

            if (!registryOk)
            {
                var compensated = await CompensateAsync(manifest, uninstaller, steps, cancellationToken)
                    .ConfigureAwait(false);
                return Fail(steps, "注册表变更失败：", registryMessage, before, manifest.Id, compensated);
            }

            // ---- 7. 备份 INI + Update INI ----
            if (request.UpdateIni)
            {
                Report(progress, 7, "更新 INI");
                var updateIni = await UpdateIniAsync(manifest, installDir, steps, cancellationToken).ConfigureAwait(false);
                if (!updateIni.Ok)
                {
                    var compensated = await CompensateAsync(manifest, uninstaller, steps, cancellationToken)
                        .ConfigureAwait(false);
                    return Fail(steps, "更新 INI 失败：", updateIni.Message, before, manifest.Id, compensated);
                }
            }
            else
            {
                Report(progress, 7, "跳过 INI 更新");
                Add(steps, "ini", "更新 INI", RdpStepOutcome.Skipped, "本轮请求没有要求更新 INI。");
            }

            // ---- 8. 显式重启 TermService ----
            if (request.RestartService)
            {
                Report(progress, 8, "重启远程桌面服务");
                var (restartOk, restartMessage, touched) = RdpServiceControl.Restart(RdpServiceControl.TermServiceName);
                foreach (var service in touched)
                {
                    if (!manifest.ServicesTouched.Contains(service))
                    {
                        manifest.ServicesTouched.Add(service);
                    }
                }

                RdpTransactionStore.AppendStep(manifest, "重启服务：" + restartMessage);
                Add(steps, "service", "重启远程桌面服务", restartOk ? RdpStepOutcome.Ok : RdpStepOutcome.Fail, restartMessage);

                if (!restartOk)
                {
                    var compensated = await CompensateAsync(manifest, uninstaller, steps, cancellationToken)
                        .ConfigureAwait(false);
                    return Fail(steps, "重启远程桌面服务失败：", restartMessage, before, manifest.Id, compensated);
                }
            }
            else
            {
                Report(progress, 8, "跳过服务重启");
                Add(steps, "service", "重启远程桌面服务", RdpStepOutcome.Skipped, "本轮请求没有要求重启服务。");
            }

            // ---- 9. 内置检测收敛 ----
            Report(progress, 9, "内置检测");
            var final = RdpReadOnlyDetector.Detect();
            var finalOk = final.AllPass;
            Add(steps, "verify", "内置检测", finalOk ? RdpStepOutcome.Ok : RdpStepOutcome.Fail,
                $"Wrapper={RdpProbeText.WrapperLabel(final.WrapperState)}，"
                + $"Service={RdpProbeText.ServiceLabel(final.ServiceState)}，"
                + $"Listener={RdpProbeText.ListenerLabel(final.ListenerState)}，"
                + $"Support={RdpProbeText.SupportLabel(final.SupportState)}");

            var summary = finalOk
                ? "成功，可以前往「设置 → 账户 → 其他用户」里添加新用户。添加完成后，在 Yomori 通道管理中保存该账户。"
                : final.SupportState == RdpSupportState.NotSupported
                    ? "当前系统版本在本机 INI 中未找到完整支持项，可能是系统较新、社区暂未覆盖，也可能是 INI 更新未成功。"
                      + "当前 FileVersion、INI 更新结果和版本匹配详情见下方诊断。"
                    : "解锁流程已执行，但内置检测没有全部通过。逐项结果见下方诊断。";

            RdpTransactionStore.Finish(
                manifest,
                finalOk ? RdpTransactionStatus.Completed : RdpTransactionStatus.Failed,
                finalOk ? "" : RdpUnlockDiagnostics.FormatSummaryLine(final));

            // ---- 10. 提醒恢复防护 ----
            var reminder = needsDownload;
            if (reminder)
            {
                Add(steps, "protect", "安全防护提醒", RdpStepOutcome.Warn,
                    "本轮下载前您确认过关闭了杀毒软件。操作已结束，请及时重新开启安全防护（Yomori 不检测也未修改其状态）。");
            }

            Report(progress, 10, "完成");

            return new RdpOperationResult
            {
                Success = finalOk,
                Summary = summary,
                Detail = RdpUnlockDiagnostics.FormatDetail(final),
                Steps = steps,
                Final = final,
                TransactionId = manifest.Id,
                NeedsProtectionReminder = reminder,
            };
        }
    }

    /// <summary>
    /// 独立的「还原注册表」（计划书 5.3）。
    /// </summary>
    /// <remarks>
    /// 这是**只恢复注册表**，不等于卸载组件：不动 INI、不动组成员、不删账户与凭据。
    /// 也不顺手卸载 Wrapper —— 若恢复范围里含服务接管键且 Wrapper 仍在加载，先拦住，
    /// 否则会得到一个"服务指向原版 dll 但 Wrapper 还挂着"的畸形状态。
    /// </remarks>
    public static Task<RdpOperationResult> RestoreRegistryAsync(
        string? transactionId,
        IProgress<RdpProgressInfo>? progress = null,
        CancellationToken cancellationToken = default) =>
        // 整段是阻塞调用（注册表读写 + SCM 停启，最长两轮 45 秒等待），必须离开 UI 线程。
        // 写成 async 却不 await 只会把阻塞留在调用线程上（编译器 CS1998 就是在提示这个）。
        Task.Run(() => RestoreRegistryCore(transactionId, progress, cancellationToken), cancellationToken);

    private static RdpOperationResult RestoreRegistryCore(
        string? transactionId,
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
            var id = transactionId;

            if (string.IsNullOrWhiteSpace(id))
            {
                var baseline = RdpTransactionStore.FindFirstBaseline();
                if (baseline is null)
                {
                    return Fail(steps, "没有可用的注册表备份：",
                        "本机没有由 Yomori 记录的解锁事务。计划书要求无有效备份时禁用按钮，而不是猜 Windows 默认值。");
                }

                id = baseline.Id;
            }

            var manifest = RdpTransactionStore.Load(id!);
            if (manifest is null)
            {
                return Fail(steps, "找不到这份备份：", $"事务 {id} 的账本不存在或已损坏。");
            }

            if (manifest.Registry.Count == 0)
            {
                return Fail(steps, "这份备份里没有注册表快照：", $"事务 {manifest.Id}。");
            }

            var before = RdpReadOnlyDetector.Detect();

            // Wrapper 还在加载时，涉及服务接管键的恢复会撕裂系统。本轮白名单里没有服务接管键，
            // 但这条判断留着 —— 以后扩白名单时它会拦住"先还原再卸载"的错误顺序。
            if (before.WrapperState == RdpWrapperState.Installed && ManifestTouchesServiceDll(manifest))
            {
                return Fail(steps, "请先卸载 RDP 解锁：",
                    "这份备份包含服务接管键，而 Wrapper 仍在加载。先「卸载 RDP 解锁」，再还原注册表。", before);
            }

            Report(progress, 1, "检查备份与冲突");
            RdpTransactionManifest restoreTx;

            try
            {
                restoreTx = RdpTransactionStore.Begin(RdpTransactionKind.RegistryRestore);
            }
            catch (Exception ex)
            {
                // 不记账就不恢复：否则"已恢复但查不到记录"，之后的一致性检查全部失真
                return Fail(steps, "无法创建事务账本，未做任何修改：",
                    $"{ex.GetType().Name}: {ex.Message}", before);
            }

            var restored = 0;
            var conflicts = 0;

            foreach (var delta in manifest.Registry)
            {
                cancellationToken.ThrowIfCancellationRequested();

                RdpRegistryValueState current;
                try
                {
                    current = RdpRegistryService.Read(delta.Before.KeyPath, delta.Before.Name);
                }
                catch (Exception ex)
                {
                    Add(steps, "read", "读取当前值", RdpStepOutcome.Fail,
                        $"{delta.Label}：{ex.GetType().Name}: {ex.Message}");
                    continue;
                }

                if (!RdpRegistryService.HasConflict(delta.Before, current))
                {
                    Add(steps, "skip", $"{delta.Label}", RdpStepOutcome.Skipped, "当前值与备份一致，无需恢复。");
                    continue;
                }

                // 冲突：备份的原值、本轮目标值、当前值三者不同 —— 说明期间有人改过第三值
                if (!RdpRegistryService.Matches(delta.After, current)
                    && !RdpRegistryService.Matches(delta.Before, current))
                {
                    conflicts++;
                    Add(steps, "conflict", $"{delta.Label}", RdpStepOutcome.Warn,
                        $"备份原值 {delta.Before.ToDisplay()}；当前值 {current.ToDisplay()}（已被第三方或策略改动，未覆盖）");
                    restoreTx.Registry.Add(new RdpRegistryDelta
                    {
                        Before = current,
                        After = delta.Before,
                        Conflict = true,
                        Note = "已存在第三方改动，未覆盖",
                    });
                    continue;
                }

                var (writeOk, writeError) = RdpRegistryService.Write(delta.Before);
                if (!writeOk)
                {
                    Add(steps, "write", $"{delta.Label}", RdpStepOutcome.Fail, writeError);
                    restoreTx.Registry.Add(new RdpRegistryDelta
                    {
                        Before = current, After = delta.Before, Note = "写入失败：" + writeError,
                    });
                    continue;
                }

                var readBack = RdpRegistryService.Read(delta.Before.KeyPath, delta.Before.Name);
                var verified = RdpRegistryService.Matches(delta.Before, readBack);
                restored++;

                Add(steps, "restore", $"{delta.Label}", verified ? RdpStepOutcome.Ok : RdpStepOutcome.Fail,
                    verified
                        ? $"已恢复为 {delta.Before.ToDisplay()}"
                        : $"回读不符：期望 {delta.Before.ToDisplay()}，实际 {readBack.ToDisplay()}");

                restoreTx.Registry.Add(new RdpRegistryDelta
                {
                    Before = current, After = delta.Before, Applied = true, Verified = verified,
                });
            }

            RdpTransactionStore.AppendStep(restoreTx, $"从 {manifest.Id} 恢复 {restored} 个值，冲突 {conflicts} 个");

            // 需要服务重新加载时提示并按受控流程重启（计划书 5.3 第 5 条）
            if (restored > 0)
            {
                Report(progress, 2, "重启远程桌面服务");
                var (ok, message, _) = RdpServiceControl.Restart(RdpServiceControl.TermServiceName);
                Add(steps, "service", "重启远程桌面服务", ok ? RdpStepOutcome.Ok : RdpStepOutcome.Warn, message);
            }

            var final = RdpReadOnlyDetector.Detect();
            var success = steps.TrueForAll(s => s.Outcome != RdpStepOutcome.Fail);

            RdpTransactionStore.Finish(
                restoreTx,
                success ? RdpTransactionStatus.Completed : RdpTransactionStatus.Failed,
                success ? "" : "部分恢复失败");

            return new RdpOperationResult
            {
                Success = success,
                Summary = restored == 0
                    ? (conflicts > 0
                        ? "注册表已是最初状态，没有需要恢复的值（存在被第三方改动的项，未覆盖）。"
                        : "注册表已经是备份里的状态，无需恢复。")
                    : $"注册表已恢复 {restored} 项"
                      + (conflicts > 0 ? $"，{conflicts} 项因第三方改动未覆盖" : "")
                      + "。注册表已还原，Wrapper 仍安装 —— 需要彻底移除请用「卸载 RDP 解锁」。",
                Detail = RdpUnlockDiagnostics.FormatDetail(final),
                Steps = steps,
                Final = final,
                TransactionId = restoreTx.Id,
            };
        }
    }

    // ==================== 内部步骤 ====================

    private static bool ApplyRegistry(RdpTransactionManifest manifest, List<RdpStepRecord> steps, out string message)
    {
        var notes = new List<string>();
        var allOk = true;

        // 策略键只报告不写：把值写进 Policies 等于替管理员下策略，而且会和域策略打架
        var policy = RdpRegistryService.ReadPolicyValue();
        if (policy.Kind != RdpRegistryKind.NotFound)
        {
            notes.Add($"策略键已存在（{policy.ToDisplay()}），按计划书 5.1 只报告不绕过。");
        }

        foreach (var delta in manifest.Registry)
        {
            // 目标：远程桌面开关写成 DWORD 0（允许连接）
            if (!delta.Before.Name.Equals("fDenyTSConnections", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (delta.Before.KeyPath.StartsWith(RdpRegistryService.PolicyKeyPath, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var target = new RdpRegistryValueState
            {
                KeyPath = delta.Before.KeyPath,
                Name = delta.Before.Name,
                Kind = RdpRegistryKind.Dword,
                NumberValue = 0,
            };

            delta.After = target;

            if (RdpRegistryService.Matches(delta.Before, target))
            {
                notes.Add($"{delta.Label} 已是 DWORD 0，无需修改。");
                delta.Applied = true;
                delta.Verified = true;
                continue;
            }

            var (ok, error) = RdpRegistryService.Write(target);
            if (!ok)
            {
                allOk = false;
                notes.Add($"{delta.Label} 写入失败：{error}");
                continue;
            }

            var readBack = RdpRegistryService.Read(target.KeyPath, target.Name);
            delta.Verified = RdpRegistryService.Matches(target, readBack);
            delta.Applied = true;

            if (!delta.Verified)
            {
                allOk = false;
                notes.Add($"{delta.Label} 回读不符：{readBack.ToDisplay()}");
                continue;
            }

            notes.Add($"{delta.Label} 已改为 DWORD 0（原 {delta.Before.ToDisplay()}）。");
        }

        message = string.Join("；", notes);
        return allOk;
    }

    private static async Task<(bool Ok, string Message)> UpdateIniAsync(
        RdpTransactionManifest manifest,
        string installDir,
        List<RdpStepRecord> steps,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(installDir))
        {
            return (false, "不知道安装目录，无法定位 RDPWInst.exe");
        }

        var tool = Path.Combine(installDir, "RDPWInst.exe");
        if (!File.Exists(tool))
        {
            return (false, $"安装目录里没有 RDPWInst.exe：{installDir}");
        }

        // 更新前的 INI 再备份一次：安装器可能已经动过它
        var iniPath = Path.Combine(installDir, "rdpwrap.ini");
        if (manifest.IniBackup.Length == 0 && File.Exists(iniPath))
        {
            manifest.IniSourcePath = iniPath;
            manifest.IniBackup = RdpTransactionStore.BackupIni(manifest.Id, iniPath);
        }

        var run = await RunToolAsync(tool, "-w", IniTimeout, cancellationToken).ConfigureAwait(false);
        RdpTransactionStore.AppendStep(manifest, $"INI 更新退出码 {run.ExitCode}：{run.OutputTail}");

        if (run.TimedOut)
        {
            return (false, $"-w 在 {IniTimeout.TotalSeconds:0} 秒内未结束，结果不明（未强制结束）");
        }

        if (run.ExitCode != 0)
        {
            return (false, $"-w 退出码 {run.ExitCode}。{run.OutputTail}");
        }

        // 成功退出不等于已更新：回读 Updated 字段（计划书 6.2）
        var updated = ReadIniUpdated(iniPath);
        Add(steps, "ini", "更新 INI", RdpStepOutcome.Ok,
            updated.Length > 0 ? $"-w 已完成，本机 INI Updated={updated}" : "-w 已完成，但读不到 Updated 字段");

        return (true, "");
    }

    private static string ReadIniUpdated(string iniPath)
    {
        try
        {
            if (!File.Exists(iniPath))
            {
                return "";
            }

            foreach (var line in File.ReadLines(iniPath))
            {
                var text = line.Trim();
                if (text.StartsWith("Updated=", StringComparison.OrdinalIgnoreCase))
                {
                    return text.Substring("Updated=".Length).Trim();
                }
            }
        }
        catch
        {
            // 读不到就当没有
        }

        return "";
    }

    /// <summary>
    /// 失败补偿（计划书 5.4）。范围比「还原注册表」大：它还会回滚 INI、恢复服务，
    /// 并在本轮自己装的 Wrapper 上执行一次经核实的卸载。
    /// </summary>
    private static async Task<bool> CompensateAsync(
        RdpTransactionManifest manifest,
        RdpDownloadedTool? uninstaller,
        List<RdpStepRecord> steps,
        CancellationToken cancellationToken)
    {
        var notes = new List<string>();
        var allOk = true;

        // 逆序补偿注册表
        for (var i = manifest.Registry.Count - 1; i >= 0; i--)
        {
            var delta = manifest.Registry[i];
            if (!delta.Applied)
            {
                continue;
            }

            var (ok, error) = RdpRegistryService.Write(delta.Before);
            var verified = ok && RdpRegistryService.Matches(
                delta.Before, RdpRegistryService.Read(delta.Before.KeyPath, delta.Before.Name));

            if (!verified)
            {
                allOk = false;
                notes.Add($"注册表 {delta.Label} 补偿失败：{error}");
            }
        }

        // INI 回滚
        if (manifest.IniBackup.Length > 0)
        {
            var (iniOk, iniMessage) = RdpTransactionStore.RestoreIni(manifest);
            if (!iniOk)
            {
                allOk = false;
                notes.Add("INI 回滚失败：" + iniMessage);
            }
        }

        // 服务：停过的就重新拉起来。TermService 是本流程唯一必然碰到的主服务，
        // 依赖服务的恢复在 RdpServiceControl.Restart 内部按「本来在跑」的清单处理。
        var (serviceOk, serviceMessage, _) = RdpServiceControl.Restart(RdpServiceControl.TermServiceName);
        if (!serviceOk)
        {
            notes.Add("服务恢复：" + serviceMessage);
        }

        // 刚装的 Wrapper：执行经核实的卸载补偿
        if (manifest.InstalledByUs)
        {
            var uninstallPath = uninstaller?.LocalPath ?? manifest.UninstallerPath;
            if (uninstallPath.Length > 0 && File.Exists(uninstallPath))
            {
                var verify = RdpToolDownloader.VerifyExisting(
                    uninstallPath, manifest.UninstallerSha256, uninstaller?.SizeBytes ?? 0);

                if (!verify.Ok)
                {
                    allOk = false;
                    notes.Add("卸载补偿跳过：卸载器二次核对失败（" + verify.Message + "）");
                }
                else
                {
                    var run = await RunToolAsync(uninstallPath, "", ToolTimeout, cancellationToken).ConfigureAwait(false);
                    notes.Add($"卸载补偿退出码 {run.ExitCode}");
                }
            }
            else
            {
                allOk = false;
                notes.Add("卸载补偿不可用：找不到配套卸载器，请按诊断手工处理");
            }
        }

        var summary = notes.Count == 0 ? "无需要补偿的项" : string.Join("；", notes);
        Add(steps, "compensate", "失败补偿", allOk ? RdpStepOutcome.Ok : RdpStepOutcome.Warn, summary);

        RdpTransactionStore.AppendStep(manifest, "补偿：" + summary);
        RdpTransactionStore.Finish(manifest, RdpTransactionStatus.Compensated, summary);

        return allOk;
    }

    private static bool ManifestTouchesServiceDll(RdpTransactionManifest manifest)
    {
        foreach (var delta in manifest.Registry)
        {
            if (delta.Before.KeyPath.Contains(@"\Services\TermService", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 跑一个外部工具并收集输出。**超时不杀进程**（计划书 6.2：正在写文件的工具不强杀）。
    /// </summary>
    private static async Task<(int ExitCode, bool TimedOut, string OutputTail)> RunToolAsync(
        string path, string arguments, TimeSpan timeout, CancellationToken cancellationToken)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = path,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(path) ?? Environment.CurrentDirectory,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };

            using var process = new Process { StartInfo = startInfo };
            var output = new StringBuilder();

            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data is not null)
                {
                    output.AppendLine(e.Data);
                }
            };
            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is not null)
                {
                    output.AppendLine(e.Data);
                }
            };

            if (!process.Start())
            {
                return (-1, false, "进程无法启动");
            }

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(timeout);

            try
            {
                await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
                process.WaitForExit();   // 等异步输出读完
                return (process.ExitCode, false, Tail(output.ToString()));
            }
            catch (OperationCanceledException)
            {
                // 不 Kill：让工具自己把文件写完，调用方按"结果不明"处理
                return (-1, true, Tail(output.ToString()));
            }
        }
        catch (Exception ex)
        {
            return (-1, false, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private static string Tail(string text)
    {
        var trimmed = (text ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            return "（无输出）";
        }

        return trimmed.Length <= 400 ? trimmed : "…" + trimmed.Substring(trimmed.Length - 400);
    }

    private static string Short(string sha) => sha.Length > 12 ? sha.Substring(0, 12) : sha;

    private static void Add(List<RdpStepRecord> steps, string key, string title, RdpStepOutcome outcome, string detail) =>
        steps.Add(new RdpStepRecord { Key = key, Title = title, Outcome = outcome, Detail = detail });

    private static void Report(IProgress<RdpProgressInfo>? progress, int step, string text) =>
        progress?.Report(new RdpProgressInfo(step, TotalSteps, text));

    private static RdpOperationResult Fail(
        List<RdpStepRecord> steps,
        string summaryPrefix,
        string reason,
        RdpUnlockSnapshot? snapshot = null,
        string transactionId = "",
        bool compensated = false)
    {
        var summary = summaryPrefix + reason;
        if (compensated)
        {
            summary += "（已按事务补偿，详见诊断）";
        }

        return new RdpOperationResult
        {
            Success = false,
            Summary = summary,
            Detail = snapshot is null ? "" : RdpUnlockDiagnostics.FormatDetail(snapshot),
            Steps = new List<RdpStepRecord>(steps),
            Final = snapshot,
            TransactionId = transactionId,
        };
    }
}
