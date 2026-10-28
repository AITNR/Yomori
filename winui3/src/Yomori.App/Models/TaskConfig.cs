// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using Yomori.Helpers;

namespace Yomori.Models;

/// <summary>
/// 单个任务配置。
/// 直接实现 INotifyPropertyChanged，可同时用于 JSON 序列化与 XAML 双向绑定。
/// 配置键名与 Python 版完全一致，保证 config.json 双向兼容。
/// </summary>
public sealed class TaskConfig : ObservableObject, ICloneable
{
    private bool _enabled = true;
    private string _name = "新任务";
    private string _scriptPath = string.Empty;
    private int _order = 1;
    private int _timeoutMinutes = 30;
    private string _timeoutAction = TimeoutActions.KillAndContinue;
    private bool _useArgs;
    private string _args = string.Empty;
    private string _waitMode = WaitModes.DirectProcess;
    private string _waitProcessName = string.Empty;
    private int _confirmEnterDelaySeconds;
    private string _channelId = string.Empty;
    private string _channelDisplay = "本地执行";
    private string _concurrentGroup = string.Empty;
    private string _concurrentPolicy = ConcurrentPolicies.WaitAll;
    private bool _enableWatchdog;
    private List<string> _processKeywords = new();
    private List<string> _windowKeywords = new();
    private string _launcherProcess = string.Empty;
    private string _mainProcess = string.Empty;
    private string _gameProcess = string.Empty;
    private int _scopeOrder;
    // ---------- 外部脚本实时日志（可选） ----------

    private bool _externalLogEnabled;
    private string _externalLogPath = string.Empty;
    private string _externalLogPattern = ExternalLogDefaults.Pattern;
    private string _externalLogEncoding = ExternalLogEncodings.Auto;
    private List<string> _externalLogKeywords = new();

    private bool _externalLogFollowTail = ExternalLogDefaults.FollowTail;

    [JsonPropertyName("enabled")]
    public bool Enabled
    {
        get => _enabled;
        set => SetProperty(ref _enabled, value);
    }

    [JsonPropertyName("name")]
    public string Name
    {
        get => _name;
        set => SetProperty(ref _name, value);
    }

    [JsonPropertyName("script_path")]
    public string ScriptPath
    {
        get => _scriptPath;
        set => SetProperty(ref _scriptPath, value);
    }

    [JsonPropertyName("order")]
    public int Order
    {
        get => _order;
        set => SetProperty(ref _order, value);
    }

    [JsonPropertyName("timeout_minutes")]
    public int TimeoutMinutes
    {
        get => _timeoutMinutes;
        set => SetProperty(ref _timeoutMinutes, value);
    }

    [JsonPropertyName("timeout_action")]
    public string TimeoutAction
    {
        get => _timeoutAction;
        set => SetProperty(ref _timeoutAction, TimeoutActions.Normalize(value));
    }

    [JsonPropertyName("use_args")]
    public bool UseArgs
    {
        get => _useArgs;
        set => SetProperty(ref _useArgs, value);
    }

    [JsonPropertyName("args")]
    public string Args
    {
        get => _args;
        set => SetProperty(ref _args, value);
    }

    [JsonPropertyName("wait_mode")]
    public string WaitMode
    {
        get => _waitMode;
        set => SetProperty(ref _waitMode, WaitModes.Normalize(value));
    }

    [JsonPropertyName("wait_process_name")]
    public string WaitProcessName
    {
        get => _waitProcessName;
        set => SetProperty(ref _waitProcessName, value);
    }

    [JsonPropertyName("confirm_enter_delay_seconds")]
    public int ConfirmEnterDelaySeconds
    {
        get => _confirmEnterDelaySeconds;
        set => SetProperty(ref _confirmEnterDelaySeconds, Math.Max(0, value));
    }

    /// <summary>
    /// 执行通道：任务在哪个会话通道里跑。
    /// 留空 = 本地执行（跑在主控端当前会话里，与改造前的行为一致）。
    /// </summary>
    [JsonPropertyName("channel_id")]
    public string ChannelId
    {
        get => _channelId;
        set => SetProperty(ref _channelId, (value ?? string.Empty).Trim());
    }

    /// <summary>
    /// 通道的显示名（**运行期填充，不落盘**）：任务只存 <see cref="ChannelId"/>，
    /// 列表上要给人看的是名字 —— 由 <c>MainViewModel.RefreshChannelChoices()</c> 从通道配置反查后写进来。
    /// </summary>
    [JsonIgnore]
    public string ChannelDisplay
    {
        get => _channelDisplay;
        set => SetProperty(ref _channelDisplay, value ?? string.Empty);
    }

    [JsonPropertyName("concurrent_group")]
    public string ConcurrentGroup
    {
        get => _concurrentGroup;
        set => SetProperty(ref _concurrentGroup, value);
    }

