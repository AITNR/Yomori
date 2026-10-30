// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Xaml.Input;
using Yomori.Models;
using Yomori.Services;
using Yomori.ViewModels;

namespace Yomori.Views;

/// <summary>通道页实时日志的显示行，不改变桥接事件模型。</summary>
public sealed class RdpLogRow
{
    public RdpLogRow(RdpLiveLog source)
    {
        Seq = source.Seq;
        Time = source.Time;
        Task = string.IsNullOrWhiteSpace(source.TaskName) ? "－" : source.TaskName;

        if (source.IsAbnormal || string.Equals(source.Level, "error", StringComparison.OrdinalIgnoreCase))
        {
            Level = "错误";
            Body = source.Message;
        }
        else if (string.Equals(source.Level, "warn", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(source.Level, "warning", StringComparison.OrdinalIgnoreCase))
        {
            Level = "警告";
            Body = source.Message;
        }
        else
        {
            Level = string.IsNullOrWhiteSpace(source.Level) ||
                    string.Equals(source.Level, "unknown", StringComparison.OrdinalIgnoreCase)
                ? "信息"
                : source.Level;
            Body = source.Message;
        }
    }

    public long Seq { get; }
    public string Time { get; }
    public string Task { get; }
    public string Level { get; }
    public string Body { get; }

}

/// <summary>
/// 一条会话通道的页面：**只显示本通道**的画面 + 进度 / 心跳 / 事件。
///
/// 状态直接取自 <see cref="RdpChannelSession"/>（每通道一份，不是全局单例），
/// 所以两条通道的页面各看各的，不会互相覆盖 —— 这正是 M3 把状态下沉的目的。
///
/// 画面：本通道一格（占满，可全屏、键盘一并接管、可弹成独立窗口）。
/// 「一眼看到全部通道」的网格在 <see cref="RdpMultiViewPage"/>（左侧菜单的「会话通道」父项）——
/// 原先它挤在本页的「单画面 / 多画面」开关后面，概念上就是拧的（详见
/// docs/channel-menu-multiview-plan.md）。
/// </summary>
public sealed partial class RdpChannelPage : Page, System.ComponentModel.INotifyPropertyChanged
{
    private readonly ObservableCollection<RdpTaskEvent> _emptyEvents = new();
    private readonly ObservableCollection<RdpLogRow> _externalLogRows = new();
    private readonly ObservableCollection<RdpLiveLog> _emptyExternalLogs = new();
    private long _clearThroughSeq;
    private string _renderedCommandId = string.Empty;

    private RdpChannelSession? _session;
    private RdpChannelSession? _renderedLogSession;

    /// <summary>导航参数带来的通道 id（<c>_sessions</c> 里还没有时用它兜底）。</summary>
    private string _channelId = string.Empty;

    private DispatcherQueueTimer? _refreshTimer;

    /// <summary>本页是否还挂在可视树里（弹出窗口关闭回调据此决定要不要立即刷界面）。</summary>
    private bool _pageAlive;

    /// <summary>
    /// 全屏编排（全屏窗口 + 键盘接管 + 宿主渲染挂起），宿主固定为唯一那一格。
    /// </summary>
    private RdpFullScreenCoordinator? _fullScreen;

    /// <summary>运行信息栏展开时的宽度上限（px）。实际宽度按页面宽度比例算，见 ApplyEventsColumnWidth。</summary>
    private const double ExpandedEventsColumnWidthMax = 420;

    /// <summary>展开时的宽度下限：再窄 TabView 里的日志行就没法看了。</summary>
    private const double ExpandedEventsColumnWidthMin = 260;

    /// <summary>
    /// 收起后的宽度 = 左内边距 12 + 那个 32×32 的图标。
    /// 为什么不是更窄：图标必须停在**展开时它所在的那个位置**（x=12），
    /// 内边距一撤，图标就跟着往左跳 12px，切换时看着像"按钮挪窝了"。
    /// ⚠️ 不能收成 0 —— 展开按钮就在栏里，栏宽归零按钮跟着消失，就再也展不开了。
    /// </summary>
    private const double CollapsedEventsColumnWidth = 44;

