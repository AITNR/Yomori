// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Linq;
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
            // ---- 1. 只读预检 ----
            Report(progress, 1, "本机只读预检");
            var before = RdpReadOnlyDetector.Detect();

            // 半截事务：既不能当成功，也不能直接覆盖（计划书 5.2）。
            // 但"上次装成功了只是没退出"这种半截不能把用户卡死 —— 现状已是目标状态就自动结案。
            var stale = RdpTransactionStore.ResolveStale(before);
            if (stale.Blocked)
            {
                return Fail(steps, "上一次操作没有收尾，先处理它：", stale.Message, before);
            }

            if (stale.Message.Length > 0)
            {
                Add(steps, "stale", "收尾上一次未完成的事务", RdpStepOutcome.Warn, stale.Message);
            }
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

            // ---- 1b. Wrapper 状态门禁 ----
            // 只有**明确**「未安装」才允许下载安装，明确「已安装」才允许复用。
            // ThirdParty / Unknown 一律停在诊断，不进下载与安装分支（2026-10-26 检查记录 P0）：
            // 前者会覆盖别人的接管，后者是把「读不出来」当成「没装」。
            if (before.WrapperState == RdpWrapperState.ThirdParty)
            {
                return Fail(steps, "本机的远程桌面服务已被别的组件接管，本次没有做任何修改：",
                    $"ServiceDll 指向 {before.ServiceDllPath}（既不是原版 termsrv.dll，也不是 rdpwrap.dll）。"
                    + "Yomori 不会自动覆盖别人的接管。请先确认那个组件是什么、由谁装的；"
                    + "确认要换成 RDP Wrapper 之后，手工卸掉它再回来点「解锁 RDP」。", before);
            }

            if (before.WrapperState == RdpWrapperState.Unknown)
            {
                return Fail(steps, "读不到远程桌面服务的接管状态，本次没有做任何修改：",
                    "无法确认本机是否已经装了解锁组件，因此既不敢安装也不敢跳过。"
                    + "请以管理员身份重新点「重新检测」；若仍读不到，点「查看诊断」把结果发出来 ——"
                    + "常见原因是 TermService\\Parameters 被策略限制，或该键被安全软件拦读。", before);
            }

            // ---- 2 / 3 / 4：需要下载时才有这两步 ----
            RdpReleaseInfo? release = null;
            RdpDownloadedTool? installer = null;
            RdpDownloadedTool? uninstaller = null;
            var needsDownload = before.WrapperState == RdpWrapperState.NotInstalled;

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
                    .ResolveLatestAsync(network, cancellationToken, forceRefresh: true)
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

                // 来源必须写出来：官方接口 / 本地缓存 / 发布页重定向，三者的校验能力不一样
                // （后两者拿不到官方大小与摘要）。
                var releaseSource = resolved.Source.Length > 0 ? resolved.Source : "官方接口";
                Add(steps, "release", "锁定发布",
                    resolved.Id > 0 ? RdpStepOutcome.Ok : RdpStepOutcome.Warn,
                    $"{resolved.Tag}（release {(resolved.Id > 0 ? resolved.Id.ToString() : "未知")}，"
                    + $"安装器 {installerAsset.Name} / 卸载器 {uninstallerAsset.Name}）；来源：{releaseSource}");

                // ---- 安装器：手动指定的优先，其次官方下载 ----
                if (request.ManualInstallerPath.Length > 0)
                {
                    Report(progress, 3, "校验手动指定的安装包");
                    var (manual, manualError) = RdpToolDownloader.InspectLocalTool(request.ManualInstallerPath);

                    if (manual is null)
                    {
                        return Fail(steps, "手动指定的安装包不可用：", manualError, before);
                    }

                    installer = manual;
                    Add(steps, "manual", "使用手动指定的安装包", RdpStepOutcome.Warn,
                        $"{Path.GetFileName(request.ManualInstallerPath)}（{manual.SizeBytes} 字节，"
                        + $"sha256={Short(manual.Sha256)}）。来源由用户提供，未与官方摘要比对。" + manual.Limits);
                }
                else
                {
                    Report(progress, 3, "下载安装器");
                    var (downloadedInstaller, installerError) = await DownloadWithRetryAsync(
                        installerAsset, resolved.Id, network, steps, "安装器", cancellationToken)
                        .ConfigureAwait(false);

                    if (downloadedInstaller is null)
                    {
                        return Fail(steps, "下载安装器失败：",
                            installerError + ManualHint, before);
                    }

                    installer = downloadedInstaller;
                }

                // ---- 卸载器：手动模式允许没有（装完就有本地控制台工具可卸） ----
                Report(progress, 3, "下载卸载器");
                var (downloadedUninstaller, uninstallerError) = await DownloadWithRetryAsync(
                    uninstallerAsset, resolved.Id, network, steps, "卸载器", cancellationToken)
                    .ConfigureAwait(false);

                if (downloadedUninstaller is null)
                {
                    if (request.ManualInstallerPath.Length == 0)
                    {
                        // 卸载器拿不到就别装了：装完无法按计划书卸载，等于留一个卸不掉的东西
                        return Fail(steps, "下载配套卸载器失败：", uninstallerError + "（没有卸载器就不安装）", before);
                    }

                    // 手动模式下继续：装完之后安装目录里会有 RDPWInst.exe，「卸载」按钮就靠它。
                    // 代价是这一步失败时无法自动回滚，所以必须写成「注意」而不是「通过」。
                    Add(steps, "uninstaller", "下载配套卸载器", RdpStepOutcome.Warn,
                        $"{uninstallerError}。本轮继续安装：装完后可用安装目录里的 RDPWInst.exe 卸载；"
                        + "但如果本次安装中途失败，将无法自动回滚。");
                }
                else
                {
                    uninstaller = downloadedUninstaller;

                    Add(steps, "download", "下载与校验", RdpStepOutcome.Ok,
                        $"{installer.Asset.Name} {installer.SizeBytes} 字节 sha256={Short(installer.Sha256)}；"
                        + $"{uninstaller.Asset.Name} sha256={Short(uninstaller.Sha256)}。" + installer.Limits);
                }
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

            // 备份目录权限：做不到「只有 SYSTEM 与 Administrators 可写」就不动系统
            // （2026-10-26 检查记录 P2：原实现把收紧结果丢掉，照常改系统）
            if (!manifest.BackupAclVerified)
            {
                RdpTransactionStore.Finish(manifest, RdpTransactionStatus.Failed, "备份目录权限未能收紧");
                return Fail(steps, "备份目录的权限没能收紧，未做任何修改：",
                    $"无法确认 {RdpTransactionStore.DirectoryFor(manifest.Id)} 只有管理员与 SYSTEM 可写，"
                    + "所以不开始修改系统 —— 备份是恢复的唯一依据，它不能放在别人能改的位置。"
                    + "请确认 %ProgramData%\\Yomori 的权限没被放宽，或以管理员身份重试。", before, manifest.Id);
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
            var installedThisRun = false;

            if (needsDownload && installer is not null)
            {
                Report(progress, 5, "运行官方安装器");
                var verify = RdpToolDownloader.VerifyExisting(installer.LocalPath, installer.Sha256, installer.SizeBytes);
                if (!verify.Ok)
                {
                    RdpTransactionStore.Finish(manifest, RdpTransactionStatus.Failed, "安装器二次核对失败：" + verify.Message);
                    return Fail(steps, "安装器在下载后被改动过，已停止：", verify.Message, before);
                }

                // 上游这个「安装器」是一键图形程序：装完会照官方 install.bat 的做法把 RDP_CnC.exe
                // 拉起来当状态窗口 —— 既挡屏幕，又让我们永远等不到"进程退出"（2026-10-26 真机）。
                // 所以优先在隐藏桌面上启动：窗口照建，但不会出现在用户屏幕上，句柄也不继承。
                var hidden = RdpHiddenProcess.StartTracked(installer.LocalPath, RdpToolRunner.InstallArguments);
                var run = new RdpToolRunResult(0, false, "（隐藏桌面启动，无输出）");

                if (hidden.Record is not null)
                {
                    // 先落盘再等：超时/崩溃之后要靠这条记录判断它还在不在写系统文件。
                    // 落不下去就不许开跑 —— 打开一个我们无法跟踪的安装器等于放弃并发保护
                    // （2026-10-26 检查记录 P0）。
                    manifest.ToolProcesses.Add(hidden.Record);
                    if (!RdpTransactionStore.TrySaveCritical(manifest, out var ledgerError))
                    {
                        return Fail(steps, "事务账本写不进磁盘，已停止，未启动安装器：",
                            $"{ledgerError}（{RdpTransactionStore.DirectoryFor(manifest.Id)}）。"
                            + "账本写不下去就没法在超时或崩溃后判断安装器做到哪一步，所以不开始安装。"
                            + "请检查磁盘空间与 %ProgramData% 权限后重试。", before, manifest.Id);
                    }

                    RdpTransactionStore.AppendStep(manifest, $"安装器在隐藏桌面上启动，pid={hidden.Record.Pid}");

                    var reached = await WaitUntilAsync(
                        () => RdpReadOnlyDetector.Detect().WrapperState == RdpWrapperState.Installed,
                        ToolTimeout,
                        cancellationToken).ConfigureAwait(false);

                    if (!reached)
                    {
                        // 关键：超时只是「我们不等了」，不代表安装器停了。
                        // 它还在写文件时必须让事务停在 Running（结果不明），绝不能说「失败」了事。
                        var (stillRunning, runningDetail) = RdpToolProcessTree.Check(hidden.Record);

                        if (stillRunning)
                        {
                            Add(steps, "install", "运行官方安装器", RdpStepOutcome.Fail,
                                $"安装器 {ToolTimeout.TotalSeconds:0} 秒内没接管 ServiceDll，而且它还在运行"
                                + $"（{runningDetail}），结果不明。");

                            RdpTransactionStore.AppendStep(
                                manifest, $"结论：安装器仍在运行（{runningDetail}），事务保持未完成");
                            RdpTransactionStore.Save(manifest);

                            return Fail(steps, "安装器还没结束，结果不明：",
                                "它仍在运行（很可能停在隐藏桌面的某个提示上），所以现在既不能说成功也不能说失败，"
                                + "事务保持「未完成」。不要在这个时候点解锁或卸载 —— 两边会抢同一个服务文件。"
                                + "等它自行结束后点「重新检测」；明显卡住就重启电脑。"
                                + "如果重启后仍挡住操作，点「查看诊断」把结果发出来。", before, manifest.Id);
                        }

                        Add(steps, "install", "运行官方安装器", RdpStepOutcome.Fail,
                            $"安装器在隐藏桌面上 {ToolTimeout.TotalSeconds:0} 秒内未完成，ServiceDll 仍未被接管；"
                            + "已确认它的进程树全部退出。");

                        // 到这里才有结论：进程树确实退出了，系统不会再被它继续改写。
                        RdpTransactionStore.AppendStep(manifest, "结论：安装器已退出且未生效，事务记为失败");
                        RdpTransactionStore.Finish(
                            manifest,
                            RdpTransactionStatus.Failed,
                            "安装器在隐藏桌面上未在超时内接管 ServiceDll，进程树已退出");

                        return Fail(steps, "安装器没有生效：",
                            "它的窗口开在隐藏桌面上（你看不到），可能停在某个提示上。重启电脑后再试一次；"
                            + "仍然不行请点「查看诊断」把结果发出来。", before, manifest.Id);
                    }

                    Add(steps, "install", "运行官方安装器", RdpStepOutcome.Ok,
                        "已在隐藏桌面上完成安装 —— 那个图形安装器的窗口不会出现在你屏幕上。");

                    // 上游安装器按官方 install.bat 的做法会拉起 RDP_CnC.exe 并常驻。
                    // 这不是我们启动的，也没有开关能拒绝；如实报告，别假装它不存在
                    // （2026-10-26 检查记录 P1）。
                    ReportCnCResidue(manifest, steps);
                }
                else
                {
                    // 隐藏桌面建不出来（会话异常 / 权限被改，极少见）→ 退回可见启动，并如实说明会弹窗
                    Add(steps, "install", "运行官方安装器", RdpStepOutcome.Warn,
                        $"无法在隐藏桌面上启动（{hidden.Error}），改为普通启动；此时会弹出第三方安装界面。");

                    run = await RdpToolRunner
                        .RunAsync(installer.LocalPath, RdpToolRunner.InstallArguments, ToolTimeout, cancellationToken)
                        .ConfigureAwait(false);

                    RdpTransactionStore.AppendStep(
                        manifest, $"安装器（{RdpToolRunner.InstallArguments}）退出码 {run.ExitCode}：{run.OutputTail}");

                    var reachedByRun = RdpReadOnlyDetector.Detect().WrapperState == RdpWrapperState.Installed;

                    if (!reachedByRun)
                    {
                        if (run.TimedOut)
                        {
                            var (toolRunning, toolDetail) = RdpToolRunner.DescribeRunning(run, "安装器");
                            Add(steps, "install", "运行官方安装器", RdpStepOutcome.Fail,
                                $"安装器 {ToolTimeout.TotalSeconds:0} 秒未结束，且 ServiceDll 仍未被接管。"
                                + (toolRunning ? $"它还在运行（{toolDetail}），事务保持未完成。" : ""));
                            return Fail(steps, "安装器超时，结果不明：",
                                "没有强制结束安装器。请等它自行结束后重新检测；若一直不结束，重启电脑后再来。",
                                before, manifest.Id);
                        }

                        var compensated = await CompensateAsync(manifest, uninstaller, steps, cancellationToken)
                            .ConfigureAwait(false);
                        return Fail(steps, "安装器报告成功，但本机没有接管：",
                            $"ServiceDll 仍为 {RdpReadOnlyDetector.Detect().ServiceDllPath}。"
                            + "可能是安全软件拦截，或系统版本不被该版本支持。", before, manifest.Id, compensated);
                    }

                    if (run.TimedOut || run.ExitCode != 0)
                    {
                        Add(steps, "install", "运行官方安装器", RdpStepOutcome.Warn,
                            $"退出码 {run.ExitCode}{(run.TimedOut ? "、超时" : "")}，"
                            + "但 ServiceDll 已被接管 —— 按状态判定为已安装，继续后续步骤。");
                    }
                    else
                    {
                        Add(steps, "install", "运行官方安装器", RdpStepOutcome.Ok, "退出码 0，安装完成。");
                    }
                }

                // 安装目录落定（后续 INI 更新要靠它定位 RDPWInst.exe）
                var afterInstall = RdpReadOnlyDetector.Detect();
                installDir = afterInstall.InstallDirectory;

                // 本轮真的装了组件 —— 后面「要不要重启服务」「INI 判据用哪份状态」都靠它
                installedThisRun = afterInstall.WrapperState == RdpWrapperState.Installed;

                // 装完先清点文件。上游 Release 的安装器只带 rdpwrap.dll / rdpwrap.ini /
                // RDP_CnC.exe，**不带 RDPWInst.exe**（v1.8.9.9 的说明只写了「给 CnC 加了
                // Update ini 与 Restart TermService 两个按钮」，却没把那个控制台工具打进包），
                // 所以「在线更新 INI」在新装的机器上很可能根本没有工具可跑 —— 提前说清，
                // 不要等到调用时才发现，也别让用户以为下载缺了东西。
                var inventory = CheckInstallFiles(installDir);
                Add(steps, "files", "安装文件清点",
                    inventory.AllCore ? RdpStepOutcome.Ok : RdpStepOutcome.Warn, inventory.Summary);

                // 安装器会用它自带的 rdpwrap.ini 无条件覆盖现有文件，而且不问你。
                // 2026-10-26 真机：覆盖进来的是一份 31 KB / 67 个段、段名全是 10.0.10240.x 的
                // 残本（Updated 却写着 2026-10-07），把本来覆盖当前构建的那份顶掉了。
                // 备份在快照那一步已经留好，这里发现「新不如旧」就换回来。
                RestoreIniIfInstallMadeItWorse(manifest, installDir, before.TermsrvVersion, steps);
            }
            else
            {
                Report(progress, 5, "跳过安装（已有安装）");
                Add(steps, "install", "运行官方安装器", RdpStepOutcome.Skipped, "本机已有有效安装，按计划书 4.1 不强制重装。");
            }

            // ---- 6. 注册表事务 ----
            Report(progress, 6, "应用注册表变更");
            var registry = ApplyRegistry(manifest);

            // 「无需修改」也算通过，但**不算改动** —— 后面决定要不要重启服务靠这个区分
            var registryChanged = manifest.Registry.Any(d => !RdpRegistryService.Matches(d.Before, d.After));

            Add(steps, "registry", "注册表变更", registry.Ok ? RdpStepOutcome.Ok : RdpStepOutcome.Fail, registry.Message);

            // 账本写不进去：「系统已动过、但没留下恢复证据」。这时候再补偿就是**没有依据地**改系统，
            // 所以直接停手，并把话说到底（2026-10-26 检查记录 P0）。
            if (registry.LedgerError.Length > 0)
            {
                return Fail(steps, "系统已经改过，但事务账本写不进磁盘，已停止且未自动回滚：",
                    $"{registry.LedgerError}（{RdpTransactionStore.DirectoryFor(manifest.Id)}）。"
                    + "记账失败意味着无法可靠执行逆序补偿，所以这次没有自动恢复。"
                    + "请先检查磁盘空间与 %ProgramData% 权限，再把「查看诊断」的结果发出来核对当前状态。",
                    before, manifest.Id);
            }

            if (!registry.Ok)
            {
                var compensated = await CompensateAsync(manifest, uninstaller, steps, cancellationToken)
                    .ConfigureAwait(false);
                return Fail(steps, "注册表变更失败：", registry.Message, before, manifest.Id, compensated);
            }

            // ---- 7. 备份 INI + Update INI ----
            var iniUpdated = false;
            var iniFailure = "";

            // ⚠️ 判据用**安装之后**的状态，不是安装之前那个 before：
            // 全新机器上 before 是「未知」（那时还没有 INI 文件），拿它判必然要走一次在线更新；
            // 而安装包自带一份 rdpwrap.ini，装完很可能已经覆盖当前构建 —— 白跑一趟还会因为
            // 上不了 GitHub 把整轮判死（2026-10-26 真机就是这么把刚装好的组件又卸掉的）。
            //
            // ⚠️ 而且只认**静态**结论（IniState）：监听器在不在是「有没有生效」的问题，
            // 不是「文件缺不缺段」的问题。混进来会让运行态暂时不可用触发一次重复联网
            // （2026-10-26 检查记录 P1）。
            var iniProbe = installedThisRun ? RdpReadOnlyDetector.Detect() : before;
            var iniPathNow = iniProbe.IniPath.Length > 0 ? iniProbe.IniPath : before.IniPath;
            var termsrvVersion = before.TermsrvVersion.Length > 0 ? before.TermsrvVersion : iniProbe.TermsrvVersion;

            if (!request.UpdateIni)
            {
                Report(progress, 7, "跳过 INI 更新");
                Add(steps, "ini", "更新 INI", RdpStepOutcome.Skipped, "本轮请求没有要求更新 INI。");
            }
            else if (!RdpIniUpdatePolicy.NeedsOnlineUpdate(iniProbe.IniState))
            {
                Report(progress, 7, "INI 已覆盖当前版本");
                Add(steps, "ini", "更新 INI", RdpStepOutcome.Skipped,
                    $"本机 rdpwrap.ini 已含与 termsrv.dll {termsrvVersion} 对应的补丁段（按文件内容判定），"
                    + "无需在线更新（-w 会去 GitHub 下载 INI，断网时必然失败）。");
            }
            else
            {
                Report(progress, 7, "更新 INI");
                var acquired = await AcquireIniAsync(
                    manifest, installDir, iniPathNow, termsrvVersion,
                    request.ManualIniPath, steps, cancellationToken).ConfigureAwait(false);

                if (acquired.Unknown)
                {
                    // 结果不明：不能当成普通「注意」继续往下重启服务。
                    // 工具可能还在写 INI，重启后的检测与实际落盘时序会对不上（检查记录 P1）。
                    Add(steps, "ini", "更新 INI", RdpStepOutcome.Fail, acquired.Message);
                    RdpTransactionStore.AppendStep(manifest, "结论：INI 更新结果不明，事务保持未完成");
                    RdpTransactionStore.Save(manifest);

                    return Fail(steps, "INI 更新结果不明，流程停在这里：",
                        acquired.Message + "　没有强制结束它，事务保持「未完成」。"
                        + "等它自行结束后点「重新检测」；在此之前不要再点解锁或卸载。", before, manifest.Id);
                }

                if (acquired.Ok)
                {
                    iniUpdated = acquired.Changed;
                }
                else
                {
                    // 失败后必须再回读一次：取 INI 失败时我们已经尝试把好的那份换回来了
                    // （本轮备份 / 历史事务备份）。换回来的那份如果确实覆盖当前构建，
                    // 这一项就是**可用**的 —— 不能因为「-w 失败」就报成「INI 没能更新」，
                    // 否则界面会劝用户去手工找文件，而他机器上已经是对的了。
                    var afterIni = RdpReadOnlyDetector.Detect();

                    if (!RdpIniUpdatePolicy.NeedsOnlineUpdate(afterIni.IniState))
                    {
                        // 文件确实被换过 → 后面该重启服务
                        iniUpdated = true;

                        Add(steps, "ini", "更新 INI", RdpStepOutcome.Warn,
                            $"{acquired.Message}　已回读确认：本机 INI 现在含 [{termsrvVersion}] 段，"
                            + "Support 可正常判定，继续后续步骤。");
                    }
                    else
                    {
                        // 不再因为取不到 INI 就把整轮判死并回滚：组件已经装好了，
                        // 回滚等于让用户从头再下一遍、再装一遍（2026-10-26 真机就是这个后果，
                        // 还顺带弹了个 CnC 的错）。
                        // 取不到 INI 是「上不了 GitHub」或「这个系统版本还没被覆盖」，
                        // 两种都不是回滚能解决的问题 —— 交给最后的内置检测如实说 Support 未覆盖。
                        iniFailure = acquired.Message + IniManualHint(installDir);
                        Add(steps, "ini", "更新 INI", RdpStepOutcome.Warn, iniFailure);
                    }
                }
            }

            // ---- 8. 显式重启 TermService ----
            // 只有本轮**真的改过**注册表或 INI 才需要重启：什么都没改还去停一次服务，
            // 除了打断远程会话、清掉监听以外没有任何作用。
            // 「本轮装过组件」同样是必须重启的理由：安装器换了 ServiceDll，正在跑的
            // TermService 还是旧映像，不重启就不会有 RDP-Tcp 监听。
            if (request.RestartService && !registryChanged && !iniUpdated && !installedThisRun)
            {
                Report(progress, 8, "跳过服务重启");
                Add(steps, "service", "重启远程桌面服务", RdpStepOutcome.Skipped,
                    "本轮注册表与 INI 都没有变化，无需重启服务。");
            }
            else if (request.RestartService)
            {
                Report(progress, 8, "重启远程桌面服务");

                // 先落盘再动手：万一停到一半崩溃，账本里要已经写着「动过它」
                manifest.ServiceTouched = true;

                // 这条写不下去就不动服务：停启服务是有破坏性的动作（打断远程会话），
                // 事后必须能在账本里查到「谁停过它」（2026-10-26 检查记录 P0）
                if (!RdpTransactionStore.TrySaveCritical(manifest, out var serviceLedgerError))
                {
                    return Fail(steps, "事务账本写不进磁盘，已停止，没有动远程桌面服务：",
                        $"{serviceLedgerError}（{RdpTransactionStore.DirectoryFor(manifest.Id)}）。"
                        + "请检查磁盘空间与 %ProgramData% 权限后重试。", before, manifest.Id);
                }

                var restart = RdpServiceControl.Restart(RdpServiceControl.TermServiceName);
                foreach (var service in restart.Touched)
                {
                    if (!manifest.ServicesTouched.Contains(service))
                    {
                        manifest.ServicesTouched.Add(service);
                    }
                }

                var restartOk = restart.CoreOk;
                var restartMessage = restart.Message;

                RdpTransactionStore.AppendStep(manifest, "重启服务：" + restartMessage);
                Add(steps, "service", "重启远程桌面服务", restartOk ? RdpStepOutcome.Ok : RdpStepOutcome.Fail, restartMessage);

                // 宿主起来了但依赖服务没起来：不算失败，但必须说出来（设备重定向会失效）
                if (restartOk && !restart.DependentsOk)
                {
                    Add(steps, "service-dependents", "依赖服务恢复", RdpStepOutcome.Warn,
                        "远程桌面服务已重启，但上面列出的依赖服务没能恢复。"
                        + "受影响的是设备/驱动器重定向这类功能，远程连接本身可用；"
                        + "重启电脑可以一次性恢复它们。");
                }

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

            // 监听器没起来是"还没生效"而不是"装坏了"：这类情况最常见的原因是
            // 系统刚更新过 termsrv.dll，正在跑的是旧映像，必须重启才能让钩子挂上。
            // 所以单独给一条明确、可执行的建议，别让用户去猜。
            var needsReboot = !finalOk
                              && final.WrapperState == RdpWrapperState.Installed
                              && final.ListenerState != RdpListenerState.Listening;

            if (needsReboot)
            {
                Add(steps, "reboot", "需要重启电脑", RdpStepOutcome.Warn,
                    "解锁组件已安装，但 WinStation 里还没有 RDP-Tcp 监听 —— 钩子尚未生效。"
                    + "请重启电脑，开机后回本页点「重新检测」。");
            }

            var nothingChanged = before.WrapperState == RdpWrapperState.Installed
                                 && !registryChanged && !iniUpdated && !installedThisRun;

            var summary = finalOk
                ? nothingChanged
                    ? "本机已经处于解锁状态，本轮没有需要改动的地方（注册表与 INI 都无需更新），服务也没有重启。"
                    : "成功，可以前往「设置 → 账户 → 其他用户」里添加新用户。添加完成后，在 Yomori 通道管理中保存该账户。"
                : iniFailure.Length > 0
                    ? "组件已安装，但本机 rdpwrap.ini 没能更新 —— 所以远程桌面暂时还用不了。"
                      + "这一步失败通常是上不了 GitHub（该命令要联网）或这个系统版本还没被社区覆盖，"
                      + "换 INI 也解决不了的情况只能等上游。"
                      + "拿到能覆盖本机版本的 INI 后点「指定 INI」，再点一次「解锁 RDP」即可，不需要重装。"
                : needsReboot
                    ? "解锁组件已安装，但远程桌面监听（RDP-Tcp）还没出现，所以现在还不能远程登录。"
                      + "请重启电脑，开机后再点「重新检测」——系统刚更新过时这一条尤其常见。"
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

            // 同解锁流程：做不到「备份只有管理员能写」就不开始改系统（检查记录 P2）
            if (!restoreTx.BackupAclVerified)
            {
                RdpTransactionStore.Finish(restoreTx, RdpTransactionStatus.Failed, "备份目录权限未能收紧");
                return Fail(steps, "备份目录的权限没能收紧，未做任何修改：",
                    $"无法确认 {RdpTransactionStore.DirectoryFor(restoreTx.Id)} 只有管理员与 SYSTEM 可写。"
                    + "请确认 %ProgramData%\\Yomori 的权限没被放宽，或以管理员身份重试。",
                    before, restoreTx.Id);
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
                    Before = current,
                    After = delta.Before,
                    Applied = true,
                    Verified = verified,
                    ReadBack = readBack.ToDisplay(),
                });

                // 每写一项就落盘：崩溃后要能看出「恢复到哪一个值了」
                if (!RdpTransactionStore.TrySaveCritical(restoreTx, out var restoreLedgerError))
                {
                    return Fail(steps, "系统已经改过，但事务账本写不进磁盘，已停止：",
                        $"{restoreLedgerError}（{RdpTransactionStore.DirectoryFor(restoreTx.Id)}）。"
                        + "请检查磁盘空间与 %ProgramData% 权限后重新点「还原注册表」。",
                        before, restoreTx.Id);
                }
            }

            RdpTransactionStore.AppendStep(restoreTx, $"从 {manifest.Id} 恢复 {restored} 个值，冲突 {conflicts} 个");

            // 需要服务重新加载时提示并按受控流程重启（计划书 5.3 第 5 条）
            var serviceOk = true;
            if (restored > 0)
            {
                Report(progress, 2, "重启远程桌面服务");
                var restart = RdpServiceControl.Restart(RdpServiceControl.TermServiceName);
                serviceOk = restart.CoreOk;

                // 重启失败是**失败**，不是「注意」：注册表改完了但服务没重新加载，
                // 用户看到的仍然是旧行为，界面却显示绿色完成（2026-10-26 检查记录 P0）。
                Add(steps, "service", "重启远程桌面服务",
                    serviceOk ? RdpStepOutcome.Ok : RdpStepOutcome.Fail,
                    serviceOk ? restart.Message : restart.Message + "　改动要等服务重新启动后才生效。");

                if (serviceOk && !restart.DependentsOk)
                {
                    Add(steps, "service-dependents", "依赖服务恢复", RdpStepOutcome.Warn,
                        "远程桌面服务已重启，但有依赖服务没能恢复（设备/驱动器重定向可能失效）；"
                        + "重启电脑可以一次性恢复。");
                }
            }

            var final = RdpReadOnlyDetector.Detect();
            var stepFailed = steps.Exists(s => s.Outcome == RdpStepOutcome.Fail);

            // 成功条件不能只看「有没有红」，还要看结果本身：没覆盖掉的冲突、
            // 没重启成功的服务，都是「部分恢复」，不是成功（2026-10-26 检查记录 P0）。
            var success = !stepFailed && conflicts == 0 && serviceOk;

            var problems = new List<string>();
            if (conflicts > 0)
            {
                problems.Add($"{conflicts} 项因第三方改动未覆盖（原值未恢复）");
            }

            if (!serviceOk)
            {
                problems.Add("TermService 重启失败，改动尚未生效");
            }

            if (stepFailed)
            {
                problems.Add("有写入或回读失败项，详见步骤");
            }

            Add(steps, "result", "恢复结果",
                success ? RdpStepOutcome.Ok : RdpStepOutcome.Fail,
                success
                    ? $"已恢复 {restored} 项，没有遗留项。"
                    : $"部分恢复：已恢复 {restored} 项；{string.Join("；", problems)}。");

            RdpTransactionStore.Finish(
                restoreTx,
                success ? RdpTransactionStatus.Completed : RdpTransactionStatus.Failed,
                success ? "" : "部分恢复：" + string.Join("；", problems));

            var summary = restored == 0
                ? (conflicts > 0
                    ? "注册表已是备份里的状态，但存在被第三方改动、因而未覆盖的项（见下方诊断）。"
                    : "注册表已经是备份里的状态，无需恢复。")
                : $"注册表已恢复 {restored} 项"
                  + (conflicts > 0 ? $"，{conflicts} 项因第三方改动未覆盖" : "")
                  + (serviceOk ? "" : "；TermService 重启失败，改动要等服务重新启动后才生效")
                  + $"（事务 {restoreTx.Id}）。注册表已还原，Wrapper 仍安装 —— 需要彻底移除请用「卸载 RDP 解锁」。";

            return new RdpOperationResult
            {
                Success = success,
                Summary = summary,
                Detail = RdpUnlockDiagnostics.FormatDetail(final),
                Steps = steps,
                Final = final,
                TransactionId = restoreTx.Id,
            };
        }
    }

    // ==================== 内部步骤 ====================

    /// <summary>
    /// 逐项应用注册表变更，**每一项都先落盘再写系统、写完立刻落盘回读结果**。
    /// </summary>
    /// <remarks>
    /// 为什么不是「全部写完后统一保存」：进程在「值已写入、账本还没更新」之间崩溃时，
    /// 磁盘上的账本仍然写着 <c>After == Before</c>，恢复端就完全看不出这个值被改过
    /// （2026-10-26 检查记录 P1）。
    /// </remarks>
    private static RegistryApplyOutcome ApplyRegistry(RdpTransactionManifest manifest)
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

            // ⚠️ 写入**之前**先把目标值落盘（2026-10-26 检查记录 P1）：
            // 只有 Before 的账本在崩溃后无法判断「这个值到底写没写过」，
            // 所谓「逆序补偿」也就失去了依据。
            if (!RdpTransactionStore.TrySaveCritical(manifest, out var ledgerErrorBeforeWrite))
            {
                return new RegistryApplyOutcome(
                    false, $"{delta.Label} 未写入：账本写不进磁盘", ledgerErrorBeforeWrite);
            }

            var (ok, error) = RdpRegistryService.Write(target);
            if (!ok)
            {
                allOk = false;
                delta.Note = "写入失败：" + error;
                notes.Add($"{delta.Label} 写入失败：{error}");
                continue;
            }

            var readBack = RdpRegistryService.Read(target.KeyPath, target.Name);
            delta.Verified = RdpRegistryService.Matches(target, readBack);
            delta.Applied = true;
            delta.ReadBack = readBack.ToDisplay();

            // 写入后立刻落盘「已应用 / 是否验证 / 回读到的值」
            if (!RdpTransactionStore.TrySaveCritical(manifest, out var ledgerErrorAfterWrite))
            {
                return new RegistryApplyOutcome(
                    false,
                    $"{delta.Label} 已写入（回读 {(delta.Verified ? "相符" : "不符")}），但账本写不进磁盘",
                    ledgerErrorAfterWrite);
            }

            if (!delta.Verified)
            {
                allOk = false;
                notes.Add($"{delta.Label} 回读不符：{readBack.ToDisplay()}");
                continue;
            }

            notes.Add($"{delta.Label} 已改为 DWORD 0（原 {delta.Before.ToDisplay()}）。");
        }

        // 「无需修改」的项也要把 After 落到盘上：账本要能解释本轮到底想改什么
        if (!RdpTransactionStore.TrySaveCritical(manifest, out var ledgerErrorAtEnd))
        {
            return new RegistryApplyOutcome(allOk, string.Join("；", notes), ledgerErrorAtEnd);
        }

        return new RegistryApplyOutcome(allOk, string.Join("；", notes), "");
    }

    /// <summary>注册表应用结果。<see cref="LedgerError"/> 非空表示账本写不进去，调用方必须停手。</summary>
    private readonly record struct RegistryApplyOutcome(bool Ok, string Message, string LedgerError);

    /// <summary>
    /// 取一份能覆盖当前构建的 INI：用户指定 → <c>RDPWInst.exe -w</c>。
    /// </summary>
    /// <remarks>
    /// <para>只有这两条路。<c>-w</c> 用 RDPWInst 自己的 WinInet，不认本程序的代理；
    /// 上不了 GitHub 时它以退出码 5 失败 —— 那属于网络/配额问题，**不再想办法绕**
    /// （2026-10-26 用户明确要求：这种情况不要管）。用户手上有 INI 就用「指定 INI」给他。</para>
    /// <para>拿不到也不让整轮失败：组件已经装好了，回滚只会让人从头再来一遍。
    /// 所以调用方把失败降成「注意」，交给最后的内置检测说清「Support 未覆盖」。</para>
    /// </remarks>
    private static async Task<IniAcquisition> AcquireIniAsync(
        RdpTransactionManifest manifest,
        string installDir,
        string iniPath,
        string version,
        string manualIniPath,
        List<RdpStepRecord> steps,
        CancellationToken cancellationToken)
    {
        if (iniPath.Length == 0)
        {
            return new IniAcquisition(false, "找不到 rdpwrap.ini 的位置：安装目录未知。", false, false);
        }

        var failures = new List<string>();

        // ---- 1. 用户指定的 INI ----
        if (manualIniPath.Length > 0)
        {
            var (ok, message) = ApplyIniFile(manifest, iniPath, manualIniPath, version);
            if (ok)
            {
                Add(steps, "ini-manual", "使用指定的 INI", RdpStepOutcome.Ok, message);
                return new IniAcquisition(true, message, true, false);
            }

            failures.Add("手动指定的 INI 不可用：" + message);
            Add(steps, "ini-manual", "使用指定的 INI", RdpStepOutcome.Warn, message);
        }

        // ---- 2. 交给 RDPWInst -w ----
        // 先看工具在不在：上游 Release 安装器不带 RDPWInst.exe，
        // 在这种机器上「在线更新」这条路根本不存在，直接说清楚，别让用户等一个必然的失败。
        if (!File.Exists(Path.Combine(installDir, "RDPWInst.exe")))
        {
            failures.Add("安装目录里没有 RDPWInst.exe —— 上游的 Release 安装器不带这个控制台工具，"
                         + "所以在这里没法在线更新 INI（CnC 的「Update ini」按钮同样会报 not found）。");
            return new IniAcquisition(false, string.Join("；", failures), false, false);
        }

        var update = await UpdateIniAsync(manifest, installDir, version, steps, cancellationToken)
            .ConfigureAwait(false);
        if (update.Ok)
        {
            return new IniAcquisition(true, update.Message, true, false);
        }

        failures.Add(update.Message);
        return new IniAcquisition(false, string.Join("；", failures), false, update.Unknown);
    }

    /// <summary>
    /// 取 INI 的结果。<see cref="Unknown"/> 为真表示工具结果不明（超时且进程可能还在写文件），
    /// 调用方必须停手，而不是当成普通「注意」继续重启服务。
    /// </summary>
    private readonly record struct IniAcquisition(bool Ok, string Message, bool Changed, bool Unknown);

    /// <summary>把一个本地 INI 文件应用到安装目录（先备份，再校验，最后原子替换）。</summary>
    private static (bool Ok, string Message) ApplyIniFile(
        RdpTransactionManifest manifest,
        string iniPath,
        string sourcePath,
        string version)
    {
        try
        {
            if (!File.Exists(sourcePath))
            {
                return (false, $"文件不存在：{sourcePath}");
            }

            var text = File.ReadAllText(sourcePath);
            return WriteIni(manifest, iniPath, text, version);
        }
        catch (Exception ex)
        {
            return (false, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// 校验并写入 INI。写之前先把现有文件备份进事务目录 —— 计划书 5.4 要求可回滚。
    /// </summary>
    private static (bool Ok, string Message) WriteIni(
        RdpTransactionManifest manifest,
        string iniPath,
        string content,
        string version)
    {
        try
        {
            if (version.Length > 0 && !content.Contains($"[{version}]", StringComparison.Ordinal))
            {
                return (false, $"这份 INI 里没有 [{version}] 段，换上去也不会生效。");
            }

            if (manifest.IniBackup.Length == 0 && File.Exists(iniPath))
            {
                manifest.IniSourcePath = iniPath;
                manifest.IniBackup = RdpTransactionStore.BackupIni(manifest.Id, iniPath);
            }

            var dir = Path.GetDirectoryName(iniPath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            // 先写临时文件再替换：半截的 INI 会让服务加载失败，比没有更糟
            var temp = iniPath + ".yomori.tmp";
            File.WriteAllText(temp, content);

            if (File.Exists(iniPath))
            {
                File.Replace(temp, iniPath, null);
            }
            else
            {
                File.Move(temp, iniPath);
            }

            var updated = "";
            foreach (var line in content.Split('\n'))
            {
                if (line.StartsWith("Updated=", StringComparison.OrdinalIgnoreCase))
                {
                    updated = line.Substring(8).Trim();
                    break;
                }
            }

            return (true, $"已覆盖 {iniPath}（{content.Length} 字符"
                          + (updated.Length > 0 ? $"，Updated={updated}" : "") + "）");
        }
        catch (Exception ex)
        {
            return (false, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// 跑 <c>RDPWInst.exe -w</c> 更新 INI。
    /// </summary>
    /// <remarks>
    /// ⚠️ <c>-w</c> 的语义是「**去 GitHub 下载最新 INI**」，不是「按当前构建号重写本地文件」。
    /// 2026-10-26 实测输出：
    /// <code>
    /// [*] Checking for updates...
    /// [*] Current update date: 2026.10.03
    /// [-] Failed to download latest INI from GitHub.
    /// </code>
    /// 退出码 5。它走 RDPWInst 自己的 WinInet，**不认本程序配置的代理**，
    /// 所以在受限网络下这一步失败只说明「上不了 GitHub」，不说明本机 INI 有问题。
    /// 因此调用方只在「本地 INI 确实不覆盖当前版本」时才该跑它。
    /// </remarks>
    private static async Task<(bool Ok, string Message, bool Unknown)> UpdateIniAsync(
        RdpTransactionManifest manifest,
        string installDir,
        string version,
        List<RdpStepRecord> steps,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(installDir))
        {
            return (false, "不知道安装目录，无法定位 RDPWInst.exe", false);
        }

        var tool = Path.Combine(installDir, "RDPWInst.exe");
        if (!File.Exists(tool))
        {
            return (false, $"安装目录里没有 RDPWInst.exe：{installDir}", false);
        }

        // 更新前的 INI 再备份一次：安装器可能已经动过它
        var iniPath = Path.Combine(installDir, "rdpwrap.ini");
        if (manifest.IniBackup.Length == 0 && File.Exists(iniPath))
        {
            manifest.IniSourcePath = iniPath;
            manifest.IniBackup = RdpTransactionStore.BackupIni(manifest.Id, iniPath);
        }

        var run = await RdpToolRunner
            .RunAsync(tool, RdpToolRunner.UpdateIniArguments, IniTimeout, cancellationToken)
            .ConfigureAwait(false);
        RdpTransactionStore.AppendStep(manifest, $"INI 更新退出码 {run.ExitCode}：{run.OutputTail}");

        if (run.TimedOut)
        {
            // 超时不是「失败」，是「不知道」：工具可能还在写 INI。
            // 这时候再往下重启服务，最终检测读到的文件与实际落盘时序对不上（检查记录 P1）。
            var (toolRunning, toolDetail) = RdpToolRunner.DescribeRunning(run, "RDPWInst.exe");

            return (false,
                $"-w 在 {IniTimeout.TotalSeconds:0} 秒内未结束，结果不明（未强制结束）"
                + (toolRunning ? $"；它还在运行（{toolDetail}）" : ""),
                true);
        }

        if (run.ExitCode != 0)
        {
            return (false,
                $"-w 退出码 {run.ExitCode}（该命令会去 GitHub 下载最新 INI，"
                + $"失败通常表示这一步上不了网，不代表本机 INI 有问题）。{run.OutputTail}",
                false);
        }

        // 成功退出不等于已更新：回读正文，确认它真的含当前构建的段。
        // ⚠️ `-w` 下载失败时会**静默回落到程序内置的那份 INI**，退出码仍是 0 ——
        // 2026-10-26 真机就是这样：界面显示「-w 已完成，Updated=2026-10-07」，
        // 而磁盘上那份只有 67 个段、全是 10.0.10240.x，当前构建根本没有。
        var text = ReadText(iniPath);

        if (!IniCovers(text, version))
        {
            var restored = RestoreIniIfBetter(manifest, version);

            return (false,
                $"-w 退出码 0，但它写进来的 INI 只有 {IniSectionCount(text)} 个段、不含 [{version}] 段 —— "
                + "这是 RDPWInst 下载失败后回落的内置样本文件，不是最新全量 INI。"
                + (restored ? "已把安装前那份换回来。" : ""),
                false);
        }

        var updated = ReadIniUpdated(iniPath);
        Add(steps, "ini", "更新 INI", RdpStepOutcome.Ok,
            $"已写入含 [{version}] 段的 INI（{IniSectionCount(text)} 个段"
            + (updated.Length > 0 ? $"，Updated={updated}" : "") + "）");

        return (true, "", false);
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

        // 服务：**只有真的动过才恢复**。失败发生在「更新 INI」这种更早的步骤时，
        // TermService 一直好好地跑着，停它一次既没必要也有破坏性（会打断远程会话）。
        if (manifest.ServiceTouched)
        {
            // 依赖服务的恢复在 Restart 内部按「本来在跑」的清单处理
            var service = RdpServiceControl.Restart(RdpServiceControl.TermServiceName);
            if (!service.CoreOk)
            {
                allOk = false;
                notes.Add("服务恢复：" + service.Message);
            }
        }
        else
        {
            notes.Add("本轮没有动过远程桌面服务，跳过重启");
        }

        // 刚装的 Wrapper：执行卸载补偿，并**回读确认**它真的不再接管
        if (manifest.InstalledByUs)
        {
            var (removed, removalNote) = await RunUninstallCompensationAsync(
                manifest, uninstaller, steps, cancellationToken).ConfigureAwait(false);

            notes.Add(removalNote);
            if (!removed)
            {
                allOk = false;
            }
        }

        var summary = notes.Count == 0 ? "无需要补偿的项" : string.Join("；", notes);
        Add(steps, "compensate", "失败补偿", allOk ? RdpStepOutcome.Ok : RdpStepOutcome.Warn, summary);

        RdpTransactionStore.AppendStep(manifest, "补偿：" + summary);

        // 补偿跑过 ≠ 补偿成功：没证明系统回到安装前状态就记 CompensationFailed，
        // 界面上必须是「需要人工处理」而不是「已补偿」（2026-10-26 检查记录 P1）
        RdpTransactionStore.Finish(
            manifest,
            allOk ? RdpTransactionStatus.Compensated : RdpTransactionStatus.CompensationFailed,
            allOk ? summary : summary + "　补偿未完成：系统可能仍处于接管状态，请按诊断人工处理。");

        return allOk;
    }

    /// <summary>
    /// 卸载补偿：优先用安装目录里的控制台工具，其次用下载的卸载器；最后**按状态**判定是否真的解除接管。
    /// </summary>
    /// <remarks>
    /// <para>先找控制台工具的原因：<c>RDPWInst.exe -u</c> 不碰 GUI。2026-10-26 真机补偿跑的是
    /// 图形卸载器，它会按官方 bat 的流程拉起 <c>RDP_CnC.exe</c>，而那时 TermService 正在切换 ——
    /// CnC 起不来，屏幕上留下一个「应用程序无法正常启动(0x0000142)」的报错框。</para>
    /// <para>为什么退出码不能当结论：卸载器被安全软件拦下、或文件被远程会话占用时，
    /// 它可能照样返回 0，而 ServiceDll 一点没变。唯一可信的判据是回读。</para>
    /// </remarks>
    private static async Task<(bool Removed, string Note)> RunUninstallCompensationAsync(
        RdpTransactionManifest manifest,
        RdpDownloadedTool? uninstaller,
        List<RdpStepRecord> steps,
        CancellationToken cancellationToken)
    {
        var notes = new List<string>();
        var ran = false;

        var consoleTool = FindConsoleUninstaller();
        if (consoleTool.Length > 0)
        {
            var consoleRun = await RdpToolRunner
                .RunAsync(consoleTool, RdpToolRunner.UninstallArguments, ToolTimeout, cancellationToken)
                .ConfigureAwait(false);

            ran = true;
            notes.Add($"卸载补偿（控制台工具 {Path.GetFileName(consoleTool)}）退出码 {consoleRun.ExitCode}"
                      + (consoleRun.TimedOut ? "（超时）" : ""));

            Add(steps, "compensate-uninstall", "卸载补偿", RdpStepOutcome.Ok,
                $"用安装目录里的 {Path.GetFileName(consoleTool)} -u 完成，不会弹出任何界面。");
        }
        else
        {
            var uninstallPath = uninstaller?.LocalPath ?? manifest.UninstallerPath;
            if (uninstallPath.Length > 0 && File.Exists(uninstallPath))
            {
                var verify = RdpToolDownloader.VerifyExisting(
                    uninstallPath, manifest.UninstallerSha256, uninstaller?.SizeBytes ?? 0);

                if (!verify.Ok)
                {
                    notes.Add("卸载补偿跳过：卸载器二次核对失败（" + verify.Message + "）");
                }
                else
                {
                    var run = await RdpToolRunner
                        .RunAsync(uninstallPath, RdpToolRunner.UninstallArguments, ToolTimeout, cancellationToken)
                        .ConfigureAwait(false);

                    ran = true;
                    notes.Add($"卸载补偿（{Path.GetFileName(uninstallPath)}）退出码 {run.ExitCode}"
                              + (run.TimedOut ? "（超时）" : ""));
                }
            }
            else
            {
                notes.Add("卸载补偿不可用：找不到配套卸载器");
            }
        }

        // 判据只认回读
        var after = RdpReadOnlyDetector.Detect();
        var removed = after.WrapperState == RdpWrapperState.NotInstalled;

        var verdict = removed
            ? $"回读确认：ServiceDll 已回到 {after.ServiceDllPath}，接管已解除"
            : $"回读确认：ServiceDll 仍为 {after.ServiceDllPath}"
              + $"（Wrapper={RdpProbeText.WrapperLabel(after.WrapperState)}），接管尚未解除";

        notes.Add(verdict);
        Add(steps, "compensate-verify", "补偿后回读", removed ? RdpStepOutcome.Ok : RdpStepOutcome.Fail, verdict);

        if (!removed && !ran)
        {
            notes.Add("请按诊断手工卸载，或重启电脑后再点「卸载 RDP 解锁」");
        }

        return (removed, string.Join("；", notes));
    }

    /// <summary>
    /// 报告上游安装器拉起的 <c>RDP_CnC.exe</c> 残留。
    /// </summary>
    /// <remarks>
    /// 官方 <c>install.bat</c> 最后一句就是 <c>start "" RDP_CnC.exe</c>，安装器照做了。
    /// 隐藏桌面只让窗口不可见，不改变「它被启动了」这件事，也没有开关能拒绝
    /// （2026-10-26 检查记录 P1）。所以我们只做两件事：如实报告，并记成 residue ——
    /// 不写进阻塞型记录，否则一个常驻 GUI 会把以后每次解锁/卸载都拦下来。
    /// </remarks>
    private static void ReportCnCResidue(RdpTransactionManifest manifest, List<RdpStepRecord> steps)
    {
        var running = RdpToolProcessTree.FindByName("RDP_CnC");
        if (running.Count == 0)
        {
            return;
        }

        var pids = new List<string>();
        foreach (var (pid, name) in running)
        {
            pids.Add($"{name}(pid {pid})");
            manifest.ToolProcesses.Add(new RdpToolProcessRecord
            {
                Pid = pid,
                Name = name,
                Role = RdpToolProcessRecord.RoleResidue,
            });
        }

        Add(steps, "residue", "第三方界面进程仍在运行", RdpStepOutcome.Warn,
            $"上游安装器按官方 install.bat 的做法拉起了 {string.Join("、", pids)}。"
            + "它的窗口开在隐藏桌面上（你看不到），进程会一直留着。"
            + "这是上游安装器的行为，Yomori 没有拒绝它的开关；不影响解锁与远程连接，"
            + "重启电脑后它会消失。");
    }

    /// <summary>读文本文件；读不出来返回空串。</summary>
    private static string ReadText(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : "";
        }
        catch
        {
            return "";
        }
    }

    /// <summary>这份 INI 正文有没有当前构建号对应的段。</summary>
    /// <remarks>
    /// 判据只用「段名存在」：一份不含 <c>[10.0.26100.9549]</c> 的 INI 换上去，
    /// 无论 Updated 写得多新都不会生效。
    /// </remarks>
    private static bool IniCovers(string text, string version) =>
        text.Length > 0 && (version.Length == 0 || text.Contains($"[{version}]", StringComparison.Ordinal));

    /// <summary>数一下 INI 有多少个 section —— 用来识别「内置样本文件」（几十个段）与全量文件（上千个段）。</summary>
    private static int IniSectionCount(string text) =>
        text.Length == 0 ? 0 : text.Split('\n').Count(line => line.TrimStart().StartsWith("[", StringComparison.Ordinal));

    /// <summary>
    /// 安装器覆盖 INI 之后，如果新的那份反而不覆盖当前构建，而备份覆盖 —— 换回备份。
    /// </summary>
    /// <remarks>
    /// 安装器释放文件时是无条件的：它不管磁盘上那份是不是比自己的新、是不是已经覆盖当前构建。
    /// 2026-10-26 真机：它把一份 31 KB / 67 个段（全是 10.0.10240.x）的残本盖上来，
    /// Updated 还写着 2026-10-07，看起来像新的。结果本来能用的机器变成「Support 未覆盖」。
    /// </remarks>
    private static void RestoreIniIfInstallMadeItWorse(
        RdpTransactionManifest manifest,
        string installDir,
        string version,
        List<RdpStepRecord> steps)
    {
        if (installDir.Length == 0 || version.Length == 0 || manifest.IniBackup.Length == 0)
        {
            return;
        }

        var iniPath = Path.Combine(installDir, "rdpwrap.ini");
        var current = ReadText(iniPath);

        if (current.Length == 0 || IniCovers(current, version))
        {
            return;
        }

        var backup = RdpTransactionStore.ReadIniBackup(manifest);

        if (IniCovers(backup, version))
        {
            var (ok, message) = RdpTransactionStore.RestoreIni(manifest);
            Add(steps, "ini-restore", "恢复安装前的 INI",
                ok ? RdpStepOutcome.Warn : RdpStepOutcome.Fail,
                ok
                    ? $"安装器自带的 INI 盖掉了原来的文件（新的一份只有 {IniSectionCount(current)} 个段、"
                      + $"不含 [{version}] 段），已换回安装前那份（{IniSectionCount(backup)} 个段）。"
                    : "安装器覆盖了 INI，且恢复备份失败：" + message);
            return;
        }

        // 本轮备份也是残本（说明上一轮已经被盖过）：去历史事务里找一份还含该段的。
        var older = RdpTransactionStore.FindIniBackupCovering(version);
        if (older.Length == 0)
        {
            return;
        }

        try
        {
            File.Copy(older, iniPath, overwrite: true);
            Add(steps, "ini-restore", "恢复历史 INI 备份",
                RdpStepOutcome.Warn,
                $"安装器自带的 INI 只有 {IniSectionCount(current)} 个段、不含 [{version}] 段；"
                + $"已从历史事务（{Path.GetFileName(Path.GetDirectoryName(older))}）里换回一份含该段的 INI。");
        }
        catch (Exception ex)
        {
            Add(steps, "ini-restore", "恢复历史 INI 备份", RdpStepOutcome.Fail,
                $"从 {older} 复制到 {iniPath} 失败：{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>把备份换回来；成功返回 true。供 <c>-w</c> 分支复用。</summary>
    private static bool RestoreIniIfBetter(RdpTransactionManifest manifest, string version)
    {
        if (manifest.IniBackup.Length == 0)
        {
            return false;
        }

        var backup = RdpTransactionStore.ReadIniBackup(manifest);
        if (!IniCovers(backup, version))
        {
            return false;
        }

        return RdpTransactionStore.RestoreIni(manifest).Ok;
    }

    /// <summary>装完之后清点关键文件，并把缺了哪些说出来。</summary>
    /// <remarks>
    /// <para>要清点的原因：上游安装包的文件集不是固定的。<c>RDPW_Installer.exe</c> 只保证
    /// <c>rdpwrap.dll</c> 与 <c>rdpwrap.ini</c>；<c>RDPWInst.exe</c>（控制台工具）在 v1.8.9.9
    /// 里没有随包提供，而 CnC 的「Update ini」按钮正是调它 —— 于是用户看到
    /// 「RDPWInst.exe not found」。</para>
    /// <para>这不是「下载少了一块」，是上游包装与按钮不匹配。我们能做的是：提前说清缺什么，
    /// 以及哪些功能因此不可用（在线更新 INI / 用本地工具卸载）。</para>
    /// </remarks>
    private static (bool AllCore, string Summary) CheckInstallFiles(string installDir)
    {
        if (installDir.Length == 0)
        {
            return (false, "拿不到安装目录，无法清点文件。");
        }

        var core = new[] { "rdpwrap.dll", "rdpwrap.ini" };
        var optional = new[] { "RDPWInst.exe", "RDP_CnC.exe", "RDPConf.exe" };

        var missingCore = new List<string>();
        var presentOptional = new List<string>();

        foreach (var name in core)
        {
            if (!File.Exists(Path.Combine(installDir, name)))
            {
                missingCore.Add(name);
            }
        }

        foreach (var name in optional)
        {
            if (File.Exists(Path.Combine(installDir, name)))
            {
                presentOptional.Add(name);
            }
        }

        if (missingCore.Count > 0)
        {
            return (false, $"缺少关键文件：{string.Join("、", missingCore)}（目录 {installDir}）。");
        }

        var text = $"rdpwrap.dll / rdpwrap.ini 就位；同目录还有 {string.Join("、", presentOptional.DefaultIfEmpty("（无其它文件）"))}。";

        if (!presentOptional.Contains("RDPWInst.exe"))
        {
            text += "　注意：没有 RDPWInst.exe（上游 Release 安装器不带它），"
                    + "所以「在线更新 INI」与「用本地工具卸载」都不可用 —— "
                    + "前者请用「指定 INI」，后者会自动改用下载的卸载器。";
        }

        return (true, text);
    }

    /// <summary>
    /// 安装目录里的控制台卸载工具（<c>RDPWInst.exe</c>）；没有就返回空串。
    /// </summary>
    /// <remarks>
    /// 与图形卸载器同源、但不带 GUI —— 补偿时用它可以完全避免 CnC 弹窗与随之而来的
    /// 「0x0000142 无法正常启动」。
    /// </remarks>
    private static string FindConsoleUninstaller()
    {
        try
        {
            var dir = RdpReadOnlyDetector.Detect().InstallDirectory;
            if (dir.Length == 0)
            {
                return "";
            }

            var path = Path.Combine(dir, "RDPWInst.exe");
            return File.Exists(path) ? path : "";
        }
        catch
        {
            return "";
        }
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

    private static string Short(string sha) => sha.Length > 12 ? sha.Substring(0, 12) : sha;

    /// <summary>
    /// INI 在线更新失败时给出的手工出口。
    /// </summary>
    /// <remarks>
    /// <c>-w</c> 走的是 RDPWInst 自己的 WinInet 下载，不认本程序的代理设置；
    /// 国内网络下它经常直接失败（退出码 5）。这时唯一的办法是手工把最新的
    /// <c>rdpwrap.ini</c> 放到安装目录 —— 比让用户去猜「为什么更新不了」有用。
    /// </remarks>
    private static string IniManualHint(string installDir) =>
        "　组件已回滚到安装前状态。拿到能覆盖本机版本的 rdpwrap.ini 后，"
        + "点「指定 INI」选中它，再点一次「解锁 RDP」即可（不会重复下载安装器）。"
        + (installDir.Length == 0 ? "" : $"　文件也可以直接覆盖 {Path.Combine(installDir, "rdpwrap.ini")}。");

    /// <summary>下载失败时附在后面的出口：告诉用户「还可以手动给一个安装包」。</summary>
    private const string ManualHint =
        "　如果本机网络拿不到 GitHub 的附件（被代理或安全软件拦截时很常见），"
        + "可以用浏览器手动下载 RDPW_Installer.exe，再点「指定安装包」选中它，然后重试。";

    /// <summary>
    /// 下载一个资产；如果是「内容不对 / 读不出来」这类偶发问题，原样再下一次。
    /// </summary>
    /// <remarks>
    /// 值一次重试的理由：Release 附件走 <c>objects.githubusercontent.com</c>，
    /// 国内链路上偶发断流与被代理塞网页都是随机的；而「拒绝非 HTTPS」「没有下载地址」
    /// 这类确定性失败在 <see cref="RdpToolDownloader.LooksRetryable"/> 里已经被排除，
    /// 不会浪费用户时间。
    /// </remarks>
    private static async Task<(RdpDownloadedTool? Tool, string Error)> DownloadWithRetryAsync(
        RdpAsset asset,
        long releaseId,
        UpdateNetworkSettings network,
        List<RdpStepRecord> steps,
        string label,
        CancellationToken cancellationToken)
    {
        var (tool, error) = await RdpToolDownloader
            .DownloadAsync(asset, releaseId, network, progress: null, cancellationToken)
            .ConfigureAwait(false);

        if (tool is not null || !RdpToolDownloader.LooksRetryable(error))
        {
            return (tool, error);
        }

        Add(steps, "retry", $"重新下载{label}", RdpStepOutcome.Warn,
            $"第一次拿到的内容不可用（{error}），正在重试一次。");

        await Task.Delay(600, cancellationToken).ConfigureAwait(false);

        var (retryTool, retryError) = await RdpToolDownloader
            .DownloadAsync(asset, releaseId, network, progress: null, cancellationToken)
            .ConfigureAwait(false);

        return retryTool is not null
            ? (retryTool, "")
            : (null, retryError + "（已自动重试一次）");
    }

    private static void Add(List<RdpStepRecord> steps, string key, string title, RdpStepOutcome outcome, string detail) =>
        steps.Add(new RdpStepRecord { Key = key, Title = title, Outcome = outcome, Detail = detail });

    /// <summary>轮询直到条件成立或超时。装完与否看状态，不看进程退没退。</summary>
    private static async Task<bool> WaitUntilAsync(Func<bool> predicate, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.Now + timeout;

        while (DateTimeOffset.Now < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                if (predicate())
                {
                    return true;
                }
            }
            catch
            {
                // 读失败当作"还没好"，继续等
            }

            await Task.Delay(500, cancellationToken).ConfigureAwait(false);
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
