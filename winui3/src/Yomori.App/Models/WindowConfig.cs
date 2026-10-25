// -*- coding: utf-8 -*-
using System;
using System.Text.Json.Serialization;
using Yomori.Helpers;

namespace Yomori.Models;

/// <summary>
/// 主窗口的位置与尺寸（"记忆上次大小"）。
///
/// 约定：
///  1. 单位为物理像素，与 AppWindow.Position / AppWindow.Size 一致；
///  2. X / Y 为 null、width / height 为 0 都表示"没有记录过"，由
///     <see cref="Services.WindowPlacement"/> 用默认值或屏幕居中兜底；
///  3. Maximized 只记"上次是不是最大化"，具体还原尺寸另存于 width / height，
///     这样最大化状态下退出、下次还原回小窗时不会拿到整屏尺寸。
/// </summary>
public sealed class WindowConfig : ObservableObject, ICloneable
{
    /// <summary>关闭按钮：每次都问（默认）。</summary>
    public const string CloseActionAsk = "ask";

    /// <summary>关闭按钮：直接收进状态栏（不再问）。</summary>
    public const string CloseActionTray = "tray";

    /// <summary>关闭按钮：直接退出（不再问）。</summary>
    public const string CloseActionExit = "exit";

    /// <summary>关闭按钮：任务执行时隐藏到状态栏，空闲时退出。</summary>
    public const string CloseActionAuto = "auto";

    private int? _x;
    private int? _y;
    private int _width;
    private int _height;
    private bool _maximized;
    private bool _sidebarCollapsed;
    private int _channelEventsWidth;
    private string _closeAction = CloseActionAsk;

    /// <summary>窗口左上角 X（物理像素）。null = 未记录。</summary>
    [JsonPropertyName("x")]
    public int? X
    {
        get => _x;
        set => SetProperty(ref _x, value);
    }

    /// <summary>窗口左上角 Y（物理像素）。null = 未记录。</summary>
    [JsonPropertyName("y")]
    public int? Y
    {
        get => _y;
        set => SetProperty(ref _y, value);
    }

    /// <summary>窗口宽度（物理像素）。0 = 未记录，用默认宽度。</summary>
    [JsonPropertyName("width")]
    public int Width
    {
        get => _width;
        set => SetProperty(ref _width, value);
    }

    /// <summary>窗口高度（物理像素）。0 = 未记录，用默认高度。</summary>
    [JsonPropertyName("height")]
    public int Height
    {
        get => _height;
        set => SetProperty(ref _height, value);
    }

    /// <summary>上次退出时是否处于最大化。</summary>
    [JsonPropertyName("maximized")]
    public bool Maximized
    {
        get => _maximized;
        set => SetProperty(ref _maximized, value);
    }

    /// <summary>
    /// 主窗口右侧「执行状态栏」是否处于收起状态。
    ///
    /// <para>和窗口几何放在一起是因为它同属纯界面偏好，也同一批落盘；
    /// 老配置里没有这个键 → 默认 false（展开），与刚做完时的观感一致。</para>
    /// </summary>
    [JsonPropertyName("sidebar_collapsed")]
    public bool SidebarCollapsed
    {
        get => _sidebarCollapsed;
        set => SetProperty(ref _sidebarCollapsed, value);
    }

    /// <summary>
    /// 通道页「运行信息」栏的宽度（物理像素）。0 = 没拖过，按页面宽度取默认比例。
    ///
    /// <para>用户拖动画面与运行信息之间的分隔条后记下来，下次进页面沿用 ——
    /// 和窗口几何、侧栏收起一样，都属纯界面偏好，放同一个节点也同一批落盘。
    /// 实际使用时还要按当前窗口宽度钳制（见 RdpChannelPage.SetEventsColumnWidth）。</para>
    /// </summary>
    [JsonPropertyName("channel_events_width")]
    public int ChannelEventsWidth
    {
        get => _channelEventsWidth;
        set => SetProperty(ref _channelEventsWidth, value);
    }

    /// <summary>
    /// 点关闭按钮时怎么处理窗口。<c>ask</c> / <c>tray</c> / <c>exit</c> / <c>auto</c>。
    ///
    /// <para>放在 window 节点里，因为它和窗口几何是同一类东西 —— 纯界面偏好。
    /// 未知值一律回落 <c>ask</c> —— 老配置里没有这个键，行为就是"每次都问"，最安全。</para>
    /// </summary>
    [JsonPropertyName("close_action")]
    public string CloseAction
    {
        get => _closeAction;
        set => SetProperty(ref _closeAction, NormalizeCloseAction(value));
    }

    /// <summary>把任意字符串收敛成四个合法值之一（未知/空 → 每次询问）。</summary>
    public static string NormalizeCloseAction(string? value)
    {
        return (value ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            CloseActionTray => CloseActionTray,
            CloseActionExit => CloseActionExit,
            CloseActionAuto => CloseActionAuto,
            _ => CloseActionAsk,
        };
    }

    /// <summary>把明显非法的数值收敛成"未记录"，真正的范围钳制交给 WindowPlacement。</summary>
    public void Sanitize()
    {
        CloseAction = NormalizeCloseAction(CloseAction);

        if (Width < 0)
        {
            Width = 0;
        }

        if (Height < 0)
        {
            Height = 0;
        }

        if (X is < -32768 or > 32768)
        {
            X = null;
        }

        if (Y is < -32768 or > 32768)
        {
            Y = null;
        }

        // 负数是脏数据，当"没拖过"处理；真正的上下限随窗口宽度变，只能在使用处钳制
        if (ChannelEventsWidth < 0)
        {
            ChannelEventsWidth = 0;
        }
    }

    public object Clone() => (WindowConfig)MemberwiseClone();
}
