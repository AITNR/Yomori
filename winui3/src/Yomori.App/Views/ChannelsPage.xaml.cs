// -*- coding: utf-8 -*-
using System;
using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Yomori.Helpers;
using Yomori.Models;
using Yomori.Services;
using Yomori.ViewModels;

namespace Yomori.Views;

/// <summary>
/// 通道管理 · 配置页（计划书 §8.3 / D4；菜单位置见 docs/channel-menu-multiview-plan.md，
/// 由原 <c>RdpPage</c> 改造）。
///
/// 本页负责<b>配</b>：左边选通道（增删改），右边编辑该通道的名称 / 主机 / 账户 /
/// 桥目录 / 凭据 / 分辨率。2026-11-12 起「通道预检」与「全局执行与通知设置」整体迁到
/// <see cref="SettingsPage"/>；每条通道的<b>画面</b>在多画面页
/// （<see cref="RdpMultiViewPage"/>）与各通道子页（<see cref="RdpChannelPage"/>）里。
///
/// 画面控件仍然留在这里（折叠状态）：一是自检模式 <c>--embed-vm</c> 要用，
/// 二是全屏 / 键盘接管那套逻辑与它绑在一起。
/// </summary>
public sealed partial class ChannelsPage : Page
{
    /// <summary>自检模式（<c>--embed-vm host user pwd</c>）下才允许本页自己挂画面。</summary>
    private readonly bool _selfCheckEmbed;

    public ChannelsPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;

        _selfCheckEmbed = Program.EmbedVmArgs is not null;

        // 会话处理（收尾动作）已迁到任务的收尾行 —— 本页不再有那个下拉框（见《任务类型与收尾计划书》§6.6）。
        // 渲染后端 / 解码方式两个下拉连同「执行与通知」卡一起迁到设置页（SettingsPage），
        // 它们的 ItemsSource 初始化与选中值同步也在那边，本页不再碰。

        // 画面控件诊断直接进运行日志（便于无人值守验收）
        EmbedView.DiagnosticLog = VM.AppendLog;

        // 画面上的 F11 请求全屏。全屏编排收在共用类里 ——
        // 通道页用的是同一份（正常模式下画面在那边，全屏也必须从那边进得去）。
        _fullScreen = new RdpFullScreenCoordinator(EmbedView, VM.AppendLog, OnFullScreenStateChanged);

