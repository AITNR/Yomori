// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Foundation;
using Windows.Graphics;
using Yomori.Helpers;
using Yomori.Models;
using Yomori.Services;
using Yomori.ViewModels;
using Yomori.Views;

namespace Yomori;

public sealed partial class MainWindow : Window
{
    /// <summary>
    /// 拖拽改大小/挪位置时 AppWindow.Changed 会连续触发，这里等手停下来再落盘，
    /// 免得一次拖拽往 config.json 写几十遍。
    /// </summary>
    private const int PlacementSaveDebounceMs = 800;

    private readonly DispatcherQueueTimer _placementSaveTimer;


    // ---------------- 关闭按钮 / 状态栏 ----------------

    /// <summary>状态栏图标（首次隐藏时创建；从未收进托盘的用户不会多一个图标）。</summary>
    private TrayIcon? _tray;

    /// <summary>程序自己发起的退出（自动退出 / 托盘菜单退出）。为 true 时关闭拦截一律放行。</summary>
    private bool _exiting;

    /// <summary>收到 WM_QUERYENDSESSION：系统要关机/注销，必须放行，不能拦。</summary>
    private bool _systemShuttingDown;

    /// <summary>关闭确认弹窗已经开着（连点关闭时不重复弹）。</summary>
    private bool _closeDialogOpen;

    /// <summary>窗口当前是不是"收在状态栏里"（而不是被用户最小化到任务栏）。</summary>
    private bool _hiddenToTray;

    /// <summary>隐藏前是否最大化，恢复时照原样还原。</summary>
    private bool _wasMaximizedBeforeHide;

    /// <summary>"已收进状态栏"的提示只发一次（会话内），避免每次隐藏都弹一条。</summary>
    private bool _trayHintShown;

    /// <summary>"任务跑完自动退出"的延时器（智能模式）。</summary>
    private DispatcherQueueTimer? _autoExitTimer;

    /// <summary>
    /// 正在播放的「子菜单入场动画」。
    ///
    /// <para>🔑 必须挂字段保活。Storyboard 不自持引用：<c>board.Begin()</c> 返回后，
    /// 若没人引用它，它连同动画时钟一起可能被回收，而元素上的动画状态不会跟着清干净 ——
    /// 之后命中测试调用 <c>CUIElement::GetLocalTransform</c> 算这个元素的变换时，
    /// 读到的就是失效对象（2026-10-27 两次崩溃的栈顶就是这个函数）。</para>
    /// </summary>
    private readonly List<Storyboard> _menuEntranceBoards = new();

    /// <summary>
    /// 上一次生成的子菜单内容签名。内容没变就不重建 —— 跑任务时
    /// <c>SessionsChanged</c> 会频繁触发，反复清空重建会让 NavigationView 内部
    /// 不停销毁/重建这些项，既闪，也把动画状态带进已脱离可视树的元素里。
    /// </summary>
    private string _channelMenuSignature = string.Empty;

    /// <summary>子菜单重建是否已排队（合并同一拍内的多次触发）。</summary>
    private bool _menuRebuildQueued;

    /// <summary>窗口是否收在状态栏里（供外部判断，例如单实例激活时决定要不要 Show）。</summary>
    public bool IsHiddenToTray => _hiddenToTray;

    public MainWindow()
    {
        InitializeComponent();

        Title = "Yomori";
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        SystemBackdrop = new MicaBackdrop();

        // 记忆上次窗口大小：先把上次记下的位置/尺寸恢复到当前显示器工作区内，
        // 之后再监听变化持续记录（恢复过程本身不触发写盘，注册顺序即是这个意思）。
        RestoreWindowPlacement();

        _placementSaveTimer = DispatcherQueue.CreateTimer();
        _placementSaveTimer.Interval = TimeSpan.FromMilliseconds(PlacementSaveDebounceMs);
        _placementSaveTimer.IsRepeating = false;
        _placementSaveTimer.Tick += (_, _) => PersistWindowPlacement();

        AppWindow.Changed += OnAppWindowChanged;

        // 关闭按钮不再等于退出：先按策略决定"收进状态栏"还是"真退"。
        // 注意这是**同步**事件，处理器里不能 await —— 处理方式是先 Cancel 拦下来，再去问。
        AppWindow.Closing += OnAppWindowClosing;

        try
        {
            var iconPath = System.IO.Path.Combine(Services.AppPaths.AssetsDir, "icon.ico");
            if (System.IO.File.Exists(iconPath))
            {
                AppWindow.SetIcon(iconPath);
            }

            // 标题栏左侧徽标。用 png 而不是 icon.ico：22px 下多尺寸 ico 的选档由
            // Windows 自己决定，容易挑到细节糊掉的那一档；png 交给 XAML 缩放更可控。
            var logoPath = System.IO.Path.Combine(Services.AppPaths.AssetsDir, "logo.png");
            if (System.IO.File.Exists(logoPath))
            {
                TitleBarLogo.Source = new BitmapImage(new Uri(logoPath, UriKind.Absolute));
            }
        }
        catch
        {
            // 图标缺失不应阻止启动
        }

        VM.ShutdownPromptRequested += OnShutdownPromptRequested;
        VM.RequestNavigation += (_, tag) => NavigateTo(tag);

        // 会话通道（M4 / 计划书 §8.1）：「会话通道」是**父项**，下面挂子菜单 ——
        //   父项本身        = 只负责展开/收起子菜单，点击**不导航**（否则和子项重复）；
        //   子项「多画面」  = 多画面页（全部通道的网格），唯一的菜单入口；
        //   子项「通道名」  = 该通道单独的画面页；
        //   子项「通道管理…」= 配置 / 凭据 / 部署代理 / 预检。
        // 子项按「配置里启用的通道 + 本轮在跑的通道」重建，配置一改就跟着变。
        //
        // ⚠️ 三条信号缺一不可：
        //   SessionsChanged        = 本轮会话集合变了（开始/结束）；
        //   Channels.CollectionChanged = 通道管理页增删通道（ReloadChannels 重建镜像）；
        //   ChannelMenuChanged     = 只改了某条通道的 enabled（任务执行页那个开关），
        //                            集合一点没动 —— 少了它就会出现「通道关掉了，左下角还挂着」。
        VM.SessionsChanged += (_, _) => RebuildChannelMenuItems();
        VM.ChannelMenuChanged += (_, _) => RebuildChannelMenuItems();
        VM.PropertyChanged += OnViewModelPropertyChanged;
        VM.Channels.CollectionChanged += (_, _) => RebuildChannelMenuItems();

        // M5：窗口关闭时释放内嵌连接（ycn_rdp_disconnect 语义 = 会话保留；原生事件循环线程须退出，
        // 否则非后台线程会拖住进程退出）
        Closed += (_, _) =>
        {
            // 关窗时把最后一次几何同步写掉：防抖定时器很可能还来不及触发
            _placementSaveTimer.Stop();
            PersistWindowPlacement();

            // 托盘图标必须在进程结束前摘掉：NIM_DELETE 没跑的话，
            // 通知区域会留一个点不动的僵尸图标，直到用户把鼠标划过去才消失。
            _autoExitTimer?.Stop();
            _autoExitTimer = null;

            // 状态栏计时器同理：窗口都没了还每秒取一次快照纯属白烧
            _sideStatusTimer?.Stop();
            _sideStatusTimer = null;

            _tray?.Dispose();
            _tray = null;

            VM.DisposeEmbedClient();
        };

        if (Content is FrameworkElement root)
        {
            root.KeyDown += OnRootKeyDown;
        }

        NavigateTo("home");

        // 子菜单先按当前配置铺一遍（配置里已启用的通道一启动就该出现在「会话通道」下面）
        RebuildChannelMenuItems();

        // 右侧执行状态栏：建行对象、恢复上次的展开/收起、起 1Hz 刷新
        InitializeSideStatus();

        // 通知铃铛：未读时每 2 秒轻摇一次（读完全部立刻停）
        InitializeNotificationBell();

        // 启动时按设置检查一次更新（后台跑，6 秒后开始；结论进铃铛）
        VM.BeginStartupUpdateCheck();


        // M5/M6 自检通道：自动导航到「通道管理」页（页面 Loaded 里自动连接、画面挂在本页）
        if (Program.EmbedVmArgs is not null)
        {
            DispatcherQueue.TryEnqueue(
                Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
                () => NavigateTo(ChannelMgmtTag));
        }
    }

