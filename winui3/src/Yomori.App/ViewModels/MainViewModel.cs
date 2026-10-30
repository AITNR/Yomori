// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Yomori.Helpers;
using Yomori.Models;
using Yomori.Services;
using Yomori.Views;

namespace Yomori.ViewModels;

public sealed class StatRow
{
    /// <summary>这条统计是从哪来的：本机 / 某个会话通道的显示名。</summary>
    public string Source { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;
    public int Count { get; set; }
    public string AverageText { get; set; } = "00:00";
    public string LastText { get; set; } = "00:00";
    public string LastTime { get; set; } = string.Empty;
}

/// <summary>
/// 全局视图模型：配置、任务队列、运行状态、日志、统计。
/// 所有来自执行器线程的回调都会先切回 UI 线程再更新绑定属性。
/// </summary>
public sealed class MainViewModel : ObservableObject
{
    private const int MaxLogLines = 4000;

    /// <summary>内嵌画面诊断行的标签。带它的行默认不进「运行日志」页（见 <see cref="ShouldShowLogLine"/>）。</summary>
    private const string EmbedLogTag = "[embed]";

    /// <summary>
    /// 内存探针行的标签（形如 <c>[memory] private_mb=393 working_set_mb=402 …</c>）。
    /// 和 <see cref="EmbedLogTag"/> 一样默认不进「运行日志」页 —— 它每几秒一行、
    /// 一屏全是数字，把"任务跑到哪一步"顶得看不见。要看同样去开发者模式。
    /// </summary>
    private const string MemoryLogTag = "[memory]";

    /// <summary>
    /// 同步等 UI 线程干完一件事的上限（<see cref="RunOnUiThreadSync"/>）。
    /// 取值理由：那里排的都是「改个状态 + 刷个界面」的短活，正常几十微秒到几毫秒；
    /// 给 5 秒是留足余量 —— 真等满 5 秒基本可以确定 UI 线程卡住了，而不是慢。
    /// </summary>
    private static readonly TimeSpan UiThreadSyncTimeout = TimeSpan.FromSeconds(5);

    private readonly ConfigManager _configManager;
    private readonly FileLogger _logger;
    private readonly RuntimeStatsManager _statsManager;
    private readonly DispatcherQueue _dispatcher;

    /// <summary>
    /// 自动切回主控会话的闸门。字段初始化器即创建，因为它的构造没有副作用，
    /// 而 Options 页在 Initialize 之前就可能读到状态。
    /// </summary>
    private readonly ControllerSessionReturnCoordinator _controllerSessionReturn =
        new(new ControllerSessionReturnService());

    /// <summary>正在跑「多通道任务启动」的嵌套计数；&gt;0 时手动连接回调不另起切回意图。</summary>
    private int _channelRunDepth;

    /// <summary>
    /// UU 远程修复的延迟调度：连接/执行这类操作之后静置 18 秒再动手，
    /// 避免和正在建立的会话抢时序。规则见 <see cref="UuSessionFixScheduler"/>。
    /// </summary>
    private readonly UuSessionFixScheduler _uuFixScheduler;

    private ScriptRunner? _runner;
    private Task? _runTask;
    private DispatcherQueueTimer? _memorySampleTimer;

    // ---------------- 自动保存（见《配置自动保存计划书》） ----------------

    /// <summary>去抖 / 防重入状态机。纯逻辑，计时器在 <see cref="_autoSaveTimer"/>。</summary>
    private readonly ConfigAutoSaver _autoSaver = new();

    /// <summary>
    /// 自动保存的延迟计时器。<b>必须挂字段</b> —— 局部变量建的
    /// <c>DispatcherQueueTimer</c> 会被 GC，<c>Tick</c> 永远不来且完全静默（本项目踩过）。
    /// </summary>
    private DispatcherQueueTimer? _autoSaveTimer;

    /// <summary>
    /// 自动保存已启用。启用之前（构造、加载、初始化）发生的改动一律不排保存：
    /// 那段流程里的赋值是程序自己做的规整（迁移、兜底），不是用户编辑。
    /// </summary>
    private bool _autoSaveEnabled;

    /// <summary>正在执行落盘内核。期间到达的通知全部忽略（它们由落盘自己产生）。</summary>
    private bool _applyingConfig;

    /// <summary>最近一次自动保存失败的信息：同一条只写一次日志，免得刷屏。</summary>
    private string _lastAutoSaveError = string.Empty;

    private bool _isRunning;
    private string _statusText = "空闲";
    private string _currentTaskName = "-";
    private string _elapsedText = "00:00";
    private string _progressText = "0 / 0";
    private TaskConfig? _selectedTask;

    private int _runTaskErrorCount;

    public MainViewModel()
    {
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        _uuFixScheduler = new UuSessionFixScheduler(
            _dispatcher,
            AppendLog,
            () => RdpSettings.UuSessionFixEnabled,
            () => UuSessionFixService.ApplyAsync(CancellationToken.None));
        _configManager = new ConfigManager(AppPaths.ConfigPath);
        _logger = new FileLogger(AppPaths.LogDir);
        _statsManager = new RuntimeStatsManager(AppPaths.StatsDir);

        Config = _configManager.Load();

        // 开发者模式一改就要重排日志页（不然开关看着像没生效）
        Config.PropertyChanged += OnConfigPropertyChanged;

        Tasks = new ObservableCollection<TaskConfig>(Config.Tasks);

        LogLines = new ObservableCollection<string>();
        StatRows = new ObservableCollection<StatRow>();

        // 通知中心：每次启动都从空列表开始（历史通知只对当时那一刻有意义，
        // 见 NotificationCenter.ResetOnStartup 的说明）。挂变更回调之后
        // 铃铛上的红点与"未读就摇"都由这里驱动。
        Notifications = new NotificationCenter();
        Notifications.ResetOnStartup();
        Notifications.Changed += (_, _) =>
        {
            OnPropertyChanged(nameof(NotifyDotVisibility));
            OnPropertyChanged(nameof(HasUnreadNotifications));
        };
    }

    // ---------------- 通知中心（铃铛） ----------------

    /// <summary>通知中心。消息落盘在程序目录的 <c>notifications.json</c>。</summary>
    public NotificationCenter Notifications { get; }

    /// <summary>铃铛上那个红点：有未读时才显示。</summary>
    public Visibility NotifyDotVisibility =>
        Notifications.HasUnread ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>有没有未读（摇铃铛动画看这个）。</summary>
    public bool HasUnreadNotifications => Notifications.HasUnread;

    /// <summary>把一条通知标为已读，并按它的动作类型打开链接/目录。</summary>
    /// <remarks>
    /// 带动作的通知**点过就移除**：动作已经执行（下载已开始、安装器已拉起、浏览器已打开），
    /// 条目留着只会让人重复点同一个动作。纯提示类（没有动作）只标已读 —— 它代表一条消息，
    /// 看过了就该在列表里留个痕迹。
    /// </remarks>
    public void OpenNotification(AppNotification? item)
    {
        if (item is null)
        {
            return;
        }

        if (!item.HasAction)
        {
            Notifications.MarkRead(item);
            return;
        }

        try
        {
            switch (item.ActionKind)
            {
                case AppNotificationActions.OpenUrl when item.ActionTarget.Length > 0:
                    UpdateService.OpenUrl(item.ActionTarget);
                    break;

                case AppNotificationActions.OpenPath when item.ActionTarget.Length > 0:
                    OpenDirectory(item.ActionTarget);
                    break;

                case AppNotificationActions.Download when item.ActionTarget.Length > 0:
                    // 不阻塞界面：下载几十秒到几分钟，交给后台，进度/结果另挂一条通知
                    _ = StartNotificationDownloadAsync(item.ActionTarget);
                    break;

                case AppNotificationActions.Install when item.ActionTarget.Length > 0:
                    // 点了就等于"我决定现在升级"：启动安装包并退出（安装程序会先关掉本进程）
                    StartInstallerAndExit(item.ActionTarget);
                    break;
            }
        }
        catch (Exception ex)
        {
            AppendLog($"[通知] 打开动作失败：{ex.Message}");
        }

        RemoveNotificationDeferred(item);
    }

    /// <summary>
    /// 延后一拍删掉这条通知。
    ///
    /// <para>为什么不就地删：调用方是 <c>ListView.ItemClick</c>，此刻集合正处在
    /// "事件处理器里改 ItemsSource"的位置上（工程里踩过同类：同步改集合落在树进入过程中，
    /// 抛 <c>stowed exception</c>）。排到下一拍再动最稳。</para>
    /// </summary>
    private void RemoveNotificationDeferred(AppNotification item)
    {
        if (!_dispatcher.TryEnqueue(() => Notifications.Remove(item)))
        {
            // 派发器已经停了（正在退出）：就地删，反正界面也不刷了
            Notifications.Remove(item);
        }
    }

    /// <summary>铃铛里点「全部已读」。</summary>
    public void MarkAllNotificationsRead() => Notifications.MarkAllRead();

    // ---------------- 启动时检查更新 ----------------

    /// <summary>
    /// 启动后延迟几秒再查：启动瞬间要抢磁盘和网络的东西已经够多了
    /// （配置、日志、画面、代理预检），错开这几秒用户完全感觉不到。
    /// </summary>
    private const int StartupUpdateCheckDelaySeconds = 6;

    /// <summary>安装包下载到哪：程序目录下的 <c>updates</c>（管理员可写，用户也好找）。</summary>
    public static string UpdateDownloadDir => Path.Combine(AppPaths.BaseDir, "updates");

    /// <summary>是否已有一次下载在跑（并发写同一个 *.part 会把文件搞坏）。</summary>
    private bool _updateDownloadBusy;

