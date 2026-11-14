// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Navigation;
using Yomori.ViewModels;

namespace Yomori.Views;

/// <summary>
/// 「会话通道」的多画面页（计划书 docs/channel-menu-multiview-plan.md §4.2）：
/// 左侧菜单点父项「会话通道」或子项「多画面（全部通道）」进来的**全部通道同屏**页面。
///
/// 网格逻辑原先长在 <see cref="RdpChannelPage"/> 里、还要靠一个「单画面 / 多画面」开关切换 ——
/// "在一条通道里看全部通道"本身概念就是拧的，现在整块搬到这里，通道页只管自己那一路。
///
/// <list type="bullet">
/// <item>网格 2×2、每页最多 4 格（4 路同时渲染是有意的上限），>4 分页；</item>
/// <item>列出的通道 = 配置里启用中的 + 本轮在跑的（见 <see cref="MainViewModel.SurfaceGridChannelIds"/>），
///       没连接的格子会给「连接到这条通道」，所以不跑任务时也能先看画面；</item>
/// <item>每格可「弹出窗口」到独立窗口，画面里按 F11 同样是弹窗；</item>
/// <item><b>本页不给全屏</b>：全屏会盖掉别的路 —— 要放大某一路请进它自己的通道页。</item>
/// </list>
/// </summary>
public sealed partial class RdpMultiViewPage : Page
{
    /// <summary>网格每页的格数（2×2）。</summary>
    private const int GridPageSize = 4;

    private readonly List<RdpSurfaceCell> _cells = new();

    private DispatcherQueueTimer? _refreshTimer;

    /// <summary>本页是否还挂在可视树里（弹出窗口关闭回调据此决定要不要立即刷界面）。</summary>
    private bool _pageAlive;

    /// <summary>分页页码（0 起）。</summary>
    private int _gridPage;

    /// <summary>翻页时才给网格播入场动画；1Hz 重排不播（否则每秒闪一次）。</summary>
    private bool _animateNextLayout;

    /// <summary>网格入场动画挂字段保活（Storyboard 不自持引用）。</summary>
    private Storyboard? _surfaceLayoutBoard;

    public RdpMultiViewPage()
    {
        InitializeComponent();

        _cells.AddRange(new[] { Cell0, Cell1, Cell2, Cell3 });
        foreach (var cell in _cells)
        {
            // 同通道页：把画面诊断日志接进运行日志，否则多画面下看不出渲染路径
            cell.View.DiagnosticLog = App.ViewModel.AppendLog;
            cell.PopOutRequested += OnCellPopOut;
            // F11 = 弹成独立窗口（多画面下没有全屏这一说）
            cell.FullScreenRequested += OnCellPopOut;
            cell.DisconnectRequested += OnCellDisconnect;
            cell.ConnectRequested += OnCellConnect;
        }

        // SurfaceRefreshRequested 改在 OnNavigatedTo 成对订阅：OnNavigatedFrom 会退订，
        // 只在构造函数订一次的话，切页回来后这条即时刷新就永久失效。
    }

    private void OnSurfaceRefreshRequested()
        => DispatcherQueue.TryEnqueue(() =>
        {
            if (_pageAlive)
            {
                LayoutSurfaces();
            }
        });

    private static MainViewModel VM => App.ViewModel;

    // ---------------- 生命周期 ----------------

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        // 与 OnNavigatedFrom 成对：先退后订保证重复导航时也只订一次。
        VM.SurfaceRefreshRequested -= OnSurfaceRefreshRequested;
        VM.SurfaceRefreshRequested += OnSurfaceRefreshRequested;

        _pageAlive = true;
        _gridPage = 0;
        LayoutSurfaces();

