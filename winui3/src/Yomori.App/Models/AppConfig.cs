// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using Yomori.Helpers;

namespace Yomori.Models;

/// <summary>全局应用配置，字段与 Python 版 config.json 完全一致。</summary>
public sealed class AppConfig : ObservableObject
{
    /// <summary>进程级共享的活动任务 worker 上限；所有 ScriptRunner 共用此上限。</summary>
    public const int MaxActiveTaskThreads = 16;

    /// <summary>
    /// WinUI 3 主题。除 light / dark 之外，仍保留 Python 版皮肤名以便旧配置平滑迁移，
    /// 这些皮肤统一映射到 Fluent 默认主题。
    /// </summary>
    public static readonly IReadOnlyList<KeyValuePair<string, string>> ThemeItems = new List<KeyValuePair<string, string>>
    {
        new("system", "跟随系统"),
        new("light", "浅色"),
        new("dark", "深色"),
    };

    private static readonly HashSet<string> LegacyThemes = new() { "yukino", "campus", "fresh", "fantasy" };

    private bool _shutdownAfterDone;
    private int _shutdownDelaySeconds = 60;
    private bool _autoExitAfterDone;
    private bool _autoStartTasks;
    private bool _windowsStartup;
    private bool _enableTimeoutScreenshot = true;
    private bool _developerMode;
    private int _externalLogKeepFiles = ExternalLogDefaults.KeepFiles;
    private int _eventLineLimit = ExternalLogDefaults.EventLineLimit;
    private string _updateChannel = UpdateChannels.Stable;
    private string _updateCheckMode = UpdateCheckModes.Notify;
    private string _updateProxyMode = UpdateProxyModes.System;
    private string _updateProxyUrl = string.Empty;

    public AppConfig()
    {
    }

    public AppConfig(IEnumerable<TaskConfig> tasks)
    {
        Tasks = new List<TaskConfig>(tasks);
    }

    [JsonPropertyName("theme")]
    public string Theme { get; set; } = "system";

    [JsonPropertyName("shutdown_after_done")]
    public bool ShutdownAfterDone
    {
        get => _shutdownAfterDone;
        set => SetProperty(ref _shutdownAfterDone, value);
    }

    [JsonPropertyName("shutdown_delay_seconds")]
    public int ShutdownDelaySeconds
    {
        get => _shutdownDelaySeconds;
        set => SetProperty(ref _shutdownDelaySeconds, value);
    }

    [JsonPropertyName("auto_exit_after_done")]
    public bool AutoExitAfterDone
    {
        get => _autoExitAfterDone;
        set => SetProperty(ref _autoExitAfterDone, value);
    }

    [JsonPropertyName("auto_start_tasks")]
    public bool AutoStartTasks
    {
        get => _autoStartTasks;
        set => SetProperty(ref _autoStartTasks, value);
    }

    [JsonPropertyName("windows_startup")]
    public bool WindowsStartup
    {
        get => _windowsStartup;
        set => SetProperty(ref _windowsStartup, value);
    }

    [JsonPropertyName("enable_timeout_screenshot")]
    public bool EnableTimeoutScreenshot
    {
        get => _enableTimeoutScreenshot;
        set => SetProperty(ref _enableTimeoutScreenshot, value);
    }

    /// <summary>
    /// 开发者模式：决定「运行日志」页要不要显示两类诊断行 ——
    /// 带 <c>[embed]</c> 标签的内嵌画面诊断（D3D 初始化 / 渲染路径 / GFX 管线状态 / 帧到达节奏）
    /// 与带 <c>[memory]</c> 标签的内存探针（private_mb / working_set_mb / threads…）。
    ///
    /// 只影响**页面显示**：关着的时候这些行照样写进 logs 目录里的日志文件，
    /// 排障该翻的证据一条不少 —— 它们只是每几秒一条，会把任务调度那几条信息淹掉。
    /// </summary>
    [JsonPropertyName("developer_mode")]
    public bool DeveloperMode
    {
        get => _developerMode;
        set => SetProperty(ref _developerMode, value);
    }