    /// <summary>
    /// 展开态的内边距（与 XAML 里 <c>Padding="12,8"</c> 一致）。
    /// 收起时改成只留左边 12px —— 图标位置不变，右半边整块省掉。
    /// </summary>
    private static readonly Thickness ExpandedEventsPanelPadding = new(12, 8, 12, 8);

    /// <summary>收起态的内边距：左右不对称，就为了让图标停在展开时那个 x 上。</summary>
    private static readonly Thickness CollapsedEventsPanelPadding = new(12, 8, 0, 8);

    /// <summary>
    /// 右栏宽度的动画时长（秒）。再长显得拖沓，再短就看不出是"滑"过去的。
    /// 动画的是列宽本身（逐帧写 <see cref="EventsColumn"/>），不是视觉变换 ——
    /// 因为要让画面真的跟着变宽，只动 Visual 会溢出到画面上去。
    /// </summary>
    private const double EventsWidthAnimationSeconds = 0.18;

    /// <summary>右栏宽度动画的计时器。<b>必须挂字段</b> —— 局部变量会被 GC，Tick 就再也不来了。</summary>
    private DispatcherQueueTimer? _eventsWidthAnimator;

    private double _animFromWidth;
    private double _animToWidth;
    private DateTimeOffset _animStartedAt;
    private Action? _animCompleted;

    /// <summary>运行信息栏是否收起（收起后整栏只剩一个图标，画面多占约 332px）。</summary>
    private bool _eventsCollapsed;

    /// <summary>拖动分隔条：按下那一刻的右栏宽度（null = 当前没在拖）。</summary>
    private double? _dragStartWidth;

    /// <summary>拖动分隔条：按下那一刻的指针 X（页面坐标）。</summary>
    private double _dragStartPointerX;

    /// <summary>
    /// 页面自身的属性变化通知。
    ///
    /// 这些绑定属性都是从 <see cref="_session"/> 现算的，页面不持有可变状态，
    /// 所以不逐个属性发通知 —— 1Hz 刷一拍、报"全体变了"（属性名传 null）就够了，
    /// 也顺手让 x:Bind 的 OneWay 变得名正言顺（否则编译器会报 WMC1506）。
    /// </summary>
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    public RdpChannelPage()
    {
        InitializeComponent();

        // 运行信息栏的宽度跟着页面宽度走（见 ApplyEventsColumnWidth）：
        // 固定值在最小窗口（960）下会把画面挤没，在大屏上又白占地方。
        SizeChanged += (_, _) => ApplyEventsColumnWidth();

        ExternalLogList.AddHandler(UIElement.PointerWheelChangedEvent,
            new PointerEventHandler(OnLogPointerWheel), true);

        Cell0.PopOutRequested += OnCellPopOut;
        Cell0.FullScreenRequested += OnCellFullScreen;
        Cell0.DisconnectRequested += OnCellDisconnect;
        Cell0.ConnectRequested += OnCellConnect;

        // 画面诊断日志（D3D 初始化成功/失败、渲染路径、Present 失败）接进运行日志。
        // 不接的话通道页——也就是用户真正看画面的地方——这些关键信息全被丢掉，
        // 出撕裂时根本分不清走的是 D3D 还是软渲染回退。
        Cell0.View.DiagnosticLog = App.ViewModel.AppendLog;

        // 画面上的 F11 也走同一条全屏路径（浮层按钮只是显式入口）
        _fullScreen = new RdpFullScreenCoordinator(Cell0.View, App.ViewModel.AppendLog, OnFullScreenStateChanged);

        // 本页只有一格，全屏按钮常开
        Cell0.ShowFullScreenButton = true;

        // 锁定 / 静音这类开关变了要立刻反映到顶部条，不能等下一拍 1Hz
        VM.SurfaceRefreshRequested += OnSurfaceRefreshRequested;
    }

    private void OnSurfaceRefreshRequested()
        => DispatcherQueue.TryEnqueue(() =>
        {
            if (_pageAlive)
            {
                RefreshSurface();
            }
        });

    private void RaiseAllChanged() =>
        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(null));

    private MainViewModel VM => App.ViewModel;

    // ---------------- 绑定属性（1Hz 由 Bindings.Update() 推送） ----------------

    /// <summary>本页对应的通道配置（<c>_sessions</c> 里没有时回退到配置）。</summary>
    private RdpChannel? Channel =>
        _session?.Channel
        ?? VM.Config.Rdp.Channels.FirstOrDefault(c =>
            string.Equals(c.Id, _channelId, StringComparison.OrdinalIgnoreCase));

