// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
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

        // 目标账户下拉的数据源：只读扫描本机账户（放后台，回来后自己切回 UI 线程）
        ReloadLocalUsers();

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
                // 换了通道 = 换了主机：本机账户列表要不要给，得按新通道重算一遍
                ApplyLocalUserItems();
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

        // 账户状态提示与凭据状态绑在一起（保存凭据后要立刻从「未保存」变成「已保存」）
        RefreshChannelUserHint();
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

    // ---------------- 新建（通道 / 账户） ----------------

    /// <summary>「新建」点下去之后走哪条路。</summary>
    private enum CreateMode
    {
        None,
        ReuseAccount,    // 本机已有账户 → 建通道
        CreateAccount,   // 本机没有账户 → 先建账户
    }

    /// <summary>
    /// 唯一的「新建」入口：先问本机有没有现成账户，再决定走哪条路。
    /// </summary>
    /// <remarks>
    /// 合并的理由：建账户与建通道本来是同一件事的前后两步。分成两个按钮之后，
    /// 用户点了「新建通道」才发现没有账户可用，还得退回来点另一个按钮 ——
    /// 问一次比让人自己判断该点哪个更省事，而且对话框里直接列出本机现有可用账户。
    /// </remarks>
    private async void OnCreate(object sender, RoutedEventArgs e)
    {
        try
        {
            switch (await AskCreateModeAsync())
            {
                case CreateMode.ReuseAccount:
                    AddChannel();
                    break;

                case CreateMode.CreateAccount:
                    await CreateLocalUserFlowAsync();
                    break;
            }
        }
        catch (Exception ex)
        {
            // async void 里漏出去的异常会直接掀掉进程 —— 就地收口并落盘
            AppPaths.WriteStartupError(ex);
            VM.AppendLog($"[会话通道] 新建流程出错：{ex.GetType().Name}：{ex.Message}");
        }
    }

    /// <summary>问"有没有现成账户"。取消返回 <see cref="CreateMode.None"/>。</summary>
    private async Task<CreateMode> AskCreateModeAsync()
    {
        if (App.MainWindow?.Content?.XamlRoot is null)
        {
            return CreateMode.None;
        }

        var available = _localUsers.Count == 0
            ? "本机当前没有可用的 Windows 账户（已禁用、系统内置、以及服务 / 沙箱身份都不算）。"
            : $"本机可用账户 {_localUsers.Count} 个："
              + string.Join("、", _localUsers.Take(6).Select(u => u.Name))
              + (_localUsers.Count > 6 ? " 等" : "") + "。";

        var panel = new StackPanel { Spacing = 10, MinWidth = 380 };
        panel.Children.Add(new TextBlock
        {
            Text = "这台电脑上已经有可以当通道目标的 Windows 账户了吗？",
            TextWrapping = TextWrapping.Wrap,
        });
        panel.Children.Add(new TextBlock
        {
            Text = available,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Opacity = 0.7,
        });
        panel.Children.Add(new TextBlock
        {
            Text = "有：直接建一条通道，然后在「目标账户」里选它。\n"
                   + "没有：先建一个 Windows 本地账户（可顺手设管理员、在登录界面隐藏、存凭据），建完还会顺带把通道建好。",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Opacity = 0.7,
        });

        var dialog = new ContentDialog
        {
            Title = "新建",
            Content = panel,
            PrimaryButtonText = "已有账户，新建通道",
            SecondaryButtonText = "还没有，先建账户",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = App.MainWindow.Content.XamlRoot,
        };

        return await DialogHelper.ShowAsync(dialog) switch
        {
            ContentDialogResult.Primary => CreateMode.ReuseAccount,
            ContentDialogResult.Secondary => CreateMode.CreateAccount,
            _ => CreateMode.None,
        };
    }

    /// <summary>
    /// 在本机创建一个 Windows 本地账户，供通道当目标账户用。
    /// </summary>
    /// <remarks>
    /// <para>通道要的账户有三个隐形要求：能远程登录、有管理员权限、密码不能几十天后过期。
    /// 让用户去「Windows 设置 → 账户」里建完再回来补这三件事，等于每次都要记一遍 ——
    /// 所以「建账户 + 加组 + 密码策略 + 可选的登录界面隐藏」在这里一次做完
    /// （见 <see cref="LocalUserAccountService.Create"/>、<see cref="LocalUserVisibility"/>）。</para>
    /// <para>本机一条通道都没有时，建完账户会顺手建一条通道并把账户填进去 —— 否则账户建好了没地方落，
    /// 用户还得再点一次「新建」。</para>
    /// <para>密码在对话框里刚输过一次，所以默认顺手写成该通道的凭据（对话框里有勾选开关）——
    /// 不让用户为了同一条密码在通道里再敲一遍。</para>
    /// </remarks>
    private async Task CreateLocalUserFlowAsync()
    {
        if (App.MainWindow?.Content?.XamlRoot is null)
        {
            return;   // 没有窗口就没有输入框（自检 / 无人值守）
        }

        // 凭据按「主机 + 账户」存。没有通道时建完会新建一条（主机继承全局设置），
        // 所以这里按"那条将要出现的通道"判断能不能存凭据，而不是只看当前有没有选中通道。
        var canAutoSave = VM.SelectedChannel is { } selected
            ? RdpTargets.IsLocal(selected.Host)
            : RdpTargets.IsLocal(VM.RdpSettings.TargetHost);

        var dialog = new CreateLocalUserDialog(canAutoSave) { XamlRoot = App.MainWindow.Content.XamlRoot };
        var result = await DialogHelper.ShowAsync(dialog);

        // 校验全部在对话框里做，走到这里 Request 一定已通过校验；取消时它是 null
        var request = dialog.Request;
        if (result != ContentDialogResult.Primary || request is null)
        {
            return;
        }

        var created = LocalUserAccountService.Create(
            request.Value.Name,
            request.Value.Password,
            "由 Yomori 创建",
            request.Value.MakeAdmin,
            VM.Config.Rdp.ChannelPasswordNeverExpires,
            request.Value.HideFromLogon);

        VM.AppendLog($"[会话通道] 新建本机用户「{request.Value.Name}」：{created.Message}（{created.Detail}）");

        // 建成了就先刷新选择器，让新账户马上可选（哪怕用户这次不打算配通道）
        if (created.Ok)
        {
            ReloadLocalUsers();
        }

        // 一条通道都没有：顺手建一条，否则账户建好没地方落
        if (created.Ok && VM.Channels.Count == 0)
        {
            AddChannel();
            if (VM.SelectedChannel is { } fresh)
            {
                canAutoSave = RdpTargets.IsLocal(fresh.Host);
            }
        }

        var channel = VM.SelectedChannel;
        var autoSaved = false;

        if (created.Ok && canAutoSave && request.Value.AutoSaveCredential && channel is not null)
        {
            channel.User = created.AccountName;
            VM.PersistChannelEdits();
            VM.SaveChannelCredential(request.Value.Password);

            // SaveChannelCredential 自己会弹失败框；这里只负责核实"是不是真的存进去了"
            autoSaved = RdpCredentialStore.Exists(channel.Host, created.AccountName);
            VM.AppendLog(autoSaved
                ? $"[会话通道] 已把「{created.AccountName}」填进通道「{channel.DisplayName}」并保存凭据。"
                : $"[会话通道] 已把「{created.AccountName}」填进通道「{channel.DisplayName}」，"
                  + "但凭据没能保存，需要在通道里手动保存一次。");
        }

        var message = autoSaved
            ? $"{created.Message}{Environment.NewLine}已填进通道「{channel!.DisplayName}」并保存好凭据，可以直接用了。"
            : created.Message;

        var answer = await DialogHelper.ShowMessageAsync(
            created.Ok ? "已创建本机用户" : "未能创建本机用户",
            created.Ok
                ? $"{message}{Environment.NewLine}{Environment.NewLine}{created.Detail}"
                : message,
            "知道了",
            created.Ok && channel is not null && !autoSaved ? "填入当前通道" : null);

        if (!created.Ok || autoSaved || answer != ContentDialogResult.Primary || channel is null)
        {
            return;
        }

        channel.User = created.AccountName;
        VM.PersistChannelEdits();
        VM.RefreshChannelReadiness();
        SyncChannelChrome();

        // 填完账户还差凭据：把焦点放到密码框，别让用户自己去找下一步
        ChannelPasswordInput.Focus(FocusState.Programmatic);
        VM.AppendLog($"[会话通道] 已把账户「{created.AccountName}」填进通道「{channel.DisplayName}」，"
                     + "下一步是「保存凭据」。");
    }

    // ---------------- 目标账户选择器 ----------------

    /// <summary>扫描到的本机可用账户（已启用、非系统内置、非纯服务身份）。</summary>
    private List<LocalUserEntry> _localUsers = new();

    /// <summary>扫描到的**全部**本机账户。只用于解释"为什么某个账户不在下拉里"，不进下拉。</summary>
    private List<LocalUserEntry> _allLocalUsers = new();

    /// <summary>上一轮扫描的一句话概况，供提示行与运行日志使用。</summary>
    private string _localUserSummary = "尚未扫描本机账户。";

    /// <summary>正在按模型回填下拉选中项：此时 SelectionChanged 不算用户操作。</summary>
    private bool _syncingUserBox;

    /// <summary>重新扫描本机账户（只读：不建、不删、不改密码、不改组）。</summary>
    private void ReloadLocalUsers() => _ = LoadLocalUsersAsync();

    private async Task LoadLocalUsersAsync()
    {
        try
        {
            // 十几次 netapi32 调用，放后台；回来必须显式切回 UI 线程再碰控件与绑定
            var scan = await Task.Run(() => LocalUserDirectory.Scan(Environment.MachineName));

            var enqueued = DispatcherQueue.TryEnqueue(() => ApplyLocalUserScan(scan));
            _ = enqueued;
        }
        catch (Exception ex)
        {
            AppPaths.WriteStartupError(ex);
            VM.AppendLog($"[会话通道] 扫描本机账户出错：{ex.GetType().Name}：{ex.Message}");
        }
    }

    private void ApplyLocalUserScan(LocalUserScan scan)
    {
        // 扫描是异步的：回来时页面可能已经被导航走了，别再往控件上写字
        if (!ChannelUserBox.IsLoaded)
        {
            return;
        }

        _localUsers = scan.Usable.ToList();
        _allLocalUsers = scan.All.ToList();
        _localUserSummary = scan.Error.Length > 0 ? scan.Error : scan.Summary;

        ApplyLocalUserItems();
        RefreshChannelUserHint();
        VM.AppendLog($"[会话通道] 本机账户扫描：{_localUserSummary}");
    }

    /// <summary>
    /// 把扫描结果灌进下拉，并按主机类型切换"下拉 / 输入框"。
    /// </summary>
    /// <remarks>
    /// 本机通道**只能从下拉里选**：手打账户名要同时记住它启没启用、是不是管理员、在不在远程桌面组，
    /// 填错唯一的表现是连接阶段报一个看起来毫不相干的错。
    /// 远程主机例外 —— 本机账户列表对对方机器毫无意义，而远程通道又必须能填账户，所以那里留一个输入框。
    /// </remarks>
    private void ApplyLocalUserItems()
    {
        var channel = VM.SelectedChannel;
        var local = channel is not null && RdpTargets.IsLocal(channel.Host);

        ChannelUserBox.Visibility = local ? Visibility.Visible : Visibility.Collapsed;
        ChannelUserTextBox.Visibility = local ? Visibility.Collapsed : Visibility.Visible;

        if (!local)
        {
            ChannelUserBox.ItemsSource = null;
            return;
        }

        // 整体替换 ItemsSource 会清掉选中项，所以换完立刻按模型回填
        ChannelUserBox.ItemsSource = _localUsers;
        SyncChannelUserBox();
    }

    /// <summary>按当前通道的账户回填下拉选中项（模型 → 控件方向）。</summary>
    private void SyncChannelUserBox()
    {
        var channel = VM.SelectedChannel;
        if (channel is null || !RdpTargets.IsLocal(channel.Host))
        {
            return;
        }

        var name = (channel.User ?? string.Empty).Trim();
        var match = _localUsers.FirstOrDefault(
            u => string.Equals(u.Name, name, StringComparison.OrdinalIgnoreCase));

        // 值不在可用列表里（远程账户残留 / 账户已被禁用）时 match 为 null：
        // 这里**故意不清空 channel.User**，也不硬塞一个假选项 —— 原值保留，由提示行说明，
        // 等用户自己从下拉里选了新的再覆盖。清空会静默丢掉用户填过的东西。
        if (ReferenceEquals(ChannelUserBox.SelectedItem, match))
        {
            return;
        }

        _syncingUserBox = true;
        try
        {
            ChannelUserBox.SelectedItem = match;
        }
        finally
        {
            _syncingUserBox = false;
        }
    }

    /// <summary>用户在下拉里选了账户：写回通道并落盘。</summary>
    private void OnChannelUserSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingUserBox || ChannelUserBox.SelectedItem is not LocalUserEntry entry)
        {
            return;
        }

        var channel = VM.SelectedChannel;
        if (channel is null
            || string.Equals(channel.User, entry.Name, StringComparison.Ordinal))
        {
            RefreshChannelUserHint();
            return;
        }

        channel.User = entry.Name;
        VM.PersistChannelEdits();
        VM.RefreshChannelReadiness();
        SyncChannelChrome();
    }

    private void OnRefreshLocalUsers(object sender, RoutedEventArgs e) => ReloadLocalUsers();

    /// <summary>
    /// 把"这个账户在本机到底什么状态"讲清楚：启用与否、是不是管理员、能否远程登录、密码策略，
    /// 以及还差哪一步（填密码 / 保存凭据）。这些信息全部来自只读扫描，不猜。
    /// </summary>
    private void RefreshChannelUserHint()
    {
        var channel = VM.SelectedChannel;
        if (channel is null)
        {
            ChannelUserHint.Visibility = Visibility.Collapsed;
            return;
        }

        ChannelUserHint.Visibility = Visibility.Visible;

        if (!RdpTargets.IsLocal(channel.Host))
        {
            ChannelUserHint.Text =
                $"目标主机不是本机：账户要按对方计算机上的实际情况填（本机账户列表帮不上忙）。（{_localUserSummary}）";
            return;
        }

        if (_allLocalUsers.Count == 0)
        {
            ChannelUserHint.Text = "正在扫描本机账户…";
            return;
        }

        var name = (channel.User ?? string.Empty).Trim();
        if (name.Length == 0)
        {
            ChannelUserHint.Text = $"还没选账户：从下拉里选一个。{_localUserSummary}";
            return;
        }

        // 在**全量**里找：账户可能因为"已禁用 / 系统内置 / 纯服务身份"没进下拉，
        // 这时要能说清是哪一种，而不是含糊地来一句"名字可能打错了"。
        var entry = _allLocalUsers.FirstOrDefault(
            u => string.Equals(u.Name, name, StringComparison.OrdinalIgnoreCase));

        if (entry is null || !entry.Usable)
        {
            var why = entry is null
                ? "本机没有这个账户（可能是域账户，或名字打错了）"
                : $"它不在可选列表里：{entry.Detail}";
            ChannelUserHint.Text = $"当前通道填的是「{name}」，但{why}。原值已保留 —— 从下拉里选一个会覆盖它。";
            return;
        }

        var note = entry.Note.Length > 0 ? $"　{entry.Note}" : string.Empty;
        ChannelUserHint.Text = $"{entry.Detail}{note}　凭据：{VM.ChannelCredentialText}";
    }

    // ---------------- 通道增删改 ----------------

    private void AddChannel()
    {
        VM.AddChannelCommand.Execute(null);
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

        // 主机在「本机 ↔ 远程」之间切换时，账户下拉的来源要跟着变（远程主机上没有本机账户）
        ApplyLocalUserItems();
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