    public MainViewModel VM => App.ViewModel;

    // ---------------- 窗口位置 / 尺寸记忆 ----------------

    /// <summary>
    /// 按 config.json 里记录的几何恢复窗口；首次运行（没记录过）则用默认尺寸并在主屏居中。
    /// </summary>
    private void RestoreWindowPlacement()
    {
        var saved = VM.Config.Window;

        // 记过位置就按那个坐标找显示器（多屏场景），否则用主屏
        DisplayArea? area = null;
        if (saved.X is not null && saved.Y is not null)
        {
            area = DisplayArea.GetFromPoint(
                new PointInt32(saved.X.Value, saved.Y.Value),
                DisplayAreaFallback.Nearest);
        }

        area ??= DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary);

        var work = area?.WorkArea ?? default;
        var placement = WindowPlacement.Resolve(
            saved.X,
            saved.Y,
            saved.Width,
            saved.Height,
            new WindowPlacement.Bounds(work.X, work.Y, work.Width, work.Height));

        AppWindow.MoveAndResize(new RectInt32(placement.X, placement.Y, placement.Width, placement.Height));

        if (AppWindow.Presenter is not OverlappedPresenter presenter)
        {
            return;
        }

        // 兜住"手一抖把窗口拖到 400x300"这种再也调不回来的情况；
        // 小屏上最小值不能超过工作区，否则窗口会被强制撑出屏幕。
        if (work.Width > 0)
        {
            presenter.PreferredMinimumWidth = Math.Min(WindowPlacement.MinWidth, work.Width);
        }

        if (work.Height > 0)
        {
            presenter.PreferredMinimumHeight = Math.Min(WindowPlacement.MinHeight, work.Height);
        }