        // 与主控端 1Hz 轮询同节奏刷新展示。重复导航先停掉上一实例，
        // 否则旧定时器不会自动回收，会累积成每拍多次重排。
        _refreshTimer?.Stop();
        _refreshTimer = null;
        _refreshTimer = DispatcherQueue.CreateTimer();
        _refreshTimer.Interval = TimeSpan.FromSeconds(1);
        _refreshTimer.IsRepeating = true;
        _refreshTimer.Tick += (_, _) => LayoutSurfaces();
        _refreshTimer.Start();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        _pageAlive = false;
        _animateNextLayout = false;
        // 页面卸载后动画回调不能再碰控件
        CancelSurfaceLayoutAnimation();
        _refreshTimer?.Stop();
        _refreshTimer = null;
        VM.SurfaceRefreshRequested -= OnSurfaceRefreshRequested;

        // 只解挂、不断开：会话与画面流照旧，切回来继续用同一个 client
        foreach (var cell in _cells)
        {
            cell.SetClient(null);
        }

        base.OnNavigatedFrom(e);
    }

    // ---------------- 画面网格 ----------------

    /// <summary>
    /// 主窗口收进状态栏时暂停 1Hz 重排。
    ///
    /// <para>窗口隐藏期间各格位的实际尺寸可能变成 0，那一拍纯属白跑；更要紧的是
    /// <see cref="LayoutSurfaces"/> 会去碰画面元素的尺寸与可见性 —— 隐藏时这些值不可信，
    /// 属于"摸到失效对象"的高风险路径（见 MEMORY 里多画面页 1Hz 重排的崩溃记录）。
    /// 只停不拆：会话与画面流都保留，恢复时接着用同一个 client。</para>
    /// </summary>
    public void SuspendRefresh() => _refreshTimer?.Stop();

    /// <summary>主窗口恢复时重启重排，并立刻补一拍（否则要等最多 1 秒画面才对齐）。</summary>
    public void ResumeRefresh()
    {
        if (!_pageAlive)
        {
            return;
        }

        LayoutSurfaces();
        _refreshTimer?.Start();
    }

    /// <summary>
    /// 按当前页摆放格位、挂上各通道的内嵌画面，并刷新角标。
    /// 每拍调用是安全的：<see cref="RdpSurfaceCell.SetClient"/> 对同一实例会短路，不会把画面重挂成闪断。
    /// </summary>
    private void LayoutSurfaces()
    {
        // 定时器的一拍可能排在 OnNavigatedFrom 之后：那时页面已离开，再改画面元素的
        // 尺寸 / 可见性 / tooltip 都是作用在脱离的树上。
        // 这里只判 _pageAlive 而不判 IsLoaded —— OnNavigatedTo 里第一次调用时页面
        // 还没上屏（IsLoaded 为 false），判 IsLoaded 会把首次布局也挡掉。
        if (!_pageAlive)
        {
            return;
        }

        var ids = VM.SurfaceGridChannelIds;

        var pageCount = Math.Max(1, (ids.Count + GridPageSize - 1) / GridPageSize);
        _gridPage = Math.Clamp(_gridPage, 0, pageCount - 1);

        var pageIds = ids.Skip(_gridPage * GridPageSize).Take(GridPageSize).ToList();

        foreach (var cell in _cells)
        {
            cell.Visibility = Visibility.Collapsed;
            // 多画面不给全屏（会盖掉别的路），用「弹出窗口」代替
            cell.ShowFullScreenButton = false;
        }

        for (var i = 0; i < _cells.Count; i++)
        {
            var cell = _cells[i];

            if (i >= pageIds.Count)
            {
                // 本页用不到的格位：解挂，别让它在后台白渲染
                cell.SetClient(null);
                continue;
            }

            var id = pageIds[i];

            cell.Assign(id, VM.ChannelDisplayName(id));
            PlaceCell(cell, i, pageIds.Count);

            // 画面已被独立窗口占用的通道：本格让位（两个渲染器不能挂同一个 client）
            cell.SetClient(VM.IsSurfacePoppedOut(id) ? null : VM.GetSurfaceClient(id));
            cell.UpdateChrome();
        }

        // 分页条：不止一页时出现
        PagerBar.Visibility = pageCount > 1 ? Visibility.Visible : Visibility.Collapsed;
        PageText.Text = $"{_gridPage + 1} / {pageCount}";
        PrevPageButton.IsEnabled = _gridPage > 0;
        NextPageButton.IsEnabled = _gridPage < pageCount - 1;

        // 一条可显示的通道都没有时的空态
        var empty = ids.Count == 0;
        SurfaceEmptyHint.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        if (empty)
        {
            SurfaceEmptyText.Text = VM.IsEmbeddedMode
                ? "还没有可显示的通道。到「通道管理…」新建并启用一条通道，或者直接开始执行任务。"
                : "当前客户端模式是「独立窗口（mstsc）」，画面在系统自带的远程桌面窗口里，应用内不显示。";
        }

        MultiViewHintText.Text = VM.IsEmbeddedMode
            ? "这里并列显示全部通道的画面；每格右上角可把它弹成独立窗口，想单独放大请看左侧菜单里它自己的那一项。"
            : "当前客户端模式是「独立窗口（mstsc）」，画面在系统自带的远程桌面窗口里；要在这里并排看画面，请到「设置 → 执行与通知」把连接方式改成「内嵌」。";

        // 只在翻页那一拍播入场动画；1Hz 的常规重排不播，否则每秒闪一次。
        if (_animateNextLayout)
        {
            _animateNextLayout = false;
            PlaySurfaceLayoutEntrance();
        }
    }

    /// <summary>把第 index 格放进 2×2 网格里合适的位置（1 / 2 / 3 格时自动跨行列）。</summary>
    private static void PlaceCell(RdpSurfaceCell cell, int index, int count)
    {
        switch (count)
        {
            case 1:
                Grid.SetRow(cell, 0);
                Grid.SetColumn(cell, 0);
                Grid.SetRowSpan(cell, 2);
                Grid.SetColumnSpan(cell, 2);
                break;

            case 2:
                Grid.SetRow(cell, 0);
                Grid.SetColumn(cell, index);
                Grid.SetRowSpan(cell, 2);
                Grid.SetColumnSpan(cell, 1);
                break;

            case 3 when index == 0:
                Grid.SetRow(cell, 0);
                Grid.SetColumn(cell, 0);
                Grid.SetRowSpan(cell, 2);
                Grid.SetColumnSpan(cell, 1);
                break;

            case 3:
                Grid.SetRow(cell, index == 1 ? 0 : 1);
                Grid.SetColumn(cell, 1);
                Grid.SetRowSpan(cell, 1);
                Grid.SetColumnSpan(cell, 1);
                break;

            default:
                Grid.SetRow(cell, index / 2);
                Grid.SetColumn(cell, index % 2);
                Grid.SetRowSpan(cell, 1);
                Grid.SetColumnSpan(cell, 1);
                break;
        }

        cell.Visibility = Visibility.Visible;
    }

    // ---------------- 交互 ----------------

    private void OnPrevPageClicked(object sender, RoutedEventArgs e)
    {
        _gridPage = Math.Max(0, _gridPage - 1);
        _animateNextLayout = true;
        LayoutSurfaces();
    }

    private void OnNextPageClicked(object sender, RoutedEventArgs e)
    {
        _gridPage++;
        _animateNextLayout = true;
        LayoutSurfaces();
    }

    /// <summary>
    /// 翻页后给整个画面网格播入场动画（淡入 + 轻微上移，200ms）。
    /// 只在翻页时播：LayoutSurfaces 每 1Hz 也会跑，那次不播，否则每秒闪一次。
    /// </summary>
    private void PlaySurfaceLayoutEntrance()
    {
        CancelSurfaceLayoutAnimation();

        if (!_pageAlive || !SurfaceLayout.IsLoaded)
        {
            return;
        }

        var duration = new Duration(TimeSpan.FromMilliseconds(200));
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };

        var transform = new CompositeTransform { TranslateY = 10 };
        SurfaceLayout.RenderTransform = transform;
        SurfaceLayout.Opacity = 0;

        var board = new Storyboard();

        var fade = new DoubleAnimation
        {
            To = 1,
            Duration = duration,
            EasingFunction = ease,
        };
        Storyboard.SetTarget(fade, SurfaceLayout);
        Storyboard.SetTargetProperty(fade, "Opacity");

        var slide = new DoubleAnimation
        {
            To = 0,
            Duration = duration,
            EasingFunction = ease,
            EnableDependentAnimation = true,
        };
        Storyboard.SetTarget(slide, transform);
        Storyboard.SetTargetProperty(slide, "TranslateY");

        board.Children.Add(fade);
        board.Children.Add(slide);
        board.Completed += OnSurfaceLayoutAnimationCompleted;

        _surfaceLayoutBoard = board;
        board.Begin();
    }

    /// <summary>停掉网格动画并复原：撤 HoldEnd、摘掉动画专用的 RenderTransform。</summary>
    private void CancelSurfaceLayoutAnimation()
    {
        if (_surfaceLayoutBoard is Storyboard board)
        {
            board.Completed -= OnSurfaceLayoutAnimationCompleted;
            board.Stop();
            _surfaceLayoutBoard = null;
        }

        if (SurfaceLayout.IsLoaded)
        {
            SurfaceLayout.Opacity = 1;
            SurfaceLayout.RenderTransform = null;
        }
    }

    private void OnSurfaceLayoutAnimationCompleted(object? sender, object e) => CancelSurfaceLayoutAnimation();

    private void OnOpenChannelManagerClicked(object sender, RoutedEventArgs e) => VM.Navigate("channel-mgmt");

    /// <summary>给当前网格里还没画面的通道各建一条内嵌连接（只看画面，不下发任务）。</summary>
    private async void OnConnectAllClicked(object sender, RoutedEventArgs e)
    {
        var pending = VM.SurfaceGridChannelIds.Where(id => !VM.HasSurface(id)).ToList();
        if (pending.Count == 0)
        {
            VM.AppendLog("[汇总] 全部通道都已经有画面了。");
            return;
        }

        // 批量连接只问一次「单会话策略」，然后逐条建连 ——
        // 每条各问一次的话，用户会连着看到 N 个同样的对话框。
        await VM.ConfirmSingleSessionBeforeConnectAsync($"「连接全部通道」（{pending.Count} 条）");

        foreach (var id in pending)
        {
            VM.ConnectChannelSurfaceNow(id);
        }

        VM.AppendLog($"[汇总] 正在为 {pending.Count} 条通道建立内嵌画面连接…");
    }

    private void OnCellConnect(object? sender, string channelId) => VM.ConnectChannelSurface(channelId);

    /// <summary>「断开」：只释放该通道的内嵌连接，目标会话与代理照常保留。</summary>
    private void OnCellDisconnect(object? sender, string channelId)
    {
        VM.DisposeSurface(channelId);
        LayoutSurfaces();
    }

    /// <summary>「弹出窗口」：把这路画面交到一个普通独立窗口，用户自己并排摆放。</summary>
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
        LayoutSurfaces();

        var window = new RdpChannelWindow(client, VM.ChannelDisplayName(channelId));

        window.Closed += (_, _) =>
        {
            // 同样先解挂、再归还，页面才会重新接管
            window.DetachView();
            VM.MarkSurfacePoppedOut(channelId, false);

            if (_pageAlive)
            {
                LayoutSurfaces();
            }
        };

        window.Activate();
        VM.AppendLog($"[通道：{VM.ChannelDisplayName(channelId)}] 画面已弹出到独立窗口。");
    }
}