    /// <summary>
    /// 「检查更新」看哪条发布通道：<c>stable</c> = 正式版，<c>beta</c> = 测试版。
    /// 只决定检查更新时比对哪一份发布，不改变程序本身的任何行为。
    /// 取值见 <see cref="UpdateChannels"/>，未知值在 <see cref="Sanitize"/> 里回落到正式版。
    /// </summary>
    /// <summary>
    /// 外部脚本日志保留多少份（**按通道各自计数**）。
    ///
    /// 例如 30 = 每条通道各留最近 30 天的日志，超出部分在启动时自动清理。
    /// 默认值见 ExternalLogDefaults.KeepFiles。
    /// </summary>
    /// <summary>
    /// 通道事件列表保留多少行（默认 <c>200</c>）。
    ///
    /// 这只是**界面滚动窗口**的大小 —— 外部日志一律完整写入独立文件，
    /// 不会因为这里被顶掉而丢失。
    /// </summary>
    [JsonPropertyName("event_line_limit")]
    public int EventLineLimit
    {
        get => _eventLineLimit;
        set => SetProperty(ref _eventLineLimit, Math.Clamp(value, ExternalLogDefaults.MinEventLines, ExternalLogDefaults.MaxEventLinesLimit));
    }

    [JsonPropertyName("external_log_keep_files")]
    public int ExternalLogKeepFiles
    {
        get => _externalLogKeepFiles;
        set => SetProperty(ref _externalLogKeepFiles, Math.Max(1, value));
    }
    [JsonPropertyName("update_channel")]
    public string UpdateChannel
    {
        get => _updateChannel;
        set => SetProperty(ref _updateChannel, value);
    }

    /// <summary>
    /// 启动时自动检查更新的方式，取值见 <see cref="UpdateCheckModes"/>。
    ///
    /// 老配置里没有这个键 → 归一成 <c>notify</c>（"每次启动检测、有更新就通知"），
    /// 这正是引入这个功能时要的行为。
    /// </summary>
    [JsonPropertyName("update_check_mode")]
    public string UpdateCheckMode
    {
        get => _updateCheckMode;
        set => SetProperty(ref _updateCheckMode, UpdateCheckModes.Normalize(value));
    }

    /// <summary>检查更新 / 下载安装包走哪种代理，取值见 <see cref="UpdateProxyModes"/>。</summary>
    [JsonPropertyName("update_proxy_mode")]
    public string UpdateProxyMode
    {
        get => _updateProxyMode;
        set => SetProperty(ref _updateProxyMode, UpdateProxyModes.Normalize(value));
    }

    /// <summary>
    /// 自定义代理地址，形如 <c>http://127.0.0.1:7890</c>。只有
    /// <see cref="UpdateProxyMode"/> 为 <c>custom</c> 时用得上；留空等同于"系统代理"。
    /// </summary>
    [JsonPropertyName("update_proxy_url")]
    public string UpdateProxyUrl
    {
        get => _updateProxyUrl;
        set => SetProperty(ref _updateProxyUrl, (value ?? string.Empty).Trim());
    }

    [JsonPropertyName("tasks")]
    public List<TaskConfig> Tasks { get; set; } = new();

    /// <summary>RDP 跨用户会话相关设置。密码不落盘，只存账户名与"是否已保存凭据"。</summary>
    [JsonPropertyName("rdp")]
    public RdpConfig Rdp { get; set; } = new();

    /// <summary>
    /// 主窗口位置/尺寸，用于"记住上次窗口大小"。整段缺失时按默认尺寸启动。
    /// </summary>
    [JsonPropertyName("window")]
    public WindowConfig Window { get; set; } = new();