    /// <summary>
    /// 启动时按设置检查一次更新（取值见 <see cref="UpdateCheckModes"/>）。
    ///
    /// <para>整段在后台跑，**不阻塞启动**，结论通过通知中心（铃铛）呈现。
    /// 没更新 / 查不到 / 本机比远端新，一律不打扰 —— 启动时弹"检查更新失败"只会变成噪声，
    /// 那些情况只写运行日志。</para>
    /// </summary>
    public void BeginStartupUpdateCheck()
    {
        var mode = UpdateCheckModes.Normalize(Config.UpdateCheckMode);
        if (mode == UpdateCheckModes.Off)
        {
            AppendLog("[更新检查] 启动检查已在设置里关闭，跳过。");
            return;
        }

        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(StartupUpdateCheckDelaySeconds)).ConfigureAwait(false);
            await RunStartupUpdateCheckAsync(mode).ConfigureAwait(false);
        });
    }

    private async Task RunStartupUpdateCheckAsync(string mode)
    {
        var channel = UpdateChannels.Normalize(Config.UpdateChannel);
        var network = UpdateNetworkSettings.FromConfig(Config);

        var result = await UpdateService.CheckAsync(channel, network).ConfigureAwait(false);
        AppendLog(result.LogLine);

        // 诊断行：不管结论是什么，先记下发布说明抓没抓到、摘要出几行。
        // 「通知里没有更新内容」这种反馈，只看着上面那行结论分不清是 body 没到手
        // 还是摘要规则没命中 —— 多这一行就能一眼判定（2026-10-29 为此查过一轮）。
        if (result.Latest is { } latest)
        {
            AppendLog($"[更新检查] 发布说明 {latest.Body.Length} 字符，摘要 {UpdateNotes.Summarize(latest.Body).Count} 行。");
        }

        if (result.Status != UpdateCheckStatus.UpdateAvailable || result.Latest is null)
        {
            return;
        }

        var release = result.Latest;
        var pick = UpdateService.PickDownload(release);

        if (mode == UpdateCheckModes.Download)
        {
            if (pick is null)
            {
                // 没有可下载资产时退回「前往发布页」那条通知 —— 但绝不能静默什么都不做
                NotifyUpdateAvailable(release);
                return;
            }

            await DownloadPackageAsync(pick.Value.Url, pick.Value.FileName, network).ConfigureAwait(false);
            return;
        }

        NotifyUpdateAvailable(release);
    }

    /// <summary>
    /// 在铃铛里挂一条「发现新版本」，带更新内容摘要与下载入口。
    ///
    /// <para><b>启动时的自动检查与设置页手动「检查更新」共用这一个方法</b>：同一份发布，
    /// 用户在哪发现它，铃铛里就该是同一个入口、同一段更新内容。各写一遍的结局是
    /// 文案与动作慢慢走散 —— 在此之前手动检查只在设置页 InfoBar 里放一句话，
    /// 铃铛里空着，用户一关设置页就再也找不到下载入口。</para>
    ///
    /// <para>去重交给 <see cref="NotificationCenter.AddIfNew"/>（按标题判）：同一次运行里
    /// 启动检查 + 手动检查命中同一版本只留一条，重复检查会把内容刷成最新。</para>
    ///
    /// <para>线程：内部走 <see cref="Post"/>，任意线程都可调用。可下载时点通知直接在后台下
    /// （见 <see cref="DownloadPackageAsync"/>），没有可下载资产则退回「用浏览器打开发布页」。</para>
    /// </summary>
    public void NotifyUpdateAvailable(GitHubRelease release)
    {
        var pick = UpdateService.PickDownload(release);

        var body = UpdateNotes.BuildBody(
            UpdateService.CurrentVersion,
            release.Version,
            UpdateChannels.Label(UpdateChannels.Normalize(Config.UpdateChannel)),
            release.PublishedAt,
            release.Body,
            canDownload: pick is not null);

        Post(() => Notifications.AddIfNew(
            $"发现新版本 {release.Version}",
            body,
            pick is null ? AppNotificationKinds.Warning : AppNotificationKinds.Update,
            pick is null ? AppNotificationActions.OpenUrl : AppNotificationActions.Download,
            pick is null ? release.PageUrl : pick.Value.Url));
    }

    /// <summary>
    /// 把安装包下载到 <see cref="UpdateDownloadDir"/>，并按结果挂通知。
    ///
    /// <para>流程设计成"点通知就下、下完再给一条可以点的安装通知"，
    /// 中间不弹任何对话框 —— 用户点的是"我要这个更新"，不该被两个弹窗拦一道。</para>
    ///
    /// <para>⚠️ 同一时刻只允许一个下载：用户在铃铛里连点两下、或者启动检查自动下载与
    /// 手动点下载撞一起，会两个线程写同一个 <c>*.part</c>。</para>
    /// </summary>
    private async Task DownloadPackageAsync(string url, string fileName, UpdateNetworkSettings network)
    {
        if (_updateDownloadBusy)
        {
            AppendLog("[更新检查] 已经有一个安装包在下载了，忽略这次请求。");
            return;
        }

        _updateDownloadBusy = true;
        var target = Path.Combine(UpdateDownloadDir, fileName);

        // 立刻挂一条"正在下载"：下载可能几十秒到几分钟，没有回执用户会以为点了没反应。
        // Progress = 0 先把进度条画出来 —— 第一个字节还没到，但"已经在下了"这件事要立刻可见。
        var progressItem = PostAndGet(() =>
        {
            var item = Notifications.Add(
                "正在下载安装包",
                $"{fileName}\n正在连接…",
                AppNotificationKinds.Download);
            item.Progress = 0;
            return item;
        });

        var progress = CreateDownloadProgress(progressItem, fileName);

        try
        {
            AppendLog($"[更新检查] 开始下载 {fileName}（代理：{UpdateProxyModes.Label(network.ProxyMode)}）。");

            var (ok, message) = await UpdateService
                .DownloadAsync(url, target, network, progress)
                .ConfigureAwait(false);

            AppendLog($"[更新检查] {message}");

            Post(() =>
            {
                // 进度那条已被结果取代，删掉免得铃铛里留一条永远"正在下载"
                Notifications.Remove(progressItem);

                if (ok)
                {
                    Notifications.Add(
                        "安装包已下载",
                        $"{Path.GetFileName(target)}\n点这条退出程序并安装。",
                        AppNotificationKinds.Download,
                        AppNotificationActions.Install,
                        target);
                }
                else
                {
                    Notifications.Add("更新下载失败", message, AppNotificationKinds.Warning);
                }
            });
        }
        finally
        {
            _updateDownloadBusy = false;
        }
    }

    /// <summary>
    /// 在 UI 线程上执行并拿回返回值（通知集合只能 UI 线程碰）。
    ///
    /// ⚠️ 只在**后台线程**上才会阻塞等待；调用方若已在 UI 线程则直接执行 ——
    /// UI 线程同步等自己排的队就是死锁（项目里记过这条）。
    /// </summary>
    private AppNotification? PostAndGet(Func<AppNotification?> action)
    {
        if (_dispatcher.HasThreadAccess)
        {
            return action();
        }

        AppNotification? result = null;
        using var done = new ManualResetEventSlim(false);

        if (!_dispatcher.TryEnqueue(() =>
            {
                result = action();
                done.Set();
            }))
        {
            return null;
        }

        done.Wait(TimeSpan.FromSeconds(2));
        return result;
    }

    /// <summary>
    /// 下载进度的上报器：只在**整数百分比变化**时刷新那条通知。
    ///
    /// <para>为什么要节流：<see cref="UpdateService.DownloadAsync"/> 每读一块（80KB）就报一次，
    /// 一个 90MB 的安装包是一千多次回调。每次都往 UI 线程排队，会把消息泵塞满、
    /// 界面反而更卡（而且人眼也读不出 37.4% 和 37.6% 的差别）。</para>
    ///
    /// <para>为什么回 UI 线程：<c>Progress</c> / <c>Body</c> 都绑在 ListView 上，
    /// 后台线程直接赋值就是跨线程改绑定（<c>0x8001010E</c>）。</para>
    ///
    /// <para>进度只进不退：<see cref="Progress{T}"/> 的回调是排队的，
    /// 排在前面的旧百分比后执行时不该把进度条往回拖。</para>
    /// </summary>
    private IProgress<double> CreateDownloadProgress(AppNotification? item, string fileName)
    {
        var gate = new object();
        var lastPercent = -1;

        return new Progress<double>(fraction =>
        {
            var percent = (int)Math.Round(Math.Clamp(fraction, 0d, 1d) * 100);

            lock (gate)
            {
                if (percent <= lastPercent)
                {
                    return;
                }

                lastPercent = percent;
            }

            Post(() =>
            {
                if (item is null)
                {
                    return;
                }

                item.Progress = percent;
                item.Body = $"{fileName}\n正在下载 {percent}%";
            });
        });
    }

    /// <summary>点「下载」那条通知：从 URL 反推文件名，走的还是同一套下载流程。</summary>
    private Task StartNotificationDownloadAsync(string url) =>
        DownloadPackageAsync(
            url,
            UpdateService.FileNameFromUrl(url),
            UpdateNetworkSettings.FromConfig(Config));

    private void StartInstallerAndExit(string setupPath)
    {
        // 先记账再启动：安装器运行期间那个 exe 被它自己占着，删不掉；
        // 只能写进清单，等新版本第一次启动时删（见 UpdatePackageCleanup）。
        UpdatePackageCleanup.MarkPendingDeletion(UpdateDownloadDir, setupPath);

        try
        {
            // 主程序是 requireAdministrator，子进程继承同一个令牌 → 不会再弹一次 UAC
            Process.Start(new ProcessStartInfo(setupPath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppendLog($"[更新检查] 启动安装程序失败：{ex.Message}");
            _ = DialogHelper.ShowMessageAsync("无法启动安装程序", ex.Message);
            return;
        }

        AppendLog("[更新检查] 安装程序已启动，本程序退出以便覆盖文件。");

        // ⚠️ 必须走 RequestExit：直接 Close() 会被关闭拦截变成"收进托盘"，
        //    进程留着就会锁住 Yomori.exe，安装程序随即覆盖失败。
        App.MainWindow?.RequestExit();
    }

    /// <summary>
    /// 清掉「已经装完」的安装包与超期残留。
    ///
    /// <para>为什么必然只能放在启动时做：点「安装」那一刻，安装器正拿着那个 exe 在跑，
    /// 文件被占用；本程序随即退出，没有机会再删。于是安装前记账、新版本首次启动删 ——
    /// 记账与清单格式见 <see cref="UpdatePackageCleanup"/>。</para>
    ///
    /// <para>失败一律只写日志：清理是多占几十 MB 的问题，不能影响启动。</para>
    /// </summary>
    private void CleanupUpdatePackagesQuietly()
    {
        try
        {
            var directory = UpdateDownloadDir;
            var (deleted, remaining) = UpdatePackageCleanup.RunPendingCleanup(directory);
            var stale = UpdatePackageCleanup.SweepStale(directory);

            if (deleted + remaining + stale == 0)
            {
                return;
            }

            AppendLog(remaining == 0
                ? $"[更新检查] 已清理 {deleted} 个安装好的安装包、{stale} 个超期残留。"
                : $"[更新检查] 已清理 {deleted + stale} 个安装包，还有 {remaining} 个删不掉（被占用，下次启动再试）。");
        }
        catch (Exception ex)
        {
            AppendLog($"[更新检查] 清理安装包失败：{ex.Message}");
        }
    }

    private void LogMemorySample()
    {
        try
        {
            using var process = System.Diagnostics.Process.GetCurrentProcess();
            AppendLog($"[memory] private_mib={process.PrivateMemorySize64 / 1048576} working_set_mib={process.WorkingSet64 / 1048576} " +
                $"threads={process.Threads.Count} handles={process.HandleCount} managed_mib={GC.GetTotalMemory(false) / 1048576} " +
                $"renderers={RdpD3DRenderer.LiveRenderers} clients={_embeds.Values.Count(c => c.Client is not null)} " +
                $"task_workers={ScriptRunner.LiveTaskWorkers} log_workers={ScriptRunner.LiveLogWorkers} " +
                $"soft_states={Yomori.Views.RdpView.LiveSoftRenderStates} soft_in_flight={Yomori.Views.RdpView.SoftRendersInFlight}");
            foreach (var connection in _embeds.Values)
            {
                if (connection.Client is { } client) { AppendSurfaceLog(connection.Key, "[memory] " + client.Diag()); }
            }
        }
        catch (Exception) { /* Sampling must not interrupt execution. */ }
    }

    // ---------------- 配置 ----------------

    public AppConfig Config { get; }

    public ObservableCollection<TaskConfig> Tasks { get; }

    public TaskConfig? SelectedTask
    {
        get => _selectedTask;
        set
        {
            if (SetProperty(ref _selectedTask, value))
            {
                OnPropertyChanged(nameof(HasSelection));
                OnPropertyChanged(nameof(HasNoSelection));
                OnPropertyChanged(nameof(CanDeleteSelected));
                OnPropertyChanged(nameof(CanMoveSelected));
                OnPropertyChanged(nameof(CanChangeSelectedKind));
            }
        }
    }

    public bool HasSelection => SelectedTask is not null;

    public bool HasNoSelection => SelectedTask is null;

    /// <summary>
    /// 选中的任务能不能删。收尾任务恒为 false —— 每通道恰好一条、由系统维护
    /// （见《任务类型与收尾计划书》§3 D4）。界面据此置灰，方法里再拒一次。
    /// </summary>
    public bool CanDeleteSelected => SelectedTask is not null && !TaskTypeResolver.IsFinish(SelectedTask);

    /// <summary>选中任务能不能上移/下移：收尾恒在末位，不参与排序。</summary>
    public bool CanMoveSelected => SelectedTask is not null && !TaskTypeResolver.IsFinish(SelectedTask);

    /// <summary>选中任务能不能改类型：收尾任务不能改（它没有别的类型可选）。</summary>
    public bool CanChangeSelectedKind => SelectedTask is not null && !TaskTypeResolver.IsFinish(SelectedTask);

    /// <summary>下拉选项源：Key 为配置值，Value 为中文显示名。</summary>
    public IReadOnlyList<KeyValuePair<string, string>> WaitModeItems => WaitModes.Items;

    /// <summary>任务类型单选的可选项：**只有脚本与等待**，收尾不在其中（系统维护）。</summary>
    public IReadOnlyList<KeyValuePair<string, string>> TaskKindItems => TaskKinds.Selectable;

    /// <summary>等待任务的时机下拉（连接后 / 连接前）。</summary>
    public IReadOnlyList<KeyValuePair<string, string>> WaitTimingItems => WaitTimings.Items;

    /// <summary>收尾动作下拉（无操作 / 断开通道 / 注销通道）。</summary>
    public IReadOnlyList<KeyValuePair<string, string>> PostTaskActionItems => SessionFinishModes.Items;

    public IReadOnlyList<KeyValuePair<string, string>> TimeoutActionItems => TimeoutActions.Items;

    public IReadOnlyList<KeyValuePair<string, string>> ConcurrentPolicyItems => ConcurrentPolicies.Items;

    public IReadOnlyList<KeyValuePair<string, string>> ThemeItems => AppConfig.ThemeItems;

    /// <summary>「设置 → 关于」里更新渠道下拉框的选项（正式版 / 测试版）。</summary>
    public IReadOnlyList<KeyValuePair<string, string>> UpdateChannelItems => UpdateChannels.Items;

    /// <summary>启动时检查更新的三种方式（不检查 / 通知 / 自动下载）。</summary>
    public IReadOnlyList<KeyValuePair<string, string>> UpdateCheckModeItems => UpdateCheckModes.Items;

    /// <summary>检查更新与下载时走的代理（系统 / 自定义 / 不使用）。</summary>
    public IReadOnlyList<KeyValuePair<string, string>> UpdateProxyModeItems => UpdateProxyModes.Items;

    // ---------------- 运行状态 ----------------

    /// <summary>
    /// 取某任务「上次运行期间新出现的进程」候选（界面「从上次运行填入」用）。
    ///
    /// <para>两个来源合并：本地执行路径读本进程里的 <c>ScriptRunner</c>；远程通道读各通道
    /// <c>status.json</c> 回传的候选（采集发生在代理进程，只能靠桥带回来）。
    /// 按镜像名去重 —— 同一个进程在两条路上都被记到并不稀奇。</para>
    /// </summary>
    public IReadOnlyList<ProcessSnapshotEntry> CandidateProcessesFor(string? taskName)
    {
        var merged = new List<ProcessSnapshotEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var name = (taskName ?? string.Empty).Trim();

        void Add(int pid, string imageName)
        {
            var text = (imageName ?? string.Empty).Trim();
            if (text.Length == 0 || !seen.Add(text))
            {
                return;
            }

            merged.Add(new ProcessSnapshotEntry(pid, 0, text));
        }

        foreach (var item in _runner?.CandidatesFor(name) ?? Array.Empty<ProcessSnapshotEntry>())
        {
            Add(item.Pid, item.Name);
        }

        foreach (var session in _sessions)
        {
            var candidates = session.LastCandidates;
            if (candidates is null)
            {
                continue;
            }

            foreach (var candidate in candidates)
            {
                // 旧代理不带任务名：宁可给出来让用户判断，也不要静默丢掉
                if (candidate.Task.Length > 0
                    && !string.Equals(candidate.Task, name, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                Add(candidate.Pid, candidate.Name);
            }
        }

        return merged;
    }

    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            if (SetProperty(ref _isRunning, value))
            {
                OnPropertyChanged(nameof(CanEdit));
                OnPropertyChanged(nameof(CanStart));
                OnPropertyChanged(nameof(CanPause));
                OnPropertyChanged(nameof(CanStop));

                // 「执行中（N 块并行）」/「已完成」这类聚合文案取决于它
                RaisePanelChanged();
            }
        }
    }

    public bool CanEdit => !IsRunning;

    public bool CanStart => !IsRunning;

    public bool CanPause => IsRunning;

    public bool CanStop => IsRunning;

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public string CurrentTaskName
    {
        get => _currentTaskName;
        private set => SetProperty(ref _currentTaskName, value);
    }

    public string ElapsedText
    {
        get => _elapsedText;
        private set => SetProperty(ref _elapsedText, value);
    }

    public string ProgressText
    {
        get => _progressText;
        private set => SetProperty(ref _progressText, value);
    }

    public ObservableCollection<string> LogLines { get; }

    /// <summary>
    /// 本次会话产生过的全部日志行（不按开发者模式过滤，上限同样是 <see cref="MaxLogLines"/>）。
    ///
    /// 留着它是为了让「开发者模式」开关一拨动就能**立刻**重排日志页：只按新开关决定
    /// 往后收不收诊断行（<c>[embed]</c> / <c>[memory]</c>）的话，用户拨开开关后要等下一行
    /// 诊断（内存探针最长十几秒、内嵌画面最长 5 秒，还得正好挂着画面）才看出变化，
    /// 会以为开关坏了。
    /// </summary>
    private readonly List<string> _logBacklog = new();

    public ObservableCollection<StatRow> StatRows { get; }

    /// <summary>
    /// 本次会话里是不是**真的**藏起过诊断行（<c>[embed]</c> / <c>[memory]</c>）。
    /// 只在 false → true 那一刻发一次变更通知，不用每条日志都刷一遍绑定。
    /// </summary>
    private bool _hasHiddenDiagnosticLine;

    /// <summary>
    /// 「运行日志」页上那句提示：开发者模式关着、并且确实藏过诊断行时才显示，
    /// 说明它们去哪儿了、要打开哪个开关。
    ///
    /// 加"确实藏过"这个条件是刻意的：从没挂过内嵌画面、也没写过内存探针行时，
    /// 页面上凭空多一行提示只是噪声。
    /// </summary>
    public Visibility DiagnosticLogHintVisibility =>
        !Config.DeveloperMode && _hasHiddenDiagnosticLine ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>统计页的空状态提示（有数据就收起来）。表格全空时界面一片空白，容易被当成模块坏了。</summary>
    public Visibility StatsEmptyVisibility =>
        StatRows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    public event EventHandler<int>? ShutdownPromptRequested;

    public event EventHandler<string>? RequestNavigation;

    // ---------------- 启动 ----------------


    /// <summary>
    /// 启动时清理超期的外部日志文件 —— **按通道各自保留最近 KeepFiles 份**。
    ///
    /// 为什么放在启动而不是运行期：清理只在“天数变多”时才有意义，
    /// 一天跑一次足够；运行期去动文件既没必要，还可能和正在写的脚本抢句柄。
    ///
    /// 全程静默：清理失败只是少清一点旧文件，绝不能因为它让启动失败。
    /// </summary>
    private void PruneExternalLogsQuietly()
    {
        try
        {
            var (deleted, freed) = ExternalLogStore.Prune(Config.ExternalLogKeepFiles);
            if (deleted > 0)
            {
                AppendLog($"外部日志清理：已删除 {deleted} 份，释放 {ExternalLogStore.FormatBytes(freed)}。");
            }
        }
        catch
        {
            // 静默：清理失败不影响启动
        }
    }
    public void Initialize()
    {
        AppendLog("程序启动。");
        AppendLog($"配置文件：{_configManager.ConfigPath}");
        AppendLog($"日志目录：{_logger.LogDir}");
        AppendLog($"资源目录：{AppPaths.AssetsDir}");

        // 启动顺手清一次超期的外部日志（每通道各留最近 KeepFiles 份）。
        // 只清文件、不碰任何运行状态；失败也只是少清一点，不影响启动。
        PruneExternalLogsQuietly();

        // 更新装完之后留下的安装包：安装器运行时它被占着删不掉，只能等这一次启动删。
        // 后台跑 —— 删几十 MB 不该拖慢启动（也一样失败不报错，只写一行日志）。
        _ = Task.Run(CleanupUpdatePackagesQuietly);

        RefreshStats();
        ReloadChannels(refreshReadiness: false);
        SelectedTask = ScopeTasks.FirstOrDefault();

        // 自动保存从这里开始生效：此前的一切赋值都是程序自己的加载与规整，不该写盘。
        EnableAutoSave();

        _memorySampleTimer = _dispatcher.CreateTimer();
        _memorySampleTimer.Interval = TimeSpan.FromMinutes(1);
        _memorySampleTimer.Tick += (_, _) => LogMemorySample();
        _memorySampleTimer.Start();

        if (Config.AutoStartTasks)
        {
            AppendLog("已启用程序启动后自动开始执行任务。");
            _ = Task.Run(async () =>
            {
                await Task.Delay(1500);
                Post(Start);
            });
        }
    }

    // ---------------- 任务编辑（在选中的执行通道内编排） ----------------

    /// <summary>在当前执行通道下新建一个任务（默认「本地执行」，免得忘了选通道就悄悄跑远程）。</summary>
    public void AddTaskToScope()
    {
        var scope = _selectedScope;
        if (scope is null || !scope.CanAddTask)
        {
            return;
        }

        var task = new TaskConfig
        {
            Enabled = true,
            TaskKind = TaskKinds.Script,
            Name = $"新任务{Tasks.Count + 1}",
            ScriptPath = string.Empty,
            Order = Tasks.Count + 1,
            TimeoutMinutes = 30,
            TimeoutAction = TimeoutActions.KillAndContinue,
            WaitMode = WaitModes.DirectProcess,
            ConcurrentPolicy = ConcurrentPolicies.WaitAll,
            ChannelId = scope.Id,
        };

        Tasks.Add(task);

        // 这条通道第一次有任务 → 同时补上那条固定的收尾任务（每通道恰好一条，见 §3 D4）
        var created = TaskScopePlanner.EnsureFinishTask(Tasks, scope.Id, Config.Rdp.Channels);
        if (created is not null)
        {
            AppendLog($"[任务] 已为「{scope.Name}」补上收尾任务（默认无操作），可随时在最后一行选择收尾动作。");
        }

        TaskScopePlanner.NormalizeOrdersByChannel(Tasks, Config.Rdp.Channels);
        RefreshChannelScopes();
        SelectedTask = task;
    }

    /// <summary>
    /// 改选中任务的类型。**收尾不在可选项里**（界面没有这个选项），这里再拒一次 ——
    /// 界面没有入口不等于代码层面安全（导入、批量操作、将来的快捷键都可能绕过）。
    /// </summary>
    public void SetSelectedTaskKind(string kind)
    {
        var task = SelectedTask;
        if (task is null)
        {
            return;
        }

        if (!TaskScopePlanner.SetTaskKind(Tasks, task, kind, Config.Rdp.Channels))
        {
            AppendLog($"[任务] 「{task.DisplayName}」的类型不能改成 {TaskKinds.Label(kind)}"
                      + (TaskTypeResolver.IsFinish(task) ? "（收尾任务由系统固定维护）。" : "。"));
            return;
        }

        SaveConfig();
        RefreshScopeTasks();
        RaiseTaskDerivedChanged();
    }

    /// <summary>改选中任务某条"按类型才有意义"的字段后，统一刷新界面派生文本。</summary>
    public void RaiseTaskDerivedChanged()
    {
        OnPropertyChanged(nameof(SelectedTask));
        OnPropertyChanged(nameof(CanDeleteSelected));
        OnPropertyChanged(nameof(CanMoveSelected));
        OnPropertyChanged(nameof(CanChangeSelectedKind));
    }

    public void DeleteSelectedTask()
    {
        if (SelectedTask is null)
        {
            return;
        }

        // 收尾任务不可删：每通道恰好一条，删了队列就没有收尾这一步。
        // 按钮已经置灰，这里是第二道闸（界面状态从来不是闸门）。
        if (TaskTypeResolver.IsFinish(SelectedTask))
        {
            AppendLog("[任务] 收尾任务固定存在，不可删除。");
            return;
        }

        var scopeId = TaskScopePlanner.NormalizeId(SelectedTask.ChannelId);
        var index = TaskScopePlanner.TasksForScope(Tasks, scopeId).IndexOf(SelectedTask);

        Tasks.Remove(SelectedTask);
        TaskScopePlanner.NormalizeOrdersByChannel(Tasks, Config.Rdp.Channels);

        // 选中同通道里的下一个（没有就上一个）：跨通道选中会让表单看起来"跳到别处"
        var remaining = TaskScopePlanner.TasksForScope(Tasks, scopeId);
        SelectedTask = remaining.Count == 0
            ? null
            : remaining[Math.Min(Math.Max(index, 0), remaining.Count - 1)];

        RefreshChannelScopes();
    }

    /// <summary>通道内上移 / 下移（delta = -1 / +1）—— 只与同通道内相邻的一项交换，别的通道不受影响。</summary>
    public void MoveSelectedTaskInScope(int delta)
    {
        if (SelectedTask is null)
        {
            return;
        }

        if (!TaskScopePlanner.TryMoveInScope(Tasks, SelectedTask, delta, Config.Rdp.Channels))
        {
            // 唯一的"拒绝"来源就是收尾任务（它恒在末位）—— 说一句，免得点了没反应
            if (TaskTypeResolver.IsFinish(SelectedTask))
            {
                AppendLog("[任务] 收尾任务固定在通道最后一位，不能上移或下移。");
            }

            return;
        }

        RefreshScopeTasks();
    }

    /// <summary>
    /// 抓手拖动落位：把 <paramref name="task"/> 挪到通道内的第 <paramref name="targetIndex"/> 位。
    ///
    /// 用 <c>ObservableCollection.Move</c> 而不是刷新重建 —— 行原地挪位，ListView 能无缝接收，
    /// 选中态也跟着对象走；重建（<c>Clear()+Add</c>）会让界面闪一下并把选中态打散。
    /// </summary>
    public void MoveTaskInScopeTo(TaskConfig? task, int targetIndex)
    {
        if (task is null)
        {
            return;
        }

        // 收尾任务不参与拖放：它恒在末位（拖动它等于自欺 —— 松手后会被重编 order 放回来）
        if (TaskTypeResolver.IsFinish(task))
        {
            return;
        }

        var from = ScopeTasks.IndexOf(task);
        if (from < 0)
        {
            return;
        }

        // 落点要夹在收尾之前：拖放不调 NormalizeOrdersByChannel（只重排已占用的 order 槽位），
        // 不夹的话收尾会被挤到中间，而"收尾恒在末位"是所有收尾动作成立的前提。
        var to = TaskScopePlanner.ClampInsertIndex(ScopeTasks, Math.Clamp(targetIndex, 0, ScopeTasks.Count - 1));
        if (to == from)
        {
            return;
        }

        ScopeTasks.Move(from, to);

        // 只重排本组已占用的 order 值（多重集合不变），再刷新界面上显示的序号
        TaskScopePlanner.ApplyScopeSequence(ScopeTasks);
        TaskScopePlanner.RenumberScope(ScopeTasks);
    }

    /// <summary>
    /// 把选中任务改派到另一条执行通道（含「本地执行」），落到目标通道末尾。
    /// 改完自动切到目标通道 —— 否则任务会从当前列表里凭空消失，看着像被删了。
    /// </summary>
    public void MoveSelectedTaskToScope(string targetScopeId)
    {
        if (SelectedTask is null)
        {
            return;
        }

        var id = TaskScopePlanner.NormalizeId(targetScopeId);
        TaskScopePlanner.MoveToScope(Tasks, SelectedTask, id, Config.Rdp.Channels);

        RefreshChannelScopes();

        var target = ChannelScopes.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.Ordinal));
        if (target is not null)
        {
            SelectedScope = target;
        }
    }

    /// <summary>
    /// 有没有"真能跑起来"的脚本任务。
    ///
    /// <para>判据必须只看 <c>script</c> 类型：等待与收尾任务没有脚本路径，
    /// 沿用"任意一条路径非空"会把只含等待/收尾的通道判成"没有可用配置"，直接不让开始执行。</para>
    /// </summary>
    public bool HasUsableTaskConfig() => TaskTypeResolver.HasRunnableScript(Tasks);

    // ---------------- 配置读写 ----------------
    //
    // 落盘只有一条路：SaveConfigNow()。手动入口（开始执行 / 导入 / 通道结构变更）走
    // SaveConfig()，用户编辑走自动保存（EnableAutoSave 里挂的三类通知 + 去抖计时器）。
    // 两条路进的是同一个内核，"按通道重编 order" 因此只存在一处，不会两边不一致。

    /// <summary>
    /// 立即落盘（会写运行日志）。留给"确实需要当场写完"的位置：
    /// 开始执行、导入配置、通道增删复制、设置页里那些拨了就该立刻生效的开关。
    /// </summary>
    public void SaveConfig()
    {
        SaveConfigNow(announce: true);

        // 刚把完整配置写下去了：作废排队中的那次自动保存，
        // 否则 600 毫秒后会把同一个文件原样再写一遍。
        _autoSaver.Clear();
    }

    /// <summary>
    /// 落盘内核。
    ///
    /// <para><paramref name="announce"/> 为 false 时（自动保存）成功不写运行日志 ——
    /// 每改一个字段就冒一行"配置已保存"会把任务调度那几条信息淹掉；失败则走
    /// <see cref="ReportAutoSaveFailure"/>，一次失败只报一次。</para>
    ///
    /// <para>整个方法<b>同步</b>执行并置 <see cref="_applyingConfig"/>：它自己会改
    /// <c>Order</c>、给 <c>Config.Tasks</c> 赋值、调 <c>Sanitize()</c>，这些都会发出通知；
    /// 不挡住就会"落盘 → 通知 → 再排一次落盘"空转。</para>
    /// </summary>
    private void SaveConfigNow(bool announce)
    {
        if (_applyingConfig)
        {
            return;
        }

        _applyingConfig = true;
        try
        {
            // 按通道重编 order，保证落盘的序号连续唯一且分组有序（执行侧只看 order）
            var before = SnapshotOrders();
            TaskScopePlanner.NormalizeOrdersByChannel(Tasks, Config.Rdp.Channels);
            var ordersChanged = !OrdersMatch(before);

            Config.Tasks = new List<TaskConfig>(Tasks);
            WindowsStartupService.Apply(Config.WindowsStartup);

            _configManager.Save(Config);
            _lastAutoSaveError = string.Empty;

            if (announce)
            {
                AppendLog("配置已保存。");
            }

            // 序号被重排时界面上的序号还是旧的：就地刷当前通道的任务视图。
            // 不重建 ChannelScopes —— 整体替换那个集合是已知崩法，这里也没有必要。
            if (ordersChanged)
            {
                RefreshScopeTasks();
            }
        }
        catch (Exception ex)
        {
            if (announce)
            {
                AppendLog($"保存配置失败：{ex.Message}");
                _ = DialogHelper.ShowMessageAsync("保存失败", $"保存配置失败：{ex.Message}");
            }
            else
            {
                ReportAutoSaveFailure(ex);
            }
        }
        finally
        {
            _applyingConfig = false;
        }
    }

    /// <summary>落盘前的 order 快照（用于判断"这次落盘是否真的重排了序号"）。</summary>
    private int[] SnapshotOrders() => Tasks.Select(t => t.Order).ToArray();

    private bool OrdersMatch(int[] before)
    {
        if (before.Length != Tasks.Count)
        {
            return false;
        }

        for (var i = 0; i < before.Length; i++)
        {
            if (before[i] != Tasks[i].Order)
            {
                return false;
            }
        }

        return true;
    }

    // ---------------- 自动保存 ----------------

    /// <summary>
    /// 开启自动保存。调用时机：<see cref="Initialize"/> 末尾 —— 配置已加载、通道已重建，
    /// 之后的每一次界面改动都会被排进落盘队列。
    ///
    /// <para>四个触发源见各订阅处；结构变更（通道增删、导入）走的是 <c>List</c> 与
    /// <c>Clear()</c>，不产生可用的集合通知，那些位置本来就显式调 <see cref="SaveConfig"/>。</para>
    /// </summary>
    public void EnableAutoSave()
    {
        if (_autoSaveEnabled)
        {
            return;
        }

        _autoSaveEnabled = true;

        Tasks.CollectionChanged += OnTasksCollectionChanged;
        foreach (var task in Tasks)
        {
            HookTask(task);
        }

        Config.PropertyChanged += OnConfigChangedForAutoSave;
        HookRdpForAutoSave();

        _autoSaveTimer = _dispatcher.CreateTimer();
        _autoSaveTimer.Interval = TimeSpan.FromMilliseconds(_autoSaver.DelayMs);
        _autoSaveTimer.IsRepeating = false;
        _autoSaveTimer.Tick += OnAutoSaveTick;
    }

    /// <summary>
    /// 立即写完待存的改动（不等待去抖窗口）。退出前必须调一次 ——
    /// 用户改完 0.3 秒就关窗口时，计时器还没触发。
    /// </summary>
    public void FlushAutoSave()
    {
        if (!_autoSaveEnabled || !_autoSaver.HasPending)
        {
            return;
        }

        _autoSaveTimer?.Stop();

        if (!_autoSaver.BeginApply())
        {
            return;
        }

        try
        {
            SaveConfigNow(announce: false);
        }
        finally
        {
            _autoSaver.EndApply();
        }
    }

    /// <summary>排一次落盘（去抖窗口内的多次调用合并成一次写盘）。</summary>
    private void ScheduleAutoSave()
    {
        if (!_autoSaveEnabled || _applyingConfig)
        {
            return;
        }

        // 属性通知可能来自后台续体（例如预检回调改通道字段）：计时器只能在 UI 线程上操作
        if (!_dispatcher.HasThreadAccess)
        {
            Post(ScheduleAutoSave);
            return;
        }

        if (!_autoSaver.MarkDirty())
        {
            return;
        }

        _autoSaveTimer?.Stop();
        _autoSaveTimer?.Start();
    }

    private void OnAutoSaveTick(DispatcherQueueTimer sender, object args)
    {
        sender.Stop();

        if (!_autoSaver.BeginApply())
        {
            return;
        }

        try
        {
            SaveConfigNow(announce: false);
        }
        finally
        {
            _autoSaver.EndApply();
        }
    }

    private void OnTasksCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
        {
            foreach (var item in e.OldItems)
            {
                UnhookTask(item as TaskConfig);
            }
        }

        if (e.NewItems is not null)
        {
            foreach (var item in e.NewItems)
            {
                HookTask(item as TaskConfig);
            }
        }

        // Reset（导入配置时的 Clear）没有 OldItems，也无从摘订阅：旧对象随集合一起放掉即可，
        // 紧接着的 Add 会各自挂上（HookTask 自身幂等）。
        ScheduleAutoSave();
    }

    /// <summary>挂任务字段变更。先 <c>-=</c> 再 <c>+=</c>，重复挂载也只会有一份订阅。</summary>
    private void HookTask(TaskConfig? task)
    {
        if (task is null)
        {
            return;
        }

        task.PropertyChanged -= OnTaskChangedForAutoSave;
        task.PropertyChanged += OnTaskChangedForAutoSave;
    }

    private void UnhookTask(TaskConfig? task)
    {
        if (task is null)
        {
            return;
        }

        task.PropertyChanged -= OnTaskChangedForAutoSave;
    }

    /// <summary>
    /// 挂通道相关变更：<c>RdpConfig</c> 自身的字段（设置页的全局 RDP 选项）与每条通道的字段。
    /// 通道列表本身是 <c>List</c>，增删不产生通知 —— 那些位置已显式落盘。
    /// </summary>
    private void HookRdpForAutoSave()
    {
        if (!_autoSaveEnabled)
        {
            return;
        }

        var rdp = Config.Rdp;
        if (rdp is null)
        {
            return;
        }

        rdp.PropertyChanged -= OnNestedConfigChangedForAutoSave;
        rdp.PropertyChanged += OnNestedConfigChangedForAutoSave;

        foreach (var channel in rdp.Channels)
        {
            HookChannel(channel);
        }
    }

    private void HookChannel(RdpChannel? channel)
    {
        if (channel is null)
        {
            return;
        }

        channel.PropertyChanged -= OnNestedConfigChangedForAutoSave;
        channel.PropertyChanged += OnNestedConfigChangedForAutoSave;
    }

    private void OnTaskChangedForAutoSave(object? sender, PropertyChangedEventArgs e) => ScheduleAutoSave();

    private void OnNestedConfigChangedForAutoSave(object? sender, PropertyChangedEventArgs e) => ScheduleAutoSave();

    /// <summary>设置页直接绑到 <see cref="AppConfig"/> 的那些字段（关机 / 退出 / 自启 / 主题 / 更新…）。</summary>
    private void OnConfigChangedForAutoSave(object? sender, PropertyChangedEventArgs e) => ScheduleAutoSave();

    /// <summary>
    /// 自动保存失败的处理：写运行日志 + 铃铛里留一条（同标题就地替换，不会堆叠）。
    ///
    /// <para>⛔ 不弹对话框 —— 这里是从计时器回调进来，弹窗会和已打开的 ContentDialog
    /// 撞成 COMException（项目已知崩法）。失败意味着用户这次改动没进盘，所以必须留痕，
    /// 不能只在日志里说一句。</para>
    /// </summary>
    private void ReportAutoSaveFailure(Exception ex)
    {
        var message = ex.Message.Length == 0 ? ex.GetType().Name : ex.Message;

        if (string.Equals(message, _lastAutoSaveError, StringComparison.Ordinal))
        {
            return;
        }

        _lastAutoSaveError = message;
        AppendLog($"配置自动保存失败：{message}");

        try
        {
            Notifications.AddIfNew(
                "配置自动保存失败",
                $"改动没能写入 config.json：{message}。程序仍在运行，但这部分改动会在退出后丢失。",
                AppNotificationKinds.Warning);
        }
        catch
        {
            // 通知本身失败不该再抛（日志里已经有那一行了）
        }
    }

    /// <summary>
    /// 改主页大图后立刻落盘。
    ///
    /// 刻意不走 <see cref="SaveConfig"/>：那条路径会顺带重排任务序号、重新同步开机自启项，
    /// 换一张图不该牵动这些。这里只写 config.json。
    /// </summary>
    public void SaveHomeBanner(string? path)
    {
        Config.HomeBannerPath = (path ?? string.Empty).Trim();

        try
        {
            _configManager.Save(Config);
        }
        catch (Exception ex)
        {
            AppendLog($"保存首页图片设置失败：{ex.Message}");
            _ = DialogHelper.ShowMessageAsync("保存失败", $"保存首页图片设置失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 记录主窗口几何，用于下次启动时"记忆上次大小"。
    ///
    /// <paramref name="bounds"/> 为 null 表示当前处于最大化/最小化等"非还原"状态：
    /// 此时只更新 <see cref="WindowConfig.Maximized"/>，保留上一次的还原尺寸，
    /// 否则最大化状态下退出会把整屏尺寸当成小窗尺寸记下来。
    ///
    /// 这是随时可能发生的后台动作，所以：不写运行日志、不弹窗，值没变时连盘都不写。
    /// 注意它只改 <c>Config.Window</c>，不会顺带把任务列表的自动保存排队提前 ——
    /// 那些改动有自己的去抖窗口，这条路径不该替它们抢写盘。
    /// </summary>
    public void SaveWindowPlacement(WindowPlacement.Placement? bounds, bool maximized)
    {
        var window = Config.Window;

        if (bounds is null)
        {
            if (window.Maximized == maximized)
            {
                return;
            }

            window.Maximized = maximized;
        }
        else
        {
            var placement = bounds.Value;
            if (window.X == placement.X && window.Y == placement.Y &&
                window.Width == placement.Width && window.Height == placement.Height &&
                window.Maximized == maximized)
            {
                return;
            }

            window.X = placement.X;
            window.Y = placement.Y;
            window.Width = placement.Width;
            window.Height = placement.Height;
            window.Maximized = maximized;
        }

        try
        {
            _configManager.Save(Config);
        }
        catch
        {
            // 窗口几何记不住不该影响用户关窗口
        }
    }

    /// <summary>
    /// 记录右侧「执行状态栏」的收起/展开状态。
    ///
    /// 与 <see cref="SaveWindowPlacement"/> / <see cref="SaveCloseAction"/> 同一类动作：
    /// 随手发生、不写运行日志、不弹窗，值没变时连盘都不写；只改 <c>Config.Window</c>。
    /// </summary>
    public void SaveSidebarCollapsed(bool collapsed)
    {
        if (Config.Window.SidebarCollapsed == collapsed)
        {
            return;
        }

        Config.Window.SidebarCollapsed = collapsed;

        try
        {
            _configManager.Save(Config);
        }
        catch
        {
            // 记不住状态栏的折叠状态不该影响用户操作
        }
    }

    /// <summary>
    /// 记录"点关闭按钮时的行为"（每次询问 / 收进状态栏 / 直接退出 / 按状态自动）。
    ///
    /// 与 <see cref="SaveWindowPlacement"/> 同一类动作：
    /// 随手发生、不写运行日志、不弹窗，值没变时连盘都不写；**只改 Config.Window**，
    /// 不牵动任务列表的自动保存（那边有自己的去抖窗口）。
    /// </summary>
    public void SaveCloseAction(string action)
    {
        var normalized = Models.WindowConfig.NormalizeCloseAction(action);

        if (string.Equals(Config.Window.CloseAction, normalized, StringComparison.Ordinal))
        {
            return;
        }

        Config.Window.CloseAction = normalized;

        try
        {
            _configManager.Save(Config);
        }
        catch
        {
            // 记不住关闭行为不该影响用户关窗口
        }
    }

    /// <summary>设置页「关闭按钮」下拉框的选项。</summary>
    public IReadOnlyList<KeyValuePair<string, string>> CloseActionItems => Models.CloseDecision.Items;

    /// <summary>
    /// 关闭弹窗顶部那句"现在退掉会损失什么"。
    ///
    /// 必须区分本地与远程：本地任务会随主控端退出被清理，
    /// 而远程通道的任务跑在被控机的代理里，主控端退出不影响它们 —— 这两件事写错一句，
    /// 用户就会以为"点退出等于把挂机全停了"，或者反过来以为"退了它们还在跑"。
    /// </summary>
    public string DescribeRunningForClose()
    {
        if (!IsRunning)
        {
            return "当前没有任务在执行。";
        }

        var active = _sessions.Where(s => s.IsActive).ToList();
        var remaining = active.Sum(s => Math.Max(1, s.Total - s.Progress + 1));

        var text = remaining > 0 ? $"当前正在执行 {remaining} 个任务。" : "当前正在执行任务。";

        if (active.Any(s => !s.IsRemote))
        {
            text += "本地执行的任务会随退出中止。";
        }

        var remote = active.Where(s => s.IsRemote).Select(s => s.DisplayName).Distinct().ToList();
        if (remote.Count > 0)
        {
            text += $"通道「{string.Join("、", remote)}」的任务在被控机上继续跑。";
        }

        return text;
    }

    public async Task ExportConfigAsync()
    {
        TaskScopePlanner.NormalizeOrdersByChannel(Tasks, Config.Rdp.Channels);

        // 先问要带走什么，别一把梭全导 —— 分享文件时最容易出事的就是把本机的
        // 开机自启、窗口大小、密码一起发出去。
        var dialog = new ExportOptionsDialog();
        if (!await dialog.ShowDialogAsync() || dialog.Result is not { } options)
        {
            return;
        }

        var path = await DialogHelper.PickSaveFileAsync(
            "auto_script_config_share.json",
            ("JSON 配置文件", ".json"),
            ("所有文件", "."));

        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        try
        {
            File.WriteAllText(path, JsonSerializer.Serialize(BuildExport(options), new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            }), new System.Text.UTF8Encoding(false));

            AppendLog($"配置已导出：{path}（{options.BuildNote()}）");
        }
        catch (Exception ex)
        {
            AppendLog($"导出配置失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 按勾选项拼出要导出的配置。
    ///
    /// 关键约束：**绝不改动运行期的 <see cref="Config"/>**。
    /// 密码是从凭据管理器现读、临时填进副本的；一旦就地改 Config，
    /// 后续任何一次 <c>SaveConfig()</c> 都会把明文密码写进 config.json —— 那就彻底破了
    /// "密码只存在凭据管理器里"这条底线。所以全程操作副本。
    /// </summary>
    private AppConfig BuildExport(ExportOptions options)
    {
        options.Normalize();

        var export = new AppConfig(options.IncludeTasks ? new List<TaskConfig>(Tasks) : new List<TaskConfig>())
        {
            ExportOptions = new ExportOptions
            {
                IncludeAppSettings = options.IncludeAppSettings,
                IncludeTasks = options.IncludeTasks,
                IncludeRemoteAccounts = options.IncludeRemoteAccounts,
                IncludePasswords = options.IncludePasswords,
            },
        };

        export.ExportOptions.Note = options.BuildNote();

        if (options.IncludeAppSettings)
        {
            export.Theme = Config.Theme;
            export.ShutdownAfterDone = Config.ShutdownAfterDone;
            export.ShutdownDelaySeconds = Config.ShutdownDelaySeconds;
            export.AutoExitAfterDone = Config.AutoExitAfterDone;
            export.AutoStartTasks = Config.AutoStartTasks;
            export.EnableTimeoutScreenshot = Config.EnableTimeoutScreenshot;
            export.DeveloperMode = Config.DeveloperMode;
            export.Window = Config.Window;
        }
        else
        {
            // 本体设置不带走时给一组无害的默认值，别沿用运行期的值：
            // 对方的导入会"只导入任务与远程设置"，但这份文件本身应该自洽。
            export.Theme = "system";
            export.ShutdownDelaySeconds = 60;
            export.EnableTimeoutScreenshot = true;
            export.Window = new WindowConfig();
        }

        // 开机自启动永远是「关」—— 分享配置时带上它，等于替别人把自启打开。
        export.WindowsStartup = false;

        export.Rdp = BuildExportRdp(options);

        return export;
    }

    /// <summary>
    /// 导出用的 RDP 块。
    ///
    /// 不勾「远程用户设置」时也要保留主机与账户名（用户明确要求：对方至少知道该连谁），
    /// 但密码一律不带。
    /// </summary>
    private RdpConfig BuildExportRdp(ExportOptions options)
    {
        if (!options.IncludeRemoteAccounts)
        {
            // 只留身份信息，行为相关的设置全部回到默认，避免把本机的行为带给别人。
            return new RdpConfig
            {
                Enabled = false,
                TargetHost = Config.Rdp.TargetHost,
                TargetUser = Config.Rdp.TargetUser,
                BridgePath = Config.Rdp.BridgePath,
                Channels = Config.Rdp.Channels
                    .Select(c => new RdpChannel
                    {
                        Id = c.Id,
                        Name = c.Name,
                        Host = c.Host,
                        User = c.User,
                        BridgePath = c.BridgePath,
                        // 凭据"已保存"是本机状态，不该让对方以为密码已经在这儿了
                        CredentialSaved = false,
                        Enabled = false,
                    })
                    .ToList(),
            };
        }

        var rdp = Config.Rdp.Clone() as RdpConfig ?? new RdpConfig();

        if (options.IncludePasswords)
        {
            // 从凭据管理器现取明文密码，填进副本。Clone 里已经清过一次了，
            // 这里填的是**只用于导出、随后就丢**的那份。
            rdp.ExportPassword = RdpCredentialStore.Read(rdp.TargetHost, rdp.TargetUser) ?? string.Empty;

            foreach (var channel in rdp.Channels)
            {
                channel.ExportPassword = RdpCredentialStore.Read(channel.Host, channel.User) ?? string.Empty;
            }
        }
        else
        {
            rdp.ExportPassword = string.Empty;
            foreach (var channel in rdp.Channels)
            {
                channel.ExportPassword = string.Empty;
                // 没带密码就别声称"凭据已存"，否则导入方会以为可以直接连
                channel.CredentialSaved = false;
            }

            rdp.CredentialSaved = false;
        }

        return rdp;
    }

    public async Task ImportConfigAsync()
    {
        // 执行中导入会把「正在跑的任务对象」从集合里换掉，通道侧的会话/代理状态与它对不上。
        // 与其让人卡在不知情的状态里，不如直接拦住（开始执行那里已有同样的守卫）。
        if (IsRunning)
        {
            AppendLog("导入配置失败：任务正在执行，请先停止再导入。");
            await DialogHelper.ShowMessageAsync("导入失败", "任务正在执行，请先点「停止」再导入配置。");
            return;
        }

        var path = await DialogHelper.PickOpenFileAsync(("JSON 配置文件", ".json"), ("所有文件", "."));
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        try
        {
            var text = File.ReadAllText(path);
            var imported = JsonSerializer.Deserialize<AppConfig>(text, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                ReadCommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });

            if (imported is null)
            {
                throw new InvalidDataException("配置文件顶层结构不是对象");
            }

            imported.Sanitize();

            // ⚠️ 顺序要紧：先解掉旧选中态，再动集合。
            //
            // 之前是「Tasks.Clear() → 填新任务 → RefreshChannelScopes() → 赋 SelectedTask」，
            // 于是 SelectedTask 在中途一直指着**已被 Clear 掉的旧 TaskConfig**（孤儿引用）。
            // 同一个对象还被 FormHost.DataContext 绑着两向 TextBox
            //（ProcessKeywordsText 的 setter 还会连带 OnPropertyChanged(ProcessKeywords)），
            // 一次赋值触发两轮回写，把这串放大。
            //
            // ⚠️⚠️ 但更要紧的是**线程**——2026-10-04 真正的根因，抛 0x8001010E：
            // 上面 `await DialogHelper.PickOpenFileAsync(...)` 用的是 Windows App SDK 1.8 的
            // FileOpenPicker，它返回的 awaitable **不捕获 SynchronizationContext**，
            // await 之后这段代码**不一定还在 UI 线程上**。而主页有
            // `x:Bind VM.Tasks.Count`，x:Bind 生成的 setter 就是 `TextBlock.set_Text`，
            // WinRT 对象只能在它所属的 UI 线程上碰。于是
            // `Tasks.Clear()` → `ClearItems()` → 触发 `Update_VM_Tasks_Count`
            // → `TextBlock.set_Text` → COMException 0x8001010E (RPC_E_WRONG_THREAD)。
            // 现场栈（startup_error.log，2026-10-04 21:15:18）：
            //   ImportConfigAsync → Tasks.Clear() → ClearItems()
            //   → HomePage.g.cs Update_VM_Tasks_Count → TextBlock.set_Text
            // 所以下面**凡是会动集合 / 触发绑定刷新的操作一律包进 RunOnUiThreadAsync**，
            // 不靠"我这里看起来是 UI 线程"的运气。
            await RunOnUiThreadAsync(() =>
            {
                // 先解掉旧选中态（旧引用当场断开），再换集合，最后按新集合重建选中态。
                SelectedTask = null;
                _selectedScope = null;

                // 任务清单。导出时没勾「任务设置」的话这份就是空的，
                // 那就**别把现有任务清掉** —— 空文件不该等价于"清空一切"。
                if (HasImportedTasks(imported))
                {
                    Tasks.Clear();
                    foreach (var task in imported.Tasks)
                    {
                        Tasks.Add(task);
                    }
                }

                // 远程用户设置：导出时勾了才带，才导入。
                // 开机自启动永远不导入（分享文件里也永远是关的，兜个底）。
                if (imported.ExportOptions?.IncludeRemoteAccounts == true)
                {
                    ApplyImportedRdp(imported.Rdp);
                }

                TaskScopePlanner.NormalizeOrdersByChannel(Tasks, Config.Rdp.Channels);
                RefreshChannelScopes();
                ReloadChannels(refreshReadiness: false);
                SelectedTask = ScopeTasks.FirstOrDefault();
            });

            // 密码写进 Windows 凭据管理器，不进 config.json —— 这步**不能**放进上面的
            // UI 线程块（CredWrite 是同步 IO），也不能在 UI 线程外碰任何绑定属性。
            var savedCredentials = SaveImportedCredentials(imported);

            SaveConfig();

            AppendLog($"配置已导入并保存：{path}");
            AppendLog(DescribeImport(imported, savedCredentials));
        }
        catch (Exception ex)
        {
            // ⚠️ 别只记 ex.Message：WinRT/COM 异常的 Message 经常是空串或一句没信息量的
            // 「该操作尝试访问的数据超出了有效范围」，落到日志里就是光秃秃一行
            // 「导入配置失败：」，完全看不出是哪一步炸的。类型名 + 栈 + 聚合内层一起记。
            var detail = DescribeException(ex);
            AppendLog($"导入配置失败：{detail}");
            await DialogHelper.ShowMessageAsync("导入失败", $"导入配置失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 导入文件里是否真的带了任务。
    ///
    /// 老版本导出、以及导出时没勾「任务设置」的文件，<c>tasks</c> 会是空数组。
    /// 那时**不能**把它当成"清空现有任务" —— 现在的做法是保持原样，
    /// 否则一次只想导入远程设置的操作会把任务清空。
    /// </summary>
    private static bool HasImportedTasks(AppConfig imported) =>
        imported.Tasks is { Count: > 0 };

    /// <summary>
    /// 把导入文件里的远程用户设置搬进 <see cref="Config"/>。
    /// 必须在 UI 线程调用（会动 Channels 集合与相关绑定）。
    /// </summary>
    private void ApplyImportedRdp(RdpConfig? source)
    {
        if (source is null)
        {
            return;
        }

        var rdp = Config.Rdp;

        rdp.Enabled = source.Enabled;
        rdp.TargetHost = source.TargetHost;
        rdp.BridgePath = source.BridgePath;
        rdp.TargetUser = source.TargetUser;
        rdp.SessionFinish = source.SessionFinish;
        rdp.NotifyOnTaskDone = source.NotifyOnTaskDone;
        rdp.NotifyOnChannelDone = source.NotifyOnChannelDone;
        rdp.NotifyOnAllDone = source.NotifyOnAllDone;
        rdp.NotifyLocalTaskDone = source.NotifyLocalTaskDone;

        // 刻意不导入 AutoReturnToControllerSession：它决定「谁的桌面被接管」，
        // 属于本机行为。跟着别人分享的配置文件走，等于让一份 json 把使用者的桌面顶掉。
        rdp.ConnectTimeoutSeconds = source.ConnectTimeoutSeconds;
        rdp.EmbedRemoteDesktop = source.EmbedRemoteDesktop;
        rdp.ClientMode = source.ClientMode;
        rdp.AudioEnabled = source.AudioEnabled;
        rdp.GfxEnabled = source.GfxEnabled;
        rdp.DesktopWidth = source.DesktopWidth;
        rdp.DesktopHeight = source.DesktopHeight;

        // 开机自启动属于本机行为，分享文件里永远是关的，别跟着打开
        rdp.CredentialSaved = false;

        if (source.Channels is { Count: > 0 })
        {
            rdp.Channels = source.Channels
                .Select(c => new RdpChannel
                {
                    Id = c.Id,
                    Name = c.Name,
                    Host = c.Host,
                    User = c.User,
                    BridgePath = c.BridgePath,
                    SessionFinish = c.SessionFinish,
                    DesktopWidth = c.DesktopWidth,
                    DesktopHeight = c.DesktopHeight,
                    Enabled = c.Enabled,
                    // 密码下一步才写进凭据管理器，这里先别声称已存
                    CredentialSaved = false,
                })
                .ToList();
        }
    }

    /// <summary>
    /// 把导入文件里的明文密码写进 Windows 凭据管理器（按 host|user 一条）。
    ///
    /// 返回成功保存的条数，用于给用户一句明确交代 ——
    /// "密码没保存进去"和"文件里根本没密码"是两回事，不说清楚会让人以为能直接连。
    /// 写完后导出用的临时字段要清掉，免得被后面的 <c>SaveConfig()</c> 带进 config.json。
    /// </summary>
    private int SaveImportedCredentials(AppConfig imported)
    {
        if (imported.ExportOptions?.IncludePasswords != true)
        {
            return 0;
        }

        var saved = 0;

        try
        {
            var rdp = imported.Rdp;
            if (rdp is null)
            {
                return 0;
            }

            if (!string.IsNullOrEmpty(rdp.ExportPassword)
                && RdpCredentialStore.Write(rdp.TargetHost, rdp.TargetUser, rdp.ExportPassword))
            {
                rdp.CredentialSaved = true;
                saved++;
            }

            foreach (var channel in rdp.Channels ?? new List<RdpChannel>())
            {
                if (string.IsNullOrEmpty(channel.ExportPassword))
                {
                    continue;
                }

                if (!RdpCredentialStore.Write(channel.Host, channel.User, channel.ExportPassword))
                {
                    continue;
                }

                saved++;

                // 标记要落在**运行期那份**通道上（导入时 ApplyImportedRdp 已经把
                // imported 的通道搬进 Config.Rdp.Channels 了，但这里按 id 找一份，
                // 不假设两边是同一个对象）
                var live = Config.Rdp.Channels.FirstOrDefault(c =>
                    string.Equals(c.Id, channel.Id, StringComparison.OrdinalIgnoreCase));
                if (live is not null)
                {
                    live.CredentialSaved = true;
                }
            }
        }
        catch (Exception ex)
        {
            // 密码保存失败不该让整个导入算失败：配置已经搬进来了，只是要重新输一次密码。
            AppendLog($"导入的密码没能存进凭据管理器（需要手动重输一次）：{ex.Message}");
        }
        finally
        {
            // 清掉临时字段：它们只属于导出产物，绝不能进 config.json
            imported.Rdp.ExportPassword = string.Empty;
            foreach (var channel in imported.Rdp?.Channels ?? new List<RdpChannel>())
            {
                channel.ExportPassword = string.Empty;
            }
        }

        return saved;
    }

    /// <summary>给用户一句"这次到底导入了什么"的实话，别让人猜。</summary>
    private static string DescribeImport(AppConfig imported, int savedCredentials)
    {
        var options = imported.ExportOptions;
        if (options is null)
        {
            return "导入说明：这份文件没有说明来源，只按旧规则导入任务配置；"
                + "自动关机、开机自启动、截图开关等本机设置保持不变。";
        }

        var parts = new List<string>();
        parts.Add(HasImportedTasks(imported)
            ? $"任务配置（{imported.Tasks.Count} 条）"
            : "未包含任务（本机任务保持不变）");

        if (options.IncludeRemoteAccounts)
        {
            parts.Add("远程用户设置");
        }

        if (options.IncludeAppSettings)
        {
            parts.Add("本体设置");
        }

        var text = "导入说明：" + string.Join("、", parts)
            + "。自动关机、开机自启动、截图开关等本机设置仍保持本机原值。";

        if (options.IncludePasswords)
        {
            text += savedCredentials > 0
                ? $"已把 {savedCredentials} 条密码存入 Windows 凭据管理器。"
                : "文件里没有可用的密码，远程连接需手动重输一次密码。";
        }

        return text;
    }

    /// <summary>
    /// 把异常摊开成能定位的一行串：类型 + 消息 + 栈 + AggregateException 内层。
    /// 栈只取本进程内的帧（WinRT 帧会淹掉真正的落点）。
    /// </summary>
    internal static string DescribeException(Exception ex)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append(ex.GetType().FullName).Append(": ").AppendLine(ex.Message);

        if (ex is AggregateException agg && agg.InnerExceptions.Count > 0)
        {
            foreach (var inner in agg.InnerExceptions)
            {
                sb.Append("  -- 内层 --> ").AppendLine(inner.GetType().FullName + ": " + inner.Message);
            }
        }
        else if (ex.InnerException is not null)
        {
            sb.Append("  -- 内层 --> ").AppendLine(ex.InnerException.GetType().FullName + ": " + ex.InnerException.Message);
        }

        if (!string.IsNullOrWhiteSpace(ex.StackTrace))
        {
            sb.AppendLine(ex.StackTrace);
        }

        // 出错现场直接落一份到 startup_error.log —— 主日志会被后续行淹没，
        // 而这个文件是追加的、只记异常，不会被业务日志冲掉。
        AppPaths.WriteStartupError(ex);

        return sb.ToString();
    }

    // ---------------- 执行控制 ----------------

    /// <summary>
    /// 开始执行。任务按「执行通道」分组：未分配通道的走本地（主控端当前会话），
    /// 分配了通道的下发到各自的目标会话 —— **通道之间并行，通道内部仍按 order 串行**。
    ///
    /// 分组与截断全部由纯函数 <see cref="RdpChannelPlanner.Plan"/> 决定（可单测），
    /// 这里只负责把规划结果落到运行时。
    /// </summary>
    public void Start()
    {
        if (IsRunning)
        {
            return;
        }

        SaveConfig();

        var plan = RdpChannelPlanner.Plan(Tasks, Config.Rdp.Channels, RdpSettings.Enabled);

        if (plan.IsEmpty)
        {
            _ = DialogHelper.ShowMessageAsync("提示", "当前没有启用的任务。");
            return;
        }

        if (!HasUsableTaskConfig())
        {
            _ = DialogHelper.ShowMessageAsync("提示", "还没有设置可执行的脚本配置。");
            return;
        }

        // 没启动的任务必须说明原因（通道停用 / 没配账户 / 超并发上限 / 引用了不存在的通道）。
        // 静默丢掉最糟 —— 用户会以为它们在跑。
        foreach (var skipped in plan.Skipped)
        {
            AppendLog("⚠ " + skipped.Describe());
        }

        // 并发组只在通道内生效（ScriptRunner 只扫本通道那一批任务）—— 跨通道同名组不会并在一起。
        // 说一句，免得用户以为"起了同一个组名就会一起跑"。
        foreach (var group in TaskScopePlanner.CrossChannelGroups(Tasks))
        {
            AppendLog($"⚠ 并发组「{group}」跨了多个执行通道：并发组只在同一个通道内生效，各通道仍会各跑各的。");
        }

        // 并发上限单独再点一次名：这是唯一一种"看起来该跑、其实没跑"的截断，
        // 而且用户自己就能解决（合并通道 / 分两轮）。其余原因多半是配置笔误，上面那行已经说明白了。
        var overflow = plan.Skipped.Where(s => s.Reason.Contains("并发上限")).ToList();
        if (overflow.Count > 0)
        {
            var names = string.Join("、", overflow.Select(s => s.Channel?.DisplayName ?? "未命名通道"));
            AppendLog(
                $"⚠ 并发上限 {RdpNativeLimits.MaxSessions}：本轮有 {overflow.Count} 条通道被截断（{names}），" +
                "它们的任务这一轮不会执行 —— 想全跑就分两轮，或把任务合并到排在前面的通道。");
        }

        ResetSessions(plan);

        // 新一轮：清掉上一轮可能残留的中止标志（否则"连接前等待"一进去就退出）
        _runAbortRequested = false;
        _runStoppedOrError = false;
        ResetRunSummaryState();
        _runStartedAt = DateTimeOffset.Now;
        _runEndedAt = null;
        _pendingRuns = plan.ToLaunch.Count + (plan.LocalTasks.Count > 0 ? 1 : 0);

        if (_pendingRuns == 0)
        {
            _ = DialogHelper.ShowMessageAsync("提示",
                "启用的任务都落在未启动的通道里（见运行日志里的原因），本轮没有可执行的任务。");
            return;
        }

        IsRunning = true;

        // 有通道被并发上限截断时弹一次说明（放在"本轮还有任务要跑"之后，免得和空任务提示叠两个窗）
        if (overflow.Count > 0)
        {
            _ = DialogHelper.ShowMessageAsync("并发上限",
                $"同时最多并行 {RdpNativeLimits.MaxSessions} 条会话通道，本轮有 {overflow.Count} 条被截断：\n\n" +
                string.Join("\n", overflow.Select(s => "· " + s.Describe())) +
                "\n\n被截断通道里的任务本轮不会执行。可以让这些通道分两轮跑，或把任务合并到排在前面的通道。");
        }

        if (plan.LocalTasks.Count > 0)
        {
            StartLocalRun(plan.LocalTasks);
        }

        if (plan.ToLaunch.Count > 0)
        {
            StartRdpPolling();
            AppendLog(
                $"本轮启动 {plan.ToLaunch.Count} 条会话通道并行执行：" +
                string.Join("、", plan.ToLaunch.Select(p => p.Channel.DisplayName)) + "。");

            _ = RunChannelsAsync(plan.ToLaunch);
        }

        RaisePanelChanged();
    }

    /// <summary>
    /// 按本轮规划重建会话列表：本地执行组（若有本地任务）+ 每条要启动的通道。
    /// 上一轮的会话整体丢弃（菜单项与"已结束"状态在 M4 里按通道保留）。
    /// </summary>
    private void ResetSessions(ExecutionPlan plan)
    {
        _sessions.Clear();

        if (plan.LocalTasks.Count > 0)
        {
            var local = new RdpChannelSession(null, RdpBridge.ForChannel(null));
            local.SetEventLineLimit(Config.EventLineLimit);
            local.BeginLocal(plan.LocalTasks.Count);
            _sessions.Add(local);
        }

        foreach (var entry in plan.ToLaunch)
        {
            var session = new RdpChannelSession(entry.Channel, RdpBridge.ForChannel(entry.Channel));
            session.SetEventLineLimit(Config.EventLineLimit);
            session.BeginLocal(entry.Tasks.Count);   // 归属性（CommandId）等真正下发时再记
            _sessions.Add(session);
        }

        // 界面默认展示第一条远程通道（没有就展示本地组）
        _activeSession = _sessions.FirstOrDefault(s => s.IsRemote) ?? _sessions.FirstOrDefault();
        OnPropertyChanged(nameof(ActiveSession));
        OnPropertyChanged(nameof(ActiveChannelName));
        OnPropertyChanged(nameof(RdpEvents));
        OnPropertyChanged(nameof(RdpExternalLogs));
        RaiseSessionsChanged();
        MirrorActiveSession();
    }

    /// <summary>
    /// 本地执行组：跑在主控端当前会话里。与改造前完全一致，只是任务集合来自规划结果
    /// （只含 channel_id 为空的那批）。
    /// </summary>
    private void StartLocalRun(IReadOnlyList<TaskConfig> tasks)
    {
        StatusText = "启动确认中";
        CurrentTaskName = "-";
        ElapsedText = "00:00";
        ProgressText = $"0 / {tasks.Count}";

        // 同一份进度也记进本地执行组：面板 / 主页概览 / 整轮汇总都从会话里读
        LocalGroup?.UpdateLocal(
            phase: "running",
            statusText: "启动确认中",
            progress: 0,
            total: tasks.Count,
            currentTask: "-",
            elapsedSeconds: 0);
        RaisePanelChanged();

        var snapshots = tasks.Select(t => t.Clone()).ToList();

        _runner = new ScriptRunner(
            snapshots,
            Config.ShutdownAfterDone,
            Config.ShutdownDelaySeconds,
            _logger,
            _statsManager,
            Config.EnableTimeoutScreenshot);

        _runner.Logged += OnRunnerLogged;
        _runner.StatusChanged += OnRunnerStatus;
        _runner.ProgressChanged += OnRunnerProgress;
        _runner.TaskStarted += OnRunnerTaskStarted;
        _runner.TaskCompleted += OnRunnerTaskCompleted;
        _runner.ElapsedChanged += OnRunnerElapsed;
        _runner.TaskLaunchSucceeded += OnRunnerTaskLaunchSucceeded;
        _runner.TaskError += OnRunnerTaskError;
        _runner.ShutdownPrompt += OnRunnerShutdownPrompt;
        _runner.Finished += OnRunnerFinished;

        // 外部脚本实时日志（本地执行路径）：远程通道那边由 RdpAgentRunner 写入独立 live-log 流，
        // 本地没有桥，所以在这里直接写入独立日志集合 + 落进归档文件。
        // 同样是**全部保留**，滚动窗口由事件列表自己维护。
        var logSession = LocalGroup;
        _runner.TaskExternalLogLineRead += (_, item) => OnExternalLogLine(logSession, item);

        _runTask = _runner.RunAsync();
    }

    /// <summary>
    /// 各通道并行启动 —— 互不等待（各自去等自己的会话与代理上线），
    /// 所以慢的通道不会拖住快的通道。
    /// </summary>
    private async Task RunChannelsAsync(IReadOnlyList<ChannelPlan> plans)
    {
        var jobs = new List<Task>();
        var localChannels = 0;

        // 任务启动会新开会话、改变控制台归属 —— 交给调度器延迟应用
        NotifyUuFixRelevantAction("开始执行任务");

        Interlocked.Increment(ref _channelRunDepth);
        try
        {
            foreach (var entry in plans)
            {
                var session = _sessions.FirstOrDefault(s =>
                    s.IsRemote && string.Equals(s.ChannelId, entry.Channel.Id, StringComparison.OrdinalIgnoreCase));

                if (session is null)
                {
                    continue;
                }

                // 预检（凭据 / 账户 / 远程桌面开关 / 共享目录）不通过就跳过这条通道 ——
                // 但计数必须减掉，否则整轮永远不会判定结束。
                if (!ValidateChannel(entry.Channel, entry.Tasks))
                {
                    AppendLog($"[通道：{entry.Channel.DisplayName}] 预检未通过，该通道本轮不执行（原因见上方提示）。");
                    session.MarkCompletionReported();
                    FinishRunPart();
                    continue;
                }

                if (RdpTargets.IsLocal(entry.Channel.Host))
                {
                    localChannels++;
                }

                jobs.Add(RunChannelAsync(session, entry.Tasks));
            }

            if (jobs.Count > 0)
            {
                await Task.WhenAll(jobs);
            }
        }
        finally
        {
            Interlocked.Decrement(ref _channelRunDepth);
        }

        // 批次收敛点：所有通道的准备流程都已返回（连接成功/失败/超时都算），
        // 此时合并成一次切回，而不是每条通道各切一次。
        await TriggerControllerSessionReturnAsync("任务启动", localChannels > 0);
    }

    /// <summary>
    /// 报告一次「可能改变控制台归属」的操作（开始执行、连接通道、连接全部、内嵌连上…）。
    ///
    /// <para>不直接执行修复：交给调度器按 18/30 秒规则合并。开关关闭时这里是空操作。</para>
    /// </summary>
    public void NotifyUuFixRelevantAction(string source)
    {
        if (!RdpSettings.UuSessionFixEnabled)
        {
            return;
        }

        _uuFixScheduler.Notify(source);
    }

    /// <summary>关闭 UU 修复开关：取消尚未开始的等待。</summary>
    public void CancelUuFixSchedule() => _uuFixScheduler.Cancel();

    /// <summary>
    /// 请求一次「把控制台切回主控会话」。调用点必须是连接批次收敛之后，
    /// 不能放进原生 Connected 回调或批量按钮的循环体里。
    ///
    /// <para>失败只记录，不改写任务本身的结果：连接与任务成功与否，
    /// 与「当前桌面归属」是两件事，混在一起会让排障失去判断依据。</para>
    /// </summary>
    private async Task TriggerControllerSessionReturnAsync(string source, bool hasLocalChannel)
    {
        if (!hasLocalChannel || !RdpSettings.AutoReturnToControllerSession)
        {
            return;
        }

        ControllerSessionReturnResult result;
        try
        {
            result = await _controllerSessionReturn
                .RequestAsync(source, enabled: true, hasLocalChannel: true, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            AppendLog($"[切回] 请求异常：{(ex.Message.Length == 0 ? ex.GetType().Name : ex.Message)}");
            return;
        }

        AppendLog(DescribeControllerSessionReturn(result));

        if (!result.IsSuccess)
        {
            return;
        }

        // 状态栏只在确实做过动作时更新；AppendLog 自带编组，UI 属性必须 Post。
        Post(() =>
        {
            if (result.Outcome == ControllerSessionReturnOutcome.Succeeded)
            {
                RdpStatusText = $"已切回主控会话（会话 {result.ControllerSessionId}）";
            }
        });
    }

    private static string DescribeControllerSessionReturn(ControllerSessionReturnResult result) =>
        result.Outcome switch
        {
            ControllerSessionReturnOutcome.Succeeded =>
                $"[切回] {result.Source}：控制台已从会话 {result.ConsoleSessionIdBefore} 切回主控会话 {result.ControllerSessionId}。",
            ControllerSessionReturnOutcome.AlreadyConsole =>
                $"[切回] {result.Source}：主控已在控制台上，无需切换。",
            ControllerSessionReturnOutcome.Busy =>
                "[切回] 已有一次切回在执行，本次请求已合并。",
            ControllerSessionReturnOutcome.Cancelled =>
                "[切回] 请求在提交前被取消（停止或退出）。",
            ControllerSessionReturnOutcome.QueryFailed =>
                "[切回] 未能确认会话归属，未执行命令：" + result.Detail,
            ControllerSessionReturnOutcome.Timeout =>
                "[切回] " + result.Detail,
            ControllerSessionReturnOutcome.CommandFailed =>
                "[切回] 切换失败：" + result.Detail,
            ControllerSessionReturnOutcome.VerifyFailed =>
                "[切回] 命令已执行但未确认切回：" + result.Detail,
            _ => "[切回] " + result.Detail,
        };

    public void Pause()
    {
        _runner?.RequestPause();
    }

    public void Resume()
    {
        _runner?.RequestResume();
    }

    /// <summary>
    /// 停止执行。界面上只有这一个停止入口（原「紧急停止全部」已合并进来）。
    ///
    /// <para>注意「停止」的语义是**中止本轮并做全量收尾**，不是"跑完当前任务"：
    /// 队列立即停，后续任务一个都不执行；收尾范围取全部任务配置（见
    /// <c>ScriptRunner.RequestStop</c> 的说明）。</para>
    /// </summary>
    public void Stop()
    {
        // 先置中止标志：还没下发指令的通道可能正卡在「连接前等待」里，
        // 它们没有任何 runner / stop.json 可通知，只能靠这个标志醒过来。
        _runAbortRequested = true;

        // 停止时先作废尚未提交的切回请求：用户要的是「停下」，不是把桌面再抢一次。
        _controllerSessionReturn.CancelPending();
        _runner?.RequestStop();
        RequestRemoteStop(emergency: false);
    }

    /// <summary>程序退出前调用：抑制尚未提交的切回，避免退出流程与会话切换抢时序。</summary>
    public void SuppressControllerSessionReturn() => _controllerSessionReturn.CancelPending();

    /// <summary>
    /// 请求**全部通道**的目标会话停止正在跑的任务。
    ///
    /// 为什么需要它：远程任务跑在目标会话的代理进程里，本地 <c>_runner</c> 管不到它 ——
    /// 两个会话之间只有桥文件能通信，所以把"停"这个意图写进各通道自己的 stop.json，
    /// 由代理每秒检查一次。带 CommandId 做归属校验，避免把下一轮指令也一起停掉。
    /// </summary>
    private void RequestRemoteStop(bool emergency)
    {
        var sent = 0;

        foreach (var session in _sessions.ToList())
        {
            if (!session.IsRemote || !session.HasCommand)
            {
                continue;
            }

            if (!session.WriteStop(emergency, out var error))
            {
                AppendChannelLog(session, $"⚠ 停止请求写入桥目录失败：{error}（目标会话可能收不到停止指令）");
                continue;
            }

            session.MarkStopping(emergency, DateTimeOffset.Now);
            sent++;
        }

        if (sent == 0)
        {
            return;
        }

        AppendLog(emergency
            ? $"已向 {sent} 个通道发送紧急停止请求，等待代理响应。"
            : $"已向 {sent} 个通道发送停止请求，等待代理收尾。");

        MirrorActiveSession();
    }

    public void CancelShutdown()
    {
        if (ShutdownService.Cancel())
        {
            AppendLog("已发送取消关机命令：shutdown /a");
        }
    }

    // ---------------- 执行器回调（均在 UI 线程执行） ----------------

    private void OnRunnerLogged(object? sender, string line) => Post(() => AppendLogLine(line));

    private void OnRunnerStatus(object? sender, string status) => Post(() =>
    {
        StatusText = status;
        LocalGroup?.UpdateLocal(statusText: status, phase: LocalPhaseFor(status));
        RaisePanelChanged();
    });

    private void OnRunnerProgress(object? sender, (int Current, int Total) progress) => Post(() =>
    {
        ProgressText = $"{progress.Current} / {progress.Total}";
        LocalGroup?.UpdateLocal(progress: progress.Current, total: progress.Total);
        RaisePanelChanged();
    });

    private void OnRunnerTaskStarted(object? sender, (string Name, int Index, int Total) info) => Post(() =>
    {
        CurrentTaskName = info.Name;
        ElapsedText = "00:00";
        ProgressText = $"{info.Index} / {info.Total}";
        LocalGroup?.UpdateLocal(
            currentTask: info.Name,
            progress: info.Index,
            total: info.Total,
            elapsedSeconds: 0);
        RaisePanelChanged();
    });

    private void OnRunnerElapsed(object? sender, int seconds) => Post(() =>
    {
        ElapsedText = FormatHelper.FormatSeconds(seconds);
        LocalGroup?.UpdateLocal(elapsedSeconds: seconds);
        RaisePanelChanged();
    });

    private void OnRunnerTaskLaunchSucceeded(object? sender, string taskName) => Post(() =>
    {
        StatusText = "运行中";
        LocalGroup?.UpdateLocal(statusText: "运行中", phase: "running");
        RaisePanelChanged();
    });

    private void OnRunnerTaskError(object? sender, string message) => Post(() => _runTaskErrorCount++);

    private void OnRunnerShutdownPrompt(object? sender, int delay) => Post(() =>
    {
        ShutdownService.Schedule(delay);
        AppendLog($"已发送延迟关机命令：shutdown /s /t {delay}");
        ShutdownPromptRequested?.Invoke(this, delay);
    });

    private void OnRunnerFinished(object? sender, bool stoppedOrError) => Post(() =>
    {
        _runner = null;
        _runTask = null;
        _runStoppedOrError |= stoppedOrError;

        var stopped = StatusText.Contains("停止");
        string summary;

        if (stoppedOrError)
        {
            if (stopped)
            {
                StatusText = "已停止";
                summary = "被手动停止";
            }
            else
            {
                StatusText = "已完成（有异常）";
                var count = Math.Max(1, _runTaskErrorCount);

                // 有原因就说原因：「发生 1 次任务异常」这种句子不含任何线索，
                // 用户在汇总通知/日志里看到它仍然不知道是哪个任务、为什么。
                summary = _localAbnormalReasons.Count > 0
                    ? _localAbnormalReasons[0]
                    : $"发生 {count} 次任务异常";
            }
        }
        else
        {
            StatusText = "已完成";
            summary = "全部任务结束";
        }

        // 与远程通道同构：只登记这块自己的结果，终态交给整轮汇总 ——
        // 多通道并行时"本地跑完了"不等于"整轮结束"。
        // 主动停止不算异常（它不该把整轮判成"有异常"，只影响是否放行自动退出）。
        var localReasons = _localAbnormalReasons.ToList();
        _localAbnormalReasons.Clear();

        _runParts.Add(new RunPartResult(
            "本地执行",
            false,
            stoppedOrError && !stopped,
            summary,
            stopped ? null : localReasons,
            stopped));

        // 本地执行组同步落终态：单块跑本地时，面板/概览就是靠它显示"已完成"
        LocalGroup?.UpdateLocal(
            phase: stopped ? "stopped" : stoppedOrError ? "error" : "done",
            statusText: StatusText);
        RaisePanelChanged();

        // 本地这一块收尾：还有远程通道在跑就只减计数，
        // 整轮结束才落终态、刷新预检、放行"完成后自动退出"（在 FinishRunPart 里）。
        FinishRunPart();
    });

    /// <summary>
    /// 把 <c>ScriptRunner</c> 的中文状态映射成会话字段用的 phase。
    /// 只有 <c>running</c> 算"活动"（概览卡的高亮与"当前展示通道"的判定都用它），
    /// 其余都是终态。判定顺序与 <see cref="OnRunnerStatus"/> 里原有的异常识别保持一致。
    /// </summary>
    private static string LocalPhaseFor(string status)
    {
        if (status.Contains("停止") || status.Contains("暂停"))
        {
            return "stopped";
        }

        if (status.Contains("异常") || status.Contains("错误") || status.Contains("失败") || status.Contains("报错"))
        {
            return "error";
        }

        return status.Contains("完成") ? "done" : "running";
    }

    // ---------------- 日志 ----------------

    public void AppendLog(string message)
    {
        var line = _logger.Log(message);
        AppendLogLine(line);
    }

    /// <summary>
    /// 带通道前缀的日志：多通道并行混跑时，一眼看出这条是哪条通道的。
    /// 前缀与通道页的展示名一致（形如 <c>[通道：1 号机 · Player2]</c>）。
    /// </summary>
    public void AppendChannelLog(RdpChannelSession session, string message) =>
        AppendLog($"[通道：{session.DisplayName}] {message}");

    private void AppendLogLine(string line)
    {
        if (!_dispatcher.HasThreadAccess)
        {
            Post(() => AppendLogLine(line));
            return;
        }

        _logBacklog.Add(line);
        while (_logBacklog.Count > MaxLogLines)
        {
            _logBacklog.RemoveAt(0);
        }

        if (!ShouldShowLogLine(line))
        {
            // 第一次藏东西时才通知界面 —— 见 _hasHiddenDiagnosticLine 的注释
            if (!_hasHiddenDiagnosticLine)
            {
                _hasHiddenDiagnosticLine = true;
                OnPropertyChanged(nameof(DiagnosticLogHintVisibility));
            }

            return;
        }

        LogLines.Add(line);
        while (LogLines.Count > MaxLogLines)
        {
            LogLines.RemoveAt(0);
        }
    }

    /// <summary>
    /// 这一行该不该出现在「运行日志」页面上。
    ///
    /// 两类诊断行默认不显示：
    ///   · 内嵌画面的（形如 <c>[embed] D3D 渲染器初始化成功…</c>）—— 挂着画面时它每 5 秒
    ///     写一行帧到达节奏 / GFX 状态，几路画面同时跑就能把"任务跑到哪一步、哪条通道出事了"
    ///     顶出屏幕；
    ///   · 内存探针的（形如 <c>[memory] private_mb=393 working_set_mb=402 …</c>）—— 同理，
    ///     而且它比内嵌那类更密（每几秒一行，一屏全是数字）。
    /// 要看得去「设置 → 开发者」打开开发者模式。
    ///
    /// ⚠️ 只挡显示，**不挡落盘**：<see cref="FileLogger"/> 那边的日志文件始终是完整的
    /// （AppendLog 是"先写文件、再决定要不要上页面"），排障时照旧能照着时间翻。
    /// </summary>
    private bool ShouldShowLogLine(string line) =>
        Config.DeveloperMode
        || (!line.Contains(EmbedLogTag, StringComparison.OrdinalIgnoreCase)
            && !line.Contains(MemoryLogTag, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 开发者模式拨动后重排日志页：打开时把攒着的 <c>[embed]</c> 行补回来，
    /// 关闭时把已经显示在页面上的那些收掉。
    ///
    /// 只改数据与 <see cref="_hasHiddenDiagnosticLine"/>，属性变更通知交给调用方发
    /// （<see cref="DiagnosticLogHintVisibility"/> 同时取决于这个字段和配置开关，
    /// 分开发容易漏掉一边）。
    /// </summary>
    private void RefreshLogFilter()
    {
        if (!_dispatcher.HasThreadAccess)
        {
            Post(RefreshLogFilter);
            return;
        }

        LogLines.Clear();
        var hidden = false;
        foreach (var line in _logBacklog)
        {
            // LogLines 与 _logBacklog 各自受 MaxLogLines 约束，筛完的行数只会更少
            if (ShouldShowLogLine(line))
            {
                LogLines.Add(line);
            }
            else
            {
                hidden = true;
            }
        }

        _hasHiddenDiagnosticLine = hidden;
    }

    /// <summary>
    /// 配置里会直接影响界面的开关（当前只有开发者模式）改完要立刻重排日志页。
    /// 设置页那边 <c>x:Bind</c> 改的就是这同一个 <see cref="Config"/> 实例，钩在这里最省事
    /// （也顺带覆盖了以后任何一处程序化改动）。
    /// </summary>
    private void OnConfigPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(AppConfig.DeveloperMode))
        {
            return;
        }

        // 属性变更通知本身只能在 UI 线程发。界面上改这个开关本来就在 UI 线程，
        // 这里只是兜住将来从别处（比如配置加载）程序化赋值的可能。
        if (!_dispatcher.HasThreadAccess)
        {
            var args = e;
            Post(() => OnConfigPropertyChanged(sender, args));
            return;
        }

        RefreshLogFilter();
        OnPropertyChanged(nameof(DiagnosticLogHintVisibility));
    }

    public void ClearLogs()
    {
        _logBacklog.Clear();
        LogLines.Clear();

        if (_hasHiddenDiagnosticLine)
        {
            _hasHiddenDiagnosticLine = false;
            OnPropertyChanged(nameof(DiagnosticLogHintVisibility));
        }
    }

    // ---------------- 统计 ----------------

    /// <summary>
    /// 汇总各路耗时统计：主控端自己跑的（runtime_stats）以及各会话通道里代理跑完发布的（桥里的 stats.json）。
    ///
    /// 以前只读主控端那份目录 —— 可任务大多交给会话通道执行，统计全落在目标账户那边，
    /// 于是这一页永远空白（见 StatsSourcePlanner 的说明）。
    /// </summary>
    public void RefreshStats()
    {
        StatRows.Clear();

        try
        {
            var rows = new List<StatRow>();

            AddStatRows(rows, StatsSourcePlanner.LocalLabel, _statsManager.LoadHistory());

            var sources = StatsSourcePlanner.Plan(Config.Rdp, RdpSessionService.PayloadSlotDirs());
            foreach (var (label, history) in StatsSourcePlanner.Collect(sources))
            {
                AddStatRows(rows, label, history);
            }

            // 同一个脚本可能在不同通道 / 本机都跑过：先按脚本名排，再按来源排，
            // 这样同名脚本的几行挨在一起，方便横向比各账户的耗时。
            rows.Sort((a, b) =>
            {
                var byName = string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase);
                return byName != 0
                    ? byName
                    : string.Compare(a.Source, b.Source, StringComparison.CurrentCultureIgnoreCase);
            });

            foreach (var row in rows)
            {
                StatRows.Add(row);
            }
        }
        catch (Exception ex)
        {
            AppendLog($"读取统计失败：{ex.Message}");
        }

        OnPropertyChanged(nameof(StatsEmptyVisibility));
    }

    private static void AddStatRows(
        List<StatRow> rows, string source, IReadOnlyDictionary<string, RuntimeHistoryItem> history)
    {
        foreach (var item in history)
        {
            rows.Add(new StatRow
            {
                Source = source,
                Name = item.Key,
                Count = item.Value.Count,
                AverageText = FormatHelper.FormatSeconds((long)item.Value.AverageSeconds),
                LastText = FormatHelper.FormatSeconds(item.Value.LastSeconds),
                LastTime = item.Value.LastTime,
            });
        }
    }

    public void OpenDirectory(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppendLog($"无法打开目录：{ex.Message}");
        }
    }

    public void Navigate(string target) => RequestNavigation?.Invoke(this, target);

    private void Post(Action action)
    {
        if (_dispatcher.HasThreadAccess)
        {
            action();
            return;
        }

        _dispatcher.TryEnqueue(() => action());
    }

    /// <summary>
    /// <see cref="Post"/> 的可等待版本：把一段代码**保证**跑在 UI 线程上并等它做完。
    ///
    /// 什么时候必须用它：<b>动 <c>ObservableCollection</c>（Tasks / ChannelScopes / ScopeTasks）</b>。
    /// 主页有 <c>x:Bind VM.Tasks.Count</c>，x:Bind 生成的 setter 是
    /// <c>TextBlock.set_Text</c> —— WinRT 对象<b>只能在它所属的 UI 线程上碰</b>，
    /// 从别的线程调用直接抛 <c>COMException 0x8001010E (RPC_E_WRONG_THREAD)</c>。
    ///
    /// 为什么会跑到别的线程去：<c>async</c> 方法在 <c>await</c> 一个<b>不捕获
    /// SynchronizationContext</b> 的 awaitable（Windows App SDK 1.8 的
    /// <c>FileOpenPicker.PickSingleFileAsync</c> 就是这种）之后，续体不一定回到 UI 线程。
    /// 于是「await 选完文件 → 直接 Tasks.Clear()」这一段就可能在工作线程上跑，
    /// 集合一清就把绑定的 TextBlock 戳炸。
    ///
    /// 触发链路（现场栈，2026-10-04）：
    /// <c>ImportConfigAsync → Tasks.Clear() → ClearItems()
    /// → HomePage.g.cs Update_VM_Tasks_Count → TextBlock.set_Text → 0x8001010E</c>
    /// </summary>
    private Task RunOnUiThreadAsync(Action action)
    {
        if (_dispatcher.HasThreadAccess)
        {
            action();
            return Task.CompletedTask;
        }

        var tcs = new TaskCompletionSource();
        if (!_dispatcher.TryEnqueue(() =>
            {
                try
                {
                    action();
                    tcs.SetResult();
                }
                catch (Exception ex)
                {
                    // 异常必须在这里接住并搬回调用方，否则 tcs 永远挂着 = 死等。
                    tcs.SetException(ex);
                }
            }))
        {
            // 队列已关闭（窗口正在关）：直接失败，别 await 一个永远不会完成的 Task。
            tcs.SetException(new InvalidOperationException("UI 线程队列已关闭，无法继续导入配置。"));
        }

        return tcs.Task;
    }

    /// <summary>
    /// 在 UI 线程上**同步**执行 <paramref name="action"/>，并把异常搬回调用线程。
    ///
    /// 🔑 与 <see cref="RunOnUiThreadAsync"/> 的分工（别混用）：
    /// <list type="bullet">
    /// <item>需要「改完再按结果继续」→ 用本方法（有状态机或多步判断的动作，
    /// 异步化会让判断与赋值在两个线程上交错）。</item>
    /// <item>只是「把值刷到界面」，晚一帧无所谓 → 用 <c>TryEnqueue</c> 投递即可
    /// （如 <see cref="MirrorSessionIfActive"/>），绝不阻塞。</item>
    /// </list>
    ///
    /// ⚠️ 绝不能在 UI 线程上同步等一个还没排到队里的任务（自己等自己 = 死锁），
    /// 所以本方法只在**非 UI 线程**时才会阻塞；已在 UI 线程就直接执行。
    ///
    /// ⚠️ 等待<b>必须有超时</b>：本方法只防住了「UI 线程等自己」这种死锁，
    /// 防不住「UI 线程因为别的原因卡住」——后者正是 <c>UiHangWatchdog</c> 要抓的假死。
    /// UI 线程一旦卡住，排进队列的 action 永远得不到执行，无限期的 <c>Wait()</c> 会让调用线程
    /// 跟着一起挂住，而且<b>不留任何日志</b>：进程活着、crash.log 干净、看门狗只报 UI 线程卡死，
    /// 查不出还有一批后台线程被同一场卡死拖住。
    /// 超时后如实记一条日志再返回 —— 调用方都是「错过了无所谓」的界面刷新操作，
    /// 丢一次远比挂死一个后台线程划算。
    /// </summary>
    private void RunOnUiThreadSync(Action action)
    {
        if (_dispatcher.HasThreadAccess)
        {
            action();
            return;
        }

        using var done = new ManualResetEventSlim(false);
        Exception? failure = null;

        if (!_dispatcher.TryEnqueue(() =>
            {
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    failure = ex;
                }
                finally
                {
                    // 必须在 try/finally 里放行：action 抛异常时也要唤醒等待方，否则调用线程死等。
                    done.Set();
                }
            }))
        {
            throw new InvalidOperationException("UI 线程队列已关闭，无法继续更新界面状态。");
        }

        if (!done.Wait(UiThreadSyncTimeout))
        {
            // UI 线程没在超时内把活干完（多半是它卡住了）。这里绝不再 throw：
            // 调用方（如界面刷新）拿这个结果只是刷个界面，抛异常反而会打断业务流程。
            // 记一条日志，让「后台线程被 UI 卡死拖住」这件事在日志里留痕、可查。
            AppendLog("[UI 线程] 同步等待界面操作超时（" + UiThreadSyncTimeout.TotalSeconds
                + "s 未完成）—— UI 线程可能已卡死，本次界面更新已放弃。");
            return;
        }

        if (failure is not null)
        {
            throw failure;
        }
    }

    // ==================================================================
    // RDP 跨用户会话：在本机另一个 Windows 账户的会话里执行任务队列
    // ==================================================================

    private DispatcherQueueTimer? _rdpPollTimer;
    private bool _isRdpBusy;
    private string _rdpStatusText = "未连接";
    private string _rdpPhase = "idle";
    private string _rdpCurrentTask = "－";
    private int _rdpProgress;
    private int _rdpTotal;
    private int _rdpElapsedSeconds;
    private string _rdpHeartbeatText = "－";
    private bool _rdpIsStale;

    /// <summary>
    /// 本轮执行的全部通道会话（含「本地执行」组）。进度 / 指令归属 / 事件序号 / 完成收尾
    /// 都按通道独立一份 —— 原来散在这里的那套单通道字段（<c>_rdpCurrentCommandId</c>、
    /// <c>_lastRdpEventSeq</c>、<c>_rdpCompletionReported</c> …）已整体下沉进
    /// <see cref="RdpChannelSession"/>（Services/RdpChannelSession.cs）。
    ///
    /// 多通道并行时这些状态必须每通道一份：留在 VM 里就只能是全局单例，
    /// 两条通道会互相覆盖 commandId 与事件序号，完成判定也会串台。
    /// </summary>
    private readonly List<RdpChannelSession> _sessions = new();

    /// <summary>界面上正在展示的那个通道（进度卡片 / 事件列表跟着它走）。</summary>
    private RdpChannelSession? _activeSession;

    /// <summary>无活跃通道时的空事件列表（保证绑定属性不返回 null）。</summary>
    private readonly ObservableCollection<RdpTaskEvent> _emptyEvents = new();

    /// <summary>无活跃通道时的空实时日志列表（保证绑定属性不返回 null）。</summary>
    private readonly ObservableCollection<RdpLiveLog> _emptyExternalLogs = new();

    /// <summary>本轮还剩几块没跑完：本地执行算一块，每条远程通道各算一块（归零即整轮结束）。</summary>
    private int _pendingRuns;

    /// <summary>
    /// 本轮各块（本地执行 / 每条通道）的收尾结果，**整轮结束**时才拿来出汇总。
    ///
    /// 为什么要攒着：多通道并行时各块收尾时间不同。若每块收尾就直接落终态，
    /// A 通道跑完而 B 还在跑时汇总会提前"休息" —— 与事实不符。取最差见 <see cref="RunSummary"/>。
    /// </summary>
    private readonly List<RunPartResult> _runParts = new();

    /// <summary>
    /// 本轮是否被停止或出现异常 —— 决定整轮结束时是否放行「完成后自动退出」。
    /// 多通道下"一块结束"不等于"整轮结束"，所以不能沿用本地 runner 的单次结果。
    /// </summary>
    private bool _runStoppedOrError;

    /// <summary>
    /// 本轮执行的墙钟起点 / 终点（<c>default</c> = 还没跑过）。
    /// 多块并行时"耗时"必须取整个轮次 —— 单块的耗时代表不了另外几条通道。
    /// </summary>
    private DateTimeOffset _runStartedAt;
    private DateTimeOffset? _runEndedAt;

    /// <summary>上一轮是否有块异常收尾（多块结束时状态面板要显示"已完成（有异常）"）。</summary>
    private bool _lastRunAbnormal;

    /// <summary>上一轮是否有块被用户手动停止（终态显示"已停止"，不算异常）。</summary>
    private bool _lastRunStopped;

    /// <summary>
    /// 上一轮的异常原因明细（一行一条，带块标签）。状态栏显示第一条、悬浮提示与运行日志显示全部。
    ///
    /// 为什么不从各块现场取：整轮结束后各通道会被重置成 idle，
    /// 现场数据会被 1Hz 心跳覆盖 —— 收尾那一刻定下来存着，界面随时可取。
    /// </summary>
    private readonly List<string> _lastRunAbnormalReasons = new();

    /// <summary>
    /// 本轮本地执行组的异常原因（由 <c>TaskCompleted</c> 逐条攒，本地组收尾时并入整轮）。
    /// 之前这里只留了一个计数 <c>_runTaskErrorCount</c>，原因在收尾时就丢了。
    /// </summary>
    private readonly List<string> _localAbnormalReasons = new();

    /// <summary>
    /// 设置页单通道入口用的伪通道 id。配置里的通道 id 是 8 位十六进制，
    /// 双下划线开头不会与它们相撞。
    /// </summary>
    private const string SettingsChannelId = "__settings__";

    private string _rdpReadinessText = "尚未检查";
    private string _rdpSessionSummary = "－";
    private string _rdpAgentStatusText = "－";

    private RelayCommand? _deployAgentCommand;
    private RelayCommand? _removeAgentCommand;
    private AsyncRelayCommand? _connectAndRunCommand;
    private RelayCommand? _disconnectSessionCommand;
    private RelayCommand? _logoffSessionCommand;
    private RelayCommand? _refreshRdpCommand;
    private RelayCommand? _openBridgeFolderCommand;

    public RdpConfig RdpSettings => Config.Rdp;

    // ---------------- 会话通道（多通道并行） ----------------

    /// <summary>
    /// 本轮会话集合发生变化 —— 主窗口据此重建左侧菜单里的通道项。
    ///
    /// 用事件而不是直接绑 <see cref="Sessions"/>：菜单项要"插到指定位置 + 带分组标题 + 结束后保留"，
    /// 由窗口统一重建比双向同步可靠得多（也避开 NavigationView 选中态与集合变更的竞态）。
    /// </summary>
    public event EventHandler? SessionsChanged;

    /// <summary>
    /// 「左侧菜单里该列哪些通道」的输入变了 —— 主窗口据此重建「会话通道」下的子项。
    ///
    /// <para>刻意与 <see cref="SessionsChanged"/> 分开：那个说的是"本轮会话集合变了"，
    /// 只挂在它上面时，"关掉一条没在跑的通道"这种纯配置变更会悄悄漏过刷新
    /// （2026-10-30 实测：在任务执行页把通道开关关掉，左下角「会话通道」里那一条还在）。
    /// 这里只发「需要重算菜单内容」的信号，具体列谁仍由窗口按「配置 + 在跑会话」现算，
    /// 不做第二份数据源。</para>
    /// </summary>
    public event EventHandler? ChannelMenuChanged;

    private void RaiseChannelMenuChanged() => ChannelMenuChanged?.Invoke(this, EventArgs.Empty);

    // ================= 任务编排：按执行通道（docs/tasks-by-channel-plan.md） =================

    /// <summary>
    /// 左侧「执行通道」列表：本地执行（恒第一项）→ 各会话通道（配置顺序）→ 未知通道（孤儿）。
    /// 由 <see cref="TaskScopePlanner.BuildScopes"/> 从「任务 + 通道配置」现算，不做第二份数据源。
    /// </summary>
    public ObservableCollection<ChannelScope> ChannelScopes { get; } = new();

    private ChannelScope? _selectedScope;

    /// <summary>当前正在编排的通道。</summary>
    public ChannelScope? SelectedScope
    {
        get => _selectedScope;
        set
        {
            if (SetProperty(ref _selectedScope, value))
            {
                OnPropertyChanged(nameof(HasScopeSelection));
                OnPropertyChanged(nameof(ScopeOwnerText));
                OnPropertyChanged(nameof(CanAddTaskToScope));
                RefreshScopeTasks();
            }
        }
    }

    public bool HasScopeSelection => SelectedScope is not null;

    /// <summary>详情卡顶部那行"所属：X"。x:Bind 走不了 null 属性链，所以在 VM 里算好。</summary>
    public string ScopeOwnerText => SelectedScope is null ? "－" : SelectedScope.Name;

    public bool CanAddTaskToScope => SelectedScope is { CanAddTask: true };

    /// <summary>
    /// 当前通道的任务 —— 是 <see cref="Tasks"/> 的**派生视图**。
    /// 写操作一律走 <see cref="TaskScopePlanner"/>，改完调 <see cref="RefreshScopeTasks"/> 重算。
    /// </summary>
    public ObservableCollection<TaskConfig> ScopeTasks { get; } = new();

    /// <summary>通道增删改 / 任务增删 / 导入配置之后都要调一次。</summary>
    public void RefreshChannelScopes()
    {
        RefreshTaskChannelDisplay();

        var keepId = _selectedScope?.Id;

        ChannelScopes.Clear();
        foreach (var scope in TaskScopePlanner.BuildScopes(Tasks, Config.Rdp.Channels, RdpSettings.Enabled))
        {
            ChannelScopes.Add(scope);
        }

        // 选中项按 id 复原：集合被整体重建后旧引用已失效，直接赋旧引用会选不上
        _selectedScope = ChannelScopes.FirstOrDefault(s => string.Equals(s.Id, keepId, StringComparison.Ordinal))
                         ?? ChannelScopes.FirstOrDefault();

        OnPropertyChanged(nameof(SelectedScope));
        OnPropertyChanged(nameof(HasScopeSelection));
        OnPropertyChanged(nameof(ScopeOwnerText));
        OnPropertyChanged(nameof(CanAddTaskToScope));
        RefreshScopeTasks();
    }

    /// <summary>防止「改 <c>scope.Enabled</c> → 触发 Toggled → 又回到这里」的自激。</summary>
    private bool _syncingScopeToggle;

    /// <summary>
    /// 任务执行页左侧每条通道行上的开关：确认这一条通道本轮要不要执行。
    ///
    /// 写的是通道自己的 <c>enabled</c>（和「通道管理」页里那个「启用该通道」是**同一份配置**，
    /// 所以两处开关永远同步）。执行规划 <see cref="RdpChannelPlanner.Plan"/> 本来就会跳过
    /// 停用通道，因此执行链路一行都不用改。
    ///
    /// ⚠️ 刻意**不调** <see cref="RefreshChannelScopes"/>：那会整体重建 <c>ChannelScopes</c>，
    /// 而这里正处在 ToggleSwitch 的 <c>Toggled</c> 里 —— 控件还绑着刚被换掉的旧对象，
    /// 正是本项目反复出现的「集合整体替换 × 绑定源失效」崩法。改成就地刷这一行。
    /// </summary>
    public void SetScopeEnabled(ChannelScope? scope, bool enabled)
    {
        if (_syncingScopeToggle || scope is null || !scope.CanToggle)
        {
            return;
        }

        _syncingScopeToggle = true;
        try
        {
            var channel = Config.Rdp.Channels.FirstOrDefault(
                c => string.Equals(c.Id, scope.Id, StringComparison.OrdinalIgnoreCase));

            if (channel is null)
            {
                // 通道已在别处被删：把开关退回「不是用户刚拨的那个值」，别留一个假状态
                scope.Enabled = !enabled;
                return;
            }

            // 列表项实体化时 x:Bind 会先填一次 IsOn，也会走到这里；值没变就空转，别落盘
            if (channel.Enabled == enabled)
            {
                return;
            }

            channel.Enabled = enabled;
            SaveConfig();

            // 就地刷新（顺序无妨，都是各自通知各自的绑定）
            scope.Enabled = channel.Enabled;
            scope.Subtitle = channel.ListSubtitle;
            scope.IsUsable = TaskScopePlanner.IsScopeUsable(channel, RdpSettings.Enabled);
            scope.HintText = TaskScopePlanner.HintFor(channel, RdpSettings.Enabled);

            AppendLog($"[会话通道] 通道「{channel.DisplayName}」已{(enabled ? "启用" : "停用")}"
                      + "，改动在下一次「开始执行」生效。");

            // 左侧「会话通道」子项只列启用中的通道 → 开关一拨就得重算。
            // 事件在 finally 之前发，但窗口那边自己会延后一拍重建，不会落在 Toggled 栈里。
            RaiseChannelMenuChanged();
        }
        finally
        {
            _syncingScopeToggle = false;
        }
    }

    /// <summary>按当前通道重算任务视图 + 通道内序号，并刷新各通道的任务数。</summary>
    public void RefreshScopeTasks()
    {
        ScopeTasks.Clear();

        var scope = _selectedScope;
        if (scope is not null)
        {
            var list = TaskScopePlanner.TasksForScope(Tasks, scope.Id);
            TaskScopePlanner.RenumberScope(list);
            foreach (var task in list)
            {
                ScopeTasks.Add(task);
            }
        }

        foreach (var item in ChannelScopes)
        {
            item.TaskCount = TaskScopePlanner.CountForScope(Tasks, item.Id);
        }

        OnPropertyChanged(nameof(ScopeTasks));
    }

    /// <summary>
    /// 反查 <see cref="TaskConfig.ChannelDisplay"/>：任务只存 channel_id，
    /// 给人看的名字得从通道配置里查（含"指向已删除通道"的情形）。
    /// </summary>
    private void RefreshTaskChannelDisplay()
    {
        var byId = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var channel in Config.Rdp.Channels)
        {
            byId[channel.Id] = channel.DisplayName;
        }

        foreach (var task in Tasks)
        {
            var id = TaskScopePlanner.NormalizeId(task.ChannelId);
            task.ChannelDisplay = id.Length == 0
                ? "本地执行"
                : byId.TryGetValue(id, out var name) ? name : $"未知通道（{id}）";
        }
    }

    private void RaiseSessionsChanged()
    {
        OnPropertyChanged(nameof(Sessions));
        OnPropertyChanged(nameof(HasRemoteSession));
        SessionsChanged?.Invoke(this, EventArgs.Empty);
    }

    // ---------------- 通道管理（ChannelsPage，M4 / 计划书 §8.3） ----------------

    /// <summary>
    /// 通道列表的界面镜像。
    ///
    /// 为什么不让界面直接绑 <c>Config.Rdp.Channels</c>：那边是 <see cref="List{T}"/>，
    /// 增删不会通知界面；这里包一层 <see cref="ObservableCollection{T}"/>，
    /// 由 <see cref="ReloadChannels"/> 单向同步 —— **配置始终是唯一事实来源**，镜像只读不反写。
    /// </summary>
    public ObservableCollection<RdpChannel> Channels { get; } = new();

    private RdpChannel? _selectedChannel;
    private string _channelReadinessText = string.Empty;
    private string _channelSessionSummary = string.Empty;
    private string _channelAgentStatusText = string.Empty;
    private string _channelCredentialText = string.Empty;
    private string _channelGrantText = string.Empty;

    /// <summary>
    /// 本机账户授权按「机器 + 账户」串行（计划书 §6）：同一账户被多条通道复用时，
    /// 后到的那次等前一次做完，然后查到"已是管理员"就不再重复弹框/记账。
    /// 单机单用户场景，一把信号量足够，不必按账户分桶。
    /// </summary>
    private readonly SemaphoreSlim _grantGate = new(1, 1);

    /// <summary>正在授权的通道 id。连点两次「保存凭据」时不重复弹框、不重复记账。</summary>
    private readonly HashSet<string> _grantInFlight = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>各通道最近一次的管理员授权结果（按通道 id 存，切通道不互相覆盖）。</summary>
    private readonly Dictionary<string, string> _channelGrantTexts = new(StringComparer.OrdinalIgnoreCase);

    private RelayCommand? _addChannelCommand;
    private RelayCommand? _duplicateChannelCommand;
    private AsyncRelayCommand? _removeChannelCommand;
    private RelayCommand? _refreshChannelCommand;
    private RelayCommand? _clearChannelCredentialCommand;
    private RelayCommand? _deployChannelAgentCommand;
    private RelayCommand? _openChannelBridgeFolderCommand;

    /// <summary>当前正在编辑的通道（通道编辑卡的绑定源）。为 null 时卡片整体不可用。</summary>
    public RdpChannel? SelectedChannel
    {
        get => _selectedChannel;
        set
        {
            if (!SetProperty(ref _selectedChannel, value))
            {
                return;
            }

            OnPropertyChanged(nameof(HasSelectedChannel));
            OnPropertyChanged(nameof(ChannelResolutionKey));
            OnPropertyChanged(nameof(ChannelBridgeDir));
            RefreshChannelReadiness();
        }
    }

    public bool HasSelectedChannel => _selectedChannel is not null;

    /// <summary>该通道的预检结论（环境层面能不能连）。</summary>
    public string ChannelReadinessText
    {
        get => _channelReadinessText;
        private set => SetProperty(ref _channelReadinessText, value);
    }

    public string ChannelSessionSummary
    {
        get => _channelSessionSummary;
        private set => SetProperty(ref _channelSessionSummary, value);
    }


    /// <summary>该通道的代理状态（「已部署/在线」两件事分开说，与全局页口径一致）。</summary>
    public string ChannelAgentStatusText
    {
        get => _channelAgentStatusText;
        private set => SetProperty(ref _channelAgentStatusText, value);
    }

    /// <summary>该通道的凭据是否已存（文案版，供列表与编辑卡显示）。</summary>
    public string ChannelCredentialText
    {
        get => _channelCredentialText;
        private set => SetProperty(ref _channelCredentialText, value);
    }

    /// <summary>
    /// 该通道最近一次的本机管理员授权结果。与「凭据」是两件事：
    /// 凭据保存成功不代表授权成功，反之亦然（计划书 §4.1 第 7 条）。
    /// </summary>
    public string ChannelGrantText
    {
        get => _channelGrantText;
        private set => SetProperty(ref _channelGrantText, value);
    }

    /// <summary>该通道的分辨率档位（按通道存，不再共用一个全局档位）。</summary>
    public string ChannelResolutionKey
    {
        get => _selectedChannel is null
            ? RdpResolutions.ToValue(0, 0)
            : RdpResolutions.ToValue(_selectedChannel.DesktopWidth, _selectedChannel.DesktopHeight);
        set
        {
            if (_selectedChannel is null)
            {
                return;
            }

            if (string.Equals(ChannelResolutionKey, value, StringComparison.Ordinal))
            {
                return;
            }

            var (width, height) = RdpResolutions.Parse(value);
            _selectedChannel.DesktopWidth = width;
            _selectedChannel.DesktopHeight = height;
            OnPropertyChanged(nameof(ChannelResolutionKey));
            OnPropertyChanged(nameof(ChannelResolutionLabel));
            SaveConfig();
        }
    }

    public string ChannelResolutionLabel => _selectedChannel is null
        ? "（未选择通道）"
        : RdpResolutions.Label(_selectedChannel.DesktopWidth, _selectedChannel.DesktopHeight);

    /// <summary>该通道的指令桥目录（留空 bridge_path 时按账户名派生，与目标会话里的代理算出来的一致）。</summary>
    public string ChannelBridgeDir => _selectedChannel is null
        ? string.Empty
        : RdpChannelPaths.Resolve(_selectedChannel.BridgePath, _selectedChannel.User);

    /// <summary>
    /// 把配置里的通道同步进界面镜像，并尽量保住当前选中的那条。
    ///
    /// <paramref name="refreshReadiness"/> 默认 true；<c>Initialize()</c> 里传 false ——
    /// 预检要跑 tasklist / powershell / reg，启动路径上没必要为"用户还没打开的页面"付这笔钱，
    /// 页面 <c>OnLoaded</c> 会自己刷新一次。
    /// </summary>
    public void ReloadChannels(bool refreshReadiness = true)
    {
        var keepId = _selectedChannel?.Id;

        Channels.Clear();
        foreach (var channel in Config.Rdp.Channels)
        {
            Channels.Add(channel);
        }

        // 选中项按 id 复原：集合被清空重建后原引用已失效，直接用旧引用会让编辑卡显示空白
        _selectedChannel = Channels.FirstOrDefault(c => c.Id == keepId) ?? Channels.FirstOrDefault();

        OnPropertyChanged(nameof(SelectedChannel));
        OnPropertyChanged(nameof(HasSelectedChannel));
        OnPropertyChanged(nameof(ChannelResolutionKey));
        OnPropertyChanged(nameof(ChannelResolutionLabel));
        OnPropertyChanged(nameof(ChannelBridgeDir));
        OnPropertyChanged(nameof(Channels));

        // 通道列表是 List，增删不产生集合通知 —— 重挂一次订阅，让新通道的字段改动也能自动保存
        HookRdpForAutoSave();

        RefreshChannelScopes();

        if (refreshReadiness)
        {
            RefreshChannelReadiness();
        }
    }

    /// <summary>
    /// 选中通道的预检。与全局 <see cref="RefreshRdpReadiness"/> 的区别：参数全部来自该通道，
    /// 结果的文案分开存，避免两条通道互相覆盖。
    /// </summary>
    public void RefreshChannelReadiness()
    {
        var channel = _selectedChannel;
        if (channel is null)
        {
            ChannelReadinessText = "还没有通道 —— 点「新建通道」加一条。";
            ChannelSessionSummary = "－";
            ChannelAgentStatusText = "－";
            ChannelCredentialText = "－";
            ChannelGrantText = "－";
            OnPropertyChanged(nameof(ChannelBridgeDir));
            return;
        }

        // 桥目录可能刚被改过，检查前确保它存在（目录按目标账户派生）
        RdpBridge.For(RdpChannelPaths.Resolve(channel.BridgePath, channel.User)).EnsureDirectory();

        var readiness = RdpSessionService.CheckReadiness(
            channel.Host, channel.User, channel.CredentialSaved, channel.BridgePath);

        var notes = new List<string>();
        if (readiness.EditionNote.Length > 0)
        {
            notes.Add(readiness.EditionNote);
        }

        notes.AddRange(readiness.Notes);

        if (readiness.IsLocal)
        {
            var session = RdpSessionService.FindSession(channel.User);
            ChannelSessionSummary = session is null
                ? "目标账户当前没有会话"
                : $"会话 {session.Id} · {session.UserName} · {session.StateText}";
        }
        else
        {
            ChannelSessionSummary = $"远程主机 {RdpTargets.Normalize(channel.Host)}：会话状态无法在本机枚举";
        }

        ChannelReadinessText = readiness.Problems.Count == 0
            ? (notes.Count == 0 ? "环境就绪，可以连接。" : $"环境就绪，可以连接。（{string.Join("；", notes)}）")
            : string.Join("；", readiness.Problems);

        ChannelAgentStatusText = DescribeAgentStatus(readiness);
        ChannelCredentialText = readiness.CredentialSaved ? "已保存" : "未保存";
        ChannelGrantText = _channelGrantTexts.TryGetValue(channel.Id, out var grantText)
            ? grantText
            : "－";

        channel.CredentialSaved = readiness.CredentialSaved;
        OnPropertyChanged(nameof(ChannelResolutionKey));
        OnPropertyChanged(nameof(ChannelResolutionLabel));
        OnPropertyChanged(nameof(ChannelBridgeDir));
    }

    public ICommand AddChannelCommand => _addChannelCommand ??= new RelayCommand(AddChannel);

    public ICommand DuplicateChannelCommand => _duplicateChannelCommand ??= new RelayCommand(DuplicateChannel);

    /// <summary>
    /// 删除通道。<c>CommandParameter</c> 是 <see cref="RdpChannel"/>，不传则删选中的那条。
    /// 里面有任务时会先弹框问归属（计划书 D5），所以是异步命令。
    /// </summary>
    public ICommand RemoveChannelCommand => _removeChannelCommand ??= new AsyncRelayCommand(RemoveChannelAsync);

    public ICommand RefreshChannelCommand => _refreshChannelCommand ??= new RelayCommand(RefreshChannelReadiness);

    public ICommand ClearChannelCredentialCommand =>
        _clearChannelCredentialCommand ??= new RelayCommand(ClearChannelCredential);

    public ICommand DeployChannelAgentCommand =>
        _deployChannelAgentCommand ??= new RelayCommand(DeployChannelAgent);

    public ICommand OpenChannelBridgeFolderCommand =>
        _openChannelBridgeFolderCommand ??= new RelayCommand(OpenChannelBridgeFolder);

    private void AddChannel()
    {
        var channel = new RdpChannel
        {
            Id = RdpChannel.NewId(),
            Name = $"通道 {Config.Rdp.Channels.Count + 1}",
            // 新通道继承全局的默认值（约定见计划书 §4.1：旧字段是"新建通道时的默认值"）。
            // 分辨率例外：全局字段还是「自适应」(0) 时不要继承 —— 那会让新通道的桌面尺寸
            // 跟着画面区域变，同一套任务换台显示器就全错位，所以回落到固定的 1920 × 1080。
            Host = RdpSettings.TargetHost,
            BridgePath = RdpSettings.BridgePath,
            // 收尾不再继承全局值：它已经归任务的收尾行所有（见《任务类型与收尾计划书》§3）
            DesktopWidth = RdpSettings.DesktopWidth > 0
                ? RdpSettings.DesktopWidth
                : RdpResolutions.DefaultWidth,
            DesktopHeight = RdpSettings.DesktopHeight > 0
                ? RdpSettings.DesktopHeight
                : RdpResolutions.DefaultHeight,
        };
        channel.Sanitize(Config.Rdp.Channels.Count + 1);

        Config.Rdp.Channels.Add(channel);
        SaveConfig();
        ReloadChannels();
        SelectedChannel = Channels.FirstOrDefault(c => c.Id == channel.Id);
        AppendLog($"[会话通道] 新建通道「{channel.DisplayName}」（{channel.Host} / 待填账户）");
    }

    private void DuplicateChannel(object? parameter)
    {
        var source = parameter as RdpChannel ?? _selectedChannel;
        if (source is null)
        {
            return;
        }

        var copy = source.Clone();
        copy.Id = RdpChannel.NewId();
        copy.Name = $"{source.DisplayName} 副本";
        // 副本默认清掉凭据标记：同名账户换个主机就用不上了，让用户自己重存一次更稳
        copy.CredentialSaved = false;

        Config.Rdp.Channels.Add(copy);
        SaveConfig();
        ReloadChannels();
        SelectedChannel = Channels.FirstOrDefault(c => c.Id == copy.Id);
        AppendLog($"[会话通道] 复制出通道「{copy.DisplayName}」，记得补账户与凭据。");
    }

    private async Task RemoveChannelAsync(object? parameter)
    {
        var channel = parameter as RdpChannel ?? _selectedChannel;
        if (channel is null)
        {
            return;
        }

        if (IsRunning || IsRdpBusy)
        {
            await DialogHelper.ShowMessageAsync("提示", "正在执行任务，先停止执行再改通道。");
            return;
        }

        var affected = TaskScopePlanner.TasksForScope(Tasks, channel.Id);

        // 收尾任务随通道一起消失：它表达的是"这条通道跑完做什么"，改派到别的通道会让目标
        // 通道多出第二条收尾（破坏"每通道恰好一条"）。先摘掉，再问其余任务的归属。
        var removedFinish = affected.Where(t => t is not null && TaskTypeResolver.IsFinish(t)).ToList();
        foreach (var finish in removedFinish)
        {
            Tasks.Remove(finish);
        }

        if (removedFinish.Count > 0)
        {
            affected = affected.Where(t => t is not null && !TaskTypeResolver.IsFinish(t)).ToList();
        }

        // 有任务指向它就先问清归属 —— 删完不管，那些任务会变成"未知通道"被静默跳过，
        // 而且在任务页里还找不到它们（计划书 D5/D6）。
        var reassignTo = TaskScopePlanner.LocalScopeId;
        if (affected.Count > 0)
        {
            var picked = await PromptReassignAsync(channel, affected);
            if (picked is null)
            {
                return;   // 取消删除
            }

            reassignTo = picked;

            if (!string.Equals(picked, TaskScopePlanner.KeepOrphan, StringComparison.Ordinal))
            {
                foreach (var task in affected)
                {
                    task.ChannelId = TaskScopePlanner.NormalizeId(picked);
                }

                // 目标 scope 原本可能是空的（没有收尾行）—— 现在它有任务了，补上那条
                TaskScopePlanner.EnsureFinishTask(Tasks, reassignTo, Config.Rdp.Channels);
                TaskScopePlanner.NormalizeOrdersByChannel(Tasks, Config.Rdp.Channels);
            }
        }

        Config.Rdp.Channels.Remove(channel);
        SaveConfig();
        ReloadChannels();

        var tail = affected.Count == 0
            ? string.Empty
            : string.Equals(reassignTo, TaskScopePlanner.KeepOrphan, StringComparison.Ordinal)
                ? $"，{affected.Count} 个任务仍指向它（会显示为「未知通道」，本轮不执行）"
                : $"，{affected.Count} 个任务已改派到「{DescribeScope(reassignTo)}」";
        AppendLog($"[会话通道] 已删除通道「{channel.DisplayName}」{tail}。");
    }

    /// <summary>把 scope id 说成人话（日志用）。</summary>
    private string DescribeScope(string scopeId)
    {
        if (TaskScopePlanner.NormalizeId(scopeId).Length == 0)
        {
            return "本地执行";
        }

        var scope = ChannelScopes.FirstOrDefault(s => string.Equals(s.Id, scopeId, StringComparison.OrdinalIgnoreCase));
        return scope?.Name ?? scopeId;
    }

    /// <summary>
    /// 删除通道时问"里面的任务交给谁"。
    /// 返回目标 scope id（空串 = 本地执行）或 <see cref="TaskScopePlanner.KeepOrphan"/>；**null = 取消删除**。
    /// </summary>
    private async Task<string?> PromptReassignAsync(RdpChannel channel, IReadOnlyList<TaskConfig> affected)
    {
        if (App.MainWindow?.Content.XamlRoot is null)
        {
            // 没有窗口（例如冒烟/无人值守）时按推荐默认值走：移回本地执行
            return TaskScopePlanner.LocalScopeId;
        }

        var names = string.Join("、", affected.Take(5).Select(t => t.DisplayName));
        if (affected.Count > 5)
        {
            names += $" 等 {affected.Count} 个";
        }

        var box = new ComboBox
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            MinWidth = 280,
            DisplayMemberPath = "Value",
            SelectedValuePath = "Key",
        };

        box.Items.Add(new KeyValuePair<string, string>(TaskScopePlanner.LocalScopeId, "本地执行（当前会话）"));

        foreach (var scope in ChannelScopes)
        {
            // 排除正要删的那条，以及"未知通道"组（改派过去等于没解决）
            if (scope.IsMissing || string.Equals(scope.Id, channel.Id, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            box.Items.Add(new KeyValuePair<string, string>(scope.Id, scope.Name));
        }

        box.Items.Add(new KeyValuePair<string, string>(TaskScopePlanner.KeepOrphan, "保持未知（这些任务本轮不执行）"));
        box.SelectedIndex = 0;

        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(new TextBlock
        {
            Text = $"通道「{channel.DisplayName}」下有 {affected.Count} 个任务：{names}",
            TextWrapping = TextWrapping.Wrap,
        });
        panel.Children.Add(new TextBlock
        {
            Text = "删掉通道后，这些任务交给谁？",
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.7,
        });
        panel.Children.Add(box);

        var dialog = new ContentDialog
        {
            Title = "删除通道",
            Content = panel,
            PrimaryButtonText = "确定",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = App.MainWindow.Content.XamlRoot,
        };

        var result = await DialogHelper.ShowAsync(dialog);
        return result == ContentDialogResult.Primary
            ? box.SelectedValue as string ?? TaskScopePlanner.LocalScopeId
            : null;
    }

    /// <summary>保存选中通道的凭据到 Windows 凭据管理器（按 host|user 一条，通道之间互不覆盖）。</summary>
    public void SaveChannelCredential(string password)
    {
        var channel = _selectedChannel;
        if (channel is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(channel.User))
        {
            _ = DialogHelper.ShowMessageAsync("提示", "请先填写该通道的目标账户名。");
            return;
        }

        if (password.Length == 0)
        {
            _ = DialogHelper.ShowMessageAsync("提示", "请填写该账户的登录密码。");
            return;
        }

        if (!RdpSessionService.SaveCredential(channel.Host, channel.User, password, out var message))
        {
            _ = DialogHelper.ShowMessageAsync("保存失败", message);
            return;
        }

        if (RdpSessionService.TryLoadPassword(channel.Host, channel.User) is null)
        {
            AppendLog($"[会话通道] 警告：「{channel.DisplayName}」的凭据写入后无法读回，远程桌面可能无法自动登录。");
        }

        channel.CredentialSaved = true;

        // 正在编辑的就是"当前设置"那条时，同步回全局字段：单通道快入口仍然读 RdpSettings
        if (string.Equals(channel.Id, SettingsChannelId, StringComparison.Ordinal))
        {
            RdpSessionService.PendingPassword = password;
        }

        SaveConfig();
        AppendLog($"[会话通道] 已保存「{channel.DisplayName}」的凭据（{RdpTargets.Normalize(channel.Host)}|{channel.User}，配置文件不保存密码）。");
        RefreshChannelReadiness();

        // 保存账户 = 明确提交动作：本机通道顺手完成组授权（计划书 §8）。
        // 字段失焦 / 导入配置 / 复制通道都走不到这里，那些路径不修改系统权限。
        StartAccountAuthorization(channel);
    }

    /// <summary>
    /// 保存凭据之后的本机账户授权。fire-and-forget：失败只记日志/状态，不打断保存流程。
    /// </summary>
    /// <remarks>
    /// <para>统一管理员策略（2026-10-26 增量计划书 §4、§5.3）：不再读系统版本、不再看
    /// Remote Desktop Users 组，先查「是不是已经管理员」，是就什么都不做，
    /// 不是才弹一次统一确认。</para>
    /// <para>计划书 §6：同一账户被多条通道复用时按「机器 + 账户」串行 —— 用一把信号量排队，
    /// 后到的那次会在前一次完成后查到"已是管理员"，于是不再重复弹框、也不重复记账。</para>
    /// </remarks>
    private void StartAccountAuthorization(RdpChannel channel)
    {
        // 账户名必须在**此刻**取快照：用户可能在授权对话框弹着的时候又去改了通道
        var channelId = channel.Id;
        var account = channel.User;
        var displayName = channel.DisplayName;
        var host = channel.Host;

        _ = AuthorizeLocalAccountAsync(channelId, host, account, displayName);
    }

    private async Task AuthorizeLocalAccountAsync(string channelId, string host, string account, string displayName)
    {
        try
        {
            if (!Config.Rdp.Enabled)
            {
                AppendLog("[账户授权] 未开启「在远程桌面会话中执行」，跳过本机账户授权。");
                return;
            }

            if (!RdpTargets.IsLocal(host))
            {
                // 远端机器上的同名账户与本机无关：不碰本机账户，也不把它算成"已授权"
                var remoteNote = "已保存远程通道；请在目标计算机设置管理员账户，未修改主控机账户。";
                SetChannelGrantText(channelId, remoteNote);
                AppendLog($"[账户授权] 「{displayName}」是远程通道（{RdpTargets.Normalize(host)}），{remoteNote}");
                return;
            }

            // 串行化：同一账户的多条通道、以及连点两次保存，都排队过这一处
            await _grantGate.WaitAsync();

            try
            {
                if (!_grantInFlight.Add(channelId))
                {
                    // 同一通道已有一轮在跑（多半正停在确认框上）：不重复弹框
                    AppendLog($"[账户授权] 「{displayName}」已有一轮授权在进行，本次跳过。");
                    return;
                }

                try
                {
                    SetChannelGrantText(channelId, "正在检查该账户是否已是本机管理员…");

                    var precheck = await Task.Run(() => LocalGroupMembershipService.Inspect(account));

                    if (precheck.Failure.Length > 0)
                    {
                        SetChannelGrantText(channelId, precheck.Failure);
                        AppendLog($"[账户授权] 「{displayName}」未授权：{precheck.Failure}{DescribeEditionSuffix()}");
                        return;
                    }

                    if (precheck.AlreadyAdministrator)
                    {
                        const string text = "账户已是本机管理员，无需修改。";
                        SetChannelGrantText(channelId, text);
                        AppendLog($"[账户授权] 「{displayName}」{text}（{precheck.AccountSid}）");
                        return;
                    }

                    // 到这里一定是「账户存在 + 确认不是管理员」：统一弹一次确认
                    var confirmed = await ShowGrantDialogOnUiThreadAsync(account);

                    if (confirmed != ContentDialogResult.Primary)
                    {
                        SetChannelGrantText(channelId, "凭据已保存；管理员授权已取消。");
                        AppendLog("[账户授权] 用户取消，未改动任何组成员；凭据仍然保留。");
                        return;
                    }

                    // 确认期间用户可能又改了通道：目标已经不是刚才确认的那个账户/机器，就别授了
                    if (!IsStillConfirmedTarget(channelId, account))
                    {
                        const string changed = "通道账户在确认期间被修改，本次未做任何授权。";
                        SetChannelGrantText(channelId, changed);
                        AppendLog($"[账户授权] 「{displayName}」{changed}（确认对象：{account}）");
                        return;
                    }

                    SetChannelGrantText(channelId, "正在授予管理员权限…");

                    var result = await Task.Run(() =>
                        LocalGroupMembershipService.AuthorizeAsAdministrator(account, precheck.AccountSid));

                    // 结果文字是绑定属性：一律收口到 UI 线程（await Task.Run 的续体不保证回来）
                    SetChannelGrantText(channelId, DescribeGrantResult(result));
                    AppendLog($"[账户授权] 「{displayName}」{result.Message}"
                              + (result.Detail.Length > 0 ? $"　{result.Detail}" : "")
                              + (result.RecordSaved ? "（已记入 grants.jsonl）" : "（授权记录写入失败）"));
                }
                finally
                {
                    _grantInFlight.Remove(channelId);
                }
            }
            finally
            {
                _grantGate.Release();
            }
        }
        catch (Exception ex)
        {
            // fire-and-forget：异常绝不能放出去，否则就是未观察异常
            SetChannelGrantText(channelId, "管理员授权过程出错，详见运行日志。");
            AppendLog($"[账户授权] 异常：{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>统一的管理员授权确认框。家庭版与非家庭版使用同一份内容，不再解释 SKU。</summary>
    private Task<ContentDialogResult> ShowAdministratorGrantDialog(string account)
    {
        var qualified = RdpGrantPlanner.QualifiedName(Environment.MachineName, account);

        return DialogHelper.ShowMessageAsync(
            "设为本机管理员",
            $"将把此计算机上的账户「{qualified}」设为管理员。该账户将获得本机管理员权限，"
            + "可管理系统设置和其他用户。原有组成员关系保持不变。是否继续？"
            + Environment.NewLine + Environment.NewLine
            + "取消仅取消管理员授权；已经成功保存的凭据会保留。",
            closeText: "取消",
            primaryText: "确认设为管理员");
    }

    /// <summary>
    /// 确认框关掉之后复核一次「要授权的对象还是刚才那个」。
    /// </summary>
    /// <remarks>
    /// 用户在确认框弹着的这段时间里完全可能切到别的通道、改掉账户名或把主机改成远程地址。
    /// 那时按旧快照继续授权，就是把管理员身份授给了"用户没确认过"的对象 ——
    /// 计划书 §4.1 第 6 条要求重新核对 SID 与通道目标，这就是那一步。
    /// </remarks>
    private bool IsStillConfirmedTarget(string channelId, string account)
    {
        var channel = Config.Rdp.Channels.FirstOrDefault(
            c => string.Equals(c.Id, channelId, StringComparison.Ordinal));

        if (channel is null)
        {
            return false;   // 通道在确认期间被删掉了
        }

        if (!RdpTargets.IsLocal(channel.Host))
        {
            return false;
        }

        return string.Equals(
            RdpGrantPlanner.BareAccountName(channel.User),
            RdpGrantPlanner.BareAccountName(account),
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 在 UI 线程上开确认框并等结果。
    /// </summary>
    /// <remarks>
    /// 为什么要多这一层：<c>ContentDialog</c> 必须在 UI 线程上创建与显示，
    /// 而本方法的上游是 <c>await Task.Run(...)</c>，其续体<b>不保证</b>回到 UI 线程。
    /// 在别的线程上 new ContentDialog 会拿到 <c>0x8001010E</c>，而 <see cref="DialogHelper"/>
    /// 会把它吞成「取消」—— 表现就是"点了保存、什么都没发生、也没授权"，查起来极费劲。
    /// </remarks>
    private async Task<ContentDialogResult> ShowGrantDialogOnUiThreadAsync(string account)
    {
        if (_dispatcher.HasThreadAccess)
        {
            return await ShowAdministratorGrantDialog(account);
        }

        var tcs = new TaskCompletionSource<ContentDialogResult>();
        if (!_dispatcher.TryEnqueue(() => ShowOnUiThreadAsync()))
        {
            // 队列已关闭（窗口正在关）：按取消处理，别让调用方死等
            return ContentDialogResult.None;
        }

        return await tcs.Task;

        async void ShowOnUiThreadAsync()
        {
            try
            {
                tcs.SetResult(await ShowAdministratorGrantDialog(account));
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        }
    }

    /// <summary>把授权结果翻成一句用户能直接读的话（计划书 §4.2 的六种结果）。</summary>
    private static string DescribeGrantResult(RdpGrantResult result)
    {
        var text = result.Outcome switch
        {
            RdpGrantOutcome.AlreadyMember => "账户已是本机管理员，无需修改。",
            RdpGrantOutcome.AddedAdministrators =>
                "已将账户设为本机管理员。新权限需要该账户注销并重新登录后才生效（已有会话不会被自动断开）。",
            RdpGrantOutcome.Unconfirmed => "管理员授权结果未确认，请重新检查。",
            RdpGrantOutcome.Failed => $"凭据保存状态：已保存；管理员授权失败：{result.Message}",
            RdpGrantOutcome.Refused => $"凭据保存状态：已保存；管理员授权未执行：{result.Message}",
            RdpGrantOutcome.SkippedRemote =>
                "已保存远程通道；请在目标计算机设置管理员账户，未修改主控机账户。",
            _ => result.Message,
        };

        if (!result.RecordSaved && result.WasAddedThisRound)
        {
            text += "（管理员已授予，但授权记录保存失败）";
        }

        return text;
    }

    /// <summary>日志里附带的系统版本信息。**只作诊断**，不参与任何授权判断。</summary>
    private static string DescribeEditionSuffix()
    {
        try
        {
            var (edition, editionId, productName) = LocalGroupMembershipService.DetectEdition();
            return $"（诊断：{RdpEditions.Label(edition)}，EditionID={editionId}，ProductName={productName}）";
        }
        catch
        {
            return "";
        }
    }

    /// <summary>
    /// 把授权结果写到该通道的状态里。绑定属性只能在 UI 线程上改，
    /// 而本方法会被后台续体调用（<c>await Task.Run</c> 之后不保证回到 UI 线程）。
    /// </summary>
    private void SetChannelGrantText(string channelId, string text)
    {
        RunOnUiThreadSync(() =>
        {
            _channelGrantTexts[channelId] = text;

            if (string.Equals(_selectedChannel?.Id, channelId, StringComparison.Ordinal))
            {
                ChannelGrantText = text;
            }
        });
    }

    public void ClearChannelCredential()
    {
        var channel = _selectedChannel;
        if (channel is null)
        {
            return;
        }

        RdpSessionService.DeleteCredential(channel.Host, channel.User);
        channel.CredentialSaved = false;
        SaveConfig();
        AppendLog($"[会话通道] 已删除「{channel.DisplayName}」的凭据。");
        RefreshChannelReadiness();
    }

    /// <summary>
    /// 部署会话代理（公共启动目录快捷方式）。代理是**全机器共用一份**的，
    /// 所以部署动作本身与通道无关，但"目标账户"要取自当前通道 ——
    /// 部署前要读它那路桥判断代理是不是正在跑任务（跑着就不能动）。
    /// </summary>
    private void DeployChannelAgent()
    {
        var channel = _selectedChannel;

        IsRdpBusy = true;
        RdpStatusText = "正在部署会话代理（首次会复制程序文件，稍等）…";
        AppendLog("[会话通道] 正在部署会话代理：把程序复制到所有用户都能访问的公共目录…");

        var bridge = channel?.BridgePath ?? RdpSettings.BridgePath;
        var targetUser = channel?.User ?? RdpSettings.TargetUser;

        Task.Run(() =>
        {
            var ok = RdpSessionService.DeployAgent(out var message, bridge, targetUser);
            return (Ok: ok, Message: message);
        }).ContinueWith(task =>
        {
            var (ok, message) = task.Result;
            _dispatcher.TryEnqueue(() =>
            {
                if (ok)
                {
                    AppendLog($"[会话通道] 会话代理：{Flatten(message)}");
                    RdpStatusText = ReportAgentDeployed(message)
                        ? "会话代理已部署（目标账户需注销重连才生效）"
                        : "会话代理已部署";
                }
                else
                {
                    // 失败原因可能是文件被占用 / PowerShell 报错 / 真的缺管理员权限。
                    // 不替用户下"就是缺权限"的结论 —— message 里已经带了真实报错与判断依据。
                    AppendLog($"[会话通道] 会话代理部署失败：{Flatten(message)}");
                    RdpStatusText = "会话代理部署失败";
                    _ = DialogHelper.ShowMessageAsync("部署失败", message);
                }

                IsRdpBusy = false;
                RefreshChannelReadiness();
                RefreshRdpReadiness();
            });
        });
    }

    private void OpenChannelBridgeFolder()
    {
        var channel = _selectedChannel;
        var dir = channel is null
            ? RdpChannelPaths.Resolve(RdpSettings.BridgePath, RdpSettings.TargetUser)
            : RdpChannelPaths.Resolve(channel.BridgePath, channel.User);

        var bridge = RdpBridge.For(dir);
        bridge.EnsureDirectory();
        bridge.OpenBridgeFolder();
    }

    /// <summary>
    /// 轻量落盘：把编辑框里的改动写进 config.json 并刷新任务页下拉与列表副标题。
    ///
    /// 输入框失焦时会调它 —— 通道名/主机/账户这类文本没有"选中即保存"的时机，
    /// 只靠一个「保存」按钮的话，用户填完直接切页面就丢了。
    /// 刻意不刷新预检（那要跑 tasklist / powershell / reg，失焦太频繁了扛不住）。
    /// </summary>
    public void PersistChannelEdits()
    {
        if (_selectedChannel is not null)
        {
            _selectedChannel.Sanitize(Config.Rdp.Channels.IndexOf(_selectedChannel) + 1);
        }

        SaveConfig();
        RefreshChannelScopes();
        OnPropertyChanged(nameof(Channels));
    }

    /// <summary>完整保存：轻量落盘 + 重跑该通道预检 + 记一条日志（对应界面上的「保存通道设置」按钮）。</summary>
    public void SaveChannelEdits()
    {
        PersistChannelEdits();
        RefreshChannelReadiness();

        var name = _selectedChannel?.DisplayName ?? "（未选择）";
        AppendLog($"[会话通道] 通道配置已保存（当前：{name}）。");
    }

    /// <summary>
    /// 下拉框用的分辨率档位。「会话处理」那组选项也不在这里 ——
    /// 收尾已迁到任务级，选项由 <see cref="PostTaskActionItems"/> 提供给任务的收尾行；
    /// 通道页那个下拉框已经删掉了（见《任务类型与收尾计划书》§6.6）。
    /// </summary>
    public IReadOnlyList<KeyValuePair<string, string>> ResolutionItems => RdpResolutions.Items;

    /// <summary>下拉框用的分辨率档位；选中「自适应」时写 0 回配置。</summary>
    public string RdpResolutionKey
    {
        get => RdpResolutions.ToValue(RdpSettings.DesktopWidth, RdpSettings.DesktopHeight);
        set
        {
            if (string.Equals(RdpResolutionKey, value, StringComparison.Ordinal))
            {
                return;
            }

            var (width, height) = RdpResolutions.Parse(value);
            RdpSettings.DesktopWidth = width;
            RdpSettings.DesktopHeight = height;

            OnPropertyChanged();
            OnPropertyChanged(nameof(RdpResolutionLabel));

            AppendLog(width > 0 && height > 0
                ? $"远程桌面分辨率已设为 {width} × {height}（下次连接时生效）。"
                : "远程桌面分辨率已设为自动（按系统默认窗口尺寸）。");
        }
    }

    /// <summary>界面上显示的分辨率文案。</summary>
    public string RdpResolutionLabel => RdpResolutions.Label(RdpSettings.DesktopWidth, RdpSettings.DesktopHeight);

    /// <summary>内嵌画面回报连接状态（连上 / 登录完成 / 断开）时同步到进度区与日志。</summary>
    public void ReportRdpStatus(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        RdpStatusText = text;
        AppendLog($"[远程桌面] {text}");

    }

    /// <summary>
    /// 只为**某条通道**建立连接（不下发任何任务）—— 通道页上「连接到这条通道」按钮走这里。
    ///
    /// 内嵌模式：画面挂进该通道页的「内嵌画面」区；
    /// 独立窗口模式：画面在系统自带的 mstsc 窗口里，这里退化为常规连接入口。
    /// </summary>
    public void ConnectChannelSurface(string channelId) => _ = ConnectChannelSurfaceGuardedAsync(channelId);

    /// <summary>
    /// <see cref="ConnectChannelSurface"/> 的前置检查版本。
    ///
    /// <para>已经连着（只是复用）时<b>不弹</b>：没有新建会话的风险，问了只是打扰。
    /// 批量入口（多画面页的「连接全部通道」）由页面自己先问一次，再循环调
    /// <see cref="ConnectChannelSurfaceNow"/> —— 否则 N 条通道会弹 N 次。</para>
    /// </summary>
    private async Task ConnectChannelSurfaceGuardedAsync(string channelId)
    {
        if (IsRdpBusy)
        {
            return;
        }

        if (IsEmbeddedMode && HasSurface(channelId))
        {
            ConnectChannelSurfaceNow(channelId);
            return;
        }

        if (!await ConfirmSingleSessionBeforeConnectAsync($"通道「{channelId}」"))
        {
            return;
        }

        ConnectChannelSurfaceNow(channelId);
    }

    /// <summary>真正的连接动作（<b>不做</b>单会话策略检查，调用方负责先确认）。</summary>
    public void ConnectChannelSurfaceNow(string channelId)
    {
        var session = _sessions.FirstOrDefault(s =>
            s.IsRemote && string.Equals(s.ChannelId, channelId, StringComparison.OrdinalIgnoreCase));

        // 还没跑过任务时 _sessions 是空的 —— 回退到配置里找通道，
        // 这样通道页上点「连接到这条通道」在开跑之前也能用。
        var channel = session?.Channel
            ?? Config.Rdp.Channels.FirstOrDefault(c =>
                string.Equals(c.Id, channelId, StringComparison.OrdinalIgnoreCase));

        if (channel is null)
        {
            AppendLog("找不到要连接的会话通道（可能已被删除）。");
            return;
        }

        // 会话通道的主入口（「连接通道」「连接全部」都走这里）。
        // 独立窗口模式会在下面再进 ConnectSurfaceNow，那里也会通知一次 ——
        // 调度器按状态机合并，重复通知只会重置 18 秒，不会叠加执行。
        NotifyUuFixRelevantAction("连接通道");

        if (!IsEmbeddedMode)
        {
            // 独立窗口模式的画面不在应用里，走常规连接入口即可。
            // 用 Now 版：单会话策略已经在外面问过了，别再弹第二次。
            ConnectSurfaceNow();
            return;
        }

        // M6：内嵌画面按通道分家 —— 每条通道各有各的 client，可以同时连、同时显示。
        if (HasSurface(channelId))
        {
            AppendSurfaceLog(channelId, "内嵌画面已连接，直接复用。");
            return;
        }

        if (TryStartEmbeddedConnect(
                channelId,
                channel.Host,
                channel.User,
                channel.DesktopWidth,
                channel.DesktopHeight,
                out var message))
        {
            AppendSurfaceLog(channelId, "正在建立内嵌画面连接…");
            return;
        }

        AppendSurfaceLog(channelId, $"内嵌连接失败：{message}");
        _ = DialogHelper.ShowMessageAsync("连接失败", message);
    }

    /// <summary>
    /// 连接前的统一闸门：确认「同一账户只保留一条会话」（fSingleSessionPerUser=1）。
    ///
    /// <para>为什么连接前要问这一句：这个值是 0 时，每连一次都会新建一条会话、
    /// 旧会话留成「已断开」，同一账户越攒越多 —— 任务派发也就没法确定该落到哪一条
    /// （真机现场：RDP Wrapper 安装时把策略键里的这一项写成了 0）。
    /// 值已经是 1 就什么都不问，直接连。</para>
    ///
    /// <para>只问一次、由用户决定：选「坚持连接」就照原样连，不改系统。</para>
    /// </summary>
    /// <param name="scope">弹窗里说明"是哪一次连接"用（通道名 / 批量 / 设置页）。</param>
    /// <returns>true = 继续连接（永远为 true，保留返回值便于将来加入"取消"）。</returns>
    public async Task<bool> ConfirmSingleSessionBeforeConnectAsync(string scope)
    {
        var state = RdpSingleSessionPolicy.Read();

        if (state.Proven)
        {
            return true;
        }

        if (state.Status == SingleSessionPolicyStatus.ReadFailed)
        {
            // 读不到就不拦：既不能证明有问题，也不该凭空打断用户
            AppendLog($"[连接前检查] 读不到 {RdpSingleSessionPolicy.ValueName}（{state.Error}），本次不做提示。");
            return true;
        }

        var current = state.Effective is int value ? value.ToString() : "未设置";
        var result = await DialogHelper.ShowMessageAsync(
            "同一账户可能被开出多条会话",
            $"{scope} 连接前检查到：{RdpSingleSessionPolicy.ValueName} 的生效值为 {current}"
            + $"（{state.Detail}）。\n\n"
            + "这一项控制「同一账户是否只保留一条远程桌面会话」。为 0 / 未设置时，"
            + "每次连接都会新建一条会话，旧会话留在「已断开」，于是同一个账户会越攒越多会话，"
            + "任务也就无法确定该交给哪一条。\n\n"
            + "建议改为 1（同一账户只保留一条会话）。改动会记录到 "
            + $"{RdpSingleSessionPolicy.LedgerPath}；若该值由域策略下发，会在策略刷新时被改回。",
            closeText: "坚持连接",
            primaryText: "修改为 1 并连接");

        if (result != Microsoft.UI.Xaml.Controls.ContentDialogResult.Primary)
        {
            AppendLog($"[连接前检查] 生效值 {current}，用户选择坚持连接（未修改策略）。");
            return true;
        }

        var change = RdpSingleSessionPolicy.Enable();
        AppendLog($"[连接前检查] {change.Message}");

        if (change.Ok)
        {
            // 注册表值对"新建会话"生效；万一服务端把它缓存在内存里，重启 TermService 才会读到新值。
            // 这里只提示，不自动重启 —— 重启会打断所有远程会话。
            AppendLog("[连接前检查] 该值对之后的连接生效；若这次仍然新建了会话，"
                      + "请重启「远程桌面服务（TermService）」后再试一次。");
        }
        else
        {
            AppendLog("[连接前检查] ⚠ 修改没有成功，仍按「坚持连接」继续；这条连接可能又会新建一条会话。");
        }

        return true;
    }

    /// <summary>「连接目标账户」按钮是否可用。</summary>
    public bool CanConnectSurface => !IsRdpBusy;

    /// <summary>
    /// 只建立远程桌面会话（独立 mstsc 窗口），不下发任何任务。
    /// 目标账户在本机已经登录着的话直接复用，不再新建连接。
    /// </summary>
    public void ConnectSurface() => _ = ConnectSurfaceGuardedAsync();

    /// <summary>
    /// <see cref="ConnectSurface"/> 的前置检查版本：先确认单会话策略，再走原流程。
    /// 所有界面入口都走这里，避免某一条路漏掉检查。
    /// </summary>
    private async Task ConnectSurfaceGuardedAsync()
    {
        if (IsRdpBusy)
        {
            return;
        }

        if (!await ConfirmSingleSessionBeforeConnectAsync("「连接目标账户」"))
        {
            return;
        }

        ConnectSurfaceNow();
    }

    /// <summary>真正的连接动作（<b>不做</b>单会话策略检查，调用方负责先确认）。</summary>
    public void ConnectSurfaceNow()
    {
        if (IsRdpBusy)
        {
            return;
        }

        var host = RdpTargets.Normalize(RdpSettings.TargetHost);
        var isLocal = RdpTargets.IsLocal(host);

        if (isLocal && !RdpSessionService.CheckReadiness(
                host, RdpSettings.TargetUser, RdpSettings.CredentialSaved, RdpSettings.BridgePath).UserExists)
        {
            _ = DialogHelper.ShowMessageAsync("无法连接",
                $"本机不存在账户「{RdpSettings.TargetUser}」。\n请先在 Windows 里创建该账户，再回到这里连接。");
            return;
        }

        NotifyUuFixRelevantAction("连接通道");

        IsRdpBusy = true;
        RdpStatusText = "正在连接目标会话…";

        try
        {
            // 内嵌模式：不管会话是否已存在都直接连 —— FreeRDP 重连会接回同一会话，
            // 画面嵌进Yomori窗口。mstsc 的「避免重复连接接管桌面」短路在这里没有意义：
            // 不连上就看不到画面。
            if (IsEmbeddedMode)
            {
                // 已有内嵌连接：直接复用（页面切换回来时点「连接预览」走到这里，不重复连）
                if (HasSurface(string.Empty))
                {
                    RdpStatusText = "内嵌画面已连接（复用现有连接）";
                    AppendLog("[内嵌] 已有内嵌连接，直接复用。");
                    RefreshRdpReadiness();
                    return;
                }

                RdpStatusText = "内嵌连接中…";
                var (embedWidth, embedHeight) = RdpResolutionSize();
                // 空 channelId = 设置页这条不属于任何会话通道的入口
                if (TryStartEmbeddedConnect(string.Empty, host, RdpSettings.TargetUser, embedWidth, embedHeight, out var embedMessage))
                {
                    AppendLog("内嵌模式下画面显示在下方「内嵌画面」卡片里，不需要打开独立远程桌面窗口。");
                    RefreshRdpReadiness();
                    return;
                }
                RdpStatusText = "连接失败";
                AppendLog($"[内嵌] {embedMessage}");
                _ = DialogHelper.ShowMessageAsync("连接失败", embedMessage);
                return;
            }

            // 已经登录着就不用再连了 —— 客户端版再连一次只会把那个桌面接管过来，
            // 当前桌面反而被锁定。这里直接当成"已连接"处理。
            if (isLocal && RdpSessionService.TryGetReusableSession(RdpSettings.TargetUser, out var existing))
            {
                var label = $"账户「{RdpSettings.TargetUser}」已经登录（会话 {existing!.Id}，{existing.StateText}）";
                RdpStatusText = $"{label}，已复用现有会话";
                AppendLog($"[远程桌面] {label}，本次不再新建连接。");
                AppendLog("[远程桌面] Windows 客户端版同时只允许一个交互式会话，重复连接会顶掉你正在使用的那个桌面。");
                RefreshRdpReadiness();
                return;
            }

            var (width, height) = RdpResolutionSize();
            var started = RdpSessionService.Connect(host, out var message, width, height);
            if (!started)
            {
                RdpStatusText = "连接失败";
                AppendLog($"[远程桌面] 连接失败：{message}");
                _ = DialogHelper.ShowMessageAsync("连接失败", message);
                return;
            }

            AppendLog($"[远程桌面] {message}");
            AppendLog("已打开远程桌面窗口。可以先在那里确认登录成功，再回到这里点「开始执行」下发任务。");
            RefreshRdpReadiness();
        }
        finally
        {
            IsRdpBusy = false;
        }
    }

    /// <summary>断开目标会话（任务与收尾设置不受影响）。</summary>
    public void DisconnectSurface()
    {
        // 内嵌模式：断开 = ycn_rdp_disconnect（目标会话保留，语义 = mstsc「断开连接」）
        if (IsEmbeddedMode)
        {
            if (!HasAnySurface)
            {
                _ = DialogHelper.ShowMessageAsync("提示", "当前没有内嵌连接。");
                return;
            }

            RdpStatusText = "正在断开内嵌连接…";

            // 逐条断开：先打上「主动断开」抑制位，免得自动重连又把连接接回来
            foreach (var key in _embeds.Where(kv => kv.Value.Client is not null).Select(kv => kv.Key).ToList())
            {
                GetOrCreateConnection(key).UserDisconnect = true;
                DisposeSurface(key);
            }

            RdpStatusText = "内嵌连接已断开（目标会话保留）";
            RefreshRdpReadiness();
            return;
        }

        if (!RdpTargets.IsLocal(RdpSettings.TargetHost))
        {
            _ = DialogHelper.ShowMessageAsync("提示",
                $"目标是远程主机 {RdpTargets.Normalize(RdpSettings.TargetHost)}，断开操作只能在目标机器上执行（或用任务结束后的「断开会话」收尾方式）。");
            return;
        }

        var session = RdpSessionService.FindSession(RdpSettings.TargetUser);
        if (session is null)
        {
            _ = DialogHelper.ShowMessageAsync("提示", "找不到目标账户的会话，可能尚未连接或已注销。");
            return;
        }

        RdpSessionService.DisconnectSession(session.Id, out var message);
        RdpStatusText = "已请求断开目标会话";
        AppendLog($"[远程桌面] {message}");
        RefreshRdpReadiness();
    }

    /// <summary>重开一个远程桌面窗口，让新的分辨率档位生效。</summary>
    public void ResizeSurface()
    {
        var (width, height) = RdpResolutionSize();
        if (width <= 0 || height <= 0)
        {
            AppendLog("[远程桌面] 当前是自动分辨率，窗口尺寸由系统默认值决定。想固定尺寸请在下拉框里选一个档位。");
            return;
        }

        AppendLog($"[远程桌面] 分辨率档位为 {width} × {height}，下次点「连接目标账户」时生效。");
        AppendLog("[远程桌面] 远程桌面客户端不支持在已连接的窗口上动态改尺寸，需要断开后重新连接。");
    }

    /// <summary>取配置里的分辨率；0 × 0 表示交给系统默认。</summary>
    private (int Width, int Height) RdpResolutionSize()
    {
        var width = RdpResolutions.Normalize(
            RdpSettings.DesktopWidth, RdpResolutions.MinWidth, RdpResolutions.MaxWidth);
        var height = RdpResolutions.Normalize(
            RdpSettings.DesktopHeight, RdpResolutions.MinHeight, RdpResolutions.MaxHeight);
        return (width, height);
    }

    // ---- M5：内嵌连接通道（client_mode = embedded，计划书 §5.6）----
    //
    // M6 起改成「每通道一份」：一个 RdpEmbeddedClient 同一时刻只能挂一个渲染视图，
    // 所以"多路画面同时可见"（网格 / 弹出窗口）的前提是每条通道各有各的连接实例。
    // Key = 通道 id；空串 "" = 设置页那个不属于任何通道的入口。

    private readonly Dictionary<string, EmbedConnection> _embeds = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>最近建立 / 操作过的那条（供「当前画面归属」一类旧属性查询）。</summary>
    private string _embedActiveKey = string.Empty;

    /// <summary>
    /// 一条内嵌画面的全部运行时状态。
    /// 之所以要打包成类：M6 之前这些是 VM 上的一组单例字段，
    /// 多通道并行时会互相覆盖（重试计数、看门狗、主动断开标志都会串台）。
    /// </summary>
    private sealed class EmbedConnection
    {
        /// <summary>通道 id；空串 = 设置页入口。</summary>
        public required string Key { get; init; }

        public RdpEmbeddedClient? Client { get; set; }
        public Task Teardown { get; set; } = Task.CompletedTask;
        public int ConnectGeneration { get; set; }

        // 上次连接用的参数 —— 自动重连必须按原样重来：
        // 换了账户会登错人，换了分辨率会让会话尺寸跳变。
        public string LastHost { get; set; } = string.Empty;
        public string User { get; set; } = string.Empty;
        public int Width { get; set; }
        public int Height { get; set; }

        public int RetryCount { get; set; }          // 黑屏自愈重试次数
        public int AutoReconnects { get; set; }      // 意外掉线自动重连次数
        public bool UserDisconnect { get; set; }     // 用户/流程主动断开（抑制自动重连）
        public DispatcherQueueTimer? Watchdog { get; set; }

        /// <summary>
        /// 画面是否已被「弹出独立窗口」占用。
        /// 占用的通道，页面内格子只显示提示、不挂 client —— 两个渲染器挂同一 client 会打架。
        /// </summary>
        public bool PoppedOut { get; set; }

        /// <summary>
        /// 用户在这格画面上点了「锁定」：鼠标穿透 + 键盘映射都说停就停。
        ///
        /// 与「键盘焦点」不是一回事 —— 焦点只决定按键去处，锁定是彻底不再往远端送输入。
        /// 每通道独立（多画面时锁一格不影响别格），且**跨重连保留**：
        /// 自动重连后回到锁前状态，不会因为掉线重连把锁悄悄解开。
        /// 只放在运行时（EmbedConnection）里、不写 config.json —— 这是「防误触」的临时闸，
        /// 重启应用即复位为未锁。
        /// </summary>
        public bool InputLocked { get; set; }

        /// <summary>用户在这格画面上点了「静音」：本机不放远端声音（每通道独立，运行时不落盘）。</summary>
        public bool LocalMuted { get; set; }

        // 事件处理器引用（闭包捕获本对象）——解绑时必须用同一批委托实例
        public EventHandler<(int Session, uint Width, uint Height)>? OnConnected;
        public EventHandler<RdpFrameEventArgs>? OnFrameArrived;
        public EventHandler<(int Session, int Reason, string Detail)>? OnDisconnected;
    }

    /// <summary>最近建立 / 操作的内嵌画面所属通道 id；当前无任何内嵌连接时为空串。</summary>
    public string ActiveEmbedChannelId => _embeds.Count == 0 ? string.Empty : _embedActiveKey;

    /// <summary>当前内嵌画面所属通道的名字（给提示文案与日志用）。</summary>
    public string ActiveEmbedChannelName => SurfaceDisplayName(_embedActiveKey);

    private string SurfaceDisplayName(string key)
    {
        if (key.Length == 0)
        {
            return "当前设置";
        }

        return _sessions.FirstOrDefault(s => s.IsRemote
            && string.Equals(s.ChannelId, key, StringComparison.OrdinalIgnoreCase))?.DisplayName ?? key;
    }

    /// <summary>取某条通道的内嵌连接实例（通道页画面挂载用）；无则 null。</summary>
    public RdpEmbeddedClient? GetSurfaceClient(string? channelId)
        => _embeds.TryGetValue(channelId ?? string.Empty, out var conn) ? conn.Client : null;

    /// <summary>这条通道当前有没有内嵌画面。</summary>
    public bool HasSurface(string? channelId) => GetSurfaceClient(channelId) is not null;

    /// <summary>当前有内嵌画面的全部通道 id（可能含设置页入口的空串）。</summary>
    public IReadOnlyList<string> SurfaceChannelIds =>
        _embeds.Where(kv => kv.Value.Client is not null).Select(kv => kv.Key).ToList();

    /// <summary>当前有没有任何内嵌画面。</summary>
    public bool HasAnySurface => _embeds.Values.Any(c => c.Client is not null);

    /// <summary>这条通道的画面是否已被「弹出独立窗口」占用。</summary>
    public bool IsSurfacePoppedOut(string? channelId)
        => _embeds.TryGetValue(channelId ?? string.Empty, out var conn) && conn.PoppedOut;

    /// <summary>
    /// 这条通道的内嵌画面是否处于「锁定」状态（鼠标穿透 + 键盘映射都说停就停）。
    /// 没有内嵌连接时恒为 false —— 没画面就没得锁。
    /// </summary>
    public bool IsSurfaceInputLocked(string? channelId)
        => (_embeds.TryGetValue(channelId ?? string.Empty, out var conn) && conn.InputLocked)
           || SurfaceChannelPreference(channelId)?.InputLocked == true;

    /// <summary>这条通道的内嵌画面是否被本机静音（远端仍在出声，只是本机不放）。</summary>
    public bool IsSurfaceMuted(string? channelId)
        => (_embeds.TryGetValue(channelId ?? string.Empty, out var conn) && conn.LocalMuted)
           || SurfaceChannelPreference(channelId)?.LocalMuted == true;

    /// <summary>
    /// 设置某条通道画面的「锁定」状态。返回设置后的实际状态；false 表示没有内嵌连接。
    /// 只改运行时标志 —— 锁定期间 RdpView 不转发指针/键盘，点解锁按钮仍然有效（它不走画面输入隧道）。
    /// </summary>
    public bool SetSurfaceInputLocked(string? channelId, bool locked)
    {
        var key = channelId ?? string.Empty;
        if (!_embeds.TryGetValue(key, out var conn) || conn.Client is null)
        {
            return false;
        }

        conn.InputLocked = locked;
        PersistSurfaceToggle(key, channel => channel.InputLocked = locked);
        AppendSurfaceLog(key, locked
            ? "已锁定画面输入：鼠标穿透与键盘映射都停用，点「已锁定」可解锁。"
            : "已解锁画面输入：鼠标与键盘恢复转发。");
        // 状态栏（1Hz 刷新）会跟着改，这里主动叫一次让 UI 立刻反应
        SurfaceRefreshRequested?.Invoke();
        return locked;
    }

    /// <summary>
    /// 设置某条通道画面的「静音」状态。返回设置后的实际状态；false 表示没有内嵌连接。
    /// </summary>
    public bool SetSurfaceMuted(string? channelId, bool muted)
    {
        var key = channelId ?? string.Empty;
        if (!_embeds.TryGetValue(key, out var conn) || conn.Client is null)
        {
            return false;
        }

        conn.LocalMuted = muted;
        PersistSurfaceToggle(key, channel => channel.LocalMuted = muted);
        // 原生侧按本会话静音（rdpsnd 音量置 0）：不用断线重连，立即生效
        conn.Client.SetMuted(muted);
        AppendSurfaceLog(key, muted
            ? "已静音：这条通道的远端声音不在本机播放（远端照常出声）。"
            : "已取消静音：恢复在本机播放这条通道的远端声音。");
        SurfaceRefreshRequested?.Invoke();
        return muted;
    }

    /// <summary>按通道 id 找运行时快照或配置里的通道（找不到返回 null）。</summary>
    private RdpChannel? SurfaceChannelPreference(string? channelId)
    {
        var key = channelId ?? string.Empty;
        return _sessions.FirstOrDefault(s => s.IsRemote
                && string.Equals(s.ChannelId, key, StringComparison.OrdinalIgnoreCase))?.Channel
            ?? Config.Rdp.Channels.FirstOrDefault(c =>
                string.Equals(c.Id, key, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// 把当前这套运行期配置写回对应通道并立刻落盘。
    /// 只改动目标通道本身，不走 <see cref="SaveConfig"/> 那条全量入口 —— 那条会重排 order、
    /// 重新同步开机自启项，静音/锁定这种小开关不该有这种副作用。
    /// </summary>
    private void PersistSurfaceToggle(string key, Action<RdpChannel> apply)
    {
        // 空 id = 设置页那个不属于任何通道的入口，没有可持久化的归属；
        // 它仍然在当前会话内即时生效，但不应该借道写到任意默认通道上。
        if (key.Length == 0)
        {
            return;
        }

        var channel = SurfaceChannelPreference(key);
        if (channel is null)
        {
            return;
        }

        apply(channel);

        try
        {
            _configManager.Save(Config);
        }
        catch (Exception ex)
        {
            // 开关本身已经生效，不让落盘失败回滚用户刚做的选择；只把问题写进运行日志。
            AppendSurfaceLog(key, $"保存画面开关失败：{ex.Message}");
        }
    }

    /// <summary>标记 / 撤销「画面已弹出到独立窗口」。</summary>
    public void MarkSurfacePoppedOut(string? channelId, bool poppedOut)
    {
        var key = channelId ?? string.Empty;
        if (_embeds.TryGetValue(key, out var conn))
        {
            conn.PoppedOut = poppedOut;
        }
    }

    /// <summary>返回该通道上次连接用的分辨率（弹出窗口新建画面时沿用，避免画面变形）。</summary>
    public (int Width, int Height) SurfaceLastSize(string? channelId)
    {
        var key = channelId ?? string.Empty;
        return _embeds.TryGetValue(key, out var conn) ? (conn.Width, conn.Height) : (0, 0);
    }

    /// <summary>
    /// 多画面网格要展示的通道 id。顺序稳定（配置顺序在前），避免 1Hz 刷新时格位跳来跳去：
    /// <list type="number">
    /// <item>配置里**启用中**的通道 —— 不跑任务也能进网格，格子里给「连接到这条通道」；</item>
    /// <item>本轮在跑的通道（含配置里已删 / 已停用的 —— 在跑就必须看得见）；</item>
    /// <item>还剩有活画面的通道（设置页入口的空串不进网格）。</item>
    /// </list>
    /// </summary>
    public IReadOnlyList<string> SurfaceGridChannelIds
    {
        get
        {
            var ids = new List<string>();

            void AddIfNew(string? id)
            {
                if (!string.IsNullOrWhiteSpace(id)
                    && !ids.Contains(id, StringComparer.OrdinalIgnoreCase))
                {
                    ids.Add(id);
                }
            }

            foreach (var channel in Config.Rdp.Channels.Where(c => c.Enabled))
            {
                AddIfNew(channel.Id);
            }

            foreach (var s in _sessions.Where(s => s.IsRemote))
            {
                AddIfNew(s.ChannelId);
            }

            foreach (var key in SurfaceChannelIds)
            {
                AddIfNew(key);
            }

            return ids;
        }
    }

    /// <summary>通道 id → 显示名（网格标题 / 弹窗标题用）。</summary>
    public string ChannelDisplayName(string? channelId)
    {
        var key = channelId ?? string.Empty;
        if (key.Length == 0)
        {
            return "当前设置";
        }

        return Config.Rdp.Channels
            .FirstOrDefault(c => string.Equals(c.Id, key, StringComparison.OrdinalIgnoreCase))?.DisplayName
            ?? key;
    }

    /// <summary>该通道画面当前的状态摘要（网格角标用）。</summary>
    public string SurfaceStateText(string? channelId)
    {
        var key = channelId ?? string.Empty;

        if (GetSurfaceClient(key) is null)
        {
            return "未连接";
        }

        if (IsSurfacePoppedOut(key))
        {
            return "已在独立窗口显示";
        }

        var session = _sessions.FirstOrDefault(s => s.IsRemote
            && string.Equals(s.ChannelId, key, StringComparison.OrdinalIgnoreCase));

        if (session is null)
        {
            return "已连接";
        }

        return session.IsActive
            ? (string.IsNullOrEmpty(session.CurrentTask) ? "执行中" : $"执行中 · {session.CurrentTask}")
            : session.StatusText;
    }

    /// <summary>连接方式下拉框数据源。</summary>
    public IReadOnlyList<KeyValuePair<string, string>> ClientModeItems => ClientModes.Items;

    /// <summary>内嵌画面渲染后端选项（绑到通道管理页的下拉框）。</summary>
    public IReadOnlyList<KeyValuePair<string, string>> RenderBackendItems => RenderBackends.Items;

    /// <summary>当前是否为内嵌连接模式（client_mode = embedded）。</summary>
    public bool IsEmbeddedMode => ClientModes.Normalize(RdpSettings.ClientMode) == ClientModes.Embedded;

    /// <summary>最近建立 / 操作的那条内嵌连接实例（null = 当前无任何内嵌画面）。</summary>
    public RdpEmbeddedClient? ActiveEmbedClient => GetSurfaceClient(_embedActiveKey);

    /// <summary>
    /// 内嵌连接集合发生变化（发起连接 / 释放）。可能在任意线程触发，订阅方自行调度到 UI 线程。
    /// </summary>
    public event Action? EmbedClientChanged;

    /// <summary>
    /// 请求画面格子刷新顶部条（锁定 / 静音这类开关变了）。
    /// 与 <see cref="EmbedClientChanged"/> 分开：那个是"某一格有连接 / 没连接"的结构性变化，
    /// 页面要重建网格；这个只是"开关状态变了"，格子重画一遍就行。
    /// </summary>
    public event Action? SurfaceRefreshRequested;

    /// <summary>
    /// 发起内嵌连接。同步发起、异步回调；凭据取自 Windows 凭据管理器。
    /// <para>黑屏自愈（M5 实测）：重连**同分辨率**会话时服务器不重绘桌面（RestoreRect 被无视），
    /// 画面永远黑屏。对策：请求宽按重试次数 +2（保持偶数不被服务器取整），必然与会话当前
    /// 分辨率不同 → 强制 resize → 服务器全量重绘；连接 6 秒后画面仍黑则宽再 +2 重连（最多 3 次）。</para>
    /// </summary>
    /// <param name="channelId">通道 id（空串 = 设置页入口）——M6 起连接按通道分家。</param>
    /// <param name="host">目标主机。</param>
    /// <param name="targetUser">目标账户（多通道下每条通道各用各的账户与凭据）。</param>
    /// <param name="width">期望宽度，0 = 用默认。</param>
    /// <param name="height">期望高度，0 = 用默认。</param>
    private bool TryStartEmbeddedConnect(
        string channelId, string host, string? targetUser, int width, int height, out string message)
    {
        message = string.Empty;
        var key = channelId ?? string.Empty;
        var normalized = RdpTargets.Normalize(host);

        // 按「主机 + 账户」取凭据：多通道并行时，同主机不同账户各有各的密码
        var password = RdpCredentialStore.Read(normalized, targetUser);
        if (password is null)
        {
            message = "该主机还没有保存凭据，请先在「会话通道」页点「保存凭据」。";
            return false;
        }

        // 账户名允许 "DOMAIN\user" 写法
        var user = (targetUser ?? string.Empty).Trim();
        string? domain = null;
        var backslash = user.IndexOf('\\');
        if (backslash > 0)
        {
            domain = user[..backslash];
            user = user[(backslash + 1)..];
        }

        DisposeSurface(key); // 幂等清理本通道的旧实例（不重置黑屏重试计数）

        var conn = GetOrCreateConnection(key);
        var generation = ++conn.ConnectGeneration;
        if (!conn.Teardown.IsCompleted)
        {
            conn.UserDisconnect = false;
            var teardown = conn.Teardown;
            _ = ContinueConnectAfterTeardownAsync();
            message = "等待旧内嵌连接释放后重新连接…";
            return true;

            async Task ContinueConnectAfterTeardownAsync()
            {
                try { await teardown.ConfigureAwait(false); }
                catch (Exception ex)
                {
                    AppendSurfaceLog(key, "旧连接释放失败：" + ex.Message);
                    return;
                }
                Post(() =>
                {
                    if (generation == conn.ConnectGeneration && !conn.UserDisconnect && conn.Client is null)
                    {
                        TryStartEmbeddedConnect(key, host, targetUser, width, height, out _);
                    }
                });
            }
        }
        conn.LastHost = normalized;
        conn.User = user;
        conn.Width = width;
        conn.Height = height;
        conn.UserDisconnect = false; // 主动连接：解除自动重连抑制
        // 连接实例会换新，但用户选择的静音 / 锁定属于这一条通道：
        // 从持久配置回填运行态，确保手动重连与掉线自动重连都会恢复上次选择。
        if (SurfaceChannelPreference(key) is { } preference)
        {
            conn.LocalMuted = preference.LocalMuted;
            conn.InputLocked = preference.InputLocked;
        }

        // 事件处理器闭包捕获 conn 本身 —— 多通道并存时才知道回调属于哪条通道。
        // 引用存在 conn 上，释放时用同一批委托实例解绑。
        var client = new RdpEmbeddedClient();
        conn.OnConnected = (_, e) => OnEmbedConnected(conn, e);
        conn.OnFrameArrived = (_, e) => OnEmbedFrameArrived(conn, e);
        conn.OnDisconnected = (_, e) => Post(() =>
        {
            if (ReferenceEquals(conn.Client, client)) { OnEmbedDisconnected(conn, e); }
        });
        client.Connected += conn.OnConnected;
        client.FrameArrived += conn.OnFrameArrived;
        client.Disconnected += conn.OnDisconnected;
        conn.Client = client;
        _embedActiveKey = key;
        // 静音状态在这里就下发（早于 Connect）：原生会话尚未建立，这次调用只会被记成
        // 「意图」并由 RdpEmbeddedClient 在 Connected 时补发 —— 别指望它当场生效。
        // 这样做的好处是 rdpsnd 起 DVC 之前标志就已经到位，不会在连上的一瞬间漏出一帧声音。
        if (conn.LocalMuted)
        {
            client.SetMuted(true);
        }
        EmbedClientChanged?.Invoke();

        var (address, port) = RdpSessionService.SplitHostPort(normalized);
        var started = client.Connect(new RdpConnectInfo
        {
            Host = address,
            Port = (ushort)(port > 0 ? port : 3389),
            Username = user,
            Domain = domain,
            Password = password,
            DesktopWidth = (uint)((width > 0 ? width : 1280) + 2 * conn.RetryCount),
            DesktopHeight = (uint)(height > 0 ? height : 720),
            AllowSelfsigned = true,
            EnableAudio = RdpSettings.AudioEnabled,
            UseGfx = RdpSettings.GfxEnabled,
        });
        if (!started)
        {
            AppendSurfaceLog(key, "连接发起失败（详见上方错误）。");
            DisposeSurface(key);
            message = "内嵌连接发起失败，详见运行日志。";
            return false;
        }

        AppendSurfaceLog(key, conn.RetryCount > 0
            ? $"黑屏自愈重试 #{conn.RetryCount}：请求 {normalized} @ {(width > 0 ? width : 1280) + 2 * conn.RetryCount}×{(height > 0 ? height : 720)}（宽 +{2 * conn.RetryCount} 强制会话刷新）"
            : $"正在连接 {normalized}（client_mode=embedded，音频={(RdpSettings.AudioEnabled ? "开" : "关")}）…");
        StartEmbedWatchdog(conn);
        return true;
    }

    /// <summary>
    /// 按上次的参数重连内嵌画面（黑屏自愈重试与意外掉线自动重连共用）。
    /// 必须带上原来那组主机 / 账户 / 分辨率 —— 换成默认值会登错账户，或让会话尺寸跳变。
    /// </summary>
    private void ReconnectEmbedded(EmbedConnection conn)
    {
        _ = TryStartEmbeddedConnect(conn.Key, conn.LastHost, conn.User, conn.Width, conn.Height, out _);
    }

    /// <summary>内嵌画面的日志：通道内嵌带通道前缀，设置页入口则只有 [内嵌]。</summary>
    private void AppendSurfaceLog(string key, string message)
    {
        var session = _sessions.FirstOrDefault(s => s.IsRemote
            && string.Equals(s.ChannelId, key, StringComparison.OrdinalIgnoreCase));

        if (session is not null)
        {
            AppendChannelLog(session, $"[内嵌] {message}");
        }
        else if (key.Length > 0)
        {
            AppendLog($"[通道：{ChannelDisplayName(key)}] [内嵌] {message}");
        }
        else
        {
            AppendLog($"[内嵌] {message}");
        }
    }

    /// <summary>取（或新建）某条通道的内嵌连接记录。字典条目刻意不随断开删除 —— 重试计数要跨连接保持。</summary>
    private EmbedConnection GetOrCreateConnection(string key)
    {
        if (_embeds.TryGetValue(key, out var existing))
        {
            return existing;
        }

        var created = new EmbedConnection { Key = key };
        _embeds[key] = created;
        return created;
    }

    private void OnEmbedFrameArrived(EmbedConnection conn, RdpFrameEventArgs e)
    {
        // 第一帧到达 = 画面在流；停掉本通道的黑屏看门狗（原生线程 → 编组）
        if (conn.Watchdog is not null)
        {
            Post(() => StopEmbedWatchdog(conn));
        }
    }

    /// <summary>
    /// 黑屏看门狗：连接后每 6 秒采样一帧亮度。全黑（&lt;20）→ 宽 +2 自动重连（最多 3 次）；
    /// 有内容 → 复位。会话重连服务器不重绘时的唯一自愈手段（M5 实测）。
    ///
    /// 🔑 <b>采样在后台线程做，判定在 UI 线程做</b>（2026-10-12 改）。
    ///
    /// 原来整段 Tick 都跑在 UI 线程上，其中 <c>TryCopyFrame</c> 是<b>阻塞式原生调用</b>：
    /// <c>ycn_rdp_copy_frame</c> 在原生 <c>g_lock</c> 里对 1920×1080×4 ≈ 8.3MB 做整帧 memcpy，
    /// 而 RDP 线程的 <c>ycn_end_paint</c> 持同一把锁写新帧 —— UI 线程会在这把锁上等，
    /// 表现为 CPU 零增长的静默假死（用户侧就是「突然卡死，什么都没留下」）。
    ///
    /// 拆成两段后：
    ///   · 抓帧 + 亮度采样 → <c>Task.Run</c>（工作线程，等锁不再冻界面）；
    ///   · 停表 / 重连 / 写日志 → 回 UI 线程（要碰 XAML 与 <c>AppendLog</c> 队列）。
    ///
    /// 注意 <c>TryCopyFrame</c> 返回的 <c>pixels</c> 是 client 内部的<b>复用缓冲</b>，
    /// 所以亮度采样必须在同一个 <c>Task.Run</c> 里同步做完，不能把数组带出去再用。
    /// </summary>
    private void StartEmbedWatchdog(EmbedConnection conn)
    {
        conn.Watchdog?.Stop();
        var timer = _dispatcher.CreateTimer();
        timer.Interval = TimeSpan.FromSeconds(6);
        var ticks = 0;
        var sampling = 0;
        timer.Tick += (_, _) =>
        {
            ticks++;

            var client = conn.Client;
            if (client is null)
            {
                timer.Stop();
                conn.Watchdog = null;
                return;
            }

            if (client.State != RdpClientState.Connected)
            {
                return; // 仍在连接中，下一拍再看
            }

            // At most one outstanding sample per connection, even if native grab stalls.
            if (System.Threading.Interlocked.CompareExchange(ref sampling, 1, 0) != 0) { return; }
            _ = Task.Run(() =>
            {
                // client 可能在这一拍里被换掉 / 断开：拿不到帧就当作「还没有帧」
                if (client.State != RdpClientState.Connected)
                {
                    return (Copied: false, Width: 0u, Height: 0u, Brightness: 0, Ticks: ticks);
                }

                if (!client.TryCopyFrame(out var w, out var h, out var st, out var px))
                {
                    return (Copied: false, Width: 0u, Height: 0u, Brightness: 0, Ticks: ticks);
                }

                long sum = 0;
                var samples = 0;
                // px 是可复用的抓帧缓冲，长度可能大于本帧字节数 —— 按 stride*height 界定有效区间
                var validBytes = checked((int)(st * h));
                for (var i = 0; i + 2 < validBytes; i += 4096 * 4)
                {
                    sum += px[i] + px[i + 1] + px[i + 2];
                    samples += 3;
                }

                var brightness = samples > 0 ? sum / samples : 0;
                return (Copied: true, Width: w, Height: h, Brightness: brightness, Ticks: ticks);
            }).ContinueWith(t =>
            {
                System.Threading.Interlocked.Exchange(ref sampling, 0);
                if (!ReferenceEquals(conn.Client, client) || !ReferenceEquals(conn.Watchdog, timer))
                {
                    _ = t.Exception; // Observe an obsolete sample failure without affecting the new connection.
                    return;
                }
                // 采样本身炸了（极端情况）：不当成黑屏，等下一拍
                if (t.IsFaulted)
                {
                    AppendSurfaceLog(conn.Key, "画面采样失败：" + Flatten(t.Exception?.GetBaseException().Message ?? "未知错误"));
                    timer.Stop();
                    conn.Watchdog = null;
                    return;
                }

                if (!t.Result.Copied)
                {
                    if (t.Result.Ticks <= 10)
                    {
                        return; // 还没有任何帧：下一拍再看（最多等 60 秒）
                    }

                    AppendSurfaceLog(conn.Key, "60 秒未收到任何画面帧；请断开后重连，或改用 mstsc 连接方式。");
                    timer.Stop();
                    conn.Watchdog = null;
                    return;
                }

                var w = t.Result.Width;
                var h = t.Result.Height;
                var brightness = t.Result.Brightness;

                if (brightness > 20)
                {
                    // 画面有内容：自愈完成
                    timer.Stop();
                    conn.Watchdog = null;
                    conn.RetryCount = 0;
                    AppendSurfaceLog(conn.Key, string.Format("画面正常（{0}×{1}）。点击画面可获得焦点并直接操作。", w, h));
                    return;
                }

                if (conn.RetryCount >= 3)
                {
                    AppendSurfaceLog(conn.Key, "多次重试仍黑屏；请点「断开画面」后重新连接，或在连接方式里改回 mstsc。");
                    timer.Stop();
                    conn.Watchdog = null;
                    return;
                }

                conn.RetryCount++;
                AppendSurfaceLog(conn.Key, string.Format("检测到静止黑屏（亮度 {0}），自动重连第 {1}/3 次…", brightness, conn.RetryCount));
                ReconnectEmbedded(conn);
            }, TaskScheduler.FromCurrentSynchronizationContext());
        };
        timer.Start();
        conn.Watchdog = timer;
    }

    private void StopEmbedWatchdog(EmbedConnection conn)
    {
        conn.Watchdog?.Stop();
        conn.Watchdog = null;
        conn.RetryCount = 0;
    }

    private void OnEmbedConnected(EmbedConnection conn, (int Session, uint Width, uint Height) e)
    {
        // 原生事件循环线程回调：AppendLog 自带编组；UI 属性必须 Post 到 UI 线程
        // （直接设 RdpStatusText 会触发 x:Bind 跨线程 set_Text → RPC_E_WRONG_THREAD 崩溃）
        AppendSurfaceLog(conn.Key, $"连接建立：桌面 {e.Width}×{e.Height}，画面已嵌入窗口。");
        conn.AutoReconnects = 0; // 连接成功：清零自动重连计数

        // 手动连接走这里：只有真正连上了才请求切回。
        // 任务批次期间不在这里动手 —— RunChannelsAsync 会在所有通道收敛后统一切一次。
        if (Volatile.Read(ref _channelRunDepth) == 0)
        {
            _ = TriggerControllerSessionReturnAsync("手动连接", true);
        }

        // 刻意不在这里通知：本回调跑在原生渲染线程上，且正处在 D3D/SwapChain 建立的关键路径里。
        // 用户点「连接通道」时已经通知过一次，这里再插一次只会把 18 秒的等待重置一遍，
        // 收益为零，却多一次跨线程入队。
        Post(() =>
        {
            if (RdpStatusText.Contains("内嵌连接中", StringComparison.Ordinal) ||
                RdpStatusText.Contains("自动重连", StringComparison.Ordinal))
            {
                RdpStatusText = "内嵌画面已连接";
            }
        });
    }

    private void OnEmbedDisconnected(EmbedConnection conn, (int Session, int Reason, string Detail) e)
    {
        // 可能在原生线程触发：AppendLog 线程安全；状态更新走 Post
        AppendSurfaceLog(conn.Key, $"连接断开：{e.Detail}（目标会话保留，可重新连接）。");
        Post(() => RdpStatusText = "内嵌连接已断开（目标会话保留）");

        // M6 自动重连：仅服务器/网络侧意外断开（环回解锁/网络闪断）。
        // LOCAL = 本端主动（用户断开/看门狗自愈链/窗口关闭）不重连；
        // ERROR = 协议错误，重连多半还错，避免风暴不重连。
        if (e.Reason == 2 && !conn.UserDisconnect && conn.RetryCount == 0)
        {
            ScheduleEmbedAutoReconnect(conn);
        }
        DisposeSurface(conn.Key);
    }

    /// <summary>
    /// 意外掉线自动重连（M6）：指数退避 1/2/4/8/16 秒，最多 5 次。
    /// 覆盖 §5.5 环回解锁场景（锁屏结束后内嵌画面自动恢复）。
    /// </summary>
    private void ScheduleEmbedAutoReconnect(EmbedConnection conn)
    {
        if (conn.AutoReconnects >= 5)
        {
            AppendSurfaceLog(conn.Key, "自动重连已达上限（5 次）；请手动重连或改用 mstsc 连接方式。");
            return;
        }
        conn.AutoReconnects++;
        var delay = Math.Min(1 << (conn.AutoReconnects - 1), 16);
        AppendSurfaceLog(conn.Key, $"{delay} 秒后自动重连（第 {conn.AutoReconnects}/5 次）…");
        Post(() =>
        {
            var timer = _dispatcher.CreateTimer();
            timer.Interval = TimeSpan.FromSeconds(delay);
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                // 等待期间已手动连接/断开则放弃
                if (conn.Client is not null || conn.UserDisconnect)
                {
                    return;
                }
                ReconnectEmbedded(conn);
            };
            timer.Start();
        });
    }

    /// <summary>
    /// 释放某条通道的内嵌连接实例（ycn_rdp_disconnect：目标会话保留）。幂等，可在任意线程调用。
    ///
    /// 🔑 原生断开是<b>异步</b>做的（2026-10-12 改）：<c>ycn_rdp_disconnect</c> 会 join
    /// 原生 RDP 线程，最坏情况在 UI 线程上阻塞 5 秒。这里先在本方法内完成解绑与状态复位
    /// （立刻生效、界面马上松开），再把 <c>Dispose</c> 丢到工作线程去等。
    /// 注意：不重置黑屏重试计数——自动重连链路依赖它跨连接保持。
    /// 另外：字典条目刻意保留（重试计数与「已弹出窗口」标记要跨连接存活）。
    /// </summary>
    public void DisposeSurface(string? channelId)
    {
        var key = channelId ?? string.Empty;
        if (!_embeds.TryGetValue(key, out var conn) || conn.Client is null)
        {
            return;
        }

        var client = conn.Client;
        conn.Client = null;
        conn.PoppedOut = false;

        if (conn.OnConnected is not null)
        {
            client.Connected -= conn.OnConnected;
        }

        if (conn.OnFrameArrived is not null)
        {
            client.FrameArrived -= conn.OnFrameArrived;
        }

        if (conn.OnDisconnected is not null)
        {
            client.Disconnected -= conn.OnDisconnected;
        }

        // ycn_rdp_disconnect 内部会 join 原生 RDP 线程（WaitForSingleObject 5 秒超时），
        // 在 UI 线程上等就是最长 5 秒的界面冻结 —— 点「断开」时用户会以为又卡死了。
        // 所以这里只做解绑，把真正的原生断开丢到工作线程；UI 侧立刻返回。
        // ⚠️ 不能改 client 内部的字段：解绑之后它只被这一个后台任务引用。
        conn.Teardown = Task.Run(async () =>
        {
            client.Dispose();
            // A five-second native wait timeout is not permission to start another session.
            await client.Completion.ConfigureAwait(false);
        });

        conn.Watchdog?.Stop();
        conn.Watchdog = null;

        if (string.Equals(_embedActiveKey, key, StringComparison.OrdinalIgnoreCase) && !HasAnySurface)
        {
            _embedActiveKey = string.Empty;
        }

        AppendSurfaceLog(key, "内嵌连接已断开（目标会话保留中）。");
        EmbedClientChanged?.Invoke();
    }

    /// <summary>断开全部内嵌画面（窗口关闭 / 退出应用时用）。</summary>
    public void DisposeEmbedClient()
    {
        foreach (var key in _embeds.Keys.ToList())
        {
            var connection = _embeds[key];
            connection.UserDisconnect = true;
            connection.ConnectGeneration++;
            DisposeSurface(key);
        }
    }

    /// <summary>当前展示通道的事件列表（跟着 <see cref="ActiveSession"/> 走）。</summary>
    public ObservableCollection<RdpTaskEvent> RdpEvents => _activeSession?.Events ?? _emptyEvents;

    /// <summary>当前展示通道的独立实时外部日志列表。</summary>
    public ObservableCollection<RdpLiveLog> RdpExternalLogs => _activeSession?.ExternalLogs ?? _emptyExternalLogs;

    /// <summary>本轮执行涉及的全部通道会话（「本地执行」组在最前）。</summary>
    public IReadOnlyList<RdpChannelSession> Sessions => _sessions;

    /// <summary>
    /// 当前展示的通道。M4 引入通道页面后由页面切换来设置；在此之前取第一条远程通道
    /// （没有就取本地组），保持"单通道观感"与改造前完全一致。
    /// </summary>
    public RdpChannelSession? ActiveSession
    {
        get => _activeSession;
        set
        {
            if (ReferenceEquals(_activeSession, value))
            {
                return;
            }

            _activeSession = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(RdpEvents));
            OnPropertyChanged(nameof(RdpExternalLogs));
            OnPropertyChanged(nameof(ActiveChannelName));
            MirrorActiveSession();
        }
    }

    /// <summary>页头 / 菜单上显示的通道名。</summary>
    public string ActiveChannelName => _activeSession?.DisplayName ?? "未连接";

    /// <summary>本轮是否存在远程通道（决定进度区是否有意义）。</summary>
    public bool HasRemoteSession => _sessions.Any(s => s.IsRemote);

    /// <summary>
    /// 把活跃通道的状态镜像到界面属性上。
    ///
    /// <c>RdpPhase</c> / <c>RdpProgress</c> / <c>RdpHeartbeatText</c> 这些原本就是 VM 自己的
    /// 字段、被 XAML 直接绑定；镜像之后 XAML 一行都不用改，单通道观感与改造前一致。
    /// M4 拆出各通道页面时再改成直接绑 Session。
    /// </summary>
    private void MirrorActiveSession()
    {
        var s = _activeSession;

        RdpPhase = s?.Phase ?? "idle";
        RdpProgress = s?.Progress ?? 0;
        RdpTotal = s?.Total ?? 0;
        RdpElapsedSeconds = s?.ElapsedSeconds ?? 0;
        RdpCurrentTask = s?.TaskDisplay ?? "－";
        RdpHeartbeatText = s?.HeartbeatText ?? "－";
        RdpIsStale = s?.IsStale ?? false;
        RdpStatusText = s?.StatusText ?? "未连接";

        // 面板与进度区是两套绑定，但数据同源 —— 任何一次镜像都要顺手通知面板
        RaisePanelChanged();
    }

    // ---------------- 状态面板（主页状态卡） ----------------

    /// <summary>
    /// 状态面板的状态文案。
    ///
    /// <para>**为什么不能直接绑 <see cref="StatusText"/>**：那套属性（`StatusText` /
    /// `CurrentTaskName` / `ElapsedText` / `ProgressText`）只由本地执行的 `ScriptRunner` 回调写入，
    /// 而远程通道的进度全落在 <see cref="RdpChannelSession"/> 里、从不回灌 ——
    /// 于是"跑远程任务时面板一直是空闲 / 00:00 / - / 0 / 0"（真机实测过）。</para>
    ///
    /// <para>这里统一按"本轮有几块"取值：单块就是那一块（观感与改造前一致），
    /// 多块给整轮聚合（一块的数字代表不了整轮）。</para>
    /// </summary>
    public string PanelStatusText
    {
        get
        {
            if (_sessions.Count == 0)
            {
                return StatusText;   // 还没跑过任何一轮：沿用本地面板（空闲 / 已完成…）
            }

            if (_sessions.Count == 1)
            {
                return _sessions[0].StatusText;
            }

            if (!IsRunning)
            {
                if (_lastRunAbnormal)
                {
                    return "已完成（有异常）";
                }

                // 被手动停止的一轮：说"已完成"是在骗人（进度条还停在一半）
                return _lastRunStopped ? "已停止" : "已完成";
            }

            return $"执行中（{_sessions.Count} 块并行）";
        }
    }

    /// <summary>面板上的"当前任务"：多块并行时跟着正在展示的那条通道走。</summary>
    public string PanelCurrentTask
    {
        get
        {
            if (_sessions.Count == 0)
            {
                return CurrentTaskName;
            }

            return _sessions.Count == 1
                ? _sessions[0].TaskDisplay
                : _activeSession?.TaskDisplay ?? "－";
        }
    }

    /// <summary>面板上的"耗时"：多块并行时取整轮的墙钟时间。</summary>
    public string PanelElapsedText
    {
        get
        {
            if (_sessions.Count == 0)
            {
                return ElapsedText;
            }

            if (_sessions.Count == 1)
            {
                return _sessions[0].ElapsedText;
            }

            if (_runStartedAt == default)
            {
                return "00:00";
            }

            var end = _runEndedAt ?? DateTimeOffset.Now;
            var seconds = (int)(end - _runStartedAt).TotalSeconds;
            return FormatHelper.FormatSeconds(Math.Max(0, seconds));
        }
    }

    /// <summary>面板上的"进度"：多块并行时给各块之和。</summary>
    public string PanelProgressText
    {
        get
        {
            if (_sessions.Count == 0)
            {
                return ProgressText;
            }

            if (_sessions.Count == 1)
            {
                return $"{_sessions[0].Progress} / {_sessions[0].Total}";
            }

            var done = _sessions.Sum(s => s.Progress);
            var total = _sessions.Sum(s => s.Total);
            return $"合计 {done} / {total}";
        }
    }

    /// <summary>本地执行组（未分配通道的那批任务；本轮没有本地任务时为 null）。</summary>
    private RdpChannelSession? LocalGroup => _sessions.FirstOrDefault(s => !s.IsRemote);

    /// <summary>
    /// 上一轮（多块并行时是整轮）是否出现过异常。
    ///
    /// <para>给右侧执行状态栏的状态点亮红灯用 —— 面板文案里虽然也播了「（有异常）」，
    /// 但状态栏那么窄，一行字经常被截掉半句，色点比文字先被看到。</para>
    /// </summary>
    public bool PanelHasAbnormal => _lastRunAbnormal;

    /// <summary>
    /// 异常原因的第一条（状态栏正文）。空串 = 没有可显示的原因。
    ///
    /// <para>「已完成（有异常）」只是结论。用户看到它之后的下一个问题是"哪儿异常了"，
    /// 所以状态栏必须跟一句具体原因（哪条通道、哪个任务、为什么）。</para>
    /// </summary>
    public string PanelAbnormalReason =>
        _lastRunAbnormalReasons.Count > 0 ? _lastRunAbnormalReasons[0] : string.Empty;

    /// <summary>除第一条之外还剩几条（状态栏次级提示）。空串 = 只有一条。</summary>
    public string PanelAbnormalMoreText => _lastRunAbnormalReasons.Count > 1
        ? $"另有 {_lastRunAbnormalReasons.Count - 1} 条，详见运行日志"
        : string.Empty;

    /// <summary>全部异常原因（换行拼接），给状态栏的悬浮提示用。</summary>
    public string PanelAbnormalDetail => string.Join(Environment.NewLine, _lastRunAbnormalReasons);

    /// <summary>
    /// 面板四项都是计算属性，值来自各通道会话 —— 任何一个块的状态变了都要整体通知一次。
    /// （属性名要写全：计算属性没法用"某个字段变了"来推断。）
    /// </summary>
    private void RaisePanelChanged()
    {
        OnPropertyChanged(nameof(PanelStatusText));
        OnPropertyChanged(nameof(PanelCurrentTask));
        OnPropertyChanged(nameof(PanelElapsedText));
        OnPropertyChanged(nameof(PanelProgressText));
        OnPropertyChanged(nameof(PanelHasAbnormal));
        OnPropertyChanged(nameof(PanelAbnormalReason));
        OnPropertyChanged(nameof(PanelAbnormalMoreText));
        OnPropertyChanged(nameof(PanelAbnormalDetail));
    }

    /// <summary>是否正在连接 / 等待目标会话。</summary>
    public bool IsRdpBusy
    {
        get => _isRdpBusy;
        private set
        {
            if (SetProperty(ref _isRdpBusy, value))
            {
                NotifyRdpCommands();
            }
        }
    }

    public string RdpStatusText
    {
        get => _rdpStatusText;
        private set => SetProperty(ref _rdpStatusText, value);
    }

    public string RdpPhase
    {
        get => _rdpPhase;
        private set
        {
            if (SetProperty(ref _rdpPhase, value))
            {
                OnPropertyChanged(nameof(RdpIsActive));
            }
        }
    }

    public bool RdpIsActive => _rdpPhase is "running";

    public string RdpCurrentTask
    {
        get => _rdpCurrentTask;
        private set => SetProperty(ref _rdpCurrentTask, value);
    }

    public int RdpProgress
    {
        get => _rdpProgress;
        private set => SetProperty(ref _rdpProgress, value);
    }

    public int RdpTotal
    {
        get => _rdpTotal;
        private set => SetProperty(ref _rdpTotal, value);
    }

    /// <summary>当前任务已运行秒数（由 Agent 每次心跳回传）。</summary>
    public int RdpElapsedSeconds
    {
        get => _rdpElapsedSeconds;
        private set
        {
            if (SetProperty(ref _rdpElapsedSeconds, value))
            {
                OnPropertyChanged(nameof(RdpElapsedText));
            }
        }
    }

    public string RdpElapsedText => FormatHelper.FormatSeconds(RdpElapsedSeconds);

    /// <summary>心跳描述，例如「3 秒前」「已失联 8 分 12 秒」。</summary>
    public string RdpHeartbeatText
    {
        get => _rdpHeartbeatText;
        private set => SetProperty(ref _rdpHeartbeatText, value);
    }

    /// <summary>Agent 是否已停止回传心跳（会话断开 / 代理被终止时的典型表现）。</summary>
    public bool RdpIsStale
    {
        get => _rdpIsStale;
        private set => SetProperty(ref _rdpIsStale, value);
    }

    public string RdpReadinessText
    {
        get => _rdpReadinessText;
        private set => SetProperty(ref _rdpReadinessText, value);
    }

    public string RdpSessionSummary
    {
        get => _rdpSessionSummary;
        private set => SetProperty(ref _rdpSessionSummary, value);
    }

    /// <summary>
    /// 会话代理状态文案：区分「还没部署」「已部署但当前不在线」「已部署且在线」。
    /// 与 <see cref="RdpReadinessText"/> 分开呈现 —— 环境就绪 ≠ 代理在跑，两件事别混在一起。
    /// </summary>
    public string RdpAgentStatusText
    {
        get => _rdpAgentStatusText;
        private set => SetProperty(ref _rdpAgentStatusText, value);
    }

    /// <summary>
    /// 当前要操作的指令桥。
    ///
    /// M1（桥实例化）阶段仍等价于"单通道"：按配置里的桥目录 + 目标账户解析出目录，
    /// 与目标会话里代理自己算出来的目录一致；M3 引入 RdpChannelSession 后，
    /// 这里会换成按通道取（<see cref="RdpBridge.ForChannel"/>）。
    /// </summary>
    private RdpBridge Bridge => RdpBridge.For(RdpChannelPaths.Resolve(RdpSettings.BridgePath, RdpSettings.TargetUser));

    public string RdpBridgePath => Bridge.BridgeDir;

    /// <summary>当前目标是不是本机（决定界面上哪些按钮有意义）。</summary>
    public bool RdpTargetIsLocal => RdpTargets.IsLocal(RdpSettings.TargetHost);

    // ---------------- 命令 ----------------

    public ICommand DeployAgentCommand => _deployAgentCommand ??= new RelayCommand(DeployRdpAgent);

    public ICommand RemoveAgentCommand => _removeAgentCommand ??= new RelayCommand(RemoveRdpAgent);

    public ICommand ConnectAndRunCommand =>
        _connectAndRunCommand ??= new AsyncRelayCommand(ConnectAndRunAsync, () => !IsRunning && !IsRdpBusy);

    public ICommand DisconnectSessionCommand =>
        _disconnectSessionCommand ??= new RelayCommand(DisconnectTargetSession);

    public ICommand LogoffSessionCommand => _logoffSessionCommand ??= new RelayCommand(LogoffTargetSession);

    public ICommand RefreshRdpCommand => _refreshRdpCommand ??= new RelayCommand(RefreshRdpReadiness);

    public ICommand OpenBridgeFolderCommand => _openBridgeFolderCommand ??= new RelayCommand(() => Bridge.OpenBridgeFolder());

    private void NotifyRdpCommands()
    {
        _connectAndRunCommand?.RaiseCanExecuteChanged();
        _deployAgentCommand?.RaiseCanExecuteChanged();
        _removeAgentCommand?.RaiseCanExecuteChanged();
        _disconnectSessionCommand?.RaiseCanExecuteChanged();
        _logoffSessionCommand?.RaiseCanExecuteChanged();
        OnPropertyChanged(nameof(CanConnectSurface));
    }

    // ---------------- 预检与凭据 ----------------

    public void RefreshRdpReadiness()
    {
        // 桥目录可能刚被改过，每次检查前确保它存在（目录本身按目标账户派生）
        Bridge.EnsureDirectory();

        var readiness = RdpSessionService.CheckReadiness(
            RdpSettings.TargetHost, RdpSettings.TargetUser, RdpSettings.CredentialSaved, RdpSettings.BridgePath);

        var notes = new List<string>();
        if (readiness.EditionNote.Length > 0)
        {
            notes.Add(readiness.EditionNote);
        }

        notes.AddRange(readiness.Notes);

        if (readiness.IsLocal)
        {
            var session = RdpSessionService.FindSession(RdpSettings.TargetUser);
            RdpSessionSummary = session is null
                ? "目标账户当前没有会话"
                : $"会话 {session.Id} · {session.UserName} · {session.StateText}";
        }
        else
        {
            RdpSessionSummary = $"远程主机 {RdpTargets.Normalize(RdpSettings.TargetHost)}：会话状态无法在本机枚举";
        }

        RdpReadinessText = readiness.Problems.Count == 0
            ? (notes.Count == 0 ? "环境就绪，可以连接。" : $"环境就绪，可以连接。（{string.Join("；", notes)}）")
            : string.Join("；", readiness.Problems);

        RdpAgentStatusText = DescribeAgentStatus(readiness);

        RdpSettings.CredentialSaved = readiness.CredentialSaved;
        OnPropertyChanged(nameof(RdpBridgePath));
        OnPropertyChanged(nameof(RdpTargetIsLocal));
    }

    /// <summary>
    /// 把"已部署"（静态：快捷方式在不在）与"在线"（运行期：代理此刻在不在跑）拆成两类文案。
    /// 两者的下一步动作完全不同：没部署要去部署，已部署但不在线要注销目标账户后重新连接。
    /// </summary>
    private static string DescribeAgentStatus(RdpReadiness readiness)
    {
        if (!readiness.IsLocal)
        {
            return "远程主机：代理是否在线需在对方机器上确认";
        }

        if (!readiness.AgentDeployed)
        {
            return "尚未部署会话代理（点「部署会话代理」）";
        }

        return readiness.AgentOnline
            ? "会话代理已部署，且当前在线"
            : "会话代理已部署，但当前不在线（目标账户未登录，或代理未随会话启动）";
    }

    /// <summary>保存目标账户凭据到 Windows 凭据管理器。密码只进内存，不写配置文件。</summary>
    public void SaveRdpCredential(string password)
    {
        if (string.IsNullOrWhiteSpace(RdpSettings.TargetUser))
        {
            _ = DialogHelper.ShowMessageAsync("提示", "请先填写目标账户名。");
            return;
        }

        if (password.Length == 0)
        {
            _ = DialogHelper.ShowMessageAsync("提示", "请填写目标账户的登录密码。");
            return;
        }

        if (!RdpSessionService.SaveCredential(RdpSettings.TargetHost, RdpSettings.TargetUser, password, out var message))
        {
            _ = DialogHelper.ShowMessageAsync("保存失败", message);
            return;
        }

        // 存进去要能取回来才作数：内嵌客户端连接时是靠取回密码来登录的（按主机 + 账户取）
        if (RdpSessionService.TryLoadPassword(RdpSettings.TargetHost, RdpSettings.TargetUser) is null)
        {
            _ = DialogHelper.ShowMessageAsync("保存异常",
                "凭据写入了，但读不回来，系统远程桌面可能登录失败。\n\n" +
                "常见原因是凭据管理器被组策略限制。");
            AppendLog("警告：凭据保存后无法读回，远程桌面可能无法自动登录。");
        }

        RdpSettings.CredentialSaved = true;
        RdpSessionService.PendingPassword = password;
        SaveConfig();
        AppendLog($"已保存目标账户「{RdpSettings.TargetUser}」的凭据（目标主机 {RdpTargets.Normalize(RdpSettings.TargetHost)}，存于 Windows 凭据管理器，配置文件不保存密码）。");
        RefreshRdpReadiness();
    }

    public void ClearRdpCredential()
    {
        RdpSessionService.DeleteCredential(RdpSettings.TargetHost, RdpSettings.TargetUser);
        RdpSessionService.PendingPassword = null;
        RdpSettings.CredentialSaved = false;
        SaveConfig();
        AppendLog("已删除保存的目标账户凭据。");
        RefreshRdpReadiness();
    }

    private void DeployRdpAgent()
    {
        // 首次部署要把整个程序（约 140MB）复制到公共目录，同步做会卡住界面
        IsRdpBusy = true;
        RdpStatusText = "正在部署会话代理（首次会复制程序文件，稍等）…";
        AppendLog("正在部署会话代理：把程序复制到所有用户都能访问的公共目录…");

        var bridge = RdpSettings.BridgePath;
        // 目标账户要带上：部署前要判断它那个常驻代理是不是正在跑任务（跑着就不能动）
        var targetUser = RdpSettings.TargetUser;

        Task.Run(() =>
        {
            var ok = RdpSessionService.DeployAgent(out var message, bridge, targetUser);
            return (Ok: ok, Message: message);
        }).ContinueWith(task =>
        {
            var (ok, message) = task.Result;
            _dispatcher.TryEnqueue(() =>
            {
                if (ok)
                {
                    AppendLog($"会话代理：{Flatten(message)}");
                    RdpStatusText = ReportAgentDeployed(message)
                        ? "会话代理已部署（目标账户需注销重连才生效）"
                        : "会话代理已部署";
                }
                else
                {
                    // 失败原因可能是文件被占用 / PowerShell 报错 / 真的缺管理员权限。
                    // 不再替用户下"就是缺权限"的结论 —— message 里已经带了真实报错和判断依据。
                    AppendLog($"[远程桌面] 会话代理部署失败：{Flatten(message)}");
                    RdpStatusText = "会话代理部署失败";
                    _ = DialogHelper.ShowMessageAsync("部署失败", message);
                }

                IsRdpBusy = false;
                RefreshRdpReadiness();
            });
        });
    }

    /// <summary>把多行报文压成一行 —— 日志是按行写的，带换行的字符串会把日志切乱。</summary>
    private static string Flatten(string text) =>
        string.IsNullOrEmpty(text)
            ? string.Empty
            : text.Replace("\r\n", " ").Replace('\n', ' ').Replace('\r', ' ');

    /// <summary>
    /// 「部署会话代理」的成功回执。
    ///
    /// 为什么成功也必须说话：这个按钮"换了程序才点一次"，但点完原先只做两件事 ——
    /// 往运行日志里追一行、改一个**通道页上根本看不到**的 <c>RdpStatusText</c>。
    /// 于是用户看到的就是"点了完全没反应"，只能来问到底成没成（2026-09-27 真机）。
    /// 而失败反而是弹窗的 —— 一静一响，正好把"部署好了"和"没点上"混成同一个体感。
    ///
    /// 返回值：本次部署是否结束了正在跑的旧代理。是的话程序副本虽然换了，
    /// 内存里那个旧进程已经不在了，得等目标账户重新登录才会拉起新副本。
    /// </summary>
    private static bool ReportAgentDeployed(string message)
    {
        var detail = Flatten(message);

        // "已结束 N 个正在运行的旧会话代理" 和 "需注销目标账户后重新连接" 是同一个信号：
        // 文件换了、旧进程没了，只有重新登录才会把新程序拉起来。
        var needRelogin = detail.Contains("已结束") || detail.Contains("注销");

        var body = "程序副本与公共启动项都已就位。\n\n"
            + (detail.Length > 0 ? detail : "（无附加说明）");

        if (needRelogin)
        {
            body += "\n\n注意：旧代理进程已经不在运行了。目标账户需要走一次「注销目标账户」再重新连接 —— "
                + "代理只在登录那一刻由启动项拉起，直接重连旧会话不会换上新程序。";
        }

        _ = DialogHelper.ShowMessageAsync("会话代理部署完成", body);
        return needRelogin;
    }

    private void RemoveRdpAgent()
    {
        var ok = RdpSessionService.RemoveAgent(out var message);
        AppendLog($"会话代理：{Flatten(message)}");
        RdpStatusText = ok ? "会话代理已移除" : "移除代理未完成";
        _ = DialogHelper.ShowMessageAsync(ok ? "会话代理已移除" : "移除代理未完成", Flatten(message));
        RefreshRdpReadiness();
    }

    private void DisconnectTargetSession()
    {
        if (!RdpTargets.IsLocal(RdpSettings.TargetHost))
        {
            _ = DialogHelper.ShowMessageAsync("提示",
                $"目标是远程主机 {RdpTargets.Normalize(RdpSettings.TargetHost)}，断开操作只能在目标机器上执行（或用任务结束后的「断开会话」收尾方式）。");
            return;
        }

        var session = RdpSessionService.FindSession(RdpSettings.TargetUser);
        if (session is null)
        {
            _ = DialogHelper.ShowMessageAsync("提示", "找不到目标账户的会话，可能尚未连接或已注销。");
            return;
        }

        RdpSessionService.DisconnectSession(session.Id, out var message);
        AppendLog(message);
        RefreshRdpReadiness();
    }

    private void LogoffTargetSession()
    {
        if (!RdpTargets.IsLocal(RdpSettings.TargetHost))
        {
            _ = DialogHelper.ShowMessageAsync("提示",
                $"目标是远程主机 {RdpTargets.Normalize(RdpSettings.TargetHost)}，注销操作只能在目标机器上执行（或用任务结束后的「注销用户」收尾方式）。");
            return;
        }

        var session = RdpSessionService.FindSession(RdpSettings.TargetUser);
        if (session is null)
        {
            _ = DialogHelper.ShowMessageAsync("提示", "找不到目标账户的会话，可能尚未连接或已注销。");
            return;
        }

        // 【防护】注销会销毁会话，会话里的代理和脚本进程会被系统一起终止。
        // 这个按钮紧挨着「连接并执行」，正在跑任务时误点的代价很大，所以先确认。
        if (RdpIsActive || IsRdpBusy)
        {
            _ = ConfirmLogoffWhileRunningAsync(session.Id);
            return;
        }

        RdpSessionService.LogoffSession(session.Id, out var message);
        AppendLog(message);
        RefreshRdpReadiness();
    }

    /// <summary>任务还在跑时的注销确认。选「取消」则什么都不做。</summary>
    private async Task ConfirmLogoffWhileRunningAsync(uint sessionId)
    {
        try
        {
            var result = await DialogHelper.ShowMessageAsync(
                "确认注销目标账户？",
                $"目标账户「{RdpSettings.TargetUser}」的会话里还有任务在跑：\n" +
                $"　　{RdpCurrentTask}（{RdpProgress} / {RdpTotal}，已运行 {RdpElapsedText}）\n\n" +
                "注销会销毁该会话，会话里的代理与脚本进程会被系统一并终止，任务就此中断、进度也无法再回传。\n\n" +
                "如果只是想关掉远程桌面窗口、让任务继续在后台跑，请改用「断开目标会话」——" +
                "断开会保留会话和进程。",
                closeText: "取消",
                primaryText: "仍要注销");

            if (result != Microsoft.UI.Xaml.Controls.ContentDialogResult.Primary)
            {
                AppendLog("已取消注销目标账户。");
                return;
            }

            RdpSessionService.LogoffSession(sessionId, out var message);
            AppendLog(message);
            RefreshRdpReadiness();
        }
        catch (Exception ex)
        {
            AppendLog($"注销确认对话框失败：{ex.Message}");
        }
    }

    // ---------------- 主流程：连接并执行 ----------------

    /// <summary>
    /// 「连接并执行」单通道入口（远程页的按钮走这里）：用设置页那组 target_host / target_user /
    /// bridge_path 合成一个临时通道，再走与多通道**完全相同**的路径 ——
    /// 免得单通道与多通道各有一套实现，日后改一处漏一处。
    /// </summary>
    public async Task ConnectAndRunAsync()
    {
        var channel = ChannelFromSettings();
        var tasks = Tasks.Where(t => t.Enabled).ToList();

        if (!ValidateChannel(channel, tasks))
        {
            return;
        }

        if (!IsRunning)
        {
            // 从设置页单独点「连接并执行」时也要把本轮状态归零（不然上一轮的异常标志会残留）
            _runStoppedOrError = false;
            ResetRunSummaryState();
        }

        var session = EnsureSession(channel);
        ActiveSession = session;

        // 单通道入口也计入本轮运行：这样"停止执行"可用，收尾也走同一套
        _pendingRuns = Math.Max(1, _pendingRuns);
        IsRunning = true;
        StartRdpPolling();

        await RunChannelAsync(session, tasks);
    }

    /// <summary>
    /// 把设置页那一组单通道配置（target_* / bridge_path / session_finish / 分辨率）合成一个通道。
    /// 用固定 id，便于日志与去重识别；参数已被 <see cref="RdpChannel"/> 的 setter 规整过。
    /// </summary>
    private RdpChannel ChannelFromSettings() => new()
    {
        Id = SettingsChannelId,
        Name = "当前设置",
        Host = RdpSettings.TargetHost,
        User = RdpSettings.TargetUser,
        BridgePath = RdpSettings.BridgePath,
        CredentialSaved = RdpSettings.CredentialSaved,
        DesktopWidth = RdpSettings.DesktopWidth,
        DesktopHeight = RdpSettings.DesktopHeight,
    };

    /// <summary>按通道 id 取已有的会话（没有就新建一个并登记进本轮）。</summary>
    private RdpChannelSession EnsureSession(RdpChannel channel)
    {
        var existing = _sessions.FirstOrDefault(s =>
            s.IsRemote && string.Equals(s.ChannelId, channel.Id, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            // 同一通道再次下发：刷新配置快照（用户可能刚改过账户/分辨率）
            return existing;
        }

        var session = new RdpChannelSession(channel, RdpBridge.ForChannel(channel));
        _sessions.Add(session);
        RaiseSessionsChanged();
        return session;
    }

    /// <summary>
    /// 把某条通道的状态镜像到界面（仅当它正在被展示）。
    ///
    /// 🔑 **收口在方法自己身上，不要指望调用方在 UI 线程**（2026-10-07 崩在这）。
    ///
    /// 现场栈（repo/logs/crash.log，17:21:17）：
    ///   RunChannelsAsync → RunChannelAsync → WaitForChannelReadyAsync
    ///   → MirrorSessionIfActive → MirrorActiveSession → RaisePanelChanged
    ///   → OnPropertyChanged → 某个 x:Bind 控件的 set_Text
    ///   → COMException 0x8001010E (RPC_E_WRONG_THREAD)，随后原生 AV 掀进程。
    ///
    /// 原因：<c>WaitForChannelReadyAsync</c> 里两处 <c>await Task.Run(...)</c>
    /// （等会话建立、等代理上线）都是**同步阻塞**的等待，续体落在线程池上；
    /// 后面紧跟的 <c>MirrorSessionIfActive</c> / <c>IsRdpBusy</c> / <c>RaisePanelChanged</c>
    /// 全都会触发 <c>x:Bind</c> 重新求值 → <c>TextBlock.set_Text</c>，
    /// 而 WinRT 对象只能在它所属的 UI 线程上碰。
    ///
    /// 为什么之前没炸：早先只有单通道，镜像发生在 <c>await</c> 之前；
    /// 改成多通道并行（<c>RunChannelsAsync</c>）后等待被包进了 async 方法，线程就变了。
    /// 所以**这不是"某处写漏了"，是所有 await Task.Run 之后的绑定刷新都得收口**。
    /// </summary>
    private void MirrorSessionIfActive(RdpChannelSession session)
    {
        if (!ReferenceEquals(_activeSession, session))
        {
            return;
        }

        if (_dispatcher.HasThreadAccess)
        {
            MirrorActiveSession();
            return;
        }

        // 非 UI 线程：只投递，不在这里等结果（调用点多在状态机里，阻塞会互相等死）。
        // 镜像只是"把当前值刷到界面"，晚一帧无所谓；真出错也不该炸掉状态机本身。
        if (!_dispatcher.TryEnqueue(MirrorActiveSession))
        {
            AppendLog("[界面] 界面线程队列已关闭，本次状态镜像被丢弃（通常正在关窗口）。");
        }
    }

    /// <summary>
    /// 通道下发前的预检（原单通道版全部保留，只是配置来源从 RdpSettings 换成通道）。
    /// 返回 false 表示这条通道不该启动，原因已经通过对话框 / 日志说明过。
    /// </summary>
    private bool ValidateChannel(RdpChannel channel, IReadOnlyList<TaskConfig> tasks)
    {
        var tag = channel.DisplayName;
        var host = RdpTargets.Normalize(channel.Host);
        var user = channel.User;
        var isLocal = RdpTargets.IsLocal(host);

        if (tasks.Count == 0)
        {
            _ = DialogHelper.ShowMessageAsync("提示", $"通道「{tag}」没有启用的任务。");
            return false;
        }

        if (!HasUsableTaskConfig())
        {
            _ = DialogHelper.ShowMessageAsync("提示", "还没有设置可执行的脚本配置。");
            return false;
        }

        var readiness = RdpSessionService.CheckReadiness(host, user, channel.CredentialSaved, channel.BridgePath);

        if (isLocal && !readiness.UserExists)
        {
            _ = DialogHelper.ShowMessageAsync("无法连接",
                $"本机不存在账户「{user}」。\n请先在 Windows 里创建该账户，再回到这里连接。");
            return false;
        }

        if (!readiness.CredentialSaved)
        {
            _ = DialogHelper.ShowMessageAsync("无法连接",
                $"通道「{tag}」还没有保存目标账户的凭据（目标主机：{host}）。\n请先填写账户名与密码并保存。");
            return false;
        }

        if (isLocal && !readiness.RemoteDesktopEnabled)
        {
            _ = DialogHelper.ShowMessageAsync("无法连接", $"系统尚未开启远程桌面，通道「{tag}」无法连接。请先在目标机器的「设置 → 系统 → 远程桌面」里开启。");
            return false;
        }

        if (!isLocal && channel.BridgePath.Trim().Length == 0)
        {
            _ = DialogHelper.ShowMessageAsync("还需要配置共享目录",
                $"通道「{tag}」的目标 {host} 是远程主机，对方读不到本机的指令文件。\n\n" +
                "请先在该通道的「指令桥目录」里填一个两台机器都能读写的共享位置（例如 \\\\192.168.1.20\\YomoriBridge），" +
                "并在对方机器上也部署会话代理、指向同一个目录。\n\n" +
                "如果只想切本机另一个账户，把远程主机改回 127.0.0.1 就行。");
            return false;
        }

        return true;
    }

    /// <summary>
    /// 在**一条通道**上执行一批任务：下发指令 → 建立/复用会话 → 等代理上线 →
    /// 之后交给 1Hz 轮询按通道收尾。
    ///
    /// 多通道并行时，每条通道各跑一份本方法（互不阻塞）；通道内部的任务仍按 order 串行
    /// —— 那是代理侧 <c>ScriptRunner</c> 的事，与主控端无关。
    /// </summary>
    /// <summary>
    /// 本轮是否已请求停止。
    ///
    /// <para>主控端原先没有这个标志：<see cref="Stop"/> 只调 <c>_runner?.RequestStop()</c> 与
    /// <see cref="RequestRemoteStop"/>，两者都作用于"已经跑起来的任务"。而「连接前等待」
    /// 发生在任何任务跑起来之前，需要一个本轮级的中止信号 —— 不能拿 <c>IsRunning</c> 代，
    /// 它在整轮结束前一直是 true。</para>
    /// </summary>
    private volatile bool _runAbortRequested;

    /// <summary>
    /// 可中止的等待（连接前等待用）。按 200ms 切片检查中止标志，返回 true = 被中止。
    ///
    /// <para>不能 <c>Thread.Sleep</c>：<c>RunChannelAsync</c> 的续体可能落在 UI 线程上，
    /// 同步阻塞会冻界面。也不能不检查标志：那样点「停止」要等整段等待走完才响应。</para>
    /// </summary>
    private async Task<bool> WaitAbortableAsync(int seconds)
    {
        var deadline = DateTime.UtcNow.AddSeconds(Math.Max(0, seconds));
        while (DateTime.UtcNow < deadline)
        {
            if (_runAbortRequested)
            {
                return true;
            }

            var remainMs = (int)Math.Min(200, Math.Max(1, (deadline - DateTime.UtcNow).TotalMilliseconds));
            await Task.Delay(remainMs).ConfigureAwait(true);
        }

        return _runAbortRequested;
    }

    /// <summary>
    /// 目标会话里的代理能不能认"等待 / 收尾"任务（需要 ≥ 2.1.0）。
    ///
    /// <para>读的是桥里 `status.json` 的 `AgentVersion` —— 那是**正在跑的**代理自己报的，
    /// 比读安装目录里的 exe 属性更可靠（远程主机的安装目录主控端根本看不到）。</para>
    ///
    /// <para>读不到（还没跑过代理 / 文件缺失）按"未知"放行：无法判断时不该阻止用户执行，
    /// 真遇到旧代理会在结果里表现为"脚本路径为空，已跳过"，日志能看出来。</para>
    ///
    /// <returns>true = 可以下发；false = 已拦截（提示与计数都处理过了）。</returns>
    /// </summary>
    private bool EnsureAgentSupportsTypedTasks(RdpBridge bridge, RdpChannelSession session, string tag)
    {
        const string minimum = "2.1.0";
        var reported = bridge.TryReadStatus()?.AgentVersion;

        if (string.IsNullOrWhiteSpace(reported))
        {
            AppendLog($"[通道：{tag}] 未读到目标会话的代理版本，按最新版处理。");
            return true;
        }

        if (AgentVersionInfo.IsAtLeast(reported, minimum))
        {
            return true;
        }

        AppendLog($"[通道：{tag}] 目标会话里的代理版本 {reported} 过旧（需 ≥ {minimum}）："
                  + "它不认等待 / 收尾任务，本轮不下发。请重新部署代理后再执行。");
        _ = DialogHelper.ShowMessageAsync("代理版本过旧",
            $"通道「{tag}」目标账户里运行的代理是 {reported}，新版的等待 / 收尾任务需要 ≥ {minimum}。\n\n" +
            "本轮这条通道不会下发。请在「通道管理」里重新部署代理（或重启目标会话让安装程序铺一次），再执行。");

        session.SetStatusText("代理版本过旧");
        MirrorSessionIfActive(session);
        session.MarkCompletionReported();
        FinishRunPart();
        return false;
    }

    private async Task RunChannelAsync(RdpChannelSession session, IReadOnlyList<TaskConfig> tasks)
    {
        var channel = session.Channel!;
        var bridge = session.Bridge;
        var tag = session.DisplayName;

        var host = RdpTargets.Normalize(channel.Host);
        var user = channel.User;
        var isLocal = RdpTargets.IsLocal(host);

        // 本通道本轮的执行顺序（收尾恒在末位，与 TaskScopePlanner 同口径）
        var orderedEnabled = tasks
            .Where(t => t is not null && t.Enabled)
            .OrderBy(t => TaskTypeResolver.IsFinish(t) ? 1 : 0)
            .ThenBy(t => t.Order)
            .ToList();

        // 旧代理不认 task_kind —— 等待/收尾任务在它眼里就是"脚本路径为空"，会被逐个标异常跳过。
        // 只要这批含这两类任务，就要求目标会话里的代理 ≥ 2.1.0，否则整批不下发。
        if (orderedEnabled.Any(t => !TaskKinds.IsScript(t.TaskKind))
            && !EnsureAgentSupportsTypedTasks(bridge, session, tag))
        {
            return;
        }

        // 连接前等待：只有本通道首条启用任务是「等待任务 + 连接前」时才发生。
        // 必须放在写指令之前 —— 代理一拿到指令就会开跑，只有这里才配叫"连接前"。
        var preWaitOrder = 0;
        if (TaskTypeResolver.ShouldWaitBeforeConnect(orderedEnabled))
        {
            var first = orderedEnabled[0];
            var seconds = Math.Clamp(first.WaitSeconds, 0, 3600);
            preWaitOrder = first.Order;

            session.SetStatusText($"等待 {seconds} 秒（通道连接前）…");
            MirrorSessionIfActive(session);
            AppendLog($"[通道：{tag}] [等待] 连接前等待 {seconds} 秒，之后下发指令。");

            if (await WaitAbortableAsync(seconds))
            {
                session.SetStatusText("已停止");
                MirrorSessionIfActive(session);
                AppendLog($"[通道：{tag}] [等待] 连接前等待被停止请求打断，本条通道不下发。");
                session.MarkCompletionReported();
                FinishRunPart();
                return;
            }

            AppendLog($"[通道：{tag}] [等待] 连接前等待结束，开始下发指令。");
        }

        var command = new RdpCommand
        {
            Tasks = tasks.Select(t => (TaskConfig)t.Clone()).ToList(),
            ShutdownAfterDone = Config.ShutdownAfterDone,
            ShutdownDelaySeconds = Config.ShutdownDelaySeconds,
            EnableTimeoutScreenshot = Config.EnableTimeoutScreenshot,
            PreWaitDoneForOrder = preWaitOrder,
            // 通知一律由主控端发（见下面的 ApplyChannelTick / ReportSessionCompletion）。
            // 这两个键现在只用来让"目标账户里仍部署着的旧版代理"闭嘴 —— 它的通知在主控端
            // 看不到，纯属噪音。所以这里**固定发 false**，不跟随设置开关。
            NotifyOnTaskDone = false,
            NotifyOnAllDone = false,
        };

        bridge.EnsureDirectory();
        if (!bridge.WriteCommand(command))
        {
            AppendLog($"[通道：{tag}] ✗ 指令下发失败：{bridge.LastError}");
            _ = DialogHelper.ShowMessageAsync("下发失败",
                $"通道「{tag}」无法写入共享指令文件：{bridge.CommandPath}\n\n" +
                "请以管理员身份运行Yomori，或把程序放在所有用户都有写权限的目录。");

            // 这条通道不再等结果：标记收尾，让整轮计数能正常归零
            session.MarkCompletionReported();
            FinishRunPart();
            return;
        }

        // 复位桥里的状态并记下本指令的归属与下发时刻 ——
        // 之后的每次轮询都用它校验 status.json 是不是本指令写的，并从此刻起算启动宽限期。
        bridge.ResetStatus(command.Id);
        session.Begin(command.Id, tasks.Count, DateTimeOffset.Now);
        session.SetStatusText("正在连接目标会话…");
        MirrorSessionIfActive(session);

        AppendLog($"[通道：{tag}] 已下发远程执行指令 {command.Id}，共 {tasks.Count} 个任务，目标：{host} / {user}。");

        // 目标账户已经登录着就不再新建连接（仅 mstsc 模式）。
        // Windows 客户端版同时只允许一个交互式会话，重复连接会把目标账户
        // 正在用的那个桌面接管过来（当前桌面则被踢到锁屏），
        // 而这对"把任务跑起来"毫无帮助 —— 代理直接在那个已有会话里跑就行。
        // 内嵌模式例外：画面要嵌进窗口就必须真正建立连接（重连会接回同一会话，无副作用）。
        RdpSessionInfo? reusableSession = null;
        var hasReusableSession = !IsEmbeddedMode
            && isLocal
            && RdpSessionService.TryGetReusableSession(user, out reusableSession);

        // M6：内嵌画面按通道分家 —— 每条通道各有各的 client，不再限制"只有第一条能内嵌"。
        // 已连上的通道直接复用，未连的才新建（原生侧上限 YCN_MAX_SESSIONS = 8，与并发上限一致）。
        var usesEmbedded = IsEmbeddedMode && !HasSurface(channel.Id);
        var reuseEmbedded = IsEmbeddedMode && HasSurface(channel.Id);

        if (reuseEmbedded)
        {
            AppendChannelLog(session, "内嵌画面已连接，直接复用（该通道的画面在它自己的通道页上）。");
        }
        else if (hasReusableSession)
        {
            AppendLog(
                $"[{tag}] 检测到账户「{user}」已经登录（会话 {reusableSession!.Id}，{reusableSession.StateText}），" +
                "直接复用它，本次不再新建远程桌面连接。");
        }
        else if (usesEmbedded)
        {
            // 内嵌模式：FreeRDP 连接，画面嵌入窗口；会话建立后代理照常接管（桥/心跳零改动）
            if (!TryStartEmbeddedConnect(channel.Id, host, user, channel.DesktopWidth, channel.DesktopHeight, out var embedMessage))
            {
                _ = DialogHelper.ShowMessageAsync("连接失败", embedMessage);
                session.SetStatusText("连接失败");
                MirrorSessionIfActive(session);
                session.MarkCompletionReported();
                FinishRunPart();
                return;
            }

            AppendChannelLog(session, "内嵌模式下任务画面显示在该通道页里；也可以随时点全屏接管键盘操作。");
            AppendChannelLog(session, "提示：任务跑在目标会话里，断开内嵌连接等于「断开连接」——会话与进程保留，任务继续跑。");
        }
        else
        {
            // 用系统自带的远程桌面（mstsc）打开独立窗口。
            // .rdp 文件名带通道 id —— 否则两条通道会互相覆盖同一份会话文件。
            var started = RdpSessionService.Connect(
                host, out var connectMessage, channel.DesktopWidth, channel.DesktopHeight, channel.Id);

            if (!started)
            {
                _ = DialogHelper.ShowMessageAsync("连接失败", connectMessage);
                session.SetStatusText("连接失败");
                MirrorSessionIfActive(session);
                session.MarkCompletionReported();
                FinishRunPart();
                return;
            }

            AppendLog($"[通道：{tag}] {connectMessage}");
            if (isLocal)
            {
                AppendLog($"[通道：{tag}] 提示：Windows 客户端版同时只允许一个交互式会话，连接后当前桌面会被锁定，任务在目标账户中执行。");
                AppendLog($"[通道：{tag}] 任务跑在目标会话里：关闭远程桌面窗口时请选「断开连接」（会话和进程会保留，任务继续跑），不要选「注销」—— 注销会销毁会话，代理与脚本会被系统一并终止。");
            }
            else
            {
                AppendLog($"[通道：{tag}] 提示：远程目标需要一个双方都能读写的共享目录来传递指令与进度，Agent 会在对方机器上自动接管任务。");
            }
        }

        IsRdpBusy = true;
        MirrorSessionIfActive(session);

        await WaitForChannelReadyAsync(session, isLocal, hasReusableSession, reusableSession);

        RefreshRdpReadiness();
    }

    /// <summary>
    /// 等这条通道"可执行"：本机目标先等会话建立，再等目标会话里的代理上线；
    /// 远程目标等不到（会话不在本机），交给 status.json 轮询反映真实进度。
    /// </summary>
    private async Task WaitForChannelReadyAsync(
        RdpChannelSession session, bool isLocal, bool hasReusableSession, RdpSessionInfo? reusableSession)
    {
        var tag = session.DisplayName;
        var user = session.TargetUser;
        var channel = session.Channel!;

        if (!isLocal)
        {
            IsRdpBusy = false;
            session.SetStatusText($"已连接 {channel.Host}，等待对方会话代理接管…");
            MirrorSessionIfActive(session);
            AppendLog($"[通道：{tag}] 远程目标无法在本机枚举会话，以下方回传的 status.json 为准；若长时间无进展，请检查对方是否已部署会话代理并指向同一共享目录。");
            return;
        }

        var timeout = RdpSettings.ConnectTimeoutSeconds;

        // 本机目标：先等目标会话真的建立起来（WTS 能枚举到）
        var connected = await Task.Run(() => RdpSessionService.WaitForSession(
            user,
            timeout,
            CancellationToken.None));

        if (!connected)
        {
            IsRdpBusy = false;
            session.SetStatusText("等待目标会话超时");
            MirrorSessionIfActive(session);
            AppendLog($"[通道：{tag}] 等待目标会话超时（{timeout} 秒）。请确认凭据正确、目标账户允许远程登录，或手动在远程桌面窗口里完成登录。");
            return;
        }

        var created = reusableSession ?? RdpSessionService.FindSession(user);
        session.SetStatusText($"目标会话已就绪（会话 {created?.Id}），正在等待会话代理上线…");
        MirrorSessionIfActive(session);
        AppendLog(hasReusableSession
            ? $"[{tag}] 复用已有会话 {created?.Id}，无需等待登录。"
            : $"[{tag}] 目标账户会话已建立。");
        AppendLog($"[通道：{tag}] 会话代理会随目标账户登录自动启动并常驻等待指令，正在确认它是否在线…");

        // 【不再隔空拉起】代理随登录自启且常驻，主控端只负责确认它上线。
        // 复用已有会话这一路同样要确认 —— 账户本来就登录着，启动项不会重跑，代理很可能不在线。
        var online = await Task.Run(() => RdpSessionService.WaitForAgentOnline(
            user,
            channel.BridgePath,
            timeout,
            CancellationToken.None));

        IsRdpBusy = false;

        if (online)
        {
            session.SetStatusText("会话代理已上线，等待接管任务…");
            MirrorSessionIfActive(session);
            AppendLog($"[通道：{tag}] 已确认会话代理在线，任务将自动交接给它。");
            return;
        }

        session.SetStatusText("目标账户已登录，但会话代理没有上线");
        MirrorSessionIfActive(session);
        AppendLog($"[通道：{tag}] 等待会话代理上线超时（{timeout} 秒）：目标账户虽然在会话里，但代理没有随之启动。");
        AppendLog($"[通道：{tag}] 原因：代理只在目标账户「登录」那一刻由启动项拉起；账户本来就登录着时，启动项不会重跑，代理也就不在。");
        _ = PromptLogoffAndReconnectAsync(user);
    }
    /// <summary>
    /// 代理没上线时的可操作引导：把「注销目标账户 → 重新连接」这条正解摆到用户面前。
    ///
    /// 为什么必须注销后重连：代理只在目标账户登录那一刻由公共启动目录的快捷方式拉起。
    /// 账户本来就登录着时，启动项不会重跑，任何"隔空拉起"的旁路都已经删除，
    /// 唯一能让代理起来的方式就是让账户重新登录一次。
    /// 注销直接复用现有的 WTS 注销能力（<see cref="RdpSessionService.LogoffSession"/>）。
    /// </summary>
    /// <param name="targetUser">出问题的通道的目标账户；不传则用设置页当前配置的账户。</param>
    private async Task PromptLogoffAndReconnectAsync(string? targetUser = null)
    {
        try
        {
            var result = await DialogHelper.ShowMessageAsync(
                "目标账户已登录，但会话代理没有上线",
                "目标账户已登录，但会话代理没有上线。\n\n" +
                "请用「注销目标账户」后重新连接一次 —— 登录时启动项会自动拉起代理。\n\n" +
                "原因：代理只在目标账户「登录」那一刻由启动项拉起；账户本来就登录着时，启动项不会重跑，代理也就不在。",
                closeText: "稍后再说",
                primaryText: "注销目标账户");

            if (result != Microsoft.UI.Xaml.Controls.ContentDialogResult.Primary)
            {
                AppendLog("已选择稍后处理；代理上线前任务不会被接管。");
                return;
            }

            var session = RdpSessionService.FindSession(targetUser ?? RdpSettings.TargetUser);
            if (session is null)
            {
                AppendLog("注销目标账户失败：找不到目标账户的会话，可能已经断开或注销。");
                return;
            }

            if (RdpSessionService.LogoffSession(session.Id, out var message))
            {
                AppendLog($"{message}请重新点击「连接并执行」——目标账户登录时启动项会自动拉起代理。");
            }
            else
            {
                AppendLog(message);
            }

            RefreshRdpReadiness();
        }
        catch (Exception ex)
        {
            AppendLog($"注销引导对话框失败：{ex.Message}");
        }
    }

    // ---------------- 状态轮询 ----------------

    private void StartRdpPolling()
    {
        _rdpPollTimer?.Stop();
        _rdpPollTimer = _dispatcher.CreateTimer();
        _rdpPollTimer.Interval = TimeSpan.FromSeconds(1);
        _rdpPollTimer.Tick += (_, _) => PollRdpStatus();
        _rdpPollTimer.Start();
    }

    private void StopRdpPolling()
    {
        _rdpPollTimer?.Stop();
        _rdpPollTimer = null;
    }

    /// <summary>
    /// 每秒遍历**全部通道**的桥。每条通道各自校验指令归属、各自判定完成 ——
    /// 这正是多通道并行与改造前"全局单例轮询"的根本差别。
    /// </summary>
    private bool _rdpPollBusy;

    private async void PollRdpStatus()
    {
        if (_sessions.Count == 0)
        {
            return;
        }

        if (_rdpPollBusy) return;
        _rdpPollBusy = true;
        try
        {
            foreach (var session in _sessions.ToList())
            {
                if (!session.IsRemote || !session.HasCommand) continue;
                var commandId = session.CommandId!;
                ServiceStopRequest(session, DateTimeOffset.Now);
                var offset = session.LiveLogByteOffset;
                var snapshot = await Task.Run(() =>
                {
                    var ok = session.Bridge.TryReadLiveLogs(commandId, ref offset, out var logs);
                    var status = session.Bridge.TryReadStatus();
                    if (status?.CommandId == commandId && (status.Phase is "done" or "stopped" or "error" ||
                        status.Events.Any(e => e.Kind == RdpEventKinds.Finished)))
                    {
                        // The agent flushes before Finished. Drain the remaining bounded backlog.
                        for (var i = 0; i < 64; i++)
                        {
                            var before = offset;
                            if (!session.Bridge.TryReadLiveLogs(commandId, ref offset, out var batch)) break;
                            ok = true;
                            logs.AddRange(batch);
                            if (logs.Count > RdpChannelSession.MaxExternalLogLines)
                                logs.RemoveRange(0, logs.Count - RdpChannelSession.MaxExternalLogLines);
                            if (offset == before) break;
                        }
                    }
                    return (Status: status, Logs: logs, Ok: ok);
                });
                await RunOnUiThreadAsync(() =>
                {
                    if (!_sessions.Contains(session) || session.CommandId != commandId) return;
                    if (snapshot.Ok) session.ApplyLiveLogs(commandId, offset, snapshot.Logs);
                    if (snapshot.Status is not null)
                    {
                        var tick = session.Tick(snapshot.Status, DateTimeOffset.Now, RdpSettings.ConnectTimeoutSeconds);
                        ApplyChannelTick(session, tick);
                    }
                });
            }
            await RunOnUiThreadAsync(() => { if (_activeSession is not null) MirrorActiveSession(); });
        }
        catch (Exception ex) { AppendLog($"读取通道状态失败：{ex.Message}"); }
        finally { _rdpPollBusy = false; }
    }

    /// <summary>
    /// 本地执行路径下读到的外部脚本日志行。
    ///
    /// 做两件事，用途不同：
    ///   ① 落进 <c>external_logs/local/local-YYYY-MM-DD.log</c>（事后排障）；
    ///   ② 追加到「本地执行」组的独立实时日志列表（界面实时看）。
    ///
    /// TRACE/DEBUG 级不落盘也不上屏，理由见 <see cref="ExternalLogFilter.ShouldArchive"/>；
    /// 被省略的行按批补一条摘要，避免让人误以为脚本没输出。
    ///
    /// 不弹通知 —— 日志是高频流，一条一弹等于刷屏；通知只留给任务完成这类里程碑。
    /// </summary>
    private readonly ConcurrentQueue<(RdpChannelSession Session, TaskExternalLogLine Item)> _localLiveLogs = new();
    private int _localLiveLogPostPending;

    /// <summary>本地执行路径下累计被省略的 TRACE/DEBUG 行数（见 <see cref="ExternalLogFilter.ShouldArchive"/>）。</summary>
    private int _omittedLocalLogLines;

    private const int OmittedLocalLogReportBatch = 500;

    private void OnExternalLogLine(RdpChannelSession? session, TaskExternalLogLine item)
    {
        if (session is null || (item.Line.Text.Length == 0 && item.Line.NodeText.Length == 0)) return;
        var archivePath = ExternalLogStore.TodayFilePath(ExternalLogStore.LocalChannelTag, DateTime.Now);

        if (!ExternalLogFilter.ShouldArchive(item.Line.Level))
        {
            var omitted = Interlocked.Increment(ref _omittedLocalLogLines);
            if (omitted % OmittedLocalLogReportBatch == 0)
            {
                ExternalLogStore.Append(
                    archivePath,
                    $"[{FormatHelper.NowTimeText()}] [已省略累计 {omitted} 行 TRACE/DEBUG 日志" +
                    "（外部日志只保留 INFO 及以上；需要明细请设 " +
                    ExternalLogFilter.VerboseEnvironmentVariable + "=1 后重启）]");
            }

            return;
        }

        // 归档写节点档；上屏用当前视图文本（为空说明这一行只在节点档里存在）。
        var text = ExternalLogText(item.Line.ArchiveText, item.Line.Level);
        ExternalLogStore.Append(archivePath, $"[{FormatHelper.NowTimeText()}] [{item.TaskName}] {text}");
        if (item.Line.Text.Length == 0) return;
        _localLiveLogs.Enqueue((session, item));
        // The display queue is bounded independently from the archive.
        while (_localLiveLogs.Count > 4000) _localLiveLogs.TryDequeue(out _);
        if (Interlocked.Exchange(ref _localLiveLogPostPending, 1) == 0)
            Post(DrainLocalLiveLogs);
    }

    private void DrainLocalLiveLogs()
    {
        for (var i = 0; i < 500 && _localLiveLogs.TryDequeue(out var entry); i++)
        {
            var line = entry.Item.Line;
            entry.Session.AddExternalLogLine(ExternalLogText(line.Text, line.Level), line.Level == ExternalLogLevel.Error,
                line.Level.ToString().ToLowerInvariant(), entry.Item.TaskName);
        }
        Interlocked.Exchange(ref _localLiveLogPostPending, 0);
        if (!_localLiveLogs.IsEmpty && Interlocked.Exchange(ref _localLiveLogPostPending, 1) == 0)
            _dispatcher.TryEnqueue(DrainLocalLiveLogs);
    }

    /// <summary>给日志文本加级别前缀（错误 / 警告），其余原样。</summary>
    private static string ExternalLogText(string text, ExternalLogLevel level) => level switch
    {
        ExternalLogLevel.Error => $"[错误] {text}",
        ExternalLogLevel.Warn => $"[警告] {text}",
        _ => text,
    };

    /// <summary>
    /// 把一次轮询结果落成用户可见的动作（日志 / 通知 / 收尾）。
    /// Session 只改状态、不产生副作用，副作用统一收在这里。
    /// </summary>
    private void ApplyChannelTick(RdpChannelSession session, RdpChannelTick tick)
    {
        var tag = session.DisplayName;

        // ① 归属不匹配：桥里还是上一轮指令的状态（或本指令尚未落地）→
        //    这是"等待代理接管"的正常过渡态，绝不报失联，也不采信它的进度与事件。
        if (tick.AwaitingAgent)
        {
            if (tick.AwaitingAgentFirstTime)
            {
                AppendLog($"[通道：{tag}] 等待目标会话中的代理接管（尚未收到本指令的心跳，属正常过渡）。");
            }

            if (tick.HeartbeatRecovered)
            {
                AppendLog($"[通道：{tag}] ✓ 会话代理心跳已恢复，任务继续运行。");
            }

            return;
        }

        // ② 事件
        foreach (var ev in tick.NewEvents)
        {
            AppendLog($"[通道：{tag}] {ev.Time} {ev.Headline}");

            // 通知只在这里（主控端）发：目标会话里弹的 Toast 在主控端桌面上看不到，
            // 所以代理侧已经完全不发通知了，全部由主控端轮询到事件后补发。
            //
            // 不做「窗口是否在前台」判断：Toast 由系统通知平台投递，与应用窗口焦点无关。
            // 真正会吞掉横幅的只有会话锁定、专注助手、系统设置里关掉本应用的横幅 ——
            // 这些都与焦点无关，按焦点拦截只会把「切到别的窗口」这种正常场景一起吞掉。
            if (ev.Kind == RdpEventKinds.TaskDone && RdpSettings.NotifyOnTaskDone)
            {
                var title = NotificationService.TaskDoneTitle(ev.Index, ev.Total, ev.IsAbnormal);
                var body = NotificationService.TaskDoneBody(ev.TaskName, ev.ElapsedSeconds);

                // 投递失败 = 通知注册本身就是坏的（见 NotificationService），只提醒一次。
                if (!NotificationService.Show(title, body))
                {
                    WarnNotificationOnce();
                }
            }
        }

        // 心跳恢复要补一条日志（否则日志会永远停在"断开连接"，用户翻日志只看到误报）
        if (tick.HeartbeatRecovered)
        {
            AppendLog($"[通道：{tag}] ✓ 会话代理心跳已恢复，任务继续运行。");
        }

        // ③ 失联：只报一次，避免每秒刷屏
        if (tick.JustWentStale)
        {
            var summary =
                $"任务「{session.TaskDisplay}」停在 {session.Progress} / {session.Total}（已运行 {session.ElapsedText}）";

            session.MarkStale();

            AppendLog($"[通道：{tag}] ⚠ 已超过 {RdpHeartbeat.TimeoutSeconds} 秒没收到会话代理心跳。最后状态：{summary}。");
            AppendLog($"[通道：{tag}] 目标会话很可能已经断开或被注销 —— 会话里的代理与脚本进程会随之终止，所以进度不再更新。");
            AppendLog($"[通道：{tag}] 如果是你主动断开的远程桌面：请用「断开连接」而不是「注销目标账户」，任务才能在后台继续跑。");

            if (RdpSettings.NotifyOnChannelDone)
            {
                NotificationService.ShowSummary($"Yomori：通道「{tag}」代理失联", $"最后状态：{summary}");
            }
        }

        // ④ 完成（幂等：一轮指令只收尾一次，事件通道与终态相位可能同时命中）
        if (tick.CompletionSignaled)
        {
            ReportSessionCompletion(session, tick);
            return;
        }

        // ⑤ 停止请求的回执：代理把相位切成 stopping，或回了"收到停止请求"的日志事件。
        //    拿到回执就不再重发，界面文案也跟着切成"正在收尾"。
        if (session.StopPending && !session.StopAcked && StopAckedBy(tick))
        {
            session.MarkStopAcked();
            AppendLog($"[通道：{tag}] 目标会话的代理已收到停止请求，正在收尾。");
        }

        // ⑥ 已请求停止但还没收尾：把文案固定住 ——
        //    否则下一拍就会被代理回传的"运行中"盖回去，用户以为点击没生效。
        if (session.StopPending)
        {
            session.SetStatusText(session.StopAcked
                ? "已请求停止，目标会话正在收尾…"
                : "已请求停止，等待目标会话确认…");
        }
    }

    /// <summary>
    /// 本拍是否表明目标代理已经收到停止请求。
    /// 相位 stopping 是代理的显式回执；日志事件是兜底（老一点的实现只写事件不改相位）。
    /// </summary>
    private static bool StopAckedBy(RdpChannelTick tick)
    {
        if (string.Equals(tick.Status?.Phase, "stopping", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        foreach (var ev in tick.NewEvents)
        {
            if (ev.Message.Contains("收到停止请求") || ev.Message.Contains("收到紧急停止请求"))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 停止请求没回执就补发 —— 之前只写一次 stop.json，之后完全不管：
    /// 代理漏读（正卡在清理里）、或还是旧副本（不认识 stop.json）时，
    /// 用户点完「停止执行」就再也等不到任何反馈，只能干看着任务继续跑。
    /// </summary>
    private void ServiceStopRequest(RdpChannelSession session, DateTimeOffset now)
    {
        if (!session.NeedsStopResend(now))
        {
            return;
        }

        if (!session.WriteStop(session.StopEmergency, out var error))
        {
            AppendChannelLog(session, $"⚠ 重发停止请求失败：{error}");
            return;
        }

        session.MarkStopResent(now);
        var tag = session.DisplayName;
        var times = session.StopSendCount;

        if (session.StopGiveUp)
        {
            AppendLog($"[通道：{tag}] ⚠ 已下发 {times} 次停止请求，目标会话的代理始终没有回执。");
            AppendLog($"[通道：{tag}] 大概率是目标会话里跑的还是旧版本代理（不认识 stop.json）。");
            AppendLog($"[通道：{tag}] 请在设置页重新「部署会话代理」，然后注销目标账户再重新连接一次。");
            return;
        }

        AppendLog($"[通道：{tag}] 停止请求暂未收到目标代理的回执（第 {times} 次下发），继续等待。");
    }

    /// <summary>
    /// 一条通道的远程执行收尾 —— "跑完了"这件事的全部用户感知都在这。
    ///
    /// 幂等：一整轮指令只收尾一次（完成信号可能来自 Finished 事件，也可能来自终态相位，
    /// 两者可能同时命中，必须保证「汇总通知 / 结束日志 / 刷统计」只做一次）。
    /// 多通道下每条通道各自收尾，收尾完一条才减计数，全部减完才算整轮结束。
    /// </summary>
    /// <param name="session">收尾的通道。</param>
    /// <param name="tick">完成那一刻的轮询结果（用于取结束文案与异常标记）。</param>
    private void ReportSessionCompletion(RdpChannelSession session, RdpChannelTick tick)
    {
        if (session.CompletionReported)
        {
            return;
        }

        session.MarkCompletionReported();

        var status = tick.Status;
        var tag = session.DisplayName;

        // 汇总正文优先取 Finished 事件的 Message。不要用 status.StatusText ——
        // 常驻代理 1Hz 的 idle 心跳会把它覆盖成「代理在线，等待指令。」，
        // 日志里已实证这个竞态真的命中过（汇总通知正文变成了一句废话）。
        var summary = status?.Events.LastOrDefault(e => e.Kind == RdpEventKinds.Finished)?.Message;
        if (string.IsNullOrWhiteSpace(summary))
        {
            summary = string.IsNullOrWhiteSpace(status?.StatusText) ? "全部任务结束" : status!.StatusText;
        }

        // 收尾方式由本通道的收尾任务决定（收尾行恒在末位，一条通道只有一条）。
        // 与代理侧同一个纯函数，两边不会各说一套。
        var finishMode = TaskTypeResolver.ResolveSessionFinish(
            TaskScopePlanner.TasksForScope(Tasks, session.ChannelId).Where(t => t.Enabled));

        // 「手动停止」不是异常：代理对停止请求同样以 abnormal 收尾
        // （旧副本更是直接把停止写成「已完成（有异常）」）。
        // 主控端自己下发过停止请求，就按"停止"记 —— 与本地执行组的判定保持一致。
        var stopped = session.StopRequested;
        var abnormal = tick.Abnormal && !stopped;

        if (stopped)
        {
            summary = "已手动停止（本轮未跑完）";
        }

        // 异常时把"哪个任务、为什么"一并收回：状态栏与运行日志都要能说出来
        var reasons = abnormal ? CollectSessionAbnormalReasons(session, status) : null;

        // 只登记这块**自己的**结果，暂不定终态 ——
        // 还有别的块在跑时看板娘必须继续"工作"，终态由 FinalizeRunSummary 取最差统一决定。
        _runParts.Add(new RunPartResult($"通道「{tag}」", true, abnormal, summary, reasons, stopped));

        if (RdpSettings.NotifyOnChannelDone)
        {
            var title = $"Yomori：通道「{tag}」任务结束";

            // 直接投递，不看窗口焦点（理由同 ApplyChannelTick 里的任务完成通知）。
            // 投递失败 = 通知注册坏了（桌面横幅不会出现），这时只能退到应用内提示。
            if (!NotificationService.ShowSummary(title, summary))
            {
                WarnNotificationOnce();
            }
        }

        AppendChannelLog(session, $"远程执行结束：{summary}（会话处理方式：{SessionFinishModes.Label(finishMode)}）。");

        FinishRunPart();
    }

    /// <summary>
    /// 从一条通道本轮的事件流里捞出异常任务的结束原因。
    ///
    /// <para>代理的 Finished 事件正文只有「已完成（有异常）」这一句 —— 那是结论、不是原因。
    /// 真正的原因在带 <c>IsAbnormal</c> 的 TaskDone 事件里（`启动失败，已跳过` /
    /// `脚本不存在，已跳过` / `超时处理：…`），任务名单独放在 <c>TaskName</c> 上。</para>
    ///
    /// <para>优先读主控端累积的 <see cref="RdpChannelSession.Events"/>（它跨 idle 心跳保留），
    /// 拿不到再退回本次 tick 的快照；两者都没有就明说"没有具体原因"，
    /// 而不是拿收尾语冒充原因。</para>
    /// </summary>
    private static IReadOnlyList<string> CollectSessionAbnormalReasons(RdpChannelSession session, RdpStatus? status)
    {
        IEnumerable<RdpTaskEvent> source = session.Events.Count > 0
            ? session.Events
            : status?.Events ?? new List<RdpTaskEvent>();

        var reasons = new List<string>();
        string? finished = null;

        foreach (var ev in source)
        {
            if (!ev.IsAbnormal)
            {
                continue;
            }

            if (ev.Kind == RdpEventKinds.Finished)
            {
                finished ??= ev.Message;
                continue;
            }

            if (ev.Kind != RdpEventKinds.TaskDone)
            {
                continue;
            }

            var name = string.IsNullOrWhiteSpace(ev.TaskName) ? "（未知任务）" : ev.TaskName.Trim();
            var detail = string.IsNullOrWhiteSpace(ev.Message) ? "异常结束" : ev.Message.Trim();
            var line = $"任务「{name}」{detail}";

            if (!reasons.Contains(line) && reasons.Count < RunSummary.MaxReasons)
            {
                reasons.Add(line);
            }
        }

        if (reasons.Count == 0)
        {
            reasons.Add(string.IsNullOrWhiteSpace(finished)
                ? "代理没有上报具体原因（详见「运行日志」里该通道的输出）"
                : $"代理只报告了「{finished.Trim()}」，没有具体任务原因（详见「运行日志」里该通道的输出）");
        }

        return reasons;
    }

    /// <summary>
    /// 一条通道收尾后的交接：还有别的通道在跑就把界面切到那条，全跑完才停轮询、落终态。
    /// </summary>
    private void FinishChannel(RdpChannelSession session)
    {
        if (!ReferenceEquals(_activeSession, session) || _pendingRuns <= 0)
        {
            return;
        }

        // 正在展示的这条已结束 → 切到还没结束的那条，用户不至于盯着一个静止的卡片
        var next = _sessions.FirstOrDefault(s => s.IsRemote && s.HasCommand && s != session);
        if (next is not null)
        {
            ActiveSession = next;
        }
    }

    /// <summary>
    /// 本轮执行的一块（本地执行算一块，每条远程通道各算一块）跑完。
    /// 归零才停轮询、刷终态、放行自动退出 —— 多通道并行时"一部分结束"不等于"整轮结束"。
    /// </summary>
    private void FinishRunPart()
    {
        _pendingRuns = Math.Max(0, _pendingRuns - 1);

        if (_sessions.Count > 0 && _pendingRuns > 0)
        {
            // 还有通道/本地任务在跑：只把已结束的那条切走，别停轮询
            var finished = _sessions.FirstOrDefault(s => s.IsRemote && !s.HasCommand && s.CompletionReported);
            if (finished is not null)
            {
                FinishChannel(finished);
            }

            return;
        }

        StopRdpPolling();
        IsRdpBusy = false;
        IsRunning = false;
        _runEndedAt = DateTimeOffset.Now;
        RefreshStats();
        RefreshRdpReadiness();

        // 整轮结束：清掉活跃指针（菜单项与事件列表留给用户看，见计划书 D3）
        RaiseRunFinished();

        // 定终态 + 出汇总（看板娘取最差、一条汇总通知、逐块汇总日志）
        FinalizeRunSummary();

        RaisePanelChanged();

        if (Config.AutoExitAfterDone && !_runStoppedOrError)
        {
            AppendLog("已启用任务完成后自动退出程序，程序即将退出。");
            var timer = _dispatcher.CreateTimer();
            timer.Interval = TimeSpan.FromMilliseconds(1500);
            timer.Tick += (_, _) =>
            {
                timer.Stop();

                // 必须走 RequestExit 而不是 Close()：装了关闭拦截之后，Close() 会被
                // "关闭Yomori？"弹窗拦住 —— 开了自动退出的用户每次都会看到一个莫名其妙的确认框，
                // 而且窗口若收在状态栏里，那个弹窗压根看不见，程序就永远退不掉了。
                App.MainWindow?.RequestExit();
            };
            timer.Start();
        }
    }

    /// <summary>整轮执行结束（本地 + 全部通道）时通知界面刷新。</summary>
    private void RaiseRunFinished()
    {
        OnPropertyChanged(nameof(HasRemoteSession));
        RefreshRdpReadiness();
    }

    /// <summary>
    /// 把"上一轮 / 本轮"的汇总状态整体归零（两个启动入口共用）。
    /// 任何一项漏掉都会让上一轮的"有异常"残留到新的一轮（设置页单通道入口踩过）。
    /// </summary>
    private void ResetRunSummaryState()
    {
        _runTaskErrorCount = 0;
        _runParts.Clear();
        _localAbnormalReasons.Clear();
        _lastRunAbnormalReasons.Clear();
        _lastRunAbnormal = false;
        _lastRunStopped = false;
    }

    /// <summary>
    /// 整轮结束定终态：**取最差**（任一异常 → 有异常）+ 一条汇总通知 + 逐块汇总日志 + 异常原因明细。
    ///
    /// 为什么不在各块收尾时直接定：多通道并行时各块完成时间不同，
    /// A 跑完就落终态会让用户以为整轮结束（而 B 其实还在跑）。
    /// 所以各块只往 <see cref="_runParts"/> 里登记结果，真正的终态在这里一次性决定。
    ///
    /// 单块（只有本地、或只有一条通道）时不发汇总通知 —— 它和按块的收尾反馈完全重复；
    /// 但**异常原因明细照写日志**：那正是用户缺的信息，越少块越不该省。
    /// </summary>
    private void FinalizeRunSummary()
    {
        var parts = _runParts.ToList();
        _runParts.Clear();

        if (parts.Count == 0)
        {
            _lastRunAbnormal = false;
            _lastRunStopped = false;
            _lastRunAbnormalReasons.Clear();
            return;
        }

        // 多块时状态面板要显示"已完成（有异常）"—— 这里定下来，面板是计算属性、不自己判
        _lastRunAbnormal = RunSummary.HasAnyAbnormal(parts);
        _lastRunStopped = parts.Any(p => p.Stopped);

        // 异常原因一次性定下来：状态栏读它、运行日志写它、悬浮提示展开它。
        // 只写「已完成（有异常）」等于只说"出问题了"，用户仍然不知道是哪条通道、哪个任务、为什么。
        _lastRunAbnormalReasons.Clear();
        if (_lastRunAbnormal)
        {
            _lastRunAbnormalReasons.AddRange(RunSummary.ComposeReasons(parts));
            foreach (var line in _lastRunAbnormalReasons)
            {
                AppendLog("[异常] " + line);
            }
        }

        if (parts.Count > 1)
        {
            AppendLog($"[汇总] {RunSummary.ComposeHeadline(parts)}");
            foreach (var line in RunSummary.ComposeLines(parts))
            {
                AppendLog("[汇总] " + line);
            }

            if (RdpSettings.NotifyOnAllDone)
            {
                var body = RunSummary.ComposeHeadline(parts);

                // 直接投递，不看窗口焦点（理由同上）。
                if (!NotificationService.ShowSummary(RunSummary.Title, body))
                {
                    WarnNotificationOnce();
                }
            }
        }

    }

    /// <summary>系统通知注册不可用这件事只提示一次，免得每条任务完成都往日志里灌一行。</summary>
    private bool _notificationWarned;

    private void WarnNotificationOnce()
    {
        if (_notificationWarned)
        {
            return;
        }

        _notificationWarned = true;
        var reason = NotificationService.LastError;
        AppendLog(reason.Length > 0
            ? $"⚠ 系统通知不可用，完成通知只会显示为应用内气泡（原因：{reason}）。"
            : "⚠ 系统通知不可用（未注册成功），完成通知只会显示为应用内气泡。");
    }

    /// <summary>本地执行时每完成一个任务也发通知（由设置开关控制）。</summary>
    private void OnRunnerTaskCompleted(object? sender, TaskCompletedInfo info) => Post(() =>
    {
        AppendLog($"任务完成：{info.Name}（{FormatHelper.FormatSeconds(info.ElapsedSeconds)}）{info.Status}");

        // 异常任务的结束原因就地留一份：本地组收尾时并进整轮汇总，
        // 让状态栏的「有异常」能说出是哪个任务、为什么（原来只剩一个计数）。
        if (info.IsAbnormal)
        {
            AddLocalAbnormalReason(
                $"任务「{info.Name}」{(string.IsNullOrWhiteSpace(info.Status) ? "异常结束" : info.Status.Trim())}");
        }

        if (RdpSettings.NotifyLocalTaskDone)
        {
            NotificationService.ShowTaskDone(info.Name, info.Index, info.Total, info.ElapsedSeconds, info.IsAbnormal);
        }
    });

    /// <summary>记一条本地异常原因（去重、有上限）。并发组下可能从工作线程回调，但外部已 <c>Post</c> 到 UI 线程。</summary>
    private void AddLocalAbnormalReason(string reason)
    {
        if (string.IsNullOrWhiteSpace(reason) || _localAbnormalReasons.Contains(reason))
        {
            return;
        }

        if (_localAbnormalReasons.Count >= RunSummary.MaxReasons)
        {
            return;
        }

        _localAbnormalReasons.Add(reason);
    }
}
