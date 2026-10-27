// -*- coding: utf-8 -*-
using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Yomori.Services;
using Yomori.ViewModels;

namespace Yomori.Views;

/// <summary>
/// 「多画面同屏」里的一格画面（M6）：通道名 + 状态 + 一块 <see cref="RdpView"/> + 无画面时的占位。
///
/// 通道页按「单画面 / 多画面」摆 1 格或最多 2×2 格，每格各挂自己那条通道的内嵌连接。
/// 之所以一通道一连接：原生侧一个 client 同一时刻只能挂一个渲染视图，
/// 让两条通道共用一个 client 会互相打断画面。
/// </summary>
public sealed partial class RdpSurfaceCell : UserControl
{
    private RdpEmbeddedClient? _client;
    private string _channelId = string.Empty;

    // 上一次设置过的 tooltip 文案。1Hz 刷新每秒都会走一遍 UpdateChrome，
    // 而 ToolTipService.SetToolTip 每次都会重建 ToolTip 对象；指针恰好停在该按钮上时，
    // 工具提示管理器会在重建过程中访问元素的子树 —— 栈上就是这样崩的：
    //   SetToolTip → Microsoft.UI.Xaml → AccessViolationException
    // （2026-10-27 21:47，RdpChannelPage 的 1Hz 定时器 → RefreshSurface → UpdateChrome）
    // 文案没变就不要再设，既省掉每秒几次的对象重建，也避开那条路径。
    private string? _muteTip;
    private string? _lockTip;
    private string? _fullScreenTip;

    /// <summary>只在文案变化时才设 tooltip（见字段注释）。</summary>
    private static void SetTipIfChanged(ref string? cache, DependencyObject target, string text)
    {
        if (string.Equals(cache, text, StringComparison.Ordinal))
        {
            return;
        }

        cache = text;
        ToolTipService.SetToolTip(target, text);
    }

    public RdpSurfaceCell()
    {
        InitializeComponent();

        // 画面上的 F11 全屏请求 —— 带上本格通道 id，页面才知道该全屏哪一路
        SurfaceView.FullScreenToggleRequested += (_, _) => FullScreenRequested?.Invoke(this, _channelId);

        // 锁定状态由画面控件自己维护（它才知道有没有卡住的键要放）；
        // 这里只需要跟着刷新按钮文案。
        SurfaceView.InputLockChanged += (_, locked) => ApplyLockChrome(locked);
    }

    /// <summary>本格对应的通道 id（空串 = 尚未分配）。</summary>
    public string ChannelId => _channelId;

    /// <summary>本格的画面控件（全屏协调器要用它当宿主）。</summary>
    public RdpView View => SurfaceView;

    /// <summary>本格当前挂着的连接；null = 无画面。</summary>
    public RdpEmbeddedClient? CurrentClient => _client;

    /// <summary>「弹出独立窗口」被点击。</summary>
    public event EventHandler<string>? PopOutRequested;

    /// <summary>全屏被请求（浮层按钮 / F11）。</summary>
    public event EventHandler<string>? FullScreenRequested;

    /// <summary>「断开」被点击。</summary>
    public event EventHandler<string>? DisconnectRequested;

    /// <summary>占位里的「连接到这条通道」被点击。</summary>
    public event EventHandler<string>? ConnectRequested;