    [JsonPropertyName("concurrent_policy")]
    public string ConcurrentPolicy
    {
        get => _concurrentPolicy;
        set => SetProperty(ref _concurrentPolicy, ConcurrentPolicies.Normalize(value));
    }

    [JsonPropertyName("enable_watchdog")]
    public bool EnableWatchdog
    {
        get => _enableWatchdog;
        set => SetProperty(ref _enableWatchdog, value);
    }

    [JsonPropertyName("process_keywords")]
    public List<string> ProcessKeywords
    {
        get => _processKeywords;
        set => SetProperty(ref _processKeywords, KeywordHelper.Normalize(value));
    }

    [JsonPropertyName("window_keywords")]
    public List<string> WindowKeywords
    {
        get => _windowKeywords;
        set => SetProperty(ref _windowKeywords, KeywordHelper.Normalize(value));
    }

    [JsonPropertyName("launcher_process")]
    public string LauncherProcess
    {
        get => _launcherProcess;
        set => SetProperty(ref _launcherProcess, value);
    }

    [JsonPropertyName("main_process")]
    public string MainProcess
    {
        get => _mainProcess;
        set => SetProperty(ref _mainProcess, value);
    }

    [JsonPropertyName("game_process")]
    public string GameProcess
    {
        get => _gameProcess;
        set => SetProperty(ref _gameProcess, value);
    }


    // ---------- 外部脚本实时日志（可选，默认关闭） ----------

    /// <summary>
    /// 是否跟踪外部脚本的实时日志文件，并把关键行送进运行事件。
    /// 关闭（默认）时下面几个字段全部忽略，行为与改造前完全一致。
    /// </summary>
    [JsonPropertyName("external_log_enabled")]
    public bool ExternalLogEnabled
    {
        get => _externalLogEnabled;
        set => SetProperty(ref _externalLogEnabled, value);
    }

    /// <summary>
    /// 日志文件路径，或**日志文件所在的目录**。支持环境变量（如 <c>%LOCALAPPDATA%</c>）。
    /// 填目录时按 <see cref="ExternalLogPattern"/> 匹配，并自动挑最近修改的那一份 ——
    /// 这样 BetterGI 按天滚动、MaaEnd 一份固定名 + 多份日期文件，都不用改配置。
    /// </summary>
    [JsonPropertyName("external_log_path")]
    public string ExternalLogPath
    {
        get => _externalLogPath;
        set => SetProperty(ref _externalLogPath, (value ?? string.Empty).Trim());
    }

    /// <summary><see cref="ExternalLogPath"/> 是目录时的文件名匹配模式，默认 <c>*.log</c>。</summary>
    [JsonPropertyName("external_log_pattern")]
    public string ExternalLogPattern
    {
        get => _externalLogPattern;
        set => SetProperty(ref _externalLogPattern, ExternalLogDefaults.NormalizePattern(value));
    }

    /// <summary>
    /// 日志文件编码：<c>auto</c>（默认）/ <c>utf8</c> / <c>gbk</c>。
    /// auto 的顺序是：BOM → UTF-8 严格校验 → 系统 ANSI（中文 Windows 通常是 GBK）。
    /// </summary>
    [JsonPropertyName("external_log_encoding")]
    public string ExternalLogEncoding
    {
        get => _externalLogEncoding;
        set => SetProperty(ref _externalLogEncoding, ExternalLogEncodings.Normalize(value));
    }

    /// <summary>
    /// 关键字：日志行里出现任意一个就<b>必定</b>进事件流（不受抽样限制）。
    /// 用来盯住「失败 / 异常 / error」这类非看不可的行。
    /// </summary>
    [JsonPropertyName("external_log_keywords")]
    public List<string> ExternalLogKeywords
    {
        get => _externalLogKeywords;
        set => SetProperty(ref _externalLogKeywords, KeywordHelper.Normalize(value));
    }

    /// <summary>
    /// 本任务最多往事件流里放几条日志（<c>0</c> = 用设置页里的默认值）。
    /// 事件列表界面只留 200 行，不设上限的话日志会把「任务完成 / 全部结束」顶出去。
    /// </summary>

    /// <summary>
    /// true（默认）= 从任务启动那一刻的文件末尾开始看，只关心本次运行产生的行；
    /// false = 从文件开头读（想看历史时开）。
    /// </summary>
    [JsonPropertyName("external_log_follow_tail")]
    public bool ExternalLogFollowTail
    {
        get => _externalLogFollowTail;
        set => SetProperty(ref _externalLogFollowTail, value);
    }

