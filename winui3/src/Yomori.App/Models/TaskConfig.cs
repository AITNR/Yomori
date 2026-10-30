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

    // ---------- 任务类型（见《任务类型与收尾计划书》§3） ----------
    // 三种类型共用这一张表，由 task_kind 决定"看哪些字段、跑什么行为"：
    //   script —— 起外部程序（原有全部字段）
    //   wait   —— 只等一段时间（wait_seconds / wait_timing）
    //   finish —— 对通道做处置（post_task_action），由系统自动维护、不可增删
    private string _taskKind = TaskKinds.Script;
    private int _waitSeconds = 30;
    private string _waitTiming = WaitTimings.AfterConnect;
    private string _postTaskAction = SessionFinishModes.Keep;

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

    // ---------- 启动方式来源（仅用于展示与追溯，**不参与执行**） ----------

    private string _launchPresetId = string.Empty;
    private int _launchPresetRev;
    // ---------- 外部脚本实时日志（可选） ----------

    private bool _externalLogEnabled;
    private string _externalLogPath = string.Empty;
    private string _externalLogPattern = ExternalLogDefaults.Pattern;
    private string _externalLogEncoding = ExternalLogEncodings.Auto;
    private List<string> _externalLogKeywords = new();

    private bool _externalLogFollowTail = ExternalLogDefaults.FollowTail;
    private bool _externalLogRecursive;
    private string _externalLogView = ExternalLogViews.Panel;

    // 日志推荐值的来源标记（与 launch_preset_* 同源：只用于展示与追溯，不参与执行）
    private string _externalLogPresetId = string.Empty;
    private int _externalLogRev;
    private bool _externalLogTouched;
    private string _cleanupPresetId = string.Empty;
    private int _cleanupRev;
    private bool _cleanupTouched;

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
        set
        {
            if (SetProperty(ref _name, value))
            {
                OnPropertyChanged(nameof(DisplayName));
            }
        }
    }

    /// <summary>
    /// 任务类型：<c>script</c>（默认）/ <c>wait</c> / <c>finish</c>。
    ///
    /// <para><b>老配置没有这个键 → 读成 <c>script</c></b>，即改造前的项目脚本任务。
    /// 切换类型不会清空其它类型的字段（切回脚本时路径还在），这是有意的：
    /// 用户误点一下不该丢配置。</para>
    /// </summary>
    [JsonPropertyName("task_kind")]
    public string TaskKind
    {
        get => _taskKind;
        set
        {
            if (SetProperty(ref _taskKind, TaskKinds.Normalize(value)))
            {
                // 列表行的类型标签、第二行摘要、详情面板分区都跟着类型走，一次全通知
                OnPropertyChanged(nameof(KindTag));
                OnPropertyChanged(nameof(HasKindTag));
                OnPropertyChanged(nameof(RowSummary));
                OnPropertyChanged(nameof(IsScriptTask));
                OnPropertyChanged(nameof(IsWaitTask));
                OnPropertyChanged(nameof(IsFinishTask));
                OnPropertyChanged(nameof(CanToggleEnabled));
                OnPropertyChanged(nameof(EnabledToggleHint));
                OnPropertyChanged(nameof(DisplayName));
            }
        }
    }

    /// <summary>等待时长（秒），仅 <c>wait</c> 类型使用；0 = 不等待。</summary>
    [JsonPropertyName("wait_seconds")]
    public int WaitSeconds
    {
        get => _waitSeconds;
        set
        {
            if (SetProperty(ref _waitSeconds, Math.Clamp(value, 0, 3600)))
            {
                OnPropertyChanged(nameof(RowSummary));
            }
        }
    }

    /// <summary>等待时机，仅 <c>wait</c> 类型使用：<c>before_connect</c> / <c>after_connect</c>。</summary>
    [JsonPropertyName("wait_timing")]
    public string WaitTiming
    {
        get => _waitTiming;
        set
        {
            if (SetProperty(ref _waitTiming, WaitTimings.Normalize(value)))
            {
                OnPropertyChanged(nameof(RowSummary));
                OnPropertyChanged(nameof(WaitTimingLabel));
            }
        }
    }

    /// <summary>
    /// 收尾动作，仅 <c>finish</c> 类型使用：<c>keep</c> / <c>disconnect</c> / <c>logoff</c>。
    /// 默认 <c>keep</c>（无操作）—— 老配置没有这个键时行为与改造前一致。
    /// </summary>
    [JsonPropertyName("post_task_action")]
    public string PostTaskAction
    {
        get => _postTaskAction;
        set
        {
            if (SetProperty(ref _postTaskAction, SessionFinishModes.Normalize(value)))
            {
                OnPropertyChanged(nameof(RowSummary));
            }
        }
    }

    // ---------- 仅供界面绑定的派生属性（不落盘） ----------

    [JsonIgnore]
    public bool IsScriptTask => TaskKinds.IsScript(_taskKind);

    [JsonIgnore]
    public bool IsWaitTask => TaskKinds.IsWait(_taskKind);

    [JsonIgnore]
    public bool IsFinishTask => TaskKinds.IsFinish(_taskKind);

    /// <summary>列表行左侧的小标签：脚本不显示（默认），等待/收尾各一个词。</summary>
    [JsonIgnore]
    public string KindTag => TaskTypeResolver.TagOf(this);

    /// <summary>有没有标签要显示（x:Bind 不能直接把空串转成 Visibility）。</summary>
    [JsonIgnore]
    public bool HasKindTag => KindTag.Length > 0;

    /// <summary>列表行第二行：脚本显示路径，等待显示时长与时机，收尾显示动作。</summary>
    [JsonIgnore]
    public string RowSummary => TaskTypeResolver.Describe(this);

    /// <summary>「等待时机」下拉当前值的中文（TextBox 里不绑键值，直接给文本）。</summary>
    [JsonIgnore]
    public string WaitTimingLabel => WaitTimings.Label(_waitTiming);

    /// <summary>
    /// 列表行那个「启用」开关能不能拨 —— 收尾任务恒为启用，故不给拨。
    ///
    /// <para>为什么不是"允许关掉、执行时再兜底"：收尾动作（断开 / 注销）决定这一轮跑完之后
    /// 通道怎么处置，关掉它等于把配置里写明要做的处置静默丢掉，事后无从发现。
    /// 与「上移 / 下移 / 删除 / 移动」同一档：收尾由系统固定维护，用户只挑动作，不挑跑不跑。</para>
    /// </summary>
    [JsonIgnore]
    public bool CanToggleEnabled => !IsFinishTask;

    /// <summary>列表行开关的提示文案：收尾说明为什么拨不动，其余说明拨它的后果。</summary>
    [JsonIgnore]
    public string EnabledToggleHint => IsFinishTask
        ? "收尾任务固定执行：本通道跑完之后按这里的「收尾动作」处置会话，不能关闭。"
        : "这条任务本轮要不要执行：关掉后它会跳过。改动即时保存，下一次「开始执行」生效。";

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

    /// <summary>
    /// 这条启动参数是从哪个预设填来的（形如 <c>m7a:daily</c>）。
    /// 只用于界面显示来源与将来做"预设已更新"提示，<b>不参与执行</b>。
    /// 空串 = 手工填写；读取期不校验该 id 是否仍存在（预设改名属迁移问题，不在这里处理）。
    /// </summary>
    [JsonPropertyName("launch_preset_id")]
    public string LaunchPresetId
    {
        get => _launchPresetId;
        set => SetProperty(ref _launchPresetId, (value ?? string.Empty).Trim());
    }

    /// <summary>当时用的预设库版本号（<c>LaunchPresetLibrary.Rev</c>），用于判断"这份参数是不是旧的"。</summary>
    [JsonPropertyName("launch_preset_rev")]
    public int LaunchPresetRev
    {
        get => _launchPresetRev;
        set => SetProperty(ref _launchPresetRev, value);
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

    /// <summary>
    /// 外部日志显示视图：<c>panel</c>（默认，任务与提示）/ <c>node</c>（节点进出）/ <c>raw</c>（原文）。
    ///
    /// <para>视图只影响"看什么"，不影响采集：同一份日志三种视图共用一次投影结果。</para>
    /// </summary>
    [JsonPropertyName("external_log_view")]
    public string ExternalLogView
    {
        get => _externalLogView;
        set => SetProperty(ref _externalLogView, ExternalLogViews.Normalize(value));
    }

    /// <summary>
    /// 目录模式下是否递归子目录。
    ///
    /// <para>只为"每次运行建一个子目录"的项目准备：LALC 的 <c>logs\&lt;run_id&gt;\run.log</c>、
    /// ok-Onmyoji 多开时的 <c>logs\&lt;实例id&gt;\</c>。默认关闭 —— 打开会扫更多文件，
    /// 采集端有深度 3 / 500 个文件两道闸。</para>
    /// </summary>
    [JsonPropertyName("external_log_recursive")]
    public bool ExternalLogRecursive
    {
        get => _externalLogRecursive;
        set => SetProperty(ref _externalLogRecursive, value);
    }

    /// <summary>
    /// 这条日志配置是从哪条预设填的：<c>m7a</c> / <c>mfaa</c> / <c>entry:auto-mas.exe</c>。
    /// 空串 = 手工填写。**不参与执行**，只用于界面显示来源、以及将来做「预设已更新」提示。
    /// </summary>
    [JsonPropertyName("external_log_preset_id")]
    public string ExternalLogPresetId
    {
        get => _externalLogPresetId;
        set => SetProperty(ref _externalLogPresetId, (value ?? string.Empty).Trim());
    }

    /// <summary>写入时的预设库版本。</summary>
    [JsonPropertyName("external_log_rev")]
    public int ExternalLogRev
    {
        get => _externalLogRev;
        set => SetProperty(ref _externalLogRev, value);
    }

    /// <summary>
    /// 用户手改过任一日志字段。
    ///
    /// <para>置位后预设不再覆盖任何字段 —— 与启动参数的「只在空值时预填」是同一条纪律，
    /// 只是这里更严格：日志路径一旦被手工指到别处（比如多开要跟另一个实例），
    /// 任何自动重算都不该把它改回去。还原推荐值时由界面显式清掉。</para>
    /// </summary>
    [JsonPropertyName("external_log_touched")]
    public bool ExternalLogTouched
    {
        get => _externalLogTouched;
        set => SetProperty(ref _externalLogTouched, value);
    }

    /// <summary>
    /// 「进程清理登记」是从哪条预设填的：<c>maaend:auto-on-launch</c>。空串 = 手工填写。
    /// 与 <see cref="ExternalLogPresetId"/> 同性质：**不参与执行**，只用于界面显示来源。
    /// </summary>
    [JsonPropertyName("cleanup_preset_id")]
    public string CleanupPresetId
    {
        get => _cleanupPresetId;
        set => SetProperty(ref _cleanupPresetId, (value ?? string.Empty).Trim());
    }

    /// <summary>写入时的预设库版本。</summary>
    [JsonPropertyName("cleanup_rev")]
    public int CleanupRev
    {
        get => _cleanupRev;
        set => SetProperty(ref _cleanupRev, value);
    }

    /// <summary>
    /// 用户手改过「清理登记」的任一个框（三个进程名框 + 两个关键词框）。
    ///
    /// <para>【为什么五个框共用一个标志】这五个框回答的是同一件事 ——「停止时要清谁」。
    /// 分开关会出现"关键词守住了、三栏被冲掉"的半对半错状态，而这种状态在日志里
    /// 无法与"用户本来就没填"区分开。共用一个标志，语义只有一句：
    /// 用户动过之后，预设一个字都不许改。</para>
    ///
    /// <para>置位时机：界面上手改并失焦、且值确实变了（见 TasksPage.MarkCleanupTouched）。
    /// 还原推荐值时由界面显式清掉。</para>
    /// </summary>
    [JsonPropertyName("cleanup_touched")]
    public bool CleanupTouched
    {
        get => _cleanupTouched;
        set => SetProperty(ref _cleanupTouched, value);
    }

    /// <summary>关键字的文本形态（分号分隔），专供 UI 两向绑定。</summary>    [JsonIgnore]
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
    public string DisplayName => !string.IsNullOrWhiteSpace(Name)
        ? Name
        : IsFinishTask ? "收尾"
        : IsWaitTask ? "等待"
        : $"任务{Order}";

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
        // 类型必须先收敛：后面几项要不要按类型做额外处理，都看它
        TaskKind = TaskKinds.Normalize(TaskKind);

        // 收尾任务恒启用：界面上不给拨（CanToggleEnabled=false），但导入的配置、
        // 手改的 json 都可能带着 enabled=false —— 这里收回来。否则就是
        // 「配置里写着跑完注销、实际没注销」这类静默回归，事后无从发现。
        if (IsFinishTask)
        {
            Enabled = true;
        }

        WaitSeconds = Math.Clamp(WaitSeconds, 0, 3600);
        WaitTiming = WaitTimings.Normalize(WaitTiming);
        PostTaskAction = SessionFinishModes.Normalize(PostTaskAction);

        TimeoutAction = TimeoutActions.Normalize(TimeoutAction);
        WaitMode = WaitModes.Normalize(WaitMode);
        ConcurrentPolicy = ConcurrentPolicies.Normalize(ConcurrentPolicy);
        TimeoutMinutes = Math.Max(0, TimeoutMinutes);
        ConfirmEnterDelaySeconds = Math.Max(0, ConfirmEnterDelaySeconds);
        Order = Math.Max(1, Order == 0 ? fallbackOrder : Order);
        ChannelId = (ChannelId ?? string.Empty).Trim();
        ConcurrentGroup = (ConcurrentGroup ?? string.Empty).Trim();
        LaunchPresetId = (LaunchPresetId ?? string.Empty).Trim();
        LaunchPresetRev = Math.Max(0, LaunchPresetRev);
        ProcessKeywords = KeywordHelper.Normalize(ProcessKeywords);
        WindowKeywords = KeywordHelper.Normalize(WindowKeywords);

        // 外部日志：全部有默认值，老配置没有这些键时走默认值 = 功能关闭
        ExternalLogPath = (ExternalLogPath ?? string.Empty).Trim();
        ExternalLogPattern = ExternalLogDefaults.NormalizePattern(ExternalLogPattern);
        ExternalLogEncoding = ExternalLogEncodings.Normalize(ExternalLogEncoding);
        ExternalLogKeywords = KeywordHelper.Normalize(ExternalLogKeywords);
        ExternalLogPresetId = (ExternalLogPresetId ?? string.Empty).Trim();
        ExternalLogRev = Math.Max(0, ExternalLogRev);

        // 清理登记：三栏都允许分号分隔的多个名字（一个游戏常常要连同平台进程、
        // Unity 崩溃处理器一起收尾），所以这里与两个关键词框同样规整一遍 ——
        // 导入的旧配置、手写的尾随空格都在这一步收敛。
        CleanupPresetId = (CleanupPresetId ?? string.Empty).Trim();
        CleanupRev = Math.Max(0, CleanupRev);
        LauncherProcess = KeywordHelper.ToText(KeywordHelper.Normalize(LauncherProcess));
        MainProcess = KeywordHelper.ToText(KeywordHelper.Normalize(MainProcess));
        GameProcess = KeywordHelper.ToText(KeywordHelper.Normalize(GameProcess));

    }

    /// <summary>
    /// 造一条"收尾任务"（每通道固定一条，由 <c>TaskScopePlanner.EnsureFinishTask</c> 维护）。
    /// 动作默认无操作 —— 与老配置读出来时的默认行为一致。
    ///
    /// <para>恒启用：界面上这条不给关闭（<see cref="CanToggleEnabled"/> 为 false，
    /// <see cref="Sanitize"/> 里也会把 enabled=false 收回来）。</para>
    /// </summary>
    public static TaskConfig CreateFinish(string channelId, int order) => new()
    {
        Enabled = true,
        TaskKind = TaskKinds.Finish,
        Name = "收尾",
        ChannelId = channelId,
        Order = order,
        PostTaskAction = SessionFinishModes.Keep,
    };

    /// <summary>造一条"等待任务"（用户点「添加」后再把类型改成等待时复用）。</summary>
    public static TaskConfig CreateWait(string channelId, int order) => new()
    {
        Enabled = true,
        TaskKind = TaskKinds.Wait,
        Name = "等待",
        ChannelId = channelId,
        Order = order,
        WaitSeconds = 30,
        WaitTiming = WaitTimings.AfterConnect,
    };
}