    /// <summary>
    /// 钉死在首页横幅上的图片路径（绝对路径）。空串 = 不钉死，从随程序发布的图库
    /// <c>assets/home_banners/</c> 里随机抽一张（每次进首页重抽）。
    ///
    /// 只记路径、不复制文件：换图后原图可以随便挪，只要路径还指得到就行；
    /// 图片被删/移走时首页回退到随机图库，不影响启动与任务执行。
    /// </summary>
    [JsonPropertyName("home_banner")]
    public string HomeBannerPath { get; set; } = string.Empty;

    /// <summary>
    /// <b>只有导出文件里才有</b>：这次导出了哪些内容类别、密码是不是明文。
    ///
    /// 正常 config.json 不写它（<see cref="Models.ExportOptions"/> 默认值不写就是 null）。
    /// 留着它是为了让接手的人打开文件就知道"为什么这台机器没配开机自启"，
    /// 而不是以为是导出坏了。
    /// </summary>
    [JsonPropertyName("export_options")]
    public ExportOptions? ExportOptions { get; set; }

    /// <summary>规范化：主题兜底、按 order 排序并重写连续序号。</summary>
    public void Sanitize()
    {
        Rdp ??= new RdpConfig();
        Rdp.Sanitize();

        Window ??= new WindowConfig();
        Window.Sanitize();

        var theme = (Theme ?? string.Empty).ToLowerInvariant();
        var allowed = false;
        foreach (var item in ThemeItems)
        {
            if (item.Key == theme)
            {
                allowed = true;
                break;
            }
        }

        Theme = allowed || LegacyThemes.Contains(theme) ? theme : "system";
        ShutdownDelaySeconds = Math.Max(0, ShutdownDelaySeconds);
        // 只做去空白：路径失效（盘符拔了、文件被删）由首页自己回退到图库随机，
        // 不能在这里清空 —— 临时拔一次 U 盘就把用户的选择抹掉太粗暴。
        HomeBannerPath = (HomeBannerPath ?? string.Empty).Trim();
        UpdateChannel = UpdateChannels.Normalize(UpdateChannel);
        UpdateCheckMode = UpdateCheckModes.Normalize(UpdateCheckMode);
        UpdateProxyMode = UpdateProxyModes.Normalize(UpdateProxyMode);
        // 只去空白：地址合法性交给使用处判断（写错了要让用户看到"代理连不上"，
        // 而不是被这里静默改写成一个看起来正常、其实不是他填的值）。
        UpdateProxyUrl = (UpdateProxyUrl ?? string.Empty).Trim();
        ExternalLogKeepFiles = Math.Max(1, ExternalLogKeepFiles);
        EventLineLimit = ExternalLogDefaults.NormalizeEventLineLimit(EventLineLimit);

        Tasks ??= new List<TaskConfig>();
        Tasks.Sort((a, b) => a.Order.CompareTo(b.Order));
        for (var i = 0; i < Tasks.Count; i++)
        {
            Tasks[i].Sanitize(i + 1);
            Tasks[i].Order = i + 1;
        }

        MigrateLegacyChannel();

        // 收尾从通道搬到任务（见《任务类型与收尾计划书》§3 D4/D8）：
        //   1) 每个执行单元补齐那条固定的收尾任务（空通道跳过）；
        //   2) 把通道上的旧 session_finish 写进它；
        //   3) 垫片归位，下次读盘即幂等。
        // 顺序不能换：迁移要写的那条收尾任务必须先存在。
        TaskScopePlanner.EnsureFinishForAllScopes(Tasks, Rdp.Channels);
        MigrateSessionFinishToTasks();
    }