    public string ChannelName =>
        _session?.DisplayName
        ?? (Channel?.DisplayName ?? (string.IsNullOrEmpty(_channelId) ? "会话通道" : _channelId));

    public string TargetText
    {
        get
        {
            var channel = Channel;
            if (channel is null)
            {
                return string.IsNullOrEmpty(_channelId)
                    ? "通道不存在或已从配置里删除。"
                    : $"通道「{_channelId}」已从配置里删除。";
            }

            var host = string.IsNullOrWhiteSpace(channel.Host) ? "本机" : channel.Host;
            var user = string.IsNullOrWhiteSpace(channel.User) ? "（未配账户）" : channel.User;
            var bridgeDir = _session?.Bridge.BridgeDir
                ?? RdpChannelPaths.Resolve(channel.BridgePath, channel.User);
            return $"{host}  /  {user}      桥目录：{bridgeDir}";
        }
    }

    // 进度卡已移除（用户要求：那块"任务：－ / 已连接"的横条不再显示，画面让位）。
    // 进度 / 心跳 / 状态这些数据仍在 _session 上，主窗口右侧的执行状态栏与多画面页照常显示，
    // 只是本页不再复述一遍 —— 想加回来时从 _session 现算即可（这里是 1Hz 刷新的）。

    public ObservableCollection<RdpTaskEvent> Events => _session?.Events ?? _emptyEvents;

    public ObservableCollection<RdpLogRow> ExternalLogRows => _externalLogRows;

    public string LiveLogStatusText => _session?.LiveLogStatusText ?? "开始执行后显示本通道的脚本日志。";

    // ---------------- 生命周期 ----------------

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        _channelId = e.Parameter as string ?? string.Empty;
        _session = VM.Sessions.FirstOrDefault(s =>
            s.IsRemote && string.Equals(s.ChannelId, _channelId, StringComparison.OrdinalIgnoreCase));

        // 通道存在就以其 id 为准（大小写 / 空白归一）
        if (_session is not null)
        {
            _channelId = _session.ChannelId;
        }

        _pageAlive = true;
        SyncExternalLogs();
        RefreshSurface();
        RaiseAllChanged();

        // 与主控端 1Hz 轮询同节奏刷新展示，避免每通道再开一套通知机制
        _refreshTimer = DispatcherQueue.CreateTimer();
        _refreshTimer.Interval = TimeSpan.FromSeconds(1);
        _refreshTimer.IsRepeating = true;
        _refreshTimer.Tick += (_, _) =>
        {
            // 页面已经离开（或正在被销毁）时不要再碰可视树：
            // 定时器的 Tick 可能排在 OnNavigatedFrom 之后，那时再改 ToolTip / 文本
            // 就是在已脱离的页面上做 XAML 操作（2026-10-27 21:47 就是这样崩的）。
            if (!_pageAlive || !IsLoaded)
            {
                return;
            }

            _session = VM.Sessions.FirstOrDefault(s =>
                s.IsRemote && string.Equals(s.ChannelId, _channelId, StringComparison.OrdinalIgnoreCase));
            SyncExternalLogs();
            RefreshSurface();
            RaiseAllChanged();
        };
        _refreshTimer.Start();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        _pageAlive = false;
        _refreshTimer?.Stop();
        _refreshTimer = null;

        // 宽度动画的 Tick 会改列定义，页面走了还跑就是白烧 CPU（也可能碰到已释放的控件）
        StopEventsWidthAnimation();

        VM.SurfaceRefreshRequested -= OnSurfaceRefreshRequested;

        // 只解绑订阅、不结束全屏：全屏窗口是独立窗口，切页不该把它关掉
        _fullScreen?.Detach();

        // 只解挂、不断开：会话与画面流照旧，切回来（或弹出窗口）继续用同一个 client
        Cell0.SetClient(null);

