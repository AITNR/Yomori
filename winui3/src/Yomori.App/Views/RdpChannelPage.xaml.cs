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
    /// 收起后的宽度：只放得下「展开」按钮。
    /// ⚠️ 不能收成 0 —— 那个按钮就在栏里，栏宽归零按钮跟着消失，就再也展不开了。
    /// </summary>
    private const double CollapsedEventsColumnWidth = 96;

    /// <summary>运行信息栏是否收起（收起后整栏收窄，画面多占约 264px）。</summary>
    private bool _eventsCollapsed;

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

    public string StatusText => _session?.StatusText ?? VM.SurfaceStateText(_channelId);

    public string HeartbeatText => _session is null ? "－" : $"心跳：{_session.HeartbeatText}";

    public string TaskText => _session is null ? "任务：－" : $"任务：{_session.TaskDisplay}";

    public string ProgressText => _session is null ? "－" : $"{_session.Progress} / {_session.Total}";

    public string ElapsedText => _session?.ElapsedText ?? "00:00";

    public double ProgressValue => _session?.Progress ?? 0;

    public double ProgressMax => Math.Max(1, _session?.Total ?? 1);

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
    /// 等于白占画面宽度。窄栏里只留「展开」按钮 —— 所以宽度不能收成 0，
    /// 否则那个按钮跟着消失，就再也点不回来了。
    /// </summary>
    private void OnToggleEventsClicked(object sender, RoutedEventArgs e)
    {
        _eventsCollapsed = !_eventsCollapsed;

        EventsTabs.Visibility = _eventsCollapsed ? Visibility.Collapsed : Visibility.Visible;
        EventsTitle.Visibility = _eventsCollapsed ? Visibility.Collapsed : Visibility.Visible;
        FollowExternalLogsToggle.Visibility = _eventsCollapsed ? Visibility.Collapsed : Visibility.Visible;
        ToggleEventsButton.Content = _eventsCollapsed ? "展开" : "收起";

        ApplyEventsColumnWidth();
    }

    /// <summary>
    /// 按页面宽度定右栏宽度：固定值在两种极端上都难看 ——
    /// 窗口只有 960（最小宽度）时 360px 会把画面挤没，而大屏上又白白浪费宽度。
    /// 所以取约三分之一并夹在 [260, 420] 之间；收起时固定窄条。
    /// </summary>
    private void ApplyEventsColumnWidth()
    {
        var width = _eventsCollapsed
            ? CollapsedEventsColumnWidth
            : Math.Clamp(ActualWidth * 0.34, ExpandedEventsColumnWidthMin, ExpandedEventsColumnWidthMax);

        if (EventsColumn is null || Math.Abs(EventsColumn.Width.Value - width) < 0.5)
        {
            return;
        }

        EventsColumn.Width = new GridLength(width);
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