        // 内嵌连接实例变化 → 按钮状态与（自检模式下）画面接管
        VM.EmbedClientChanged += OnEmbedClientChanged;
    }

    public MainViewModel VM => App.ViewModel;

    // ---------------- 生命周期 ----------------

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        VM.PropertyChanged += OnViewModelPropertyChanged;

        // 全局预检（RefreshRdpReadiness）与执行 / 通知设置已迁到设置页。
        // 这里只刷「每通道预检」—— 编辑卡里的凭据 / 授权状态也由它带出来。
        VM.RefreshChannelReadiness();
        SyncChannelChrome();

        if (VM.ActiveEmbedClient is not null)
        {
            ApplyEmbedClientState();
        }

        // M5/M6 自检通道：--embed-vm 走正式 VM 连接路径（client_mode=embedded →
        // ConnectSurface → TryStartEmbeddedConnect → EmbedClientChanged → 画面接管）
        var autoVm = Program.EmbedVmArgs;
        if (autoVm is { Length: 3 })
        {
            CollapseNonEmbedSections();
            VM.RdpSettings.TargetHost = autoVm[0];
            VM.RdpSettings.TargetUser = autoVm[1];
            VM.RdpSettings.ClientMode = ClientModes.Embedded;
            RdpCredentialStore.Write(autoVm[0], autoVm[1], autoVm[2]);
            _ = DispatcherQueue.TryEnqueue(() => VM.ConnectSurface());
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        // 页面卸载后动画回调不能再碰控件
        CancelChannelEditorAnimation();

        // 页面切换（Frame.Navigate 每次新建实例）：解除订阅。
        // 内嵌连接归 VM 持有，不受页面生命周期影响。
        VM.PropertyChanged -= OnViewModelPropertyChanged;
        VM.EmbedClientChanged -= OnEmbedClientChanged;
        // 只解绑订阅，不结束全屏：全屏窗口是独立窗口，切页不该把它关掉
        _fullScreen?.Detach();
        EmbedView.DetachClient();
        _embedStatsTimer?.Stop();
    }

    /// <summary>通道编辑卡入场动画挂字段保活（Storyboard 不自持引用）。</summary>
    private Storyboard? _channelEditorBoard;

    /// <summary>VM 里的通道状态变了就同步一次界面装饰（空列表提示、编辑卡可用态）。</summary>
    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MainViewModel.SelectedChannel):
                SyncChannelChrome();
                // 只有真正换选中通道才播；Channels / ActiveSession 那些 1Hz 刷新别反复闪
                PlayChannelEditorEntrance();
                break;
            case nameof(MainViewModel.Channels):
            case nameof(MainViewModel.ChannelCredentialText):
            case nameof(MainViewModel.ActiveSession):
                SyncChannelChrome();
                break;
        }
    }

    /// <summary>
    /// 切换通道后给右侧编辑卡播入场动画（淡入 + 轻微上移，200ms）。
    /// Opacity 终值沿用 SyncChannelChrome 的语义：没选中通道时是 0.5（变灰），不是 1。
    /// </summary>
    private void PlayChannelEditorEntrance()
    {
        CancelChannelEditorAnimation();

        if (!ChannelEditorCard.IsLoaded)
        {
            return;
        }

        var targetOpacity = VM.SelectedChannel is null ? 0.5 : 1.0;
        var duration = new Duration(TimeSpan.FromMilliseconds(200));
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };

        var transform = new CompositeTransform { TranslateY = 10 };
        ChannelEditorCard.RenderTransform = transform;
        ChannelEditorCard.Opacity = 0;

        var board = new Storyboard();

        var fade = new DoubleAnimation
        {
            To = targetOpacity,
            Duration = duration,
            EasingFunction = ease,
        };
        Storyboard.SetTarget(fade, ChannelEditorCard);
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
        board.Completed += OnChannelEditorAnimationCompleted;

        _channelEditorBoard = board;
        board.Begin();
    }

    /// <summary>停掉编辑卡动画并复原：撤 HoldEnd、摘掉动画专用的 RenderTransform。</summary>
    private void CancelChannelEditorAnimation()
    {
        if (_channelEditorBoard is Storyboard board)
        {
            board.Completed -= OnChannelEditorAnimationCompleted;
            board.Stop();
            _channelEditorBoard = null;
        }

        if (ChannelEditorCard.IsLoaded)
        {
            ChannelEditorCard.Opacity = VM.SelectedChannel is null ? 0.5 : 1.0;
            ChannelEditorCard.RenderTransform = null;
        }
    }

    private void OnChannelEditorAnimationCompleted(object? sender, object e) => CancelChannelEditorAnimation();

    /// <summary>把空列表提示、编辑卡可用态这些"装饰性"状态刷一遍。预检结果归设置页。</summary>
    private void SyncChannelChrome()
    {
        var selected = VM.SelectedChannel;

        ChannelEmptyBar.IsOpen = VM.Channels.Count == 0;

        // Border 没有 IsEnabled（那是 Control 上的），用「点不动 + 变灰」表达同一件事
        ChannelEditorCard.IsHitTestVisible = selected is not null;
        ChannelEditorCard.Opacity = selected is null ? 0.5 : 1.0;

        ChannelEditorHint.Text = selected is null
            ? "未选择通道"
            : $"正在编辑：{selected.DisplayName}";
    }

    /// <summary>自检模式：折叠本页除画面卡以外的全部内容，让画面直接进视口。</summary>
    private void CollapseNonEmbedSections()
    {
        if (EmbedCard.Parent is not Panel root)
        {
            return;
        }

        foreach (var child in root.Children)
        {
            if (child is FrameworkElement fe && !ReferenceEquals(child, EmbedCard))
            {
                fe.Visibility = Visibility.Collapsed;
            }
        }

        // 折叠它的是上面那圈，自己得显出来
        EmbedCard.Visibility = Visibility.Visible;
    }

    // ---------------- 通道增删改 ----------------

    private void OnAddChannel(object sender, RoutedEventArgs e)
    {
        VM.AddChannelCommand.Execute(null);
        SyncChannelChrome();
    }

    private void OnDuplicateChannel(object sender, RoutedEventArgs e)
    {
        VM.DuplicateChannelCommand.Execute(VM.SelectedChannel);
        SyncChannelChrome();
    }

    private void OnRemoveChannel(object sender, RoutedEventArgs e)
    {
        VM.RemoveChannelCommand.Execute(VM.SelectedChannel);
        SyncChannelChrome();
    }

    private void OnSaveChannelEdits(object sender, RoutedEventArgs e)
    {
        VM.SaveChannelEdits();
        SyncChannelChrome();
    }

    /// <summary>
    /// 文本类字段（名称 / 主机 / 账户 / 桥目录）没有"选中即保存"的时机，
    /// 失焦时轻量落盘一次，免得用户填完直接切页面就丢了。
    /// </summary>
    private void OnChannelFieldLostFocus(object sender, RoutedEventArgs e)
    {
        VM.PersistChannelEdits();
        SyncChannelChrome();
    }

    /// <summary>把该通道的主机填回本机环回。</summary>
    private void OnUseLocalHost(object sender, RoutedEventArgs e)
    {
        var channel = VM.SelectedChannel;
        if (channel is null)
        {
            return;
        }

        channel.Host = RdpTargets.DefaultHost;
        VM.PersistChannelEdits();
        VM.RefreshChannelReadiness();
        SyncChannelChrome();
    }

    private void OnOpenChannelBridgeFolder(object sender, RoutedEventArgs e)
    {
        VM.OpenChannelBridgeFolderCommand.Execute(null);
        SyncChannelChrome();
    }

    private void OnSaveChannelCredential(object sender, RoutedEventArgs e)
    {
        VM.SaveChannelCredential(ChannelPasswordInput.Password);
        ChannelPasswordInput.Password = string.Empty;
        SyncChannelChrome();
    }

    private void OnClearChannelCredential(object sender, RoutedEventArgs e)
    {
        VM.ClearChannelCredential();
        SyncChannelChrome();
    }

    /// <summary>跳到任务执行页 —— 那边才是"哪条通道跑哪些任务"的地方（本页只管配通道）。</summary>
    private void OnOpenTasks(object sender, RoutedEventArgs e) => VM.Navigate("tasks");

    // ---------------- 内嵌画面（正常折叠；自检模式与全屏时用到） ----------------

    private void OnEmbedClientChanged()
    {
        // 事件可能来自原生线程：调度到 UI 线程再碰控件
        var enqueued = DispatcherQueue.TryEnqueue(() => ApplyEmbedClientState());
        _ = enqueued;
    }

    /// <summary>
    /// 把 VM 当前内嵌连接状态同步到按钮与画面。
    ///
    /// 画面挂载（<c>AttachClient</c>）**只在自检模式下做**：正常运行时一个 client 只能挂一块画面，
    /// 画面归各通道页（<see cref="RdpChannelPage"/>），本页若也挂上就会和它抢渲染视图。
    /// </summary>
    private void ApplyEmbedClientState()
    {
        var client = VM.ActiveEmbedClient;

        if (client is not null)
        {
            EmbedConnectButton.IsEnabled = false;
            EmbedDisconnectButton.IsEnabled = true;
            EmbedFullScreenButton.IsEnabled = true;

            if (_selfCheckEmbed && !ReferenceEquals(EmbedView.CurrentClient, client))
            {
                EmbedView.AttachClient(client);
                StartEmbedStats();
            }

            EmbedStatusText.Text = _selfCheckEmbed
                ? "自检模式：画面显示在本页。"
                : $"内嵌画面正在通道「{VM.ActiveEmbedChannelName}」的页面里显示。";
        }
        else
        {
            EmbedConnectButton.IsEnabled = true;
            EmbedDisconnectButton.IsEnabled = false;
            EmbedFullScreenButton.IsEnabled = false;
            _embedStatsTimer?.Stop();
            EmbedStatusText.Text = "内嵌连接已断开（目标会话保留）。";
        }
    }

    private void OnEmbedConnect(object sender, RoutedEventArgs e)
    {
        if (!VM.IsEmbeddedMode)
        {
            EmbedStatusText.Text = "当前是 mstsc 独立窗口模式；要内嵌画面请先把连接方式切到「内嵌」。";
            return;
        }

        VM.ConnectSurface();
    }

    private void OnEmbedDisconnect(object sender, RoutedEventArgs e)
    {
        // 先让 VM 释放连接（真实 client 归它持有），再复位本页视图 ——
        // 只断视图会让 VM 里留下一条"没有画面的活连接"，下次连接判定会误以为还在。
        VM.DisposeEmbedClient();
        EmbedView.Disconnect();
        EmbedConnectButton.IsEnabled = true;
        EmbedDisconnectButton.IsEnabled = false;
        EmbedStatusText.Text = "已断开。";
    }

    /// <summary>画面帧数/键盘计数状态栏（每秒刷新）。</summary>
    private void StartEmbedStats()
    {
        _embedStatsTimer?.Stop();
        _embedStatsTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _embedStatsTimer.Tick += (_, _) =>
            EmbedStatusText.Text = $"画面 {EmbedView.FramesRendered} 帧 | 键盘 {EmbedView.KeysForwarded} 键";
        _embedStatsTimer.Start();
    }

    // ---------------- 全屏 + WH_KEYBOARD_LL 键盘接管 ----------------

    /// <summary>画面帧数 / 键盘计数的状态栏计时器（自检模式下才有内容）。</summary>
    private DispatcherTimer? _embedStatsTimer;

    /// <summary>
    /// 全屏编排：全屏窗口 + 键盘接管 + 宿主渲染挂起 —— 与通道页（<see cref="RdpChannelPage"/>）共用同一实现。
    /// 这段逻辑原先内联在本页，导致正常模式下画面在通道页、却没有任何入口能触发全屏。
    /// </summary>
    private RdpFullScreenCoordinator? _fullScreen;

    private void OnEmbedFullScreenToggle(object sender, RoutedEventArgs e) => _fullScreen?.Toggle();

    /// <summary>全屏状态变化：切按钮文案与状态提示。</summary>
    private void OnFullScreenStateChanged(bool active)
    {
        EmbedFullScreenButton.Content = active ? "退出全屏" : "全屏";
        EmbedStatusText.Text = active
            ? "全屏中：键盘全部转投远端（F11 退出）"
            : "已退出全屏。";
    }
}