    /// <summary>本格是否显示「全屏」按钮（只有单画面模式才给 —— 多画面下用「弹出窗口」）。</summary>
    public bool ShowFullScreenButton
    {
        set => FullScreenButton.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>全屏状态变化（切图标 + tooltip）。</summary>
    public void SetFullScreenActive(bool active)
    {
        // 图标 = 这条路的当前状态：没全屏显示放大箭头，全屏中显示缩小箭头。
        FullScreenIconEnter.Visibility = active ? Visibility.Collapsed : Visibility.Visible;
        FullScreenIconExit.Visibility = active ? Visibility.Visible : Visibility.Collapsed;

        SetTipIfChanged(ref _fullScreenTip, FullScreenButton, active
            ? "退出全屏：回到页面里的这一格（F11 亦可退出）"
            : "全屏显示这一路画面（键盘一并接管；F11 退出）");
    }

    private static MainViewModel VM => App.ViewModel;

    /// <summary>把本格分配给某条通道（只设身份与标题，不碰画面）。</summary>
    public void Assign(string channelId, string displayName)
    {
        _channelId = channelId ?? string.Empty;
        TitleText.Text = displayName;
        TopBar.Visibility = _channelId.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// 挂上 / 解下画面。传 null 表示本格没有画面（占位会显示出来）。
    /// 同一个实例重复调用是空操作 —— 1Hz 刷新不会把画面重挂成闪断。
    /// </summary>
    public void SetClient(RdpEmbeddedClient? client)
    {
        if (ReferenceEquals(_client, client))
        {
            return;
        }

        _client = client;

        if (client is null)
        {
            SurfaceView.DetachClient();
        }
        else
        {
            SurfaceView.AttachClient(client);
            // 锁定是纯本机输入闸门，连接一建立就按通道配置恢复，避免重连/重启后失效。
            SurfaceView.IsInputLocked = VM.IsSurfaceInputLocked(_channelId);

            // 静音状态按通道记忆，VM 在发起连接时已下发过一次；这里再下发一次是幂等的。
            // 注意会话此刻通常还没 RUNNING：SetMuted 只会记下意图，由客户端在 Connected 补发。
            if (VM.IsSurfaceMuted(_channelId))
            {
                client.SetMuted(true);
            }
        }

        UpdateChrome();
    }

    /// <summary>刷新状态文案与按钮可用性（1Hz 由页面调）。</summary>
    public void UpdateChrome()
    {
        var hasClient = _client is not null;

        Placeholder.Visibility = hasClient ? Visibility.Collapsed : Visibility.Visible;
        DisconnectButton.IsEnabled = hasClient;
        PopOutButton.IsEnabled = hasClient;
        FullScreenButton.IsEnabled = hasClient;
        MuteButton.IsEnabled = hasClient;
        LockButton.IsEnabled = hasClient;

        // ⚠️ 顺序要紧：先刷一遍常态配色，再让 Apply*Chrome 各自上生效态。
        //    反过来会把「已静音 / 已锁定」的强调色又刷回灰色。
        ApplyIconButtonChrome();

        // 锁定 / 静音都有状态，且都按通道记忆 —— 每拍都从 VM 取，不缓存
        ApplyLockChrome(hasClient && VM.IsSurfaceInputLocked(_channelId));
        ApplyMuteChrome(hasClient && VM.IsSurfaceMuted(_channelId));

        StateText.Text = VM.SurfaceStateText(_channelId);

        if (hasClient)
        {
            return;
        }

        if (_channelId.Length == 0)
        {
            PlaceholderText.Text = "这一格还没有分配通道。";
        }
        else if (VM.IsSurfacePoppedOut(_channelId))
        {
            PlaceholderText.Text = "这路画面正在独立窗口里显示。\n关掉那个窗口，画面会自动回到这里。";
        }
        else if (!VM.IsEmbeddedMode)
        {
            PlaceholderText.Text =
                "当前客户端模式是「独立窗口（mstsc）」，画面在系统自带的远程桌面窗口里，应用内不显示。\n" +
                "想在这里看到内嵌画面，请到「通道管理…」把客户端模式改为「内嵌」。";
        }
        else
        {
            PlaceholderText.Text = "这条通道还没有画面。\n点下面的按钮建连（开始执行时也会自动连上）。";
        }
    }

    private void OnConnectClicked(object sender, RoutedEventArgs e)
        => ConnectRequested?.Invoke(this, _channelId);

    private void OnPopOutClicked(object sender, RoutedEventArgs e)
        => PopOutRequested?.Invoke(this, _channelId);

    private void OnFullScreenClicked(object sender, RoutedEventArgs e)
        => FullScreenRequested?.Invoke(this, _channelId);

    private void OnDisconnectClicked(object sender, RoutedEventArgs e)
        => DisconnectRequested?.Invoke(this, _channelId);

    /// <summary>
    /// 静音开关。状态写进 VM（按通道记），原生侧同步把 rdpsnd 音量置 0 / 还原。
    /// 本机静音是「本机不放声」，远端会话照常发声 —— 不是把远端也静掉。
    /// </summary>
    private void OnMuteClicked(object sender, RoutedEventArgs e)
    {
        if (_channelId.Length == 0 && _client is null)
        {
            return;
        }

        var next = !VM.IsSurfaceMuted(_channelId);
        VM.SetSurfaceMuted(_channelId, next);
        ApplyMuteChrome(next);
    }

    /// <summary>
    /// 锁定开关：停用鼠标穿透 + 键盘映射（防误触），再点一次解除。
    /// 注意「锁定」按钮本身仍在顶部条上 —— 它不走画面输入隧道，所以锁定后依然点得动。
    /// </summary>
    private void OnLockClicked(object sender, RoutedEventArgs e)
    {
        // 让画面控件自己切（它会顺手把可能卡住的键/鼠标按钮释放掉）
        SurfaceView.IsInputLocked = !SurfaceView.IsInputLocked;
        VM.SetSurfaceInputLocked(_channelId, SurfaceView.IsInputLocked);
    }

    /// <summary>
    /// 按锁定状态切图标（开锁 ↔ 闭锁）并给生效态加淡强调色底。
    /// 不换 AccentButtonStyle：那个样式会把底色和图标一起染成强调色，
    /// 两个图标就分不出"哪个是当前状态"了。这里只换底色 + 图标色，形状自己说话。
    /// </summary>
    private void ApplyLockChrome(bool locked)
    {
        // 图标 = 这把锁现在的状态：没锁时是开着的锁（E785），锁上了就是闭锁（E72E）。
        LockIconUnlocked.Visibility = locked ? Visibility.Collapsed : Visibility.Visible;
        LockIconPadlock.Visibility = locked ? Visibility.Visible : Visibility.Collapsed;

        // 锁定是「当前生效」的状态，用淡强调色标出来，避免用户忘了自己锁着
        LockButton.Background = locked ? AccentTint : null;
        LockButton.Foreground = locked ? AccentFg : NormalFg;
        LockButton.BorderBrush = locked ? AccentBorder : null;

        // 按钮上没文字了，tooltip 是唯一说明 —— 必须跟着状态走，
        // 否则悬停看到的永远是同一句、不知道现在是锁着还是没锁。
        SetTipIfChanged(ref _lockTip, LockButton, locked
            ? "已锁定：鼠标穿透与键盘映射已停用，点一下解锁"
            : "锁定画面输入：停用鼠标穿透与键盘映射，防止误操作");
    }

    /// <summary>按静音状态切图标（有声 ↔ 静音）并给生效态加淡强调色底。</summary>
    private void ApplyMuteChrome(bool muted)
    {
        MuteIconSound.Visibility = muted ? Visibility.Collapsed : Visibility.Visible;
        MuteIconMuted.Visibility = muted ? Visibility.Visible : Visibility.Collapsed;

        MuteButton.Background = muted ? AccentTint : null;
        MuteButton.Foreground = muted ? AccentFg : NormalFg;
        MuteButton.BorderBrush = muted ? AccentBorder : null;

        SetTipIfChanged(ref _muteTip, MuteButton, muted
            ? "已静音：这台机器不放这条通道的声音（远端照常发声），点一下恢复"
            : "本机静音：远端照常发声，只是这台机器不放这条通道的声音");
    }

    // ---- 图标按钮的配色 ----
    // 常态用「次要文字」色（比纯白/纯黑柔和，在画面上方不抢戏）；
    // 生效态用强调色，让"正在静音 / 正在锁定"一眼可见。
    // 主题画刷取不到时用兜底色（下面的 ARGB 字面量），避免因缺资源整页崩。

    /// <summary>
    /// 顶条上所有图标按钮统一走这套配色。
    /// 五个按钮（弹出/静音/锁定/全屏/断开）逐个手工设过一遍颜色，后来加按钮很容易漏，
    /// 颜色深浅不一 —— 所以统一在这里刷一遍，各 Apply*Chrome 只管自己的「生效态」。
    /// </summary>
    private void ApplyIconButtonChrome()
    {
        // 单态按钮没有生效态，只需要常态前景色（禁用时由 Button 自己变灰）
        foreach (var b in new[] { PopOutButton, DisconnectButton })
        {
            b.Background = null;
            b.BorderBrush = null;
            b.Foreground = NormalFg;
        }

        // 两态按钮的「未生效」那一态也在这里兜住，
        // 这样即使 Apply*Chrome 还没跑过（首帧）颜色也是对的。
        MuteButton.Foreground = NormalFg;
        LockButton.Foreground = NormalFg;
        FullScreenButton.Foreground = NormalFg;
    }

    private static Brush NormalFg =>
        ThemeBrush("TextFillColorSecondaryBrush", C(0xB3, 0xB3, 0xB3));

    private static Brush AccentFg =>
        ThemeBrush("AccentTextFillColorPrimaryBrush", C(0x00, 0x78, 0xD4));

    private static Brush AccentTint =>
        ThemeBrush("AccentFillColorSecondaryBrush", C(0x30, 0x78, 0xD4, 0x40));

    private static Brush AccentBorder =>
        ThemeBrush("AccentControlBorderBrush", C(0x00, 0x78, 0xD4, 0x80));

    private static Windows.UI.Color C(byte r, byte g, byte b, byte a = 0xFF)
        => Windows.UI.Color.FromArgb(a, r, g, b);

    /// <summary>取主题画刷；取不到就用兜底色（避免因缺资源整页崩）。</summary>
    private static Brush ThemeBrush(string key, Windows.UI.Color fallback)
    {
        if (Application.Current.Resources.TryGetValue(key, out var v) && v is Brush b)
        {
            return b;
        }
        return new SolidColorBrush(fallback);
    }
}