        base.OnNavigatedFrom(e);
    }

    // ---------------- 画面（本通道一格） ----------------

    /// <summary>
    /// 把本通道这一格挂上/解下画面并刷新角标。每拍调用安全：
    /// <see cref="RdpSurfaceCell.SetClient"/> 对同一实例会短路，不会把画面重挂成闪断。
    /// </summary>
    private void RefreshSurface()
    {
        if (string.IsNullOrEmpty(_channelId))
        {
            Cell0.Assign(string.Empty, string.Empty);
            Cell0.SetClient(null);
            SurfaceEmptyText.Text = "没有指定通道。请从左侧菜单「会话通道」下面选一条通道。";
            SurfaceEmptyHint.Visibility = Visibility.Visible;
            return;
        }

        Cell0.Assign(_channelId, VM.ChannelDisplayName(_channelId));

        // 画面已被独立窗口占用的通道：本格让位（两个渲染器不能挂同一个 client）
        Cell0.SetClient(VM.IsSurfacePoppedOut(_channelId) ? null : VM.GetSurfaceClient(_channelId));
        Cell0.UpdateChrome();

        // 这一格自己会说明"没画面的原因"（占位文案），这里只在通道配置都没了时补一句
        var channelGone = Channel is null && _session is null;
        SurfaceEmptyHint.Visibility = channelGone ? Visibility.Visible : Visibility.Collapsed;
        if (channelGone)
        {
            SurfaceEmptyText.Text = $"通道「{_channelId}」已从配置里删除，这一页只剩历史事件。";
        }
    }

    // ---------------- 交互 ----------------

    private void OnConnectPreviewClicked(object sender, RoutedEventArgs e)
        => VM.ConnectChannelSurface(_channelId);

    private void OnOpenBridgeClicked(object sender, RoutedEventArgs e)
    {
        var dir = _session?.Bridge.BridgeDir
            ?? RdpChannelPaths.Resolve(Channel?.BridgePath, Channel?.User);

        var bridge = RdpBridge.For(dir);
        bridge.EnsureDirectory();
        bridge.OpenBridgeFolder();
    }

    private void OnCellConnect(object? sender, string channelId)
        => VM.ConnectChannelSurface(channelId);

    /// <summary>「断开」：只释放该通道的内嵌连接，目标会话与代理照常保留。</summary>
    private void OnCellDisconnect(object? sender, string channelId)
    {
        VM.DisposeSurface(channelId);
        RefreshSurface();
        RaiseAllChanged();
    }

    /// <summary>「全屏」：新窗口接管本通道的内嵌连接（键盘一并接管，F11 退出）。</summary>
    private void OnCellFullScreen(object? sender, string channelId) => _fullScreen?.Toggle();

    /// <summary>「弹出窗口」：把这路画面交到一个普通独立窗口，用户自己摆放。</summary>
    private void OnCellPopOut(object? sender, string channelId) => PopOutSurface(channelId);

    private void PopOutSurface(string channelId)
    {
        var client = VM.GetSurfaceClient(channelId);
        if (client is null)
        {
            return;
        }

        if (VM.IsSurfacePoppedOut(channelId))
        {
            VM.AppendLog($"[通道：{VM.ChannelDisplayName(channelId)}] 这路画面已经在独立窗口里了。");
            return;
        }

        // 顺序要紧：先把本格让出来（解挂），再让新窗口接管同一个 client，
        // 否则两个渲染器会同时挂在一条会话上互相打架。
        VM.MarkSurfacePoppedOut(channelId, true);
        RefreshSurface();

        var window = new RdpChannelWindow(client, VM.ChannelDisplayName(channelId));

        window.Closed += (_, _) =>
        {
            // 同样先解挂、再归还，页面才会重新接管
            window.DetachView();
            VM.MarkSurfacePoppedOut(channelId, false);

            if (_pageAlive)
            {
                RefreshSurface();
            }
        };

        window.Activate();
        VM.AppendLog($"[通道：{VM.ChannelDisplayName(channelId)}] 画面已弹出到独立窗口。");
    }

    /// <summary>
    /// 运行信息栏的收起 / 展开。
    ///
    /// 收起时**整栏一起收窄**（不只是隐藏列表）：这个栏在画面右边，留着 360px 的空白
    /// 等于白占画面宽度。窄栏里只留一个图标 —— 所以宽度不能收成 0，
    /// 否则那个图标跟着消失，就再也点不回来了。
    ///
    /// 宽度变化走动画（<see cref="AnimateEventsColumnWidth"/>），收起 / 展开两个图标
    /// 都固定在同一格里，切换时位置不动，只换箭头方向。
    /// </summary>
    private void OnToggleEventsClicked(object sender, RoutedEventArgs e)
    {
        _eventsCollapsed = !_eventsCollapsed;

        EventsTabs.Visibility = _eventsCollapsed ? Visibility.Collapsed : Visibility.Visible;
        EventsTitle.Visibility = _eventsCollapsed ? Visibility.Collapsed : Visibility.Visible;
        FollowExternalLogsToggle.Visibility = _eventsCollapsed ? Visibility.Collapsed : Visibility.Visible;
        CollapseEventsButton.Visibility = _eventsCollapsed ? Visibility.Collapsed : Visibility.Visible;
        ExpandEventsButton.Visibility = _eventsCollapsed ? Visibility.Visible : Visibility.Collapsed;

        if (_eventsCollapsed)
        {
            // 先让栏滑到窄位，到位后再撤卡片底与内边距 ——
            // 反过来做的话，那一条会先"空掉"再慢慢收，看着像闪了一下。
            AnimateEventsColumnWidth(CollapsedEventsColumnWidth, ApplyCollapsedPanelChrome);
            return;
        }

        // 展开：卡片样式先装回去（否则长出来的是一块透明区域），再让宽度长出来
        ApplyExpandedPanelChrome();
        EventsSplitter.Visibility = Visibility.Visible;
        AnimateEventsColumnWidth(ClampExpandedWidth(ExpandedEventsTargetWidth()));
    }

    /// <summary>展开态的目标宽度：用户拖过的值优先，否则按页面宽度取约 1/3。</summary>
    private double ExpandedEventsTargetWidth()
    {
        var saved = VM.Config.Window.ChannelEventsWidth;
        return saved > 0 ? saved : ActualWidth * 0.34;
    }

    /// <summary>把展开宽度钳到有效区间：下限保日志可读，上限保画面不被压没。</summary>
    private double ClampExpandedWidth(double width) => Math.Clamp(
        width,
        ExpandedEventsColumnWidthMin,
        Math.Min(ExpandedEventsColumnWidthMax, Math.Max(ExpandedEventsColumnWidthMin, ActualWidth * 0.6)));

    /// <summary>展开态的外观：卡片底 + 边框 + 对称内边距，全部交给 YomoriCardStyle 与本地的 Padding。</summary>
    private void ApplyExpandedPanelChrome()
    {
        EventsPanel.Padding = ExpandedEventsPanelPadding;
        EventsPanel.ClearValue(Border.BackgroundProperty);
        EventsPanel.ClearValue(Border.BorderThicknessProperty);
    }

    /// <summary>收起态的外观：只留左边 12px 内边距（图标位置与展开态一致），卡片底与边框撤掉。</summary>
    private void ApplyCollapsedPanelChrome()
    {
        EventsPanel.Padding = CollapsedEventsPanelPadding;
        EventsPanel.Background = null;
        EventsPanel.BorderThickness = new Thickness(0);

        // 分隔条此刻拖不动（见 OnSplitterPointerPressed），留着只是多占 8px
        EventsSplitter.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// 按当前状态把右栏宽度落到列定义上（无动画）。
    ///
    /// 三个来源按优先级：收起（固定窄条）→ 用户拖过的宽度（存配置里）→ 页面宽度的约 1/3。
    /// 挂在 SizeChanged 上，所以窗口缩放时不会跑出有效范围。**只在页面里生效的前提**是：
    /// 拖动过程中的中间值不走这里（否则每次 SizeChanged 都会被"用户宽度"覆盖回去，
    /// 手指一拖就弹回原位）。
    /// </summary>
    private void ApplyEventsColumnWidth()
    {
        // 窗口缩放 / 重新布局时以最终值为准，进行中的动画让位（否则它会和新的目标值打架）
        StopEventsWidthAnimation();

        if (_eventsCollapsed)
        {
            ApplyCollapsedPanelChrome();
            SetEventsColumnWidth(CollapsedEventsColumnWidth);
            return;
        }

        ApplyExpandedPanelChrome();
        EventsSplitter.Visibility = Visibility.Visible;
        SetEventsColumnWidth(ExpandedEventsTargetWidth());
    }

    /// <summary>
    /// 逐帧把列宽从当前值动画到目标值（ease-out cubic，180ms）。
    ///
    /// <para>为什么逐帧写列宽而不做 XAML 动画：<c>ColumnDefinition.Width</c> 不是依赖属性，
    /// Storyboard 动不了；而动画 Visual 的尺寸只会让内容溢出到画面上 —— 这里要的是
    /// **画面真的变宽**，只能改布局。</para>
    ///
    /// <para>逐帧写 <see cref="GridLength"/> 会触发布局，但一次动画只有十来帧、
    /// 每次只算一列，代价可以忽略。</para>
    /// </summary>
    /// <param name="target">目标宽度（px）。</param>
    /// <param name="onCompleted">动画到位后执行（放在这里是为了"缩完再撤样式"）。</param>
    private void AnimateEventsColumnWidth(double target, Action? onCompleted = null)
    {
        StopEventsWidthAnimation();

        var from = EventsColumn?.Width.IsAbsolute == true ? EventsColumn.Width.Value : 0;
        if (Math.Abs(target - from) < 1)
        {
            SetEventsColumnWidthRaw(target);
            onCompleted?.Invoke();
            return;
        }

        _animFromWidth = from;
        _animToWidth = target;
        _animStartedAt = DateTimeOffset.UtcNow;
        _animCompleted = onCompleted;

        var timer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(16);
        timer.IsRepeating = true;
        timer.Tick += (_, _) => OnEventsWidthAnimationTick();
        _eventsWidthAnimator = timer;
        timer.Start();
    }

    private void OnEventsWidthAnimationTick()
    {
        var elapsed = (DateTimeOffset.UtcNow - _animStartedAt).TotalSeconds;
        var progress = Math.Clamp(elapsed / EventsWidthAnimationSeconds, 0, 1);
        var eased = 1 - Math.Pow(1 - progress, 3);

        SetEventsColumnWidthRaw(_animFromWidth + (_animToWidth - _animFromWidth) * eased);

        if (progress < 1)
        {
            return;
        }

        var completed = _animCompleted;
        StopEventsWidthAnimation();
        completed?.Invoke();
    }

    /// <summary>停掉进行中的宽度动画（重复点击、窗口缩放、页面离开都要停）。</summary>
    private void StopEventsWidthAnimation()
    {
        if (_eventsWidthAnimator is { } timer)
        {
            timer.Stop();
            _eventsWidthAnimator = null;
        }

        _animCompleted = null;
    }

    /// <summary>动画专用的写入口：绕过钳制，保留帧间的小数宽度。</summary>
    private void SetEventsColumnWidthRaw(double width)
    {
        if (EventsColumn is null)
        {
            return;
        }

        if (Math.Abs(EventsColumn.Width.Value - width) >= 0.1)
        {
            EventsColumn.Width = new GridLength(width);
        }
    }

    /// <summary>
    /// 右栏宽度的唯一出口：先按窗口实际宽度钳制再写列定义。
    /// 下限保底是为了 TabView 里的日志行不至于挤成一列字，
    /// 上限取「页面宽度的 60%」与绝对上限的较小值 —— 窗口窄的时候不能把画面压没。
    /// </summary>
    private void SetEventsColumnWidth(double width)
    {
        if (EventsColumn is null)
        {
            return;
        }

        var clamped = _eventsCollapsed ? CollapsedEventsColumnWidth : ClampExpandedWidth(width);

        if (Math.Abs(EventsColumn.Width.Value - clamped) >= 0.5)
        {
            EventsColumn.Width = new GridLength(clamped);
        }
    }

    private void OnSplitterPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        // 收起状态下不拖：右栏只剩一条窄条，拖动没有意义，还会和「展开」按钮抢事件
        if (_eventsCollapsed || !EventsSplitter.CapturePointer(e.Pointer))
        {
            return;
        }

        _dragStartWidth = EventsColumn.Width.Value;
        _dragStartPointerX = e.GetCurrentPoint(this).Position.X;
        e.Handled = true;
    }

    private void OnSplitterPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_dragStartWidth is not double start)
        {
            return;
        }

        // 指针往右 = 分隔条往右 = 右栏变窄，所以是"起点宽度 - 位移"
        var x = e.GetCurrentPoint(this).Position.X;
        SetEventsColumnWidth(start - (x - _dragStartPointerX));
        e.Handled = true;
    }

    private void OnSplitterPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_dragStartWidth is null)
        {
            return;
        }

        _dragStartWidth = null;
        EventsSplitter.ReleasePointerCapture(e.Pointer);
        e.Handled = true;

        // 松手才落盘：拖动过程中每一帧都写 config.json 是不可接受的（每帧一次磁盘 IO）
        var width = (int)Math.Round(EventsColumn.Width.Value);
        if (VM.Config.Window.ChannelEventsWidth == width)
        {
            return;
        }

        VM.Config.Window.ChannelEventsWidth = width;
        VM.SaveConfig();
    }

    private void OnClearExternalLogsClicked(object sender, RoutedEventArgs e)
    {
        _clearThroughSeq = (_session?.ExternalLogs ?? _emptyExternalLogs).Select(l => l.Seq).DefaultIfEmpty(0).Max();
        _externalLogRows.Clear();
    }

    private void OnFollowExternalLogsChanged(object sender, RoutedEventArgs e)
    {
        if (FollowExternalLogsToggle?.IsOn == true)
        {
            ScrollExternalLogsToBottom();
        }
    }

    private void OnEventsTabChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.AddedItems.OfType<TabViewItem>().Contains(ExternalLogsTab) && FollowExternalLogsToggle?.IsOn == true)
        {
            ScrollExternalLogsToBottom();
        }
    }

    private void OnLogPointerWheel(object sender, PointerRoutedEventArgs e)
    {
        if (e.GetCurrentPoint(ExternalLogList).Properties.MouseWheelDelta > 0)
            FollowExternalLogsToggle.IsOn = false;
    }

    private void OnLogKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key is Windows.System.VirtualKey.Up or Windows.System.VirtualKey.PageUp or Windows.System.VirtualKey.Home)
            FollowExternalLogsToggle.IsOn = false;
    }

    private void OnLogLevelChanged(object sender, SelectionChangedEventArgs e) => SyncExternalLogs();

    private void SyncExternalLogs()
    {
        if (ExternalLogList is null) return;
        var source = _session?.ExternalLogs ?? _emptyExternalLogs;
        var commandId = source.FirstOrDefault()?.CommandId ?? _session?.CommandId ?? string.Empty;
        if (!ReferenceEquals(_renderedLogSession, _session) || commandId != _renderedCommandId)
        {
            _renderedLogSession = _session;
            _renderedCommandId = commandId;
            _clearThroughSeq = 0;
            _externalLogRows.Clear();
        }
        var level = (LogLevelFilter?.SelectedItem as ComboBoxItem)?.Tag as string ?? "all";
        var rows = source.Where(l => l.Seq > _clearThroughSeq &&
            (level == "all" || l.Level == level || level == "warn" && l.Level == "error"))
            .Select(l => new RdpLogRow(l)).ToList();
        // Incremental reconciliation preserves the user's scroll position while following is off.
        var desired = rows.Select(r => r.Seq).ToHashSet();
        for (var i = _externalLogRows.Count - 1; i >= 0; i--)
            if (!desired.Contains(_externalLogRows[i].Seq)) _externalLogRows.RemoveAt(i);
        for (var i = 0; i < rows.Count; i++)
            if (i >= _externalLogRows.Count || _externalLogRows[i].Seq != rows[i].Seq)
                _externalLogRows.Insert(i, rows[i]);
        if (FollowExternalLogsToggle?.IsOn == true && ReferenceEquals(EventsTabs?.SelectedItem, ExternalLogsTab))
            ScrollExternalLogsToBottom();
    }

    private void ScrollExternalLogsToBottom()
    {
        if (ExternalLogList is null || ExternalLogList.Items.Count == 0)
        {
            return;
        }

        DispatcherQueue.TryEnqueue(() =>
        {
            if (ExternalLogList.Items.Count > 0)
            {
                ExternalLogList.ScrollIntoView(ExternalLogList.Items[ExternalLogList.Items.Count - 1]);
            }
        });
    }

    /// <summary>全屏状态变化：切本格的按钮文案（画面本身由协调器挂起/恢复渲染）。</summary>
    private void OnFullScreenStateChanged(bool active) => Cell0.SetFullScreenActive(active);
}
