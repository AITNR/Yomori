// -*- coding: utf-8 -*-
using System;
using System.Text.Json.Serialization;
using Yomori.Helpers;

namespace Yomori.Models;

/// <summary>通知的种类（只影响铃铛里的图标/强调色与排障归类）。</summary>
public static class AppNotificationKinds
{
    public const string Info = "info";

    /// <summary>有可用更新。</summary>
    public const string Update = "update";

    /// <summary>安装包已下载完。</summary>
    public const string Download = "download";

    /// <summary>失败（检查更新失败、下载中断…）。</summary>
    public const string Warning = "warning";
}

/// <summary>点开一条通知后要做什么。</summary>
public static class AppNotificationActions
{
    public const string None = "none";

    /// <summary>用浏览器打开 <c>ActionTarget</c>（URL）。</summary>
    public const string OpenUrl = "open_url";

    /// <summary>在资源管理器里打开 <c>ActionTarget</c>（文件或目录路径）。</summary>
    public const string OpenPath = "open_path";

    /// <summary>点这条就开始下载安装包（<c>ActionTarget</c> = 安装包直链）。</summary>
    public const string Download = "download";

    /// <summary>
    /// 点这条就退出程序并运行已下载的安装包（<c>ActionTarget</c> = 本地路径）。
    ///
    /// <para>为什么动作做到"安装"这一级：用户点的是"我决定现在升级"，
    /// 再让他去文件夹里双击一次 exe 属于多余的一步。</para>
    /// </summary>
    public const string Install = "install";
}

/// <summary>
/// 通知中心里的一条消息。
///
/// <para>⚠️ 这个类会被序列化进 <c>notifications.json</c>，字段改名等于改存储格式。
/// 已读状态一起存 —— 用户关掉程序再打开，不应该被同一条更新提醒再摇一次。</para>
///
/// <para>⚠️ 不引用任何 <c>Microsoft.UI.Xaml</c> 类型（连 <c>Visibility</c> 都不行）：
/// 冒烟工程把 Models 直接编译进纯 .NET 目标，见 MEMORY 的「Models 层纪律」。</para>
/// </summary>
public sealed class AppNotification : ObservableObject
{
    /// <summary>
    /// 「这条没有进度可显示」的哨兵值。
    ///
    /// <para>用 -1 而不是 0：0 是「刚开始下、还一个字节没到」，两者在界面上要区分开
    /// （前者不画进度条，后者画一条 0% 的）。</para>
    /// </summary>
    public const double NoProgress = -1;

    private bool _isRead;
    private string _body = string.Empty;
    private double _progress = NoProgress;

    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    /// <summary>
    /// 正文。**必须是可通知的属性**：下载过程中这行字会带着百分比刷新，
    /// 界面上的 <c>x:Bind</c> 也因此必须写 <c>Mode=OneWay</c>（默认 OneTime 不会跟着变）。
    /// </summary>
    [JsonPropertyName("body")]
    public string Body
    {
        get => _body;
        set => SetProperty(ref _body, value ?? string.Empty);
    }

    /// <summary>
    /// 下载进度（0~100），<see cref="NoProgress"/> 表示这条没有进度条。
    ///
    /// <para>⚠️ 只能在 UI 线程上改：它是绑到界面的属性，后台线程直接赋值就是
    /// 跨线程改绑定（<c>0x8001010E</c> 那条），调用方要自己 Post 回 UI 线程。</para>
    /// </summary>
    /// <remarks>不落盘：它是「这一次下载」的瞬时状态，存下来只会让下次启动看到个假的 37%。</remarks>
    [JsonIgnore]
    public double Progress
    {
        get => _progress;
        set
        {
            var clamped = value < 0 ? NoProgress : Math.Min(100d, value);
            if (SetProperty(ref _progress, clamped))
            {
                OnPropertyChanged(nameof(HasProgress));
                OnPropertyChanged(nameof(ProgressOpacity));
            }
        }
    }

    /// <summary>这条有没有进度可显示（真值判断；界面上绑的是 <see cref="ProgressOpacity"/>）。</summary>
    [JsonIgnore]
    public bool HasProgress => _progress >= 0;

    /// <summary>
    /// 进度条的 <c>Opacity</c>（1 显示 / 0 隐藏）。
    ///
    /// <para>为什么用不透明度而不是 <c>Visibility</c>：<c>Visibility</c> 是
    /// <c>Microsoft.UI.Xaml</c> 的类型，Models 层不许出现（见 MEMORY 的「Models 层纪律」）；
    /// 用 double 就不用引入 <c>BoolToVisibility</c> 转换器，模板里一行绑定即可。</para>
    /// </summary>
    [JsonIgnore]
    public double ProgressOpacity => _progress >= 0 ? 1d : 0d;

    /// <summary>取值见 <see cref="AppNotificationKinds"/>。</summary>
    [JsonPropertyName("kind")]
    public string Kind { get; set; } = AppNotificationKinds.Info;

    [JsonPropertyName("created_at")]
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;

    [JsonPropertyName("is_read")]
    public bool IsRead
    {
        get => _isRead;
        set => SetProperty(ref _isRead, value);
    }

    /// <summary>取值见 <see cref="AppNotificationActions"/>。</summary>
    [JsonPropertyName("action_kind")]
    public string ActionKind { get; set; } = AppNotificationActions.None;

    /// <summary>动作参数（URL 或路径）。</summary>
    [JsonPropertyName("action_target")]
    public string ActionTarget { get; set; } = string.Empty;

    /// <summary>这条有没有可点的动作。</summary>
    [JsonIgnore]
    public bool HasAction =>
        ActionTarget.Length > 0
        && ActionKind is AppNotificationActions.OpenUrl
            or AppNotificationActions.OpenPath
            or AppNotificationActions.Download
            or AppNotificationActions.Install;

    /// <summary>动作按钮上的字（没有动作时为空串）。</summary>
    [JsonIgnore]
    public string ActionText => ActionKind switch
    {
        AppNotificationActions.OpenUrl => "前往下载",
        AppNotificationActions.OpenPath => "打开所在文件夹",
        AppNotificationActions.Download => "下载",
        AppNotificationActions.Install => "安装",
        _ => string.Empty,
    };

    /// <summary>界面上显示的相对时间（今天只给时分，更早给日期）。</summary>
    [JsonIgnore]
    public string TimeText
    {
        get
        {
            var local = CreatedAt.ToLocalTime();
            var today = DateTimeOffset.Now.Date;
            return local.Date == today
                ? local.ToString("HH:mm")
                : local.ToString("MM-dd HH:mm");
        }
    }
}