    /// <summary>
    /// 旧配置迁移：把通道级的 <c>session_finish</c> 搬进任务级收尾任务。
    ///
    /// <para>为什么必须做：不迁的话，用户原本配着"跑完注销用户"的通道，升级后会静默变成
    /// "无操作" —— 表现是目标会话不再释放，而且没有任何提示。这种回归最难查，因为
    /// 界面上一切正常。</para>
    ///
    /// <para>幂等靠两条：只写"动作仍是 keep"的收尾任务（用户显式选过就不覆盖），
    /// 写完把垫片归位成 keep。</para>
    /// </summary>
    private void MigrateSessionFinishToTasks()
    {
        foreach (var channel in Rdp.Channels)
        {
            if (channel is null)
            {
                continue;
            }

            var legacy = SessionFinishModes.Normalize(channel.SessionFinish);
            if (legacy == SessionFinishModes.Keep)
            {
                continue;
            }

            var finish = TaskScopePlanner.TasksForScope(Tasks, channel.Id)
                .FirstOrDefault(TaskTypeResolver.IsFinish);

            // 空通道（连收尾行都没有）→ 无处安放，丢弃；与"空通道保持空"的约定一致
            if (finish is not null
                && SessionFinishModes.Normalize(finish.PostTaskAction) == SessionFinishModes.Keep)
            {
                finish.PostTaskAction = legacy;
            }

            channel.SessionFinish = SessionFinishModes.Keep;
        }

        // 旧全局节的垫片同样归位：它的值在上面的 MigrateLegacyChannel 里已经搬进通道
        Rdp.SessionFinish = SessionFinishModes.Keep;
    }

    /// <summary>
    /// 旧配置迁移：改造前只有一组全局 target_host / target_user，只能跑一路远程。
    /// 这里把它变成一个「默认通道」，并把没指定通道的任务都指过去 —— 升级后行为不变。
    ///
    /// 幂等：已经有通道（用户自己建过）或从没配过目标账户时，什么都不做。
    /// （早先还要看 RDP 总开关，2026-10-26 总开关已从界面移除且恒为开启 —— 见
    /// <see cref="RdpConfig.Enabled"/>，这条前置条件随之消失。）
    /// </summary>
    private void MigrateLegacyChannel()
    {
        if (Rdp.Channels.Count > 0)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(Rdp.TargetUser))
        {
            return;
        }

        var channel = new RdpChannel
        {
            Id = RdpChannel.LegacyDefaultId,
            Name = Rdp.TargetUser,
            Host = Rdp.TargetHost,
            User = Rdp.TargetUser,
            BridgePath = Rdp.BridgePath,
            CredentialSaved = Rdp.CredentialSaved,
            SessionFinish = Rdp.SessionFinish,
            DesktopWidth = Rdp.DesktopWidth,
            DesktopHeight = Rdp.DesktopHeight,
            Enabled = true,
        };

        channel.Sanitize(1);
        Rdp.Channels.Add(channel);

        foreach (var task in Tasks)
        {
            if (string.IsNullOrWhiteSpace(task.ChannelId))
            {
                task.ChannelId = channel.Id;
            }
        }
    }

    public AppConfig CloneWithTasks(IEnumerable<TaskConfig> tasks) => new(tasks)
    {
        Theme = Theme,
        ShutdownAfterDone = ShutdownAfterDone,
        ShutdownDelaySeconds = ShutdownDelaySeconds,
        AutoExitAfterDone = AutoExitAfterDone,
        AutoStartTasks = AutoStartTasks,
        WindowsStartup = WindowsStartup,
        EnableTimeoutScreenshot = EnableTimeoutScreenshot,
        DeveloperMode = DeveloperMode,
        UpdateChannel = UpdateChannel,
        UpdateCheckMode = UpdateCheckMode,
        UpdateProxyMode = UpdateProxyMode,
        UpdateProxyUrl = UpdateProxyUrl,
        ExternalLogKeepFiles = ExternalLogKeepFiles,
        EventLineLimit = EventLineLimit,
        Rdp = (RdpConfig)Rdp.Clone(),
        Window = (WindowConfig)Window.Clone(),
        HomeBannerPath = HomeBannerPath,
    };
}