        if (saved.Maximized)
        {
            presenter.Maximize();
        }
    }

    private void OnAppWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (!args.DidSizeChange && !args.DidPositionChange && !args.DidPresenterChange)
        {
            return;
        }

        // 重复调用即刷新倒计时：手停下 PlacementSaveDebounceMs 后才真正写盘
        _placementSaveTimer.Stop();
        _placementSaveTimer.Start();
    }

    /// <summary>
    /// 把当前窗口几何交给 ViewModel 落盘。
    /// 最小化状态的坐标是假的（约 -32000），跳过；最大化/全屏时只记"是不是最大化"，
    /// 保留上一次的还原尺寸。
    /// </summary>
    private void PersistWindowPlacement()
    {
        try
        {
            var presenter = AppWindow.Presenter as OverlappedPresenter;

            switch (presenter?.State)
            {
                case OverlappedPresenterState.Minimized:
                    return;
                case OverlappedPresenterState.Maximized:
                    VM.SaveWindowPlacement(null, maximized: true);
                    return;
                default:
                    var position = AppWindow.Position;
                    var size = AppWindow.Size;
                    VM.SaveWindowPlacement(
                        new WindowPlacement.Placement(position.X, position.Y, size.Width, size.Height),
                        maximized: false);
                    return;
            }
        }
        catch
        {
            // 记窗口几何失败不该影响用户操作
        }
    }

    // ---------------- 关闭按钮 / 状态栏 ----------------

    /// <summary>
    /// 关闭拦截。**先 Cancel，再去问** —— <see cref="AppWindow.Closing"/> 是同步事件，
    /// 处理器里不能 await，所以顺序只能是"先拦下来，再异步决定"。
    ///
    /// <para>放行的只有两种情况：程序自己发起的退出（<see cref="_exiting"/>）、
    /// 以及系统要关机/注销（<see cref="_systemShuttingDown"/>，由托盘窗口的
    /// <c>WM_QUERYENDSESSION</c> 置位）。后者绝不能拦 —— 拦了 Windows 会判定
    /// "此应用阻止关机"，用户看到的是关机卡住。</para>
    /// </summary>
    private void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_exiting || _systemShuttingDown)
        {
            // 系统关机/注销不会再走 RequestExit，自动保存的去抖窗口里的改动只能在这里落盘
            TryFlushAutoSave();
            return;
        }

        args.Cancel = true;

        // 弹窗已经开着时用户又点了一次关闭（或连点）：直接忽略这次，别再排一个
        if (_closeDialogOpen)
        {
            return;
        }

        _closeDialogOpen = true;
        _ = HandleCloseRequestAsync();
    }

    /// <summary>
    /// 按 <c>window.close_action</c> 决定这次关闭怎么处理。
    ///
    /// <para>三种策略直接执行；<c>ask</c> 才弹窗。弹窗里的勾选决定"要不要记住"以及"记成什么"，
    /// 具体规则全在 <see cref="CloseDecision"/>（纯函数，冒烟里覆盖）。</para>
    /// </summary>
    private async Task HandleCloseRequestAsync()
    {
        try
        {
            var action = WindowConfig.NormalizeCloseAction(VM.Config.Window.CloseAction);
            var outcome = CloseDecision.Decide(action, VM.IsRunning);

            if (outcome is CloseOutcome.Exit or CloseOutcome.HideToTray)
            {
                ApplyCloseOutcome(outcome);
                return;
            }

            var dialog = new CloseConfirmDialog(() => VM.IsRunning, VM.DescribeRunningForClose());
            var result = await DialogHelper.ShowAsync(dialog);

            // 取消（右上角叉 / Esc / 点空白）：什么都不做，窗口留着
            if (result == ContentDialogResult.None)
            {
                return;
            }

            var clicked = result == ContentDialogResult.Secondary ? CloseOutcome.Exit : CloseOutcome.HideToTray;

            // 勾了智能模式时按钮上写的是"当前状态下会发生的事"，这里按**点下去那一刻**的状态
            // 再算一次，避免弹窗开着时任务刚好结束导致动作和文案不一致。
            var actual = dialog.SmartMode
                ? CloseDecision.Decide(WindowConfig.CloseActionAuto, VM.IsRunning)
                : clicked;

            var persist = CloseDecision.PersistFor(clicked, dialog.RememberChoice, dialog.SmartMode);
            if (persist is not null)
            {
                VM.SaveCloseAction(persist);
            }

            ApplyCloseOutcome(actual);
        }
        finally
        {
            _closeDialogOpen = false;
        }
    }

    private void ApplyCloseOutcome(CloseOutcome outcome)
    {
        switch (outcome)
        {
            case CloseOutcome.Exit:
                RequestExit();
                break;
            case CloseOutcome.HideToTray:
                HideToTray();
                break;
        }
    }

    /// <summary>
    /// 创建状态栏图标（幂等）。失败返回 false，调用方必须回退到"最小化到任务栏"，
    /// **绝不能静默隐藏窗口** —— 那样用户会以为程序被关掉了，其实进程还在跑任务，再也找不回来。
    /// </summary>
    private bool EnsureTray()
    {
        if (_tray is { IsInstalled: true })
        {
            return true;
        }

        var iconPath = Path.Combine(AppPaths.AssetsDir, "icon.ico");
        var tray = new TrayIcon(iconPath);

        tray.ShowRequested += (_, _) => RestoreFromTray();
        tray.ExitRequested += (_, _) => RequestExit();
        tray.CloseBehaviorRequested += (_, _) =>
        {
            RestoreFromTray();
            NavigateTo("settings");
        };
        tray.StartStopRequested += (_, _) => OnTrayStartStop();

        // 系统关机/注销：这个标记必须由托盘窗口的 WM_QUERYENDSESSION 置位，
        // 因为 WinUI 的窗口没有可挂钩的 WndProc。
        tray.SystemSessionEnding += (_, _) => _systemShuttingDown = true;

        _tray = tray;

        if (tray.TryInstall())
        {
            UpdateTrayState();
            return true;
        }

        var reason = tray.LastError;
        tray.Dispose();
        _tray = null;

        VM.AppendLog($"⚠ 无法创建状态栏图标，本次改为最小化到任务栏：{reason}");
        return false;
    }

    /// <summary>同步托盘图标的提示与菜单文案（运行状态变化时调用）。</summary>
    private void UpdateTrayState()
    {
        if (_tray is not { IsInstalled: true })
        {
            return;
        }

        var running = VM.IsRunning;
        _tray.SetStartStopLabel(running);
        _tray.Tip = running ? "Yomori — 正在执行任务" : "Yomori — 空闲";
    }

    /// <summary>收进状态栏：窗口隐藏、进程继续跑。</summary>
    public void HideToTray()
    {
        if (_hiddenToTray)
        {
            return;
        }

        if (!EnsureTray())
        {
            // 托盘不可用时的退路：最小化。至少用户还能从任务栏点回来。
            if (AppWindow.Presenter is OverlappedPresenter presenter)
            {
                presenter.Minimize();
            }

            return;
        }

        // 隐藏前把几何写掉：隐藏后 AppWindow 的几何不一定还能读到可信值
        PersistWindowPlacement();
        _wasMaximizedBeforeHide = (AppWindow.Presenter as OverlappedPresenter)?.State == OverlappedPresenterState.Maximized;

        // 窗口不可见期间不要再按 1Hz 重排画面（多画面页的 LayoutSurfaces）。
        // 隐藏时 surface 尺寸可能为 0，那一拍纯属白跑，还可能摸到失效对象 ——
        // 与本机头号未修问题同族，这里先把它停掉。
        SuspendSurfaceRefresh();

        AppWindow.Hide();
        _hiddenToTray = true;

        UpdateTrayState();

        if (!_trayHintShown)
        {
            _trayHintShown = true;
            NotificationService.Show("已收进状态栏", "任务仍在继续。双击状态栏里的Yomori图标可以回到主界面。");
        }
    }

    /// <summary>
    /// 从状态栏恢复窗口。单实例激活、托盘双击、托盘菜单都会调到这里。
    /// 恢复后必须做三件事：还原最大化、把窗口钳回可见区域（防显示器拔掉后跑到屏幕外）、
    /// 重启画面重排。
    /// </summary>
    public void RestoreFromTray()
    {
        _autoExitTimer?.Stop();
        _autoExitTimer = null;

        if (!_hiddenToTray)
        {
            ActivateWindow();
            return;
        }

        _hiddenToTray = false;

        AppWindow.Show();

        if (_wasMaximizedBeforeHide && AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.Maximize();
        }

        ClampWindowIntoView();
        ResumeSurfaceRefresh();
        ActivateWindow();
    }

    private void ActivateWindow()
    {
        try
        {
            // 窗口可能只是被最小化（不是收进托盘）：先还原，否则 Activate 只会闪一下任务栏
            if (AppWindow.Presenter is OverlappedPresenter presenter &&
                presenter.State == OverlappedPresenterState.Minimized)
            {
                presenter.Restore();
            }

            var hwnd = WindowHelper.GetHwnd(this);
            if (hwnd != IntPtr.Zero)
            {
                TrayNative.SetForegroundWindow(hwnd);
            }

            Activate();
        }
        catch
        {
            // 抢焦点失败无所谓
        }
    }

    /// <summary>把窗口钳制回某个显示器的可见区域内（复用 WindowPlacement 的"标题栏至少露出"规则）。</summary>
    private void ClampWindowIntoView()
    {
        try
        {
            var position = AppWindow.Position;
            var size = AppWindow.Size;

            var area = DisplayArea.GetFromPoint(
                new PointInt32(position.X, position.Y),
                DisplayAreaFallback.Primary);

            var work = area?.WorkArea ?? default;
            if (work.Width <= 0 || work.Height <= 0)
            {
                return;
            }

            var placement = WindowPlacement.Resolve(
                position.X,
                position.Y,
                size.Width,
                size.Height,
                new WindowPlacement.Bounds(work.X, work.Y, work.Width, work.Height));

            if (placement.X != position.X || placement.Y != position.Y ||
                placement.Width != size.Width || placement.Height != size.Height)
            {
                AppWindow.MoveAndResize(new RectInt32(placement.X, placement.Y, placement.Width, placement.Height));
            }
        }
        catch
        {
            // 几何修正失败不该阻断恢复窗口
        }
    }

    private void SuspendSurfaceRefresh()
    {
        try
        {
            if (ContentFrame.Content is RdpMultiViewPage page)
            {
                page.SuspendRefresh();
            }
        }
        catch
        {
            // 停不下重排也得继续隐藏窗口
        }
    }

    private void ResumeSurfaceRefresh()
    {
        try
        {
            if (ContentFrame.Content is RdpMultiViewPage page)
            {
                page.ResumeRefresh();
            }
        }
        catch
        {
            // 重排失败不该让"恢复窗口"这一步整个失败
        }
    }

    /// <summary>
    /// 程序发起退出的**唯一出口**。
    ///
    /// <para>为什么必须统一：关闭拦截装上之后，任何一处直接 <c>Close()</c> 都会被自己的弹窗挡住 ——
    /// 典型受害者是"任务完成后自动退出"（否则用户每次都会看到一个莫名其妙的"关闭Yomori？"）。
    /// 这里先置 <see cref="_exiting"/>，让 <see cref="OnAppWindowClosing"/> 直接放行。</para>
    /// </summary>
    public void RequestExit()
    {
        if (_exiting)
        {
            return;
        }

        _exiting = true;

        // 自动保存走 600 毫秒去抖，这个窗口期内关程序会把最近的改动一起带走 —— 先落盘。
        // 放在最前面：后面几步（摘托盘、关窗口）任何一步抛异常都不该让改动留在内存里。
        TryFlushAutoSave();

        // 退出流程一开始就掐掉「切回主控会话」的排队请求：再等窗口 Closed 才处理，
        // 切换命令可能已经和关闭时序抢在一起了。
        try
        {
            App.ViewModel?.SuppressControllerSessionReturn();
        }
        catch
        {
            // 退出路径不因为这一步失败而中断
        }

        _autoExitTimer?.Stop();
        _autoExitTimer = null;

        try
        {
            PersistWindowPlacement();
        }
        catch
        {
            // 几何记不住也得退
        }

        try
        {
            _tray?.Dispose();
        }
        catch
        {
            // 图标摘不掉也得退（进程结束时系统会清）
        }

        _tray = null;

        try
        {
            Close();
        }
        catch
        {
            Environment.Exit(0);
        }
    }

    /// <summary>
    /// 退出前把自动保存的待存改动写完。
    ///
    /// <para>失败只吞掉不抛：退出路径上任一步抛异常都会变成"程序关不掉"或
    /// "改动留在内存里"两种糟糕结果，而落盘失败自有
    /// <c>MainViewModel.ReportAutoSaveFailure</c> 那条链路留痕。</para>
    /// </summary>
    private void TryFlushAutoSave()
    {
        try
        {
            VM.FlushAutoSave();
        }
        catch
        {
            // 退出不因为落盘失败而中断
        }
    }

    /// <summary>托盘菜单里的「开始执行 / 停止执行」。开始时把窗口叫回来，免得用户在看不见界面时误以为没反应。</summary>
    private void OnTrayStartStop()
    {
        var running = VM.IsRunning;

        RestoreFromTray();

        if (running)
        {
            VM.Stop();
        }
        else
        {
            VM.Start();
        }

        UpdateTrayState();
    }

    /// <summary>
    /// 智能模式：任务跑完（<see cref="MainViewModel.IsRunning"/> 由 true→false）且窗口仍在状态栏里，
    /// 延迟几秒后退出。延迟是为了让"本轮结束"的汇总通知先弹出来。
    ///
    /// <para>三个取消条件缺一不可：窗口已被叫回来（用户正在看）、又开始跑了、策略被改成别的。
    /// 少一个都会出现"用户盯着界面，窗口自己关了"。</para>
    /// </summary>
    private void ScheduleAutoExit()
    {
        if (_autoExitTimer is not null)
        {
            return;
        }

        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromSeconds(3);
        timer.IsRepeating = false;

        timer.Tick += (_, _) =>
        {
            timer.Stop();
            _autoExitTimer = null;

            if (_exiting || !_hiddenToTray || VM.IsRunning)
            {
                return;
            }

            if (WindowConfig.NormalizeCloseAction(VM.Config.Window.CloseAction) != WindowConfig.CloseActionAuto)
            {
                return;
            }

            NotificationService.Show("Yomori", "任务已结束，程序即将退出。");
            RequestExit();
        };

        _autoExitTimer = timer;
        timer.Start();
    }

    // ---------------- 右侧执行状态栏：展开 / 收起 / 1Hz 刷新 ----------------

    /// <summary>展开态宽度（与 XAML 里的初值保持一致）。</summary>
    private const double SideStatusExpandedWidth = 264;

    /// <summary>收起态宽度：只留一个展开按钮 + 竖排文字的窄条。</summary>
    private const double SideStatusCollapsedWidth = 44;

    /// <summary>
    /// 状态栏能显示的执行项目上限。通道配置本身的硬上限就是 8 条
    /// （见 <c>RdpConfig</c> 的校验），所以行对象按这个数一次建满、之后复用。
    /// </summary>
    private const int SideStatusMaxRows = 8;

    /// <summary>状态栏的轮询计时器。只在主窗口生命周期内跑。</summary>
    private DispatcherQueueTimer? _sideStatusTimer;

    /// <summary>展开/收起的宽度过渡时长。</summary>
    private const int SideStatusSlideMs = 200;

    // ---------------- 通知铃铛 ----------------

    /// <summary>未读期间的摇动间隔（用户要求：每 2 秒轻摇一次，读完全部立刻停）。</summary>
    private static readonly TimeSpan BellShakeInterval = TimeSpan.FromSeconds(2);

    /// <summary>铃铛的 2 秒计时器。<b>必须是字段</b>，局部变量会被 GC 回收且不报错。</summary>
    private DispatcherQueueTimer? _bellTimer;

    /// <summary>摇动动画。<b>必须是字段</b>：Storyboard 只存局部变量时可能播到一半被回收。</summary>
    private Storyboard? _bellShake;

    /// <summary>
    /// 铃铛初始化：列表数据源、摇动动画、2 秒心跳。
    ///
    /// ⚠️ ItemsSource 用代码赋值而不是 x:Bind —— Window 上的 x:Bind 求值早于
    /// 这边的集合准备，绑了会拿到空列表（右侧状态栏的 SideRowList 也是同样的原因手赋值）。
    /// </summary>
    private void InitializeNotificationBell()
    {
        NotifyList.ItemsSource = VM.Notifications.Items;

        _bellShake = BuildBellShake();

        _bellTimer = DispatcherQueue.CreateTimer();
        _bellTimer.Interval = BellShakeInterval;
        _bellTimer.IsRepeating = true;
        _bellTimer.Tick += (_, _) =>
        {
            if (VM.HasUnreadNotifications)
            {
                PlayBellShake();
            }
        };
        _bellTimer.Start();

        VM.Notifications.Changed += (_, _) => UpdateNotifyChrome();
        UpdateNotifyChrome();
    }

    /// <summary>
    /// 一记轻摇：0 → +9° → -9° → +6° → -4° → 0，共 0.5 秒。
    /// 动的是 <see cref="RotateTransform"/>（渲染变换），属独立动画 ——
    /// **不需要** <c>EnableDependentAnimation</c>（只有 Width/Height 那类布局属性才需要）。
    /// </summary>
    private Storyboard BuildBellShake()
    {
        var animate = new DoubleAnimationUsingKeyFrames();

        animate.KeyFrames.Add(new EasingDoubleKeyFrame { KeyTime = KeyTime.FromTimeSpan(TimeSpan.Zero), Value = 0 });
        animate.KeyFrames.Add(new EasingDoubleKeyFrame { KeyTime = KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(90)), Value = 9 });
        animate.KeyFrames.Add(new EasingDoubleKeyFrame { KeyTime = KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(200)), Value = -9 });
        animate.KeyFrames.Add(new EasingDoubleKeyFrame { KeyTime = KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(310)), Value = 6 });
        animate.KeyFrames.Add(new EasingDoubleKeyFrame { KeyTime = KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(420)), Value = -4 });
        animate.KeyFrames.Add(new EasingDoubleKeyFrame { KeyTime = KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(500)), Value = 0 });

        Storyboard.SetTarget(animate, NotifyRotate);
        Storyboard.SetTargetProperty(animate, "Angle");

        var storyboard = new Storyboard();
        storyboard.Children.Add(animate);
        return storyboard;
    }

    private void PlayBellShake()
    {
        if (_bellShake is null)
        {
            return;
        }

        // HoldEnd 会让动画结束后继续压住 Angle：先 Stop 再显式归零，
        // 否则下一轮会从上一轮的终点起算，越摇越歪。
        _bellShake.Stop();
        NotifyRotate.Angle = 0;

        try
        {
            _bellShake.Begin();
        }
        catch
        {
            // 元素还没进可视树等边界情况：摇不动就算了，不值得因此崩掉主窗口
        }
    }

    /// <summary>刷新红点、空列表提示与 tooltip；未读清零时立刻停止摇动并归零。</summary>
    private void UpdateNotifyChrome()
    {
        var unread = VM.HasUnreadNotifications;

        NotifyDot.Visibility = unread ? Visibility.Visible : Visibility.Collapsed;
        NotifyEmptyHint.Visibility = VM.Notifications.Items.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;

        ToolTipService.SetToolTip(NotifyButton, unread
            ? $"通知（{VM.Notifications.UnreadCount} 条未读）"
            : "通知");

        if (!unread)
        {
            _bellShake?.Stop();
            NotifyRotate.Angle = 0;
        }
    }

    private void OnNotificationClicked(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is AppNotification item)
        {
            VM.OpenNotification(item);
            UpdateNotifyChrome();
        }
    }

    /// <summary>
    /// 面板一打开就把未读清掉。
    ///
    /// 用户已经把它拉下来看了，再挂着红点、继续每 2 秒摇铃铛，就是"信息已经送达却还在催"。
    /// （这是实际反馈：看到了通知，铃铛还显示未读。）
    /// </summary>
    private void OnNotifyFlyoutOpened(object? sender, object e)
    {
        VM.MarkAllNotificationsRead();
        UpdateNotifyChrome();
    }

    /// <summary>删除单条通知。点的是"这一条"，所以不弹确认 —— 删错了再等下一次启动检查即可。</summary>
    private void OnDeleteNotificationClicked(object sender, RoutedEventArgs e)
    {
        // 不用 e.Handled：Button.Click 的 RoutedEventArgs 根本没有这个属性（那是
        // PointerRoutedEventArgs 等才有的）。也不担心冒泡成 ListView 的 ItemClick ——
        // Button 自己会吃掉指针事件，ItemClick 不会跟着触发。
        if (sender is not FrameworkElement element || element.Tag is not AppNotification item)
        {
            return;
        }

        VM.Notifications.Remove(item);
        UpdateNotifyChrome();
    }

    private void OnMarkAllNotificationsRead(object sender, RoutedEventArgs e)
    {
        VM.MarkAllNotificationsRead();
        UpdateNotifyChrome();
    }

    private async void OnClearNotifications(object sender, RoutedEventArgs e)
    {
        if (VM.Notifications.Items.Count == 0)
        {
            return;
        }

        var confirm = await Helpers.DialogHelper.ShowMessageAsync(
            "清空通知",
            "确定清空全部通知吗？这条操作不可撤销。",
            closeText: "取消",
            primaryText: "清空");

        if (confirm == ContentDialogResult.Primary)
        {
            VM.Notifications.Clear();
            UpdateNotifyChrome();
        }
    }

    /// <summary>切换过程中"旧内容淡出"的时长（短于宽度过渡，先看不见再变窄，避免文字被挤成一团）。</summary>
    private const int SideStatusFadeOutMs = 90;

    /// <summary>切换完成后"新内容淡入"的时长。</summary>
    private const int SideStatusFadeInMs = 140;

    /// <summary>正在跑的过渡动画（重复点击时先停掉它，不等它自己结束）。</summary>
    private Storyboard? _sideStatusBoard;

    /// <summary>状态栏当前是否收起（与配置里的 <c>window.sidebar_collapsed</c> 同步）。</summary>
    private bool _sideStatusCollapsed;

    /// <summary>
    /// 上一拍写进控件的内容签名。拼字符串比对而不是无条件重设 ——
    /// 见 <see cref="RefreshSideStatus"/> 的注释。
    /// </summary>
    private string _sideStatusSignature = string.Empty;

    /// <summary>复用的「执行项目」行对象（固定 <see cref="SideStatusMaxRows"/> 个）。</summary>
    public ObservableCollection<SideStatusRow> SideStatusRows { get; } = new();

    /// <summary>
    /// 状态栏的一次性初始化：建行对象、恢复上次的展开/收起状态、起 1Hz 计时器。
    /// </summary>
    private void InitializeSideStatus()
    {
        for (var i = 0; i < SideStatusMaxRows; i++)
        {
            SideStatusRows.Add(new SideStatusRow());
        }

        // 直接赋 ItemsSource，不走 x:Bind：Window 上的 x:Bind 求值时机比这行更早，
        // 集合内容不可靠；属性赋值则一定会让 ItemsControl 重新取一次。
        SideRowList.ItemsSource = SideStatusRows;

        // 收起态那份用的是**同一个集合**：两套界面显示同一批行，才不会出现
        // "收起时 3/7、展开时 5/7" 这种对不上的情况。
        SideCollapsedRows.ItemsSource = SideStatusRows;

        SetSideStatusCollapsed(VM.Config.Window.SidebarCollapsed, persist: false);

        _sideStatusTimer = DispatcherQueue.CreateTimer();
        _sideStatusTimer.Interval = TimeSpan.FromSeconds(1);
        _sideStatusTimer.IsRepeating = true;
        _sideStatusTimer.Tick += (_, _) => RefreshSideStatus();
        _sideStatusTimer.Start();

        // 先刷一次，别让用户看到"空闲 / 0 / 0"闪一下
        RefreshSideStatus();
    }

    /// <summary>
    /// 切换展开/收起。
    ///
    /// <para>用户点击时播一段过渡：旧内容淡出 → 宽度滑到目标值 → 新内容淡入。
    /// 宽度是**布局属性**，动画必须开 <c>EnableDependentAnimation</c>，否则整段会被静默跳过
    /// （项目里切子菜单那套动画踩过同一个坑）。</para>
    ///
    /// <para><paramref name="persist"/> 为 false 时只在界面上应用（启动时按配置恢复用），
    /// 不写盘 —— 否则启动那一刻会把"配置里本来就是 false"再写一遍。</para>
    /// </summary>
    private void SetSideStatusCollapsed(bool collapsed, bool persist)
    {
        var changed = _sideStatusCollapsed != collapsed;
        _sideStatusCollapsed = collapsed;

        if (persist)
        {
            VM.SaveSidebarCollapsed(collapsed);
        }

        // 启动恢复（状态没变）或控件还没进过布局树：直接落终态，不播动画
        // —— 否则程序一起手就自己"啪"地缩一下。
        if (changed && SideStatusPanel.IsLoaded)
        {
            PlaySideStatusTransition(collapsed);
            return;
        }

        _sideStatusBoard?.Stop();
        _sideStatusBoard = null;
        ApplySideStatusLayout(collapsed);
    }

    /// <summary>把状态栏直接摆成某个终态（不带动画）。两套内容各留 Opacity=1。</summary>
    private void ApplySideStatusLayout(bool collapsed)
    {
        SideStatusExpanded.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
        SideStatusCollapsed.Visibility = collapsed ? Visibility.Visible : Visibility.Collapsed;
        SideStatusPanel.Width = collapsed ? SideStatusCollapsedWidth : SideStatusExpandedWidth;
        SideStatusExpanded.Opacity = 1;
        SideStatusCollapsed.Opacity = 1;
    }

    /// <summary>
    /// 播一段展开/收起过渡。
    ///
    /// <para>顺序刻意是"先淡出、再变宽、最后淡入"：宽度过渡期间内容仍参与布局，
    /// 如果它还是可见的，264px 的文字会被一路挤扁，看起来像在抽搐。
    /// 90ms 内淡到看不见，剩下的变宽过程就是干净的一条空面板在滑。</para>
    ///
    /// <para>两段动画分开播（Completed 里接第二段），而不是塞进一个 Storyboard 用 BeginTime：
    /// 第二段依赖第一段的终态（切换 Visibility），用一个 board 就得靠延迟猜时间。</para>
    /// </summary>
    private void PlaySideStatusTransition(bool collapsed)
    {
        // 上一次过渡还没结束（用户连点）：直接停掉，接着播这一次。
        // Stop 会把动画值释放回基础值，所以下面显式把两边的 Opacity 归 1 再重新淡出。
        _sideStatusBoard?.Stop();
        _sideStatusBoard = null;
        SideStatusExpanded.Opacity = 1;
        SideStatusCollapsed.Opacity = 1;

        var outgoing = collapsed ? (FrameworkElement)SideStatusExpanded : SideStatusCollapsed;
        var incoming = collapsed ? (FrameworkElement)SideStatusCollapsed : SideStatusExpanded;

        var board = new Storyboard();

        var fadeOut = new DoubleAnimation
        {
            To = 0,
            Duration = new Duration(TimeSpan.FromMilliseconds(SideStatusFadeOutMs)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        Storyboard.SetTarget(fadeOut, outgoing);
        Storyboard.SetTargetProperty(fadeOut, "Opacity");
        board.Children.Add(fadeOut);

        // 宽度属于依赖动画：不开这个开关，这一条会被静默丢弃，面板会"瞬移"而不是滑过去
        var slide = new DoubleAnimation
        {
            To = collapsed ? SideStatusCollapsedWidth : SideStatusExpandedWidth,
            Duration = new Duration(TimeSpan.FromMilliseconds(SideStatusSlideMs)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut },
            EnableDependentAnimation = true,
        };
        Storyboard.SetTarget(slide, SideStatusPanel);
        Storyboard.SetTargetProperty(slide, "Width");
        board.Children.Add(slide);

        board.Completed += (_, _) =>
        {
            board.Stop();
            _sideStatusBoard = null;

            // 先落终态（切 Visibility + 把宽度钉到目标值），再让新内容淡入
            ApplySideStatusLayout(collapsed);
            incoming.Opacity = 0;

            var fadeIn = new DoubleAnimation
            {
                To = 1,
                Duration = new Duration(TimeSpan.FromMilliseconds(SideStatusFadeInMs)),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            };
            Storyboard.SetTarget(fadeIn, incoming);
            Storyboard.SetTargetProperty(fadeIn, "Opacity");

            var inBoard = new Storyboard();
            inBoard.Children.Add(fadeIn);

            // HoldEnd 会让动画值长期压住 Opacity，这之后谁再改它都不生效 —— 必须卸载
            inBoard.Completed += (_, _) =>
            {
                inBoard.Stop();
                incoming.Opacity = 1;
            };

            inBoard.Begin();
        };

        _sideStatusBoard = board;
        board.Begin();
    }

    private void OnToggleSideStatus(object sender, RoutedEventArgs e) =>
        SetSideStatusCollapsed(!_sideStatusCollapsed, persist: true);

    /// <summary>
    /// 刷一次状态栏。
    ///
    /// <para>**为什么是轮询而不是绑定**：<see cref="RdpChannelSession"/> 的
    /// Progress / StatusText / HeartbeatText 都是不可观察的普通属性，只在主控端
    /// 轮询桥里的 status.json 时就地改写 —— 绑不上去，只能定时取快照。
    /// 主页「会话通道」概览用的也是这个办法。</para>
    ///
    /// <para>**但不做每拍无条件重设**：先把整屏要显示的内容拼成一个签名，
    /// 和上一拍一样就立刻返回。项目里多画面页那套 1Hz 全量重设已经三次掀掉进程，
    /// 状态栏宁可多拼一个字符串，也不每秒重写一遍控件属性。</para>
    /// </summary>
    private void RefreshSideStatus()
    {
        // 计时器在 UI 线程上跑，但留一道闸：这条链上以后若有人从后台调它，也不至于炸。
        if (!DispatcherQueue.HasThreadAccess)
        {
            DispatcherQueue.TryEnqueue(RefreshSideStatus);
            return;
        }

        var vm = VM;
        var sessions = vm.Sessions;

        var statusText = vm.PanelStatusText;
        var taskText = vm.PanelCurrentTask;
        var progressText = vm.PanelProgressText;
        var elapsedText = vm.PanelElapsedText;
        var abnormalReason = vm.PanelAbnormalReason;
        var abnormalMore = vm.PanelAbnormalMoreText;

        var done = 0;
        var total = 0;
        var anyActive = false;

        for (var i = 0; i < sessions.Count; i++)
        {
            var session = sessions[i];
            done += session.Progress;
            total += session.Total;
            anyActive |= session.IsActive;
        }

        // 「有异常」由 VM 统一判（多块并行时那是整轮汇总的结论，单块各有各的收尾时机）
        var anyAbnormal = vm.PanelHasAbnormal;

        var ratio = total > 0 ? Math.Clamp((double)done / total, 0d, 1d) : 0d;
        var rowCount = Math.Min(sessions.Count, SideStatusMaxRows);

        // 签名要覆盖"看得见的每一个取值的来源"
        var signature = new System.Text.StringBuilder(160)
            .Append(statusText).Append('\n')
            .Append(taskText).Append('\n')
            .Append(progressText).Append('\n')
            .Append(elapsedText).Append('\n')
            .Append(ratio.ToString("F4")).Append('\n')
            .Append(anyActive ? '1' : '0')
            .Append(anyAbnormal ? '1' : '0')
            // 收起态的各通道进度只在执行中出现，这个开关也属于"看得见的取值"
            .Append(vm.IsRunning ? '1' : '0')
            .Append('\n')
            .Append(abnormalReason).Append('\n')
            .Append(abnormalMore).Append('\n')
            .Append(rowCount);

        for (var i = 0; i < rowCount; i++)
        {
            var session = sessions[i];
            signature.Append('\n')
                     .Append(session.DisplayName)
                     .Append('|').Append(session.Progress).Append('/').Append(session.Total)
                     .Append('|').Append(session.StatusText)
                     .Append('|').Append(session.IsStale ? '1' : '0')
                     .Append('|').Append(session.IsActive ? '1' : '0');
        }

        var key = signature.ToString();
        if (string.Equals(key, _sideStatusSignature, StringComparison.Ordinal))
        {
            return;
        }

        _sideStatusSignature = key;

        SideStatusValue.Text = statusText;
        SideCurrentTask.Text = taskText;
        SideProgressText.Text = progressText;
        SideElapsedText.Text = elapsedText;

        // 收起态只有 44px 宽："合计 3 / 7" 这种带空格与前缀的文本会折成三四行，
        // 去掉空格和「合计」两个字，只留裸数字
        SideCollapsedProgressText.Text = progressText
            .Replace(" ", string.Empty, StringComparison.Ordinal)
            .Replace("合计", string.Empty, StringComparison.Ordinal);

        // 各通道进度只在执行中出现：空闲时没有进度可看，留着只会把 44px 的窄条塞满数字。
        // 行内容本身跟着 SideStatusRows 走（与展开态同一集合），这里只开关整块。
        SideCollapsedRows.Visibility = vm.IsRunning ? Visibility.Visible : Visibility.Collapsed;

        SideProgressBar.Value = ratio;

        // 三个色点互斥显示：颜色由主题资源给，代码只负责选哪一个露出来
        SideDotActive.Visibility = anyActive ? Visibility.Visible : Visibility.Collapsed;
        SideDotAbnormal.Visibility = anyAbnormal ? Visibility.Visible : Visibility.Collapsed;
        SideDotIdle.Visibility = anyActive || anyAbnormal ? Visibility.Collapsed : Visibility.Visible;

        RefreshSideAbnormalBox(vm);

        SideEmptyHint.Visibility = rowCount == 0 ? Visibility.Visible : Visibility.Collapsed;

        for (var i = 0; i < SideStatusRows.Count; i++)
        {
            var row = SideStatusRows[i];

            if (i >= rowCount)
            {
                row.Hide();
                continue;
            }

            var session = sessions[i];
            row.Show(
                session.DisplayName,
                session.Progress,
                session.Total,
                session.IsStale ? $"{session.StatusText}（失联）" : session.StatusText,
                session.IsActive);
        }
    }

    /// <summary>
    /// 状态栏的「异常原因」块。
    ///
    /// <para>调用方（<see cref="RefreshSideStatus"/>）已经做过整屏签名比对，
    /// 所以这里可以直接写控件，不必再逐项比 —— 唯一的例外是 ToolTip：
    /// <c>ToolTipService.SetToolTip</c> 每次都会重建悬浮窗口，
    /// 项目里反复设置它掀掉过进程（多画面页 1Hz 全量重设那三次），
    /// 因此它单独按文本去重、且复用同一个 TextBlock 实例。</para>
    /// </summary>
    private void RefreshSideAbnormalBox(MainViewModel vm)
    {
        var reason = vm.PanelAbnormalReason;
        var show = vm.PanelHasAbnormal && !string.IsNullOrWhiteSpace(reason);

        SideAbnormalBox.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (!show)
        {
            return;
        }

        SideAbnormalReason.Text = reason;

        var more = vm.PanelAbnormalMoreText;
        SideAbnormalMore.Text = more;
        SideAbnormalMore.Visibility = string.IsNullOrEmpty(more) ? Visibility.Collapsed : Visibility.Visible;

        SetSideAbnormalTip(vm.PanelAbnormalDetail);
    }

    /// <summary>异常原因悬浮提示里那个 TextBlock（一次创建、之后只改 Text）。</summary>
    private TextBlock? _sideAbnormalTip;

    /// <summary>上一次设进悬浮提示的文本（内容没变就不重设）。</summary>
    private string _sideAbnormalTipText = string.Empty;

    /// <summary>把全部异常原因挂成悬浮提示 —— 状态栏最多显示 4 行，剩下的靠它看完。</summary>
    private void SetSideAbnormalTip(string detail)
    {
        if (string.Equals(detail, _sideAbnormalTipText, StringComparison.Ordinal))
        {
            return;
        }

        _sideAbnormalTipText = detail;

        if (string.IsNullOrEmpty(detail))
        {
            ToolTipService.SetToolTip(SideAbnormalBox, null);
            return;
        }

        _sideAbnormalTip ??= new TextBlock
        {
            MaxWidth = 420,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
        };

        _sideAbnormalTip.Text = detail;
        ToolTipService.SetToolTip(SideAbnormalBox, _sideAbnormalTip);
    }

    // ---------------- 导航 ----------------

    private string _currentTag = string.Empty;

    /// <summary>左侧菜单里各条通道项用的 tag 前缀（M4）。</summary>
    private const string ChannelTagPrefix = "ch:";

    /// <summary>
    /// 「通道管理…」子项 tag —— 配置 / 凭据 / 部署代理 / 预检（<see cref="Views.ChannelsPage"/>）。
    /// 刻意不再叫 "channels"：那个 tag 现在只是「会话通道」父项的标识，
    /// 导航语义已让给「多画面」子项（<see cref="MultiViewTag"/>）。
    /// </summary>
    private const string ChannelMgmtTag = "channel-mgmt";

    /// <summary>
    /// 「多画面（全部通道）」子项 tag —— 多画面页（全部通道网格）唯一的菜单入口。
    /// 「会话通道」父项点击只展开/收起，不导航；旧 tag "channels" 在
    /// <see cref="NavigateTo"/> 里统一映射到这个 tag（外部入口 + 选中态都对得上）。
    /// </summary>
    private const string MultiViewTag = "multiview";

    /// <summary>子菜单展开时的级联入场动画节奏：单项 180ms、每项错开 25ms，总延迟封顶 120ms（通道多也不拖沓）。</summary>
    private const int MenuEntranceDurationMs = 180;
    private const int MenuEntranceStepMs = 25;
    private const int MenuEntranceMaxDelayMs = 120;

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        // 通道配置整体换过（ReloadChannels 会 raise Channels）→ 子菜单跟着重建
        if (e.PropertyName == nameof(MainViewModel.Channels))
        {
            RebuildChannelMenuItems();
            return;
        }

        if (e.PropertyName == nameof(MainViewModel.IsRunning))
        {
            // IsRunning 理论上由 UI 线程改，但这条链上还接着托盘图标与计时器，
            // 一旦将来有人从后台线程改它，碰控件/建计时器都会立刻炸 —— 先回 UI 线程再说。
            if (!DispatcherQueue.HasThreadAccess)
            {
                DispatcherQueue.TryEnqueue(() => OnViewModelPropertyChanged(sender, e));
                return;
            }

            UpdateTrayState();

            // 智能模式：躲进状态栏之后任务跑完了 → 到点自动退出。
            // 只有"仍然躲着"才算数；用户中途把窗口叫回来就不再自动关（见 ScheduleAutoExit）。
            if (_hiddenToTray && !VM.IsRunning &&
                WindowConfig.NormalizeCloseAction(VM.Config.Window.CloseAction) == WindowConfig.CloseActionAuto)
            {
                ScheduleAutoExit();
            }
        }
    }

    /// <summary>
    /// 重建「会话通道」父项下的子菜单（排到下一拍执行）。
    ///
    /// 为什么不在 XAML 里绑集合：子项要先列配置通道、再补在跑的通道、末尾还要钉一个「通道管理…」，
    /// 由窗口统一重建比双向同步可靠得多，也避开 NavigationView 选中态与集合变更的竞态。
    ///
    /// <para>🔑 一律延后一拍：<c>MenuItems</c> 的增删会立刻引发 NavigationView 子树的进出
    /// （<c>Enter</c>/<c>Leave</c>）。触发本方法的都是属性变更 / 集合变更回调，这些回调
    /// 有可能正处在一趟布局或树进入过程里 —— 边遍历边改集合，2026-10-27 19:43 那次
    /// <c>FailFastWithStowedExceptions</c>（栈底是 <c>CUIElementCollection::Append</c> →
    /// <c>EnterImpl</c>）就是这个形状。排到下一拍就不可能落在遍历中间。</para>
    /// </summary>
    private void RebuildChannelMenuItems()
    {
        if (!DispatcherQueue.HasThreadAccess)
        {
            DispatcherQueue.TryEnqueue(RebuildChannelMenuItems);
            return;
        }

        // 多次触发合并成一次：任务运行期间 SessionsChanged 可能连发好几次。
        if (_menuRebuildQueued)
        {
            return;
        }

        _menuRebuildQueued = true;
        if (!DispatcherQueue.TryEnqueue(() =>
            {
                _menuRebuildQueued = false;
                RebuildChannelMenuItemsCore();
            }))
        {
            _menuRebuildQueued = false;
        }
    }

    private void RebuildChannelMenuItemsCore()
    {
        var parent = ChannelsNavItem;

        // 内容没变就整个跳过：不只省事，更是**避免销毁重建**。
        // 这些项上可能正挂着入场动画的 RenderTransform，反复重建等于把带动画的元素
        // 丢进垃圾堆，而动画时钟仍挂在它们身上。
        var entries = EnumerateChannelMenuEntries();
        var signature = BuildChannelMenuSignature(entries);
        if (signature == _channelMenuSignature && parent.MenuItems.Count > 0)
        {
            if (_currentTag.Length > 0)
            {
                SyncNavigationSelection(_currentTag);
            }
            return;
        }

        _channelMenuSignature = signature;

        // 🔑 清空前必须先停动画、摘掉变换：动画的 Completed 回调会晚于元素被移除，
        // 那时再改 Opacity / TranslateY 就是把动画值写进一棵已经不存在的子树。
        // 2026-10-27 两次崩溃的栈顶正是 CUIElement::GetLocalTransform ——
        // 命中测试遍历左侧菜单时，算到某个已失效的 CompositeTransform 上。
        StopChannelMenuAnimations();

        parent.MenuItems.Clear();
        parent.MenuItems.Add(new NavigationViewItem
        {
            Tag = MultiViewTag,
            Content = "多画面（全部通道）",
            Icon = new SymbolIcon(Symbol.ViewAll),
        });

        foreach (var (id, name, tooltip) in entries)
        {
            var item = new NavigationViewItem
            {
                Tag = ChannelTagPrefix + id,
                Content = name,
                Icon = new SymbolIcon(Symbol.Link),
            };

            ToolTipService.SetToolTip(item, tooltip);
            parent.MenuItems.Add(item);
        }

        parent.MenuItems.Add(new NavigationViewItemSeparator());
        parent.MenuItems.Add(new NavigationViewItem
        {
            Tag = ChannelMgmtTag,
            Content = "通道管理…",
            Icon = new SymbolIcon(Symbol.Setting),
        });

        // 重建会把原来的项对象整体换掉 → 选中态需按 tag 复原，
        // 否则用户正停在某条通道页时，菜单会"莫名其妙没选中任何一项"。
        if (_currentTag.Length > 0)
        {
            SyncNavigationSelection(_currentTag);
        }
    }

    /// <summary>
    /// 「会话通道」父项被点击时切换子菜单的展开/收起。
    ///
    /// 为什么延后一拍再看状态：NavigationView 在部分版本里会自己切换 IsExpanded，
    /// 有的版本不会（表现为"点不动"）。这里先记下点击时的状态，下一拍再比对 ——
    /// 状态变了说明框架已经切过（不重复切，避免抵消成"点了没反应"），
    /// 没变则由我们补上切换。两种行为下结果一致。
    /// </summary>
    private void ToggleChannelMenu()
    {
        var parent = ChannelsNavItem;
        var wasExpanded = parent.IsExpanded;

        DispatcherQueue.TryEnqueue(() =>
        {
            if (parent.IsExpanded == wasExpanded)
            {
                parent.IsExpanded = !wasExpanded;
            }

            if (parent.IsExpanded)
            {
                PlayChannelMenuEntrance();
            }
        });
    }

    /// <summary>
    /// 展开时给子项来一段级联入场动画（淡入 + 从上方轻微下滑）。
    ///
    /// 为什么要自己播：NavigationView 自带的只有面板高度撑开和 chevron 旋转，
    /// 子项是"凭空出现"的，通道一多就很生硬。按索引错开、时长压到 180ms，不挡操作。
    /// 只在用户点父项展开时播 —— 子菜单因会话变化重建（跑任务时很频繁）不播，否则菜单会一直闪。
    /// </summary>
    private void PlayChannelMenuEntrance()
    {
        // 上一段还没播完就又展开：先把旧的停掉并摘掉变换，避免同一元素上叠两套动画。
        StopChannelMenuAnimations();

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var duration = new Duration(TimeSpan.FromMilliseconds(MenuEntranceDurationMs));
        var index = 0;

        foreach (var entry in ChannelsNavItem.MenuItems)
        {
            if (entry is not FrameworkElement element)
            {
                continue;
            }

            var delay = TimeSpan.FromMilliseconds(Math.Min(index * MenuEntranceStepMs, MenuEntranceMaxDelayMs));
            index++;

            var transform = new CompositeTransform { TranslateY = -12 };
            element.RenderTransform = transform;
            element.Opacity = 0;

            var board = new Storyboard();

            var fade = new DoubleAnimation
            {
                To = 1,
                Duration = duration,
                BeginTime = delay,
                EasingFunction = ease,
            };

            Storyboard.SetTarget(fade, element);
            Storyboard.SetTargetProperty(fade, "Opacity");

            // 位移属于"依赖动画"（要 UI 线程逐帧算），不开这个开关整段会被静默跳过
            var slide = new DoubleAnimation
            {
                To = 0,
                Duration = duration,
                BeginTime = delay,
                EasingFunction = ease,
                EnableDependentAnimation = true,
            };

            Storyboard.SetTarget(slide, transform);
            Storyboard.SetTargetProperty(slide, "TranslateY");

            board.Children.Add(fade);
            board.Children.Add(slide);

            // 收尾卸掉动画值：HoldEnd 会让动画值长期压住属性，之后谁再改 Opacity 都不生效。
            // 同时把 RenderTransform 摘掉 —— 留着一个只为动画存在的 CompositeTransform，
            // 等于给命中测试留一个随时可能失效的变换对象。
            board.Completed += (_, _) =>
            {
                _menuEntranceBoards.Remove(board);
                board.Stop();

                // 元素可能已经在重建时脱离子树了：那就什么都别碰。
                if (element.IsLoaded)
                {
                    element.Opacity = 1;
                    element.RenderTransform = null;
                }
            };

            // 保活：Storyboard 不自持引用，没人引用它就会被回收（见字段注释）。
            _menuEntranceBoards.Add(board);
            board.Begin();
        }
    }

    /// <summary>
    /// 停掉全部入场动画并摘掉它们挂在元素上的变换。
    ///
    /// <para>调用点只有两处、都是"元素即将离开子树"之前：重建子菜单清空前、
    /// 以及再次展开前。先停再拆，动画的收尾回调就不会摸到已经脱树的元素。</para>
    /// </summary>
    private void StopChannelMenuAnimations()
    {
        if (_menuEntranceBoards.Count == 0)
        {
            return;
        }

        // Stop() 可能同步触发 Completed，而那个回调会从集合里移除自己 —— 先拍快照。
        foreach (var board in _menuEntranceBoards.ToArray())
        {
            board.Stop();
        }

        _menuEntranceBoards.Clear();
    }

    /// <summary>
    /// 子菜单内容签名。只包含会显示出来的东西（id / 名称 / 提示），
    /// 拿它判断"这次重建是不是白干"。
    /// </summary>
    private static string BuildChannelMenuSignature(IReadOnlyList<(string Id, string Name, string Tooltip)> entries)
    {
        var builder = new System.Text.StringBuilder();
        foreach (var (id, name, tooltip) in entries)
        {
            builder.Append(id).Append('\u001f').Append(name).Append('\u001f').Append(tooltip).Append('\u001e');
        }
        return builder.ToString();
    }

    /// <summary>
    /// 菜子里该列哪些通道：① 配置里启用中的通道（不跑任务也能点进去手动连画面看）；
    /// ② 本轮在跑的通道（含配置里已删 / 已停用的 —— 在跑就必须看得见）。
    /// </summary>
    private List<(string Id, string Name, string Tooltip)> EnumerateChannelMenuEntries()
    {
        var entries = new List<(string, string, string)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var channel in VM.Config.Rdp.Channels)
        {
            if (!channel.Enabled || string.IsNullOrWhiteSpace(channel.Id) || !seen.Add(channel.Id))
            {
                continue;
            }

            entries.Add((channel.Id, channel.DisplayName, $"{channel.Host} / {channel.User}"));
        }

        foreach (var session in VM.Sessions.Where(s => s.IsRemote))
        {
            if (!seen.Add(session.ChannelId))
            {
                continue;
            }

            entries.Add((
                session.ChannelId,
                session.DisplayName,
                $"{session.TargetHost} / {session.TargetUser} · {session.StatusText}"));
        }

        return entries;
    }

    private void OnNavigationSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is not NavigationViewItem item || item.Tag is not string tag)
        {
            return;
        }

        // 父项「会话通道」只是展开开关，永远不该成为导航目标。
        // XAML 里已经用 SelectsOnInvoked="False" 让它点不动选中态，这里再兜一道：
        // 万一某条路径仍把父项设成 SelectedItem（旧版本框架 / 代码里直接赋值），
        // 别顺手把 tag="channels" 经 NavigateTo 映射到「多画面」—— 那正是"点父项和点子项一个效果"的成因。
        if (item == ChannelsNavItem)
        {
            SyncNavigationSelection(_currentTag);
            return;
        }

        NavigateTo(tag);
    }

    /// <summary>
    /// 父项（「会话通道」）与叶子项都会触发这里：父项只做展开，**不导航也不改选中态**；
    /// 叶子项才导航（<see cref="NavigateTo"/> 按 tag 去重，SelectionChanged 同时来也不会重复导航）。
    /// </summary>
    private void OnNavigationItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
    {
        if (args.InvokedItemContainer is not NavigationViewItem item || item.Tag is not string tag || tag.Length == 0)
        {
            return;
        }

        // 父项与子项各开各的页会让人以为功能重合 —— 父项点击只展开/收起子菜单，
        // 多画面入口由子项「多画面（全部通道）」独自承担。
        if (item == ChannelsNavItem)
        {
            ToggleChannelMenu();
            return;
        }

        NavigateTo(tag);
    }

    private void NavigateTo(string tag)
    {
        // 旧 tag "channels"（原「会话通道」父项入口，ChannelsPage「打开多画面」还在用）
        // 统一落到「多画面」子项：父项不再作为导航目标，选中态也要落在子项上。
        if (string.Equals(tag, "channels", StringComparison.Ordinal))
        {
            tag = MultiViewTag;
        }

        if (string.Equals(_currentTag, tag, StringComparison.Ordinal))
        {
            return;
        }

        _currentTag = tag;

        // 单条通道页：把通道 id 作为导航参数带过去
        if (tag.StartsWith(ChannelTagPrefix, StringComparison.Ordinal))
        {
            ContentFrame.Navigate(typeof(Views.RdpChannelPage), tag[ChannelTagPrefix.Length..]);
            ChannelsNavItem.IsExpanded = true;
            SyncNavigationSelection(tag);
            return;
        }

        switch (tag)
        {
            case "tasks":
                ContentFrame.Navigate(typeof(Views.TasksPage));
                break;
            case "stats":
                ContentFrame.Navigate(typeof(Views.StatsPage));
                break;
            case "logs":
                ContentFrame.Navigate(typeof(Views.LogsPage));
                break;
            case MultiViewTag:
                // 「多画面（全部通道）」子项：多画面页（父项点击不走这里，见 OnNavigationItemInvoked）
                ContentFrame.Navigate(typeof(Views.RdpMultiViewPage));
                break;
            case ChannelMgmtTag:
                ContentFrame.Navigate(typeof(Views.ChannelsPage));
                break;
            case "settings":
                ContentFrame.Navigate(typeof(Views.SettingsPage));
                break;
            default:
                ContentFrame.Navigate(typeof(Views.HomePage));
                break;
        }

        SyncNavigationSelection(tag);
    }

    private void SyncNavigationSelection(string tag)
    {
        var target = FindNavItemByTag(NavView.MenuItems, tag)
            ?? FindNavItemByTag(NavView.FooterMenuItems, tag);

        if (target is not null)
        {
            NavView.SelectedItem = target;
        }
    }

    /// <summary>按 tag 找菜单项（含父项的子项 —— 通道项现在挂在「会话通道」下面）。</summary>
    private static NavigationViewItem? FindNavItemByTag(IEnumerable<object> items, string tag)
    {
        foreach (var item in items)
        {
            if (item is not NavigationViewItem nav)
            {
                continue;
            }

            if (nav.Tag is string itemTag && string.Equals(itemTag, tag, StringComparison.Ordinal))
            {
                return nav;
            }

            var nested = FindNavItemByTag(nav.MenuItems, tag);
            if (nested is not null)
            {
                return nested;
            }
        }

        return null;
    }

    // ---------------- 控制栏 ----------------

    private void OnStartClicked(object sender, RoutedEventArgs e) => VM.Start();

    private void OnPauseClicked(object sender, RoutedEventArgs e) => VM.Pause();

    private void OnResumeClicked(object sender, RoutedEventArgs e) => VM.Resume();

    private void OnStopClicked(object sender, RoutedEventArgs e) => VM.Stop();

    private void OnCancelShutdownClicked(object sender, RoutedEventArgs e) => VM.CancelShutdown();

    private async void OnShutdownPromptRequested(object? sender, int delaySeconds)
    {
        // 窗口收在状态栏里时，这个倒计时对话框是**看不见**的（XamlRoot 还在，但窗口不可见），
        // 用户只会看到机器到点直接关机。所以先把窗口叫回来。
        RestoreFromTray();

        var dialog = new Views.ShutdownCountdownDialog(delaySeconds)
        {
            XamlRoot = Content.XamlRoot,
        };

        // 走统一入口：排队 + 吞异常，避免和别的对话框撞上直接抛 COMException 崩进程
        var result = await Helpers.DialogHelper.ShowAsync(dialog);
        if (result == ContentDialogResult.Primary)
        {
            VM.CancelShutdown();
        }
    }

    // ---------------- 快捷键：F8 停止 ----------------

    private void OnRootKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.F8)
        {
            VM.Stop();
            e.Handled = true;
        }
    }
}