    /// <summary>关键字的文本形态（分号分隔），专供 UI 两向绑定。</summary>
    [JsonIgnore]
    public string ExternalLogKeywordsText
    {
        get => KeywordHelper.ToText(_externalLogKeywords);
        set
        {
            // 同 ProcessKeywordsText：这里是两向绑定的落点，
            // 绝不能再补一次 ExternalLogKeywords 的通知（会与回写互相触发）。
            SetProperty(ref _externalLogKeywords, KeywordHelper.Normalize(value));
        }
    }

    /// <summary>本功能是否真的启用（开关开了 + 路径非空）。</summary>
    [JsonIgnore]
    public bool ExternalLogActive => _externalLogEnabled && !string.IsNullOrWhiteSpace(_externalLogPath);

    /// <summary>生效的事件上限（任务级为 0 时回落设置页的默认值）。</summary>

    // ---------- 仅供 UI 绑定的文本代理 ----------

    [JsonIgnore]
    public string ProcessKeywordsText
    {
        get => KeywordHelper.ToText(_processKeywords);
        set
        {
            var normalized = KeywordHelper.Normalize(value);

            // ⚠️ 别顺手补 OnPropertyChanged(ProcessKeywords)：这个 setter 是**两向绑定**的落点，
            // TextBox 每次失焦/回车都会写回来。再补一次 ProcessKeywords 通知，
            // 绑在它上面的控件会重新计算并再写回来 —— 两轮回写互相触发，
            // 在集合被整体替换（导入配置）时足以把 UI 线程拖进死循环。
            // ProcessKeywords 与本属性在同一个类上，绑定系统自己会监听到它的通知。
            SetProperty(ref _processKeywords, normalized);
        }
    }

    [JsonIgnore]
    public string WindowKeywordsText
    {
        get => KeywordHelper.ToText(_windowKeywords);
        set
        {
            var normalized = KeywordHelper.Normalize(value);
            SetProperty(ref _windowKeywords, normalized);
        }
    }

    /// <summary>
    /// **通道内**的显示序号（1..N）—— 由 <c>TaskScopePlanner.RenumberScope</c> 在刷新视图时写入。
    /// 落盘的 <see cref="Order"/> 仍是全局唯一整数（执行顺序的排序键），两者不是一回事：
    /// 界面上要给人看"这条通道里的第几个"，执行侧要的是全局次序。
    /// </summary>
    [JsonIgnore]
    public int ScopeOrder
    {
        get => _scopeOrder;
        set
        {
            if (SetProperty(ref _scopeOrder, value))
            {
                OnPropertyChanged(nameof(ScopeOrderText));
            }
        }
    }

    /// <summary>序号的文本形态 —— x:Bind 不会替你把 int 转成 string，直接绑整数编译不过。</summary>
    [JsonIgnore]
    public string ScopeOrderText => _scopeOrder.ToString();

    [JsonIgnore]
    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? $"任务{Order}" : Name;

    [JsonIgnore]
    public bool IsConfigured => !string.IsNullOrWhiteSpace(ScriptPath);

    /// <summary>超时秒数（0 表示不限时）。</summary>
    [JsonIgnore]
    public int TimeoutSeconds => Math.Max(0, TimeoutMinutes) * 60;

    public TaskConfig Clone()
    {
        var copy = (TaskConfig)MemberwiseClone();
        copy._processKeywords = new List<string>(_processKeywords);
        copy._windowKeywords = new List<string>(_windowKeywords);
        copy._externalLogKeywords = new List<string>(_externalLogKeywords);
        return copy;
    }

    object ICloneable.Clone() => Clone();

    /// <summary>把当前实例规整为合法值（枚举兜底、序号连续）。</summary>
    public void Sanitize(int fallbackOrder)
    {
        TimeoutAction = TimeoutActions.Normalize(TimeoutAction);
        WaitMode = WaitModes.Normalize(WaitMode);
        ConcurrentPolicy = ConcurrentPolicies.Normalize(ConcurrentPolicy);
        TimeoutMinutes = Math.Max(0, TimeoutMinutes);
        ConfirmEnterDelaySeconds = Math.Max(0, ConfirmEnterDelaySeconds);
        Order = Math.Max(1, Order == 0 ? fallbackOrder : Order);
        ChannelId = (ChannelId ?? string.Empty).Trim();
        ConcurrentGroup = (ConcurrentGroup ?? string.Empty).Trim();
        ProcessKeywords = KeywordHelper.Normalize(ProcessKeywords);
        WindowKeywords = KeywordHelper.Normalize(WindowKeywords);

        // 外部日志：全部有默认值，老配置没有这些键时走默认值 = 功能关闭
        ExternalLogPath = (ExternalLogPath ?? string.Empty).Trim();
        ExternalLogPattern = ExternalLogDefaults.NormalizePattern(ExternalLogPattern);
        ExternalLogEncoding = ExternalLogEncodings.Normalize(ExternalLogEncoding);
        ExternalLogKeywords = KeywordHelper.Normalize(ExternalLogKeywords);

    }
}
