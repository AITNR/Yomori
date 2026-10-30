// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Yomori.Helpers;
using Yomori.Models;

namespace Yomori.Services;

public sealed class AbnormalEvent
{
    public string Time { get; set; } = string.Empty;
    public string TaskName { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string Severity { get; set; } = "error";
    public string LogFile { get; set; } = string.Empty;
    public string LogSession { get; set; } = string.Empty;
}

/// <summary>单个任务完成的快照，用于通知与 RDP 跨会话状态回传。</summary>
public sealed class TaskCompletedInfo
{
    public string Name { get; set; } = string.Empty;

    /// <summary>1-based 序号；并发组内为组内推算序号。未知时为 0。</summary>
    public int Index { get; set; }

    public int Total { get; set; }

    public int ElapsedSeconds { get; set; }

    /// <summary>人类可读的结束原因，例如「正常结束，退出码：0」。</summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>是否为异常结束（启动失败 / 超时强退 / 路径无效等）。</summary>
    public bool IsAbnormal { get; set; }

    public DateTimeOffset FinishedAt { get; set; } = DateTimeOffset.Now;

    public string Headline =>
        Total > 0 && Index > 0 ? $"({Index}/{Total}) {Name}" : Name;
}

public sealed class AbnormalReport
{
    public string Version { get; set; } = "v2.0 WinUI";
    public string CreatedAt { get; set; } = string.Empty;
    public string LogFile { get; set; } = string.Empty;
    public string LogSession { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public List<AbnormalEvent> Events { get; set; } = new();
}

/// <summary>带任务归属的外部日志行，供代理写入独立 live-log 文件。</summary>
public sealed class TaskExternalLogLine
{
    public TaskExternalLogLine(string taskName, ExternalLogLine line)
    {
        TaskName = taskName;
        Line = line;
    }

    public string TaskName { get; }
    public ExternalLogLine Line { get; }
}

internal sealed class ExternalLogTailState
{
    public ExternalLogTailState(TaskConfig task, ExternalLogTail tail)
    {
        Task = task;
        Tail = tail;
        Stop = new CancellationTokenSource();
    }

    public TaskConfig Task { get; }
    public ExternalLogTail Tail { get; }
    public CancellationTokenSource Stop { get; }
    public Task? Worker { get; set; }
    public string ReportedError { get; set; } = string.Empty;
}

/// <summary>
/// 任务队列执行器。完整移植 Python 版 ScriptRunnerWorker 的调度语义：
/// 顺序执行 / 并发组 / 暂停 / 停止 / 紧急停止 / 三层清理 / 超时兜底 / 过早退出识别。
/// </summary>
public sealed class ScriptRunner
{
    private const double MonitorAbsentGraceSeconds = 15.0;
    private const double MonitorDetectDeadlineSeconds = 180.0;
    private const double LaunchConfirmSeconds = 2.0;

    /// <summary>
    /// 执行期间登记脚本后代的间隔（秒）。
    ///
    /// <para>只取 2 秒的理由：一次 Toolhelp32 快照在正常机器上 2-5ms，2 秒一次的摊薄开销可忽略；
    /// 而"启动器 → 游戏"这一跳的可观测窗口实测约 5 秒（2026-10-29 终末地：22:04:46 起 MaaEnd，
    /// 22:04:51 Games.exe 创建），2 秒足以在中间层退出前把下一层记进册。</para>
    ///
    /// <para>不取更密的另一个原因：每轮都要对已入册节点做存活检查（各开一次进程句柄），
    /// 密度再高就变成和清理动作抢句柄了。</para>
    /// </summary>
    private const double DescendantSweepSeconds = 2.0;

    /// <summary>
    /// 直接进程秒退后确认"是否由新进程接力"的最长等待（秒）。
    /// 提权重启要过 UAC、新实例要初始化，给 10 秒足够；太长会把真崩溃的判定拖慢。
    /// </summary>
    private const double LaunchRelayGraceSeconds = 10.0;
    private const int ErrorLockBaseSeconds = 15;
    private static readonly SemaphoreSlim s_taskThreadSlots = new(AppConfig.MaxActiveTaskThreads);
    private static int s_liveTaskWorkers;
    private static int s_liveLogWorkers;

    private readonly List<TaskConfig> _tasks;
    private readonly bool _shutdownAfterDone;
    private readonly int _shutdownDelaySeconds;
    private readonly bool _enableTimeoutScreenshot;
    private readonly FileLogger _logger;
    private readonly RuntimeStatsManager _stats;

    private readonly object _sync = new();
    private readonly List<IMonitoredProcess> _currentProcesses = new();
    private readonly Queue<IMonitoredProcess> _processDisposalQueue = new();
    private readonly Dictionary<int, List<string>> _processNamesByPid = new();
    private readonly Dictionary<int, TaskConfig> _tasksByPid = new();

    /// <summary>
    /// 本轮通过窗口标题监控**实际命中**的进程镜像名（如 <c>YuanShen</c> / <c>StarRail</c>）。
    /// 配置里写的常是中文窗口标题，靠它杀不掉也查不出，见 <see cref="LearnMonitorImageNames"/>。
    /// </summary>
    private readonly HashSet<string> _learnedImageNames = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 本轮**脚本进程派生出来的后代**（PID + 镜像名）。
    /// 停止时必须连同它们一起清，理由见 <see cref="LearnScriptDescendants"/>。
    /// 按 PID 记而不是按名字记 —— 名字容易撞车（脚本常会派生 <c>conhost.exe</c> 这类公共进程），
    /// 按名字清会误伤系统里别的同名进程。
    /// </summary>
    private readonly List<ProcessSnapshotEntry> _scriptDescendants = new();

    private readonly List<AbnormalEvent> _abnormalEvents = new();

    /// <summary>
    /// 当前运行中任务的外部日志跟踪器。
    ///
    /// 为什么放在 runner 里：<c>ScriptRunner</c> 同时被主控端本地执行与
    /// 远程代理（<see cref="RdpAgentRunner"/>）复用，挂在这里两条路都有，
    /// 不用各写一遍。每个任务始终由独立 worker 抽取，暂停/启动确认也不停读。
    /// </summary>
    private readonly Dictionary<TaskConfig, ExternalLogTailState> _externalLogTails = new();
    private bool _externalLogTailsClosed;

    private volatile bool _stopRequested;
    private readonly CancellationTokenSource _stopCancellation = new();
    private readonly object _runLock = new();
    private Task? _runTask;
    /// <summary>
    /// 停止信号（<c>RequestStop</c> / <c>RequestEmergencyStop</c> 置位）。
    /// 让轮询循环能用可中断等待替代 <c>Thread.Sleep</c>：点「停止」时不必再
    /// 等满一个轮询周期才响应，循环里的 <c>_stopRequested</c> 检查也能立刻生效。
    /// </summary>
    private readonly ManualResetEventSlim _stopSignal = new(false);
    private volatile bool _pauseRequested;
    private int _liveTaskWorkers;
    private volatile bool _runFinished;

    /// <summary>
    /// 「本次运行期间新出现的进程」候选，按任务名分组。
    ///
    /// <para>【为什么需要它】预设只能覆盖有数据的项目，48 款游戏里绝大多数（尤其手游走模拟器）
    /// 没有可靠的镜像名可预置。与其猜，不如把"这台机器上真的冒出来了什么"递给用户勾一下 ——
    /// 用户不需要知道 `Endfield.exe` 长什么样，点一下就把名字填进「游戏进程」。</para>
    ///
    /// <para>只在**本地执行**路径有意义：远程通道的采集发生在代理进程里，本进程读不到。
    /// 界面据此提示「远程通道请到目标会话里看」。</para>
    /// </summary>
    private readonly Dictionary<string, List<ProcessSnapshotEntry>> _candidateProcesses =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>当前实际执行 <see cref="RunOneTask"/> 的任务 worker 数，可供主界面采样。</summary>
    public static int LiveTaskWorkers => Volatile.Read(ref s_liveTaskWorkers);

    /// <summary>当前外部日志轮询 worker 数，可供主界面采样。</summary>
    public static int LiveLogWorkers => Volatile.Read(ref s_liveLogWorkers);

    public ScriptRunner(
        IEnumerable<TaskConfig> tasks,
        bool shutdownAfterDone,
        int shutdownDelaySeconds,
        FileLogger logger,
        RuntimeStatsManager stats,
        bool enableTimeoutScreenshot)
    {
        _tasks = new List<TaskConfig>(tasks);
        _shutdownAfterDone = shutdownAfterDone;
        _shutdownDelaySeconds = Math.Max(0, shutdownDelaySeconds);
        _enableTimeoutScreenshot = enableTimeoutScreenshot;
        _logger = logger;
        _stats = stats;
        AbnormalReportPath = Path.Combine(logger.LogDir, "last_abnormal_report.json");
    }

    // ---------------- 外部脚本实时日志 ----------------

    /// <summary>进程启动前建立 baseline；每个任务始终由独立 worker 读取。</summary>
    private void StartExternalLogTail(TaskConfig task, string scriptDirectory)
    {
        if (!task.ExternalLogActive) { return; }
        StopExternalLogTail(task);
        try
        {
            lock (_sync)
            {
                // WaitFirst 并发任务可能仍在启动，队列收尾后禁止再注册 worker。
                if (_externalLogTailsClosed) { return; }
                var tail = new ExternalLogTail(task.ExternalLogPath, task.ExternalLogPattern,
                    task.ExternalLogEncoding, task.ExternalLogFollowTail, scriptDirectory,
                    task.ExternalLogRecursive, task.ExternalLogView);
                var state = new ExternalLogTailState(task, tail);
                _externalLogTails[task] = state;
                var start = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                state.Worker = Task.Run(async () =>
                {
                    await start.Task.ConfigureAwait(false);
                    await ExternalLogTailLoopAsync(state).ConfigureAwait(false);
                });
                start.SetResult(true);
            }
        }
        catch (Exception ex)
        {
            EmitExternalLogDiagnostic(task, $"日志采集无法启动：{ex.Message}");
        }
    }

    private void StopAllExternalLogTails()
    {
        List<TaskConfig> tasks;
        lock (_sync)
        {
            _externalLogTailsClosed = true;
            tasks = new List<TaskConfig>(_externalLogTails.Keys);
        }
        foreach (var task in tasks) { StopExternalLogTail(task); }
    }

    /// <summary>等待该任务 worker 完成 final drain 和 Dispose 后才返回。</summary>
    private void StopExternalLogTail(TaskConfig task)
    {
        ExternalLogTailState? state;
        lock (_sync) { _externalLogTails.Remove(task, out state); }
        if (state is null) { return; }
        state.Stop.Cancel();
        try
        {
            state.Worker?.GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            EmitExternalLogDiagnostic(task, $"日志采集收尾失败：{ex.Message}");
        }
        finally { state.Stop.Dispose(); }
    }

    private async Task ExternalLogTailLoopAsync(ExternalLogTailState state)
    {
        Interlocked.Increment(ref s_liveLogWorkers);
        try
        {
            // 保持异步启动，不让注册日志 worker 的任务同步执行首次文件轮询。
            await Task.Yield();
            while (!state.Stop.IsCancellationRequested)
            {
                ReadExternalLog(state, final: false);
                try
                {
                    await Task.Delay(ExternalLogDefaults.PollIntervalMs, state.Stop.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (state.Stop.IsCancellationRequested)
                {
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            EmitExternalLogDiagnostic(state.Task, $"日志采集停止：{ex.Message}");
        }
        finally
        {
            try
            {
                // HasUnreadData covers both the 256 KiB read cap and the 200-line emit cap.
                // Keep shutdown bounded even when a writer continues appending indefinitely.
                for (var batch = 0; batch < 64; batch++)
                {
                    ReadExternalLog(state, final: true);
                    if (!state.Tail.HasUnreadData) { break; }
                }
            }
            finally
            {
                try
                {
                    state.Tail.Dispose();
                }
                finally
                {
                    Interlocked.Decrement(ref s_liveLogWorkers);
                }
            }
        }
    }

    private int ReadExternalLog(ExternalLogTailState state, bool final)
    {
        var lines = final ? state.Tail.Drain(state.Task.ExternalLogKeywords)
            : state.Tail.Poll(state.Task.ExternalLogKeywords);
        var error = state.Tail.LastError;
        if (!string.Equals(state.ReportedError, error, StringComparison.Ordinal))
        {
            state.ReportedError = error;
            EmitExternalLogDiagnostic(state.Task, error.Length == 0 ? "日志采集已恢复。" : error);
        }
        foreach (var line in lines) { EmitExternalLogLine(state.Task, line); }

        // 心跳 / 日志中断：没有新行的时候才更需要它（否则长任务在面板上看起来像卡死）。
        var tick = state.Tail.Tick();
        if (tick is not null) { EmitExternalLogLine(state.Task, tick); }

        return lines.Count;
    }

    private void EmitExternalLogDiagnostic(TaskConfig task, string message) =>
        EmitExternalLogLine(task, new ExternalLogLine
        {
            Text = $"[日志采集] {message.Replace("异常", "错误", StringComparison.Ordinal)}",
            Level = ExternalLogLevel.Warn,
        }, diagnostic: true);

    private void EmitExternalLogLine(TaskConfig task, ExternalLogLine line, bool diagnostic = false)
    {
        // 诊断只走带任务归属的实时日志通道，不经 Logged/status.json.Events。
        InvokeSafely(TaskExternalLogLineRead, new TaskExternalLogLine(task.DisplayName, line), nameof(TaskExternalLogLineRead));
        if (!diagnostic) { InvokeSafely(ExternalLogLineRead, line, nameof(ExternalLogLineRead)); }
    }

    public string AbnormalReportPath { get; }

    public bool HadTaskError { get; private set; }

    public int TaskErrorCount { get; private set; }

    /// <summary>
    /// 本轮是否被停止请求打断。
    ///
    /// <para>存在的理由：<see cref="Finished"/> 的参数是"停止或出错"，
    /// 停止分支同样传 true。回调方若只看那个参数，会把"用户点了停止"写成"异常"
    /// —— 远程代理以前就是这么把停止报成「已完成（有异常）」的。</para>
    /// </summary>
    public bool WasStopped => _stopRequested;

    public IReadOnlyList<AbnormalEvent> AbnormalEvents => _abnormalEvents;

    public event EventHandler<string>? Logged;
    public event EventHandler<string>? StatusChanged;
    public event EventHandler<(int Current, int Total)>? ProgressChanged;
    public event EventHandler<(string Name, int Index, int Total)>? TaskStarted;
    public event EventHandler<TaskCompletedInfo>? TaskCompleted;
    public event EventHandler<int>? ElapsedChanged;
    public event EventHandler<string>? TaskLaunchSucceeded;
    public event EventHandler<string>? TaskError;
    public event EventHandler<int>? ShutdownPrompt;
    public event EventHandler<bool>? Finished;

    /// <summary>
    /// 外部脚本实时日志的一行（**全部**行都会触发，不抽样、不丢弃）。
    /// 订阅方自行决定怎么展示；跟踪失败不触发本事件。
    /// </summary>
    public event EventHandler<ExternalLogLine>? ExternalLogLineRead;

    /// <summary>外部日志行及其任务名，供代理独立写入 live-log.ndjson。</summary>
    public event EventHandler<TaskExternalLogLine>? TaskExternalLogLineRead;

    // ---------------- 对外控制 ----------------

    public void RequestPause()
    {
        _pauseRequested = true;
        RaiseStatus("已暂停");
        Log("收到暂停请求：任务队列会暂停计时和后续检查，已启动的外部脚本不会被强制暂停。");
    }

    public void RequestResume()
    {
        if (!_pauseRequested)
        {
            return;
        }

        _pauseRequested = false;
        RaiseStatus("运行中");
        Log("收到继续请求：任务队列恢复运行。");
    }

    /// <summary>
    /// 停止执行：队列立即停，并按**全部任务配置**做统一收尾。
    ///
    /// <para>【收尾范围为什么取全部】界面上只有一个停止按钮（原「紧急停止全部」已合并进来），
    /// 那它必须是"能停干净"的那个 —— 只清本轮启动过的任务会漏掉「启动后不等待」那类
    /// 已经脱离本轮的进程，用户看到的就是"点了停止，游戏还在跑"。</para>
    ///
    /// <para>【为什么这里不做整轮清理】本方法由界面 / 停止监听线程**同步**调用，
    /// 而按关键词清理要枚举进程、逐个发 taskkill。在这十几秒里工作线程被完全占住：
    /// 进度停摆、心跳停摆、状态不再回传 —— 主控端看到的就是"点了停止，界面卡住没反应"
    /// （真机日志实证过：停止到收尾隔了 14 秒）。所以这里只做两件轻量且立刻见效的事：
    /// 置标志位 + 结束已登记的直接脚本进程；三层清理交给主循环下一拍（250ms 内必到）。</para>
    /// </summary>
    public void RequestStop()
    {
        _stopRequested = true;
        _stopCancellation.Cancel();
        _stopSignal.Set();
        _pauseRequested = false;
        Log("收到停止请求：本轮立即停止，并按全部任务配置清理进程。");
        TerminateTrackedProcesses("停止执行");
    }

    /// <summary>
    /// 兼容旧主控端下发的 <c>stop.json</c>（<c>emergency = true</c>）。
    ///
    /// <para>停止只有一个档位了，本方法等价于 <see cref="RequestStop"/>；保留它是因为
    /// 桥协议里 <c>emergency</c> 字段还在，旧版本主控端仍会带着 true 发过来。</para>
    /// </summary>
    public void RequestEmergencyStop() => RequestStop();

    /// <summary>
    /// 立刻结束当前登记在册的脚本进程（轻量：只 Kill，不枚举、不扫关键词）。
    /// 关键词那层留给主循环，避免把调用方（计时回调 / 停止监听线程）堵住。
    /// </summary>
    private void TerminateTrackedProcesses(string scopeLabel)
    {
        List<IMonitoredProcess> processes;
        lock (_sync)
        {
            processes = new List<IMonitoredProcess>(_currentProcesses);
        }

        // 在这里就把"脚本进程派生了谁"记下来 —— 这是停止链路上**最早**的一刻，
        // 晚一点父进程可能已经死了，只剩孤儿记录（2026-09-27 漏网事故的直接原因）。
        // 开销只是一次进程快照（毫秒级），不影响本方法"轻量"的约束。
        LearnScriptDescendants(processes);

        foreach (var process in processes)
        {
            if (TerminateTrackedProcess(process))
            {
                Log($"{scopeLabel}：正在终止脚本进程 PID：{process.Pid}");
            }
        }
    }

    /// <summary>Starts this runner at most once; concurrent callers observe the same execution task.</summary>
    public Task RunAsync()
    {
        lock (_runLock)
        {
            return _runTask ??= Task.Run(RunCore);
        }
    }

    // ---------------- 主循环 ----------------

    private void RunCore()
    {
        var stopped = false;

        // 候选进程按"本轮"重算：上一轮的候选留到新一轮开始前都还能被界面读到
        // （用户可能跑完才想起来去点「从上次运行填入」），但不能跨轮混。
        lock (_sync)
        {
            _candidateProcesses.Clear();
        }

        try
        {
            RaiseStatus("运行中");

            var enabledTasks = new List<TaskConfig>();
            foreach (var task in _tasks)
            {
                if (task.Enabled)
                {
                    enabledTasks.Add(task);
                }
            }

            enabledTasks.Sort((a, b) => a.Order.CompareTo(b.Order));

            var total = enabledTasks.Count;
            if (total == 0)
            {
                Log("没有启用的任务，执行结束。");
                RaiseStatus("已完成");
                RaiseProgress(0, 0);
                RaiseFinished(false);
                return;
            }

            Log($"运行日志文件：{Path.GetFileName(_logger.LogPath)}");
            Log($"开始执行任务队列，共 {total} 个启用任务。");
            RaiseProgress(0, total);

            var index = 0;
            while (index < total)
            {
                WaitIfPaused();
                if (_stopRequested)
                {
                    stopped = true;
                    Log("检测到停止请求，后续任务不再执行。");
                    break;
                }

                var task = enabledTasks[index];
                var groupName = task.ConcurrentGroup.Trim();

                if (groupName.Length > 0)
                {
                    var groupTasks = new List<TaskConfig> { task };
                    var j = index + 1;
                    while (j < total && enabledTasks[j].ConcurrentGroup.Trim() == groupName)
                    {
                        groupTasks.Add(enabledTasks[j]);
                        j++;
                    }

                    RaiseProgress(index + 1, total);
                    RaiseStatus("并发运行中");
                    RaiseElapsed(0);
                    var groupOutcome = RunConcurrentGroup(groupName, groupTasks, index + 1, total);
                    index = j;

                    if (groupOutcome != RunOutcome.Continue)
                    {
                        stopped = true;
                        break;
                    }
                }
                else
                {
                    RaiseProgress(index + 1, total);
                    RaiseTaskStarted(task.DisplayName, index + 1, total);
                    RaiseStatus("运行中");
                    RaiseElapsed(0);
                    if (!WaitForTaskThreadSlot())
                    {
                        stopped = true;
                        break;
                    }

                    RunOutcome outcome;
                    try
                    {
                        outcome = RunOneTask(task, index + 1, total);
                    }
                    catch (Exception ex)
                    {
                        var message = $"任务「{task.DisplayName}」执行异常：{ex.Message}";
                        Log(message);
                        MarkTaskError(message, task.DisplayName);
                        RaiseTaskCompleted(task.DisplayName, index + 1, total, 0, "执行异常", abnormal: true);
                        outcome = RunOutcome.Continue;
                    }
                    finally
                    {
                        s_taskThreadSlots.Release();
                    }

                    index++;

                    if (outcome != RunOutcome.Continue)
                    {
                        stopped = true;
                        break;
                    }
                }
            }

            if (stopped)
            {
                // 统一收尾（必须在清空登记表**之前**）：正常路径上 RunOneTask 已经就地清过了，
                // 但「启动确认 / 接力确认被停止打断」走的是另一条分支（只放弃等待、不做清理），
                // 常规停止在那条路上就没人杀目标进程 —— 表现成"点了停止，游戏还在跑"。
                CleanupForStop();
                StopAllExternalLogTails();

                RaiseStatus("已停止");
                Log("任务队列已停止。");
                SaveAbnormalReport();
                RaiseFinished(true);
                return;
            }

            if (HadTaskError)
            {
                RaiseStatus("已完成（有异常）");
                RaiseProgress(total, total);
                Log("任务队列执行结束，但存在启动失败、过早退出、超时强退等异常。");
                Log("异常已写入上次运行报告；下次启动会主动汇报。");
            }
            else if (_abnormalEvents.Count > 0)
            {
                RaiseStatus("已完成（有提醒）");
                RaiseProgress(total, total);
                Log("任务队列执行结束，存在配置或监控目标提醒，但未判定为严重异常。");
                Log("提醒已写入上次运行报告；下次启动会主动汇报。");
            }
            else
            {
                RaiseStatus("已完成");
                RaiseProgress(total, total);
                Log("所有任务执行完成。");
            }

            SaveAbnormalReport();

            if (_shutdownAfterDone)
            {
                var delay = Math.Max(1, _shutdownDelaySeconds == 0 ? 60 : _shutdownDelaySeconds);
                Log($"已启用自动关机，将交由主界面弹出 {delay} 秒倒计时确认窗口。");
                InvokeSafely(ShutdownPrompt, delay, nameof(ShutdownPrompt));
            }
            else
            {
                Log("未启用自动关机。");
            }

            SaveStatsQuietly("本次运行");

            RaiseFinished(HadTaskError);
        }
        catch (Exception ex)
        {
            RaiseStatus("异常");
            Log($"执行器发生异常：{ex.Message}");
            MarkTaskError($"执行器发生异常：{ex.Message}");
            SaveAbnormalReport();
            SaveStatsQuietly("异常前");

            RaiseFinished(true);
        }
        finally
        {
            StopAllExternalLogTails();
            QueueAllTrackedProcessesForDisposeIfFinished(markRunFinished: true);
        }
    }

    private RunOutcome RunConcurrentGroup(string groupName, List<TaskConfig> groupTasks, int startIndex, int total)
    {
        var policy = ConcurrentPolicies.Normalize(groupTasks[0].ConcurrentPolicy);
        Log($"开始并发组「{groupName}」，共 {groupTasks.Count} 个任务，策略：{ConcurrentPolicies.Label(policy)}。");
        RaiseTaskStarted($"并发组：{groupName}", startIndex, total);

        // WaitFirst 仍在本组成员全部启动后才等待组首任务。若前面的 WaitFirst
        // 任务仍占用 16 个活动槽位，后续组成员会等待槽位，即使本组组首已完成；
        // 这是上限带来的背压，不改变任务策略。

        var results = new Dictionary<int, RunOutcome>();
        var resultsLock = new object();
        var doneEvents = new List<ManualResetEventSlim>();
        var threads = new List<Thread>();

        for (var i = 0; i < groupTasks.Count; i++)
        {
            doneEvents.Add(new ManualResetEventSlim(false));
        }

        for (var i = 0; i < groupTasks.Count; i++)
        {
            if (!WaitForTaskThreadSlot())
            {
                return RunOutcome.StopRequested;
            }

            var capturedIndex = i;
            var capturedTask = groupTasks[i];
            var thread = new Thread(() =>
            {
                try
                {
                    RunOutcome outcome;
                    try
                    {
                        outcome = RunOneTask(capturedTask, startIndex + capturedIndex, total);
                    }
                    catch (Exception ex)
                    {
                        var message = $"任务「{capturedTask.DisplayName}」执行异常：{ex.Message}";
                        Log(message);
                        MarkTaskError(message, capturedTask.DisplayName);
                        RaiseTaskCompleted(capturedTask.DisplayName, startIndex + capturedIndex, total, 0, "执行异常", abnormal: true);
                        outcome = RunOutcome.Continue;
                    }

                    lock (resultsLock)
                    {
                        results[capturedIndex] = outcome;
                    }
                }
                finally
                {
                    doneEvents[capturedIndex].Set();
                    s_taskThreadSlots.Release();
                }
            })
            {
                IsBackground = true,
                Name = $"concurrent-{groupName}-{i}",
            };

            threads.Add(thread);
            try
            {
                thread.Start();
            }
            catch
            {
                s_taskThreadSlots.Release();
                doneEvents[capturedIndex].Set();
                throw;
            }
            Thread.Sleep(200);
        }

        if (policy == ConcurrentPolicies.WaitFirst)
        {
            Log($"并发组「{groupName}」采用只等待组首任务策略；组首任务完成后会继续后续队列，其它同组任务可能仍在运行。");
            while (!doneEvents[0].IsSet)
            {
                WaitIfPaused();
                if (_stopRequested)
                {
                    return RunOutcome.StopRequested;
                }

                WaitPoll(250);
            }

            Log($"并发组「{groupName}」组首任务已完成，继续后续队列。");
            lock (resultsLock)
            {
                return results.TryGetValue(0, out var first) ? first : RunOutcome.Continue;
            }
        }

        while (true)
        {
            WaitIfPaused();
            if (_stopRequested)
            {
                return RunOutcome.StopRequested;
            }

            var allDone = true;
            foreach (var done in doneEvents)
            {
                if (!done.IsSet)
                {
                    allDone = false;
                    break;
                }
            }

            if (allDone)
            {
                break;
            }

            WaitPoll(250);
        }

        lock (resultsLock)
        {
            foreach (var outcome in results.Values)
            {
                if (outcome != RunOutcome.Continue)
                {
                    return RunOutcome.StopAll;
                }
            }
        }

        Log($"并发组「{groupName}」全部任务已完成。");
        return RunOutcome.Continue;
    }

    private RunOutcome RunOneTask(TaskConfig task, int index, int total)
    {
        Interlocked.Increment(ref s_liveTaskWorkers);
        Interlocked.Increment(ref _liveTaskWorkers);

        // 「本次运行新出现了哪些进程」的基准：必须在真正起进程**之前**拍。
        // 放在 wrapper 里而不是 Core 里，是因为 Core 有多条 return 路径（正常完成 /
        // 停止 / 超时 / 失败），只有 finally 能一次盖全。
        var beforePids = CapturePidSnapshot();
        try
        {
            return RunOneTaskCore(task, index, total);
        }
        finally
        {
            RecordCandidateProcesses(task, beforePids);
            Interlocked.Decrement(ref s_liveTaskWorkers);
            var remaining = Interlocked.Decrement(ref _liveTaskWorkers);
            if (remaining == 0)
            {
                QueueAllTrackedProcessesForDisposeIfFinished();
            }
        }
    }

    // ---------------- 「本次运行新出现的进程」采集 ----------------

    /// <summary>
    /// 出现在候选里只会误导用户的进程名：系统/公共宿主、以及本程序自己。
    ///
    /// <para>判据是"杀掉它或把它登记进清理名单，会波及与本任务无关的东西"。
    /// 与既有《进程清理登记自动适配计划书》§2.3 的黑名单同源，另加 Yomori 自身与
    /// 几个每次跑自动化都必然出现的壳（`conhost` / `WerFault` / `SearchApp`）。</para>
    /// </summary>
    private static readonly HashSet<string> CandidateProcessBlocklist = new(StringComparer.OrdinalIgnoreCase)
    {
        // 内核 / 会话宿主
        "system", "idle", "smss.exe", "csrss.exe", "wininit.exe", "winlogon.exe",
        "services.exe", "lsass.exe", "svchost.exe", "fontdrvhost.exe", "dwm.exe",
        // 桌面外壳与交互宿主
        "explorer.exe", "sihost.exe", "ctfmon.exe", "conhost.exe", "dllhost.exe",
        "runtimebroker.exe", "taskhostw.exe", "searchapp.exe", "startmenuexperiencehost.exe",
        "shellexperiencehost.exe", "backgroundtaskhost.exe", "applicationframehost.exe",
        "textinputhost.exe", "werfault.exe", "wmiprvse.exe", "taskmgr.exe",
        // 本程序自己
        "yomori.exe", "yomori.agent.exe", "msedgewebview2.exe",
        // 运行时与脚本宿主：自动化工具必然带它们，但不是"这次任务专用的"
        "cmd.exe", "powershell.exe", "pwsh.exe", "python.exe", "pythonw.exe",
        "node.exe", "java.exe", "javaw.exe", "dotnet.exe",
        // 系统服务（真机日志里这三个被当成候选报出来过）
        "rtkauduservice64.exe", "searchprotocolhost.exe", "searchfilterhost.exe",
        "searchindexer.exe", "sppsvc.exe", "spoolsv.exe", "audiodg.exe",
        "msmpeng.exe", "securityhealthservice.exe", "securityhealthsystray.exe",
        "smartscreen.exe", "wudfhost.exe", "useroobebroker.exe", "dcomlaunch.exe",
        "igfxem.exe", "nvcontainer.exe", "nvngx_update.exe", "nvsphelper64.exe",
        // 游戏栏与浏览器：跑自动化时被系统顺手拉起，或用户自己开着，都不是游戏本体
        "gamebar.exe", "gamebarftserver.exe", "gamebarpresencewriter.exe",
        "xboxgamebarwidgets.exe", "msedge.exe", "chrome.exe", "firefox.exe",
        // 本程序自己起的检测进程（TasklistHasImage）也会被采集到
        "tasklist.exe",
    };

    private static HashSet<int> CapturePidSnapshot()
    {
        var set = new HashSet<int>();
        foreach (var item in ProcessHelper.SnapshotProcesses())
        {
            if (item.Pid > 0)
            {
                set.Add(item.Pid);
            }
        }

        return set;
    }

    /// <summary>
    /// 算出本次任务"新出现、且不像工具/系统"的进程，存进 <see cref="_candidateProcesses"/>。
    ///
    /// <para>**只记录、不写入任何配置**。这些名字会被拿去填「游戏进程」，而那一栏是
    /// 按名全局匹配杀进程的 —— 猜错就是误杀别人的程序，必须由用户勾选确认。</para>
    /// </summary>
    private void RecordCandidateProcesses(TaskConfig task, HashSet<int> beforePids)
    {
        if (beforePids.Count == 0)
        {
            // 开工前拍不到快照（系统受限），此时"差集"就是全量进程表 —— 宁可没有候选，
            // 也不能把几百个进程名糊到用户脸上。
            return;
        }

        var after = ProcessHelper.SnapshotProcesses();
        if (after.Count == 0)
        {
            return;
        }

        // 已在册的脚本进程与后代不算候选：它们本来就会被清理，不需要用户登记
        var knownPids = new HashSet<int>();
        lock (_sync)
        {
            foreach (var process in _currentProcesses)
            {
                knownPids.Add(process.Pid);
            }

            foreach (var item in _scriptDescendants)
            {
                knownPids.Add(item.Pid);
            }
        }

        // 工具自己的名字也不算：那正是"清理登记"里已经自动覆盖的部分
        var toolNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var scriptPath = task.ScriptPath.Trim().Length > 0
            ? Path.GetFullPath(Environment.ExpandEnvironmentVariables(task.ScriptPath.Trim()))
            : null;
        foreach (var name in ProcessNamesForTask(task, scriptPath))
        {
            foreach (var candidate in ProcessHelper.ProcessNameCandidates(name))
            {
                toolNames.Add(ProcessHelper.BareImageName(candidate));
            }
        }

        var picked = new List<ProcessSnapshotEntry>();
        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in after)
        {
            if (item.Pid <= 0 || beforePids.Contains(item.Pid) || knownPids.Contains(item.Pid))
            {
                continue;
            }

            var bare = ProcessHelper.BareImageName(item.Name);
            if (bare.Length == 0
                || CandidateProcessBlocklist.Contains(bare)
                || CandidateProcessBlocklist.Contains(item.Name)
                || toolNames.Contains(bare)
                || !seenNames.Add(bare))
            {
                continue;
            }

            picked.Add(item);
            if (picked.Count >= CandidateProcessLimit)
            {
                break;
            }
        }

        if (picked.Count == 0)
        {
            return;
        }

        lock (_sync)
        {
            _candidateProcesses[task.DisplayName] = picked;
        }

        Log($"本次运行新出现 {picked.Count} 个进程，可作为「游戏进程」候选："
            + string.Join("、", picked.ConvertAll(p => p.Name)));
    }

    /// <summary>单个任务最多给几个候选（再多用户就不看了，而且多半是噪音）。</summary>
    private const int CandidateProcessLimit = 12;

    /// <summary>取某个任务的候选进程（界面的「从上次运行填入」用）。</summary>
    public IReadOnlyList<ProcessSnapshotEntry> CandidatesFor(string? taskName)
    {
        lock (_sync)
        {
            return _candidateProcesses.TryGetValue(taskName ?? string.Empty, out var list)
                ? new List<ProcessSnapshotEntry>(list)
                : new List<ProcessSnapshotEntry>();
        }
    }

    /// <summary>
    /// 全部候选（任务名 + 镜像名 + PID），供代理写进 <c>status.json</c> 回传主控端。
    ///
    /// <para>为什么要带任务名：一次运行可能有多个任务，主控端要按"用户当前选中的那条任务"
    /// 过滤候选 —— 把 A 任务的候选填到 B 任务里，等于给 B 配了它根本不认识的进程名。</para>
    /// </summary>
    public List<(string Task, string Name, int Pid)> CandidateSnapshot()
    {
        var result = new List<(string, string, int)>();
        lock (_sync)
        {
            foreach (var pair in _candidateProcesses)
            {
                foreach (var item in pair.Value)
                {
                    result.Add((pair.Key, item.Name, item.Pid));
                }
            }
        }

        return result;
    }

    private RunOutcome RunOneTaskCore(TaskConfig task, int index, int total)
    {
        var name = task.DisplayName;
        var scriptText = task.ScriptPath.Trim();
        var timeoutSeconds = task.TimeoutSeconds;

        Log($"开始执行任务：{name}");
        Log(FileLogger.BuildSessionMarker(name, "START"));
        Log($"脚本路径：{scriptText}");
        if (task.UseArgs && task.Args.Trim().Length > 0)
        {
            Log($"启动参数：{task.Args.Trim()}");
        }

        var monitorSpec = BuildMonitorSpec(task);
        if (task.WaitMode == WaitModes.FireAndContinue)
        {
            Log("等待模式：启动后不等待；此任务启动成功后会立即进入后续任务。");
        }
        else
        {
            Log($"监控模式：{monitorSpec.Describe()}。监控目标出现后开始等待，连续消失 {MonitorAbsentGraceSeconds:0} 秒后继续下一个任务；超时仅作为兜底。");
        }

        if (task.ConcurrentGroup.Trim().Length > 0)
        {
            Log($"并发组：{task.ConcurrentGroup.Trim()}，组策略：{ConcurrentPolicies.Label(task.ConcurrentPolicy)}");
        }

        if (scriptText.Length == 0)
        {
            var message = $"任务「{name}」脚本路径为空，已跳过。";
            Log(message);
            MarkTaskError(message, name);
            RaiseTaskCompleted(name, index, total, 0, "脚本路径为空，已跳过", abnormal: true);
            return RunOutcome.Continue;
        }

        var scriptPath = Path.GetFullPath(Environment.ExpandEnvironmentVariables(scriptText));
        if (!File.Exists(scriptPath))
        {
            var message = $"任务「{name}」脚本不存在，已跳过：{scriptPath}";
            Log(message);
            MarkTaskError(message, name);
            RaiseTaskCompleted(name, index, total, 0, "脚本不存在，已跳过", abnormal: true);
            return RunOutcome.Continue;
        }

        string fileName;
        string arguments;
        try
        {
            (fileName, arguments) = CommandLine.BuildTaskCommand(scriptPath, task.UseArgs, task.Args);
        }
        catch (Exception ex)
        {
            var message = $"任务「{name}」构建启动命令失败，已跳过：{ex.Message}";
            Log(message);
            MarkTaskError(message, name);
            RaiseTaskCompleted(name, index, total, 0, "构建启动命令失败，已跳过", abnormal: true);
            return RunOutcome.Continue;
        }

        var workingDirectory = Path.GetDirectoryName(scriptPath) ?? AppPaths.BaseDir;

        StartExternalLogTail(task, workingDirectory);

        IMonitoredProcess process;
        try
        {
            process = ProcessLauncher.Start(fileName, arguments, workingDirectory);
        }
        catch (Exception ex)
        {
            StopExternalLogTail(task);
            var message = $"任务「{name}」启动失败，已跳过：{ex.Message}";
            Log(message);
            MarkTaskError(message, name);
            RaiseTaskCompleted(name, index, total, 0, "启动失败，已跳过", abnormal: true);
            return RunOutcome.Continue;
        }

        var processNames = ProcessNamesForTask(task, scriptPath);
        TrackProcess(process, processNames, task);

        Log($"任务「{name}」已启动，PID：{process.Pid}");


        if (processNames.Count > 0)
        {
            Log($"已登记可强制结束的同名进程：{string.Join(", ", processNames)}");
        }

        var stopwatch = Stopwatch.StartNew();
        if (!ConfirmLaunchSuccess(task, process, name, stopwatch, monitorSpec))
        {
            // 停止请求把启动确认 / 接力确认打断了 —— 进程很可能还活着（是我们主动放弃等待，
            // 不是它退出了），所以既不解除登记、也不记"启动失败或提前退出"这条异常：
            // 登记信息要留给 RunCore 的统一收尾去清（提权接力起来的新实例也在关键词范围内）。
            if (_stopRequested)
            {
                Log($"任务「{name}」在启动确认阶段被停止请求打断，交给统一收尾清理。");
                return RunOutcome.StopRequested;
            }

            UntrackProcess(process);
            StopExternalLogTail(task);
            var failedElapsed = (int)stopwatch.Elapsed.TotalSeconds;
            _stats.AddRecord(name, failedElapsed, "启动失败或提前退出", task.WaitMode);
            RaiseTaskCompleted(name, index, total, failedElapsed, "启动失败或提前退出", abnormal: true);
            return RunOutcome.Continue;
        }

        InvokeSafely(TaskLaunchSucceeded, name, nameof(TaskLaunchSucceeded));

        if (task.WaitMode == WaitModes.FireAndContinue)
        {
            Log($"任务「{name}」已按“启动后不等待”处理，继续后续任务。");
            UntrackProcess(process);
            _stats.AddRecord(name, 0, "启动后不等待", task.WaitMode);
            RaiseTaskCompleted(name, index, total, 0, "已启动（启动后不等待）", abnormal: false);
            return RunOutcome.Continue;
        }

        if (task.ConfirmEnterDelaySeconds > 0)
        {
            SchedulePressEnter(task.ConfirmEnterDelaySeconds, name);
        }

        var lastEmitSecond = -1;
        var earlyExitThreshold = timeoutSeconds > 0 ? timeoutSeconds / 6.0 : 0;

        var monitorSeen = false;
        var monitorWaitLogged = false;
        double? monitorAbsentSince = null;

        // 后代扫描的节拍：轮询是 200～350ms，扫描取 2 秒（一次 Toolhelp32 快照，2-5ms）。
        // 为什么不能只在停止那一刻扫：中间层（启动器）拉起游戏后自己就退了，
        // 等用户按停止时它早已不在进程表里，BFS 从那一步就断（见 LearnScriptDescendants 的增量爬升）。
        var lastDescendantSweep = stopwatch.Elapsed;

        RunOutcome FinishWithLog(string status, bool checkEarlyExit = true, bool abnormal = false)
        {
            var elapsedDone = (int)stopwatch.Elapsed.TotalSeconds;
            RaiseElapsed(elapsedDone);
            Log(FileLogger.BuildSessionMarker(name, "END"));
            Log($"任务「{name}」{status}，耗时：{FormatHelper.FormatSeconds(elapsedDone)}。");

            if (checkEarlyExit && earlyExitThreshold > 0 && elapsedDone < earlyExitThreshold)
            {
                var message =
                    $"任务「{name}」疑似过早退出：实际耗时 {FormatHelper.FormatSeconds(elapsedDone)}，" +
                    $"低于预估/最大时间 1/6（{FormatHelper.FormatSeconds((long)earlyExitThreshold)}）。";
                Log(message);
                MarkTaskError(message, name);
                abnormal = true;
            }

            // 最后一次抽水：脚本退出前的最后几行别漏掉
            StopExternalLogTail(task);

            UntrackProcess(process);
            _stats.AddRecord(name, elapsedDone, status, task.WaitMode);
            RaiseTaskCompleted(name, index, total, elapsedDone, status, abnormal);
            return RunOutcome.Continue;
        }

        while (true)
        {
            WaitIfPaused();
            if (_stopRequested)
            {
                Log($"任务「{name}」执行中收到停止请求，正在按统一清理顺序收尾。");

                // 收尾范围取全部任务配置：界面上只有一个停止按钮，它必须能停干净。
                var scope = new List<TaskConfig>(_tasks);

                CleanupProcessesForTasks(scope, new List<IMonitoredProcess> { process }, "停止执行", includeWindowKeywords: true);
                StopExternalLogTail(task);
                UntrackProcess(process);
                return RunOutcome.StopRequested;
            }

            var exitCode = PollTrackedProcess(process);
            var now = stopwatch.Elapsed.TotalSeconds;

            // 执行期间持续登记后代（每 DescendantSweepSeconds 一次）。
            // LearnScriptDescendants 内部有去重，只在发现新进程时才写日志，不会刷屏。
            if ((stopwatch.Elapsed - lastDescendantSweep).TotalSeconds >= DescendantSweepSeconds)
            {
                lastDescendantSweep = stopwatch.Elapsed;
                LearnScriptDescendants(null);
            }

            var monitorKind = monitorSpec.Kind;

            if (monitorKind is MonitorKinds.Window or MonitorKinds.Process or MonitorKinds.Cmdline)
            {
                var (present, matchedKeyword) = IsMonitorPresent(monitorSpec);
                var label = monitorSpec.Label;

                if (present)
                {
                    monitorAbsentSince = null;
                    if (!monitorSeen)
                    {
                        monitorSeen = true;
                        Log($"检测到{label}「{matchedKeyword}」，现在开始等待它消失。");

                        // 监控命中 = 目标真的起来了，此刻它的**真实镜像名**是可查的。
                        // 记下来，停止时按它清理和复查，不再依赖窗口标题（见 LearnMonitorImageNames）。
                        LearnMonitorImageNames(task);
                    }
                }
                else if (monitorSeen)
                {
                    if (monitorAbsentSince is null)
                    {
                        monitorAbsentSince = now;
                        Log($"{label}暂时消失，开始 {(int)MonitorAbsentGraceSeconds} 秒确认，避免更新/闪退造成误判。");
                    }
                    else if (now - monitorAbsentSince.Value >= MonitorAbsentGraceSeconds)
                    {
                        return FinishWithLog($"{label}已连续消失 {(int)MonitorAbsentGraceSeconds} 秒");
                    }
                }
                else if (exitCode is not null)
                {
                    if (!monitorWaitLogged)
                    {
                        monitorWaitLogged = true;
                        Log($"直接启动进程已结束，仍在等待{label}出现：{string.Join(", ", monitorSpec.Keywords)}。");
                    }

                    if (now >= MonitorDetectDeadlineSeconds)
                    {
                        var keywords = string.Join(", ", monitorSpec.Keywords);
                        var message = $"直接进程已结束，180 秒内未检测到{label}「{keywords}」，按提醒处理：可能是配置不匹配";
                        Log(message);
                        MarkTaskWarning(message, name);
                        return FinishWithLog(message, checkEarlyExit: false);
                    }
                }
            }
            else if (exitCode is not null)
            {
                return FinishWithLog($"正常结束，退出码：{exitCode}");
            }

            var elapsed = (int)now;
            if (elapsed != lastEmitSecond)
            {
                lastEmitSecond = elapsed;
                RaiseElapsed(elapsed);
            }

            if (timeoutSeconds > 0 && elapsed >= timeoutSeconds)
            {
                RaiseStatus("超时处理中");
                var timeoutMessage =
                    $"任务「{name}」已超时，最大运行时间：{task.TimeoutMinutes} 分钟，" +
                    $"超时处理方式：{TimeoutActions.Label(task.TimeoutAction)}。";
                Log(timeoutMessage);

                var screenshotNames = new List<string>();
                if (_enableTimeoutScreenshot)
                {
                    var before = CaptureTimeoutScreenshot(name, "before_kill");
                    if (before is not null)
                    {
                        screenshotNames.Add(Path.GetFileName(before));
                    }
                }

                var timeoutOutcome = HandleTimeout(task, process, name);
                if (task.TimeoutAction != TimeoutActions.SkipAndContinue) { StopExternalLogTail(task); }

                if (_enableTimeoutScreenshot)
                {
                    var after = CaptureTimeoutScreenshot(name, "after_kill");
                    if (after is not null)
                    {
                        screenshotNames.Add(Path.GetFileName(after));
                    }
                }

                if (screenshotNames.Count > 0)
                {
                    timeoutMessage = $"{timeoutMessage} 已保存现场截图：{string.Join(", ", screenshotNames)}";
                }

                MarkTaskError(timeoutMessage, name);
                _stats.AddRecord(name, elapsed, "超时处理", task.WaitMode);
                RaiseTaskCompleted(name, index, total, elapsed, $"超时处理：{TimeoutActions.Label(task.TimeoutAction)}", abnormal: true);
                return timeoutOutcome;
            }

            WaitPoll(250);
        }
    }

    private bool ConfirmLaunchSuccess(
        TaskConfig task,
        IMonitoredProcess process,
        string name,
        Stopwatch stopwatch,
        MonitorSpec monitorSpec)
    {
        while (stopwatch.Elapsed.TotalSeconds < LaunchConfirmSeconds)
        {
            WaitIfPaused();
            if (_stopRequested)
            {
                return false;
            }

            var exitCode = PollTrackedProcess(process);
            if (exitCode is not null)
            {
                var (present, matchedKeyword) = IsMonitorPresent(monitorSpec);
                if (present)
                {
                    Log($"任务「{name}」直接进程已退出，但已检测到{monitorSpec.Label}「{matchedKeyword}」，判定启动成功。");
                    return true;
                }

                // 直接进程秒退 ≠ 启动失败：提权重启（BetterGI 未以管理员运行时会 runas 重启自己并
                // 用退出码 553 退出）、单实例转发都会让原进程立刻退出、由新进程接手。
                // 给它一个短窗口确认是否真的被"接力"起来了。
                if (TryConfirmRelayLaunch(task, name, stopwatch, monitorSpec))
                {
                    return true;
                }

                var message = $"工作出现了问题！！！任务「{name}」运行出现问题或者提前退出了，退出码：{exitCode}。";
                Log(message);
                MarkTaskError(message, name);
                return false;
            }

            WaitPoll(200);
        }

        Log($"任务「{name}」启动确认通过：进程稳定运行超过 {(int)LaunchConfirmSeconds} 秒。");
        return true;
    }

    /// <summary>
    /// 接力判定：直接进程已退出、监控目标还没出现时，等一小会儿看真正的程序有没有被"接力"起来。
    ///
    /// 典型场景：BetterGI 检测到自己不是管理员就用 runas 提权重启，原进程带着退出码 553 秒退，
    /// 提权后的新实例才是我要等的那个（它起来后才会拉起原神窗口，监控目标短期内本就不该出现）。
    /// 单实例程序同理：新实例把参数转给已在运行的实例后就退出。
    ///
    /// 命中条件（任一）：① 监控目标出现；② 与脚本/任务登记同名的进程又在跑。
    /// 命中即判启动成功 —— 后续由主循环继续按监控目标等待完成。
    /// 纯"等待直接进程"模式没有接力可言（退出就是结束），直接返回 false。
    /// </summary>
    private bool TryConfirmRelayLaunch(
        TaskConfig task,
        string name,
        Stopwatch stopwatch,
        MonitorSpec monitorSpec)
    {
        if (monitorSpec.Kind == MonitorKinds.Direct)
        {
            return false;
        }

        var relayNames = ProcessNamesForTask(task, task.ScriptPath);
        if (relayNames.Count == 0)
        {
            var fallback = Path.GetFileNameWithoutExtension(task.ScriptPath.Trim());
            if (fallback.Length > 0)
            {
                relayNames.Add(fallback);
            }
        }

        while (stopwatch.Elapsed.TotalSeconds < LaunchRelayGraceSeconds)
        {
            WaitIfPaused();
            if (_stopRequested)
            {
                return false;
            }

            var (present, matchedKeyword) = IsMonitorPresent(monitorSpec);
            if (present)
            {
                Log($"任务「{name}」直接进程已退出，但已检测到{monitorSpec.Label}「{matchedKeyword}」，判定为提权/单实例接力成功。");
                return true;
            }

            foreach (var candidate in relayNames)
            {
                if (ProcessHelper.IsProcessNameRunning(candidate))
                {
                    Log($"任务「{name}」直接进程已退出，但检测到同名进程「{candidate}」在运行，判定为提权/单实例接力成功。");
                    return true;
                }
            }

            WaitPoll(300);
        }

        return false;
    }

    private RunOutcome HandleTimeout(TaskConfig task, IMonitoredProcess process, string name)
    {
        switch (task.TimeoutAction)
        {
            case TimeoutActions.SkipAndContinue:
                Log($"任务「{name}」超时：按照配置不结束当前脚本，直接继续下一个任务。注意：这可能导致多个脚本同时运行。");
                UntrackProcess(process);
                RaiseStatus("运行中");
                return RunOutcome.Continue;

            case TimeoutActions.StopAll:
                Log($"任务「{name}」超时：正在停止全部任务，并按统一清理顺序收尾当前任务。");
                CleanupProcessesForTasks(new List<TaskConfig> { task }, new List<IMonitoredProcess> { process }, "超时处理", includeWindowKeywords: true);
                UntrackProcess(process);
                return RunOutcome.StopAll;

            default:
                Log($"任务「{name}」超时：正在按启动脚本进程 → 目标进程 → 游戏窗口/扩展进程顺序强制收尾，然后继续下一个任务。");
                CleanupProcessesForTasks(new List<TaskConfig> { task }, new List<IMonitoredProcess> { process }, "超时处理", includeWindowKeywords: true);
                UntrackProcess(process);
                RaiseStatus("运行中");
                return RunOutcome.Continue;
        }
    }

    // ---------------- 进程清理 ----------------

    private void CleanupProcessesForTasks(
        List<TaskConfig> tasks,
        List<IMonitoredProcess>? directProcesses,
        string scopeLabel,
        bool includeWindowKeywords)
    {
        // ⚠️ 必须在任何结束动作**之前**学：父子关系一动手就变了（根一死只剩孤儿记录）
        LearnScriptDescendants(directProcesses);

        foreach (var process in directProcesses ?? new List<IMonitoredProcess>())
        {
            if (process is not null && TerminateTrackedProcess(process))
            {
                Log($"{scopeLabel}：正在终止脚本进程 PID：{process.Pid}");
            }
        }

        // 紧接着收脚本进程派生的后代 —— 它才是"真正干活的引擎"。
        // 留到后面收等于白给了它一个把游戏重新拉起来的时间窗（真机事故就是这么来的）。
        TerminateScriptDescendants(scopeLabel);

        // 第 0 层：本轮监控实际命中的进程。
        // 配置里写的往往是中文窗口标题（「原神」「星穹铁道」），而真实镜像名是英文
        // （YuanShen / StarRail）—— 按名字 taskkill 打不中中文，只能靠窗口标题那一路兜底；
        // 这里用监控时学到的真实镜像名直接杀，不依赖窗口是否还在。
        List<string> learned;
        lock (_sync)
        {
            learned = new List<string>(_learnedImageNames);
        }

        if (learned.Count > 0)
        {
            Log($"{scopeLabel}：正在清理本轮监控到的实际进程：{string.Join(", ", learned)}");
            foreach (var name in learned)
            {
                var result = ProcessHelper.TerminateProcessName(name);
                Log($"{scopeLabel}：结束进程名「{name}」：{result.Summary}");
                if (!result.Ok)
                {
                    Log($"{scopeLabel}：↳ {result.Hint}");
                }
            }

            WaitPoll(350);
        }

        var killedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var task in tasks)
        {
            var scriptPath = task.ScriptPath.Trim().Length > 0
                ? Path.GetFullPath(Environment.ExpandEnvironmentVariables(task.ScriptPath.Trim()))
                : null;

            var taskName = task.DisplayName;

            foreach (var (stage, rawNames) in BuildCleanupPlan(task, scriptPath))
            {
                if (rawNames.Count == 0)
                {
                    continue;
                }

                var stageNames = new List<string>();
                foreach (var rawName in rawNames)
                {
                    foreach (var candidate in ProcessHelper.ProcessNameCandidates(rawName))
                    {
                        AddUnique(stageNames, candidate);
                    }
                }

                if (stageNames.Count == 0)
                {
                    continue;
                }

                Log($"{scopeLabel}：任务「{taskName}」正在清理{stage}：{string.Join(", ", stageNames)}");

                foreach (var rawName in rawNames)
                {
                    var key = rawName.Trim();
                    if (key.Length == 0 || !killedKeys.Add(key))
                    {
                        continue;
                    }

                    TerminateProcessKeyword(key);
                }

                WaitPoll(350);
            }

            if (includeWindowKeywords)
            {
                foreach (var keyword in KeywordHelper.Normalize(task.WindowKeywords))
                {
                    TerminateProcessesByWindowKeyword(keyword, scopeLabel, taskName);
                }
            }
        }

        ReportCleanupResult(tasks, includeWindowKeywords, scopeLabel);
    }

    /// <summary>
    /// 记下"脚本进程派生了哪些子进程"。
    ///
    /// 为什么必须学：像 March7th 这类工具，配置里指向的 `March7th Launcher.exe` **只是个壳**，
    /// 真正干活的是它派生的 `March7th Assistant.exe`。停止时若只按配置里写的名字清理，
    /// 子进程会完全漏网 —— 它随后会自己把游戏重新拉起来。2026-09-27 真机实测：
    /// 22:42:01 打完「✓ 都已结束」，22:42:57 游戏就被它重启，一直跑到 22:49 才收尾，
    /// 用户看到的就是"明明停止了，过一会又自己跑起来"。
    ///
    /// 调用时机：必须在**任何结束动作之前**。根进程一死，进程表里就只剩孤儿记录，
    /// 那时再想按 PPID 补课已经晚了 —— 这也是当初漏网的直接原因。
    ///
    /// 记 PID 而不是名字：脚本常会派生 <c>conhost.exe</c> 这类公共进程，
    /// 按名字清会误伤系统里别的同名实例，按 PID 才精准。
    /// </summary>
    private void LearnScriptDescendants(List<IMonitoredProcess>? directProcesses)
    {
        var rootPids = new List<int>();

        lock (_sync)
        {
            foreach (var pid in _tasksByPid.Keys)
            {
                if (!rootPids.Contains(pid))
                {
                    rootPids.Add(pid);
                }
            }
        }

        foreach (var process in directProcesses ?? new List<IMonitoredProcess>())
        {
            if (process is not null && process.Pid > 0 && !rootPids.Contains(process.Pid))
            {
                rootPids.Add(process.Pid);
            }
        }

        // 【增量爬升】已经记录、而且现在还活着的后代，也当作新的起点。
        //
        // 为什么必须这样：BFS 每轮都从"根"出发，而它只能沿**本次快照里存在的节点**往下走。
        // 中间层（启动器）一旦先退出，它的 PID 就永远进不了队列，孙辈从此访问不到 ——
        // 2026-10-29 终末地实测：`Endfield.exe` ← `Games.exe`(3964) ← `57696`(已退出)，
        // 从 MaaEnd 的 PID 出发一步就断，游戏本体始终取不到。
        // 把"已入册且还活着"的节点补成根，下一轮就能从中断处继续往下爬：
        // 第 1 轮学到启动器 → 第 2 轮从中断处学到 Games → 第 3 轮学到 Endfield。
        List<ProcessSnapshotEntry> knownDescendants;
        lock (_sync)
        {
            knownDescendants = new List<ProcessSnapshotEntry>(_scriptDescendants);
        }

        foreach (var item in knownDescendants)
        {
            if (item.Pid > 0 && !rootPids.Contains(item.Pid)
                && ProcessHelper.IsSameProcessAlive(item.Pid, item.Name))
            {
                rootPids.Add(item.Pid);
            }
        }

        if (rootPids.Count == 0)
        {
            return;
        }

        var snapshot = ProcessHelper.SnapshotProcesses();
        if (snapshot.Count == 0)
        {
            Log("停止清理：读不到系统进程表，无法登记脚本进程的后代（只能退回按名字清理，可能漏掉子进程）。");
            return;
        }

        foreach (var pid in rootPids)
        {
            foreach (var child in ProcessHelper.DescendantsOf(pid, snapshot))
            {
                var known = false;
                lock (_sync)
                {
                    foreach (var item in _scriptDescendants)
                    {
                        if (item.Pid == child.Pid)
                        {
                            known = true;
                            break;
                        }
                    }

                    if (!known)
                    {
                        _scriptDescendants.Add(child);
                    }
                }

                if (known)
                {
                    continue;
                }

                Log($"已记下脚本进程（PID {pid}）派生的子进程：{child.Name}（PID {child.Pid}）—— 停止时按它清理与复查。");
            }
        }
    }

    /// <summary>
    /// 结束本轮登记过的脚本后代（按 PID，**先深后浅**）。
    ///
    /// 每个都必须给回执：这次事故里最难发现的一环正是"上一步报成功、子进程其实还在"，
    /// 所以这里不用任何"没抛异常就算过"的写法，结论一律来自进程表的复查。
    /// </summary>
    /// <summary>
    /// 清理脚本后代时要跳过的"公共宿主"进程。
    ///
    /// <para>【为什么单列一份】它们确实是脚本进程的后代（`conhost.exe` 是控制台宿主、
    /// `msedgewebview2.exe` 是工具自己的 UI 壳），但**杀掉它们没有意义** —— 宿主一退它们自己就走，
    /// 而每一个都要单独花一次 taskkill（固定 ~2 秒）。真机日志里一次停止有 4 个 conhost +
    /// 4 个 msedgewebview2，光这些就白等 16 秒。</para>
    ///
    /// <para>与 <see cref="CandidateProcessBlocklist"/> 的区别：那份是"别推荐给用户填进清理名单"，
    /// 这份是"清理时干脆别碰"。两者有重叠，但目的不同，不合并。</para>
    /// </summary>
    private static readonly HashSet<string> SharedHostProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "conhost.exe", "msedgewebview2.exe", "werfault.exe", "runtimebroker.exe",
        "dllhost.exe", "taskhostw.exe", "sihost.exe", "ctfmon.exe",
        "textinputhost.exe", "applicationframehost.exe", "searchapp.exe",
        "startmenuexperiencehost.exe", "shellexperiencehost.exe",
        "backgroundtaskhost.exe", "wmiprvse.exe", "dwm.exe",
    };

    private void TerminateScriptDescendants(string scopeLabel)
    {
        List<ProcessSnapshotEntry> descendants;
        lock (_sync)
        {
            descendants = new List<ProcessSnapshotEntry>(_scriptDescendants);
        }

        if (descendants.Count == 0)
        {
            return;
        }

        // 倒着收 = 从最深的叶子往根：先断掉"会自己重启游戏"的引擎，壳留到最后。
        // 同时把公共宿主剔出去（它们会随宿主退出，不值得各花 2 秒）。
        var targets = new List<ProcessSnapshotEntry>();
        var skippedHosts = new List<string>();
        for (var i = descendants.Count - 1; i >= 0; i--)
        {
            var item = descendants[i];
            if (!ProcessHelper.IsSameProcessAlive(item.Pid, item.Name))
            {
                continue;
            }

            if (SharedHostProcesses.Contains(item.Name) || SharedHostProcesses.Contains(ProcessHelper.BareImageName(item.Name)))
            {
                AddUnique(skippedHosts, item.Name);
                continue;
            }

            targets.Add(item);
        }

        if (skippedHosts.Count > 0)
        {
            Log($"{scopeLabel}：跳过 {skippedHosts.Count} 个公共宿主进程（{string.Join("、", skippedHosts)}）"
                + " —— 它们会随宿主退出，单独结束每个要白等约 2 秒。");
        }

        if (targets.Count == 0)
        {
            return;
        }

        var nameList = new List<string>();
        foreach (var item in targets)
        {
            AddUnique(nameList, item.Name);
        }

        Log($"{scopeLabel}：正在清理 {targets.Count} 个脚本派生的子进程（叶子优先）：{string.Join("、", nameList)}");

        // ① 先逐个托管结束：毫秒级，绝大多数进程在这一步就没了。
        var refused = new List<int>();
        foreach (var item in targets)
        {
            if (!ProcessHelper.TryKill(item.Pid))
            {
                refused.Add(item.Pid);
            }
        }

        // ② 被拒的合成一次 taskkill：taskkill 的固定开销约 2 秒/次，与杀几个无关，
        //    所以 N 个被拒的进程也只该付一次代价（原先是一个一个来，12 个 = 24 秒）。
        if (refused.Count > 0)
        {
            var batch = ProcessHelper.TerminatePidTreeBatch(refused);
            Log($"{scopeLabel}：{refused.Count} 个进程直接结束被拒，改用一次批量 taskkill：{batch.Summary}");
        }

        // ③ 复查给回执：结论只来自"还在不在"，不看上一步返回了什么。
        foreach (var item in targets)
        {
            if (!ProcessHelper.IsSameProcessAlive(item.Pid, item.Name))
            {
                Log($"{scopeLabel}：已结束「{item.Name}」（PID {item.Pid}）");
                continue;
            }

            var closed = ProcessHelper.CloseMainWindowsByPid(item.Pid);
            Log($"{scopeLabel}：结束「{item.Name}」（PID {item.Pid}）：未能结束"
                + (closed > 0
                    ? $"；已向其 {closed} 个窗口发送关闭请求（走它自己的退出流程）"
                    : "；也没有可发送关闭请求的窗口"));
        }
    }

    /// <summary>
    /// 记下"本轮监控命中的那个窗口属于哪个进程"。
    ///
    /// 为什么必须学：任务配置里写的往往是中文窗口标题（「原神」「星穹铁道」），
    /// 而游戏真实镜像名是英文（<c>YuanShen</c> / <c>StarRail</c>）——
    /// 只用配置里的关键词，会同时踩两个坑：
    ///   ① 杀不掉：<c>taskkill /IM "原神"</c> 打不中英文镜像名；
    ///   ② 复查不出：按中文名字查进程永远是"不在"，明明还在跑也会报"已结束"。
    /// 监控命中的那一刻真实镜像名是可查的，记下来就能绕开窗口标题这条脆弱链路。
    ///
    /// <para>【为什么条件从"监控类型是窗口"改成"有窗口关键词"】原先只在
    /// <c>monitor.kind == window</c> 时学，而 MaaEnd 这类预设的监控类型是 <c>process</c>
    /// （关键词写的是工具名 <c>MaaEnd</c>）—— 于是这条"绕开配置写错"的路一直没启用。
    /// 现在只要任务配了窗口关键词就学，与监控类型无关：学到的名字只进
    /// <c>_learnedImageNames</c>，不改变 <c>BuildMonitorSpec</c> 的任何判定。</para>
    /// </summary>
    private void LearnMonitorImageNames(TaskConfig task)
    {
        var keywords = KeywordHelper.Normalize(task.WindowKeywords);
        if (keywords.Count == 0)
        {
            // 没有窗口关键词就没有反查的入口（靠窗口标题找 PID 必须先有标题片段）
            return;
        }

        foreach (var keyword in keywords)
        {
            foreach (var pid in ProcessHelper.WindowPidsByKeyword(keyword))
            {
                try
                {
                    using var process = Process.GetProcessById(pid);
                    var imageName = process.ProcessName;
                    if (imageName.Length == 0)
                    {
                        continue;
                    }

                    lock (_sync)
                    {
                        if (!_learnedImageNames.Add(imageName))
                        {
                            continue;
                        }
                    }

                    Log($"已记下监控命中的真实进程名：{imageName}（PID {pid}）—— 停止时按它清理与复查，不再依赖窗口标题。");
                }
                catch
                {
                    // 进程已退出或拒绝访问，跳过
                }
            }
        }
    }

    /// <summary>
    /// 清理后复查：把"到底关掉没有"明确写进日志。
    ///
    /// 为什么必要：<c>taskkill</c> 对被内核级反作弊保护的游戏会失败，而失败与成功在旧日志里
    /// 长得一模一样（都只有"正在结束"）。用户只能对着还在跑的游戏猜是没杀掉还是画面没刷新。
    /// 这里用同一套候选名 + 监控学到的真实镜像名 + 窗口关键词再查一遍：
    /// 干净就明确确认，有残留就点名并给出下一步。
    /// </summary>
    private void ReportCleanupResult(List<TaskConfig> tasks, bool includeWindowKeywords, string scopeLabel)
    {
        var leftovers = new List<string>();

        // 本轮"名单里到底有几个名字"。为 0 时不能报 ✓ —— 复查用的是同一份名单，
        // 名单空 → 什么都查不到 → 打出「已确认，相关进程都已结束」，而目标（比如游戏）
        // 明明还在跑。2026-09-27 那次假 ✓ 是"清了一半漏了另一半"，这一次更彻底：名单是空的。
        var planNameCount = 0;
        var gameStageCount = 0;

        List<string> learned;
        lock (_sync)
        {
            learned = new List<string>(_learnedImageNames);
        }

        planNameCount += learned.Count;

        foreach (var name in learned)
        {
            if (ProcessHelper.IsProcessNameRunning(name))
            {
                AddUnique(leftovers, name + "（监控命中的实际进程）");
            }
        }

        // 脚本进程派生的后代：按 PID 复查（按名字查会撞上同名的公共进程，如 conhost.exe）
        List<ProcessSnapshotEntry> descendants;
        lock (_sync)
        {
            descendants = new List<ProcessSnapshotEntry>(_scriptDescendants);
        }

        var descendantLeftovers = new List<string>();
        foreach (var item in descendants)
        {
            if (!ProcessHelper.IsSameProcessAlive(item.Pid, item.Name))
            {
                continue;
            }

            AddUnique(descendantLeftovers, item.ToString());
            AddUnique(leftovers, $"{item}（脚本进程派生的子进程）");
        }

        planNameCount += descendants.Count;

        foreach (var task in tasks)
        {
            var scriptPath = task.ScriptPath.Trim().Length > 0
                ? Path.GetFullPath(Environment.ExpandEnvironmentVariables(task.ScriptPath.Trim()))
                : null;

            foreach (var (stage, rawNames) in BuildCleanupPlan(task, scriptPath))
            {
                if (rawNames.Count > 0)
                {
                    planNameCount += rawNames.Count;
                    if (string.Equals(stage, GameCleanupStage, StringComparison.Ordinal))
                    {
                        gameStageCount += rawNames.Count;
                    }
                }

                foreach (var rawName in rawNames)
                {
                    foreach (var candidate in ProcessHelper.ProcessNameCandidates(rawName))
                    {
                        if (ProcessHelper.IsProcessNameRunning(candidate))
                        {
                            AddUnique(leftovers, $"{candidate}（{stage}）");
                        }
                    }
                }
            }

            if (!includeWindowKeywords)
            {
                continue;
            }

            foreach (var keyword in KeywordHelper.Normalize(task.WindowKeywords))
            {
                var pids = ProcessHelper.WindowPidsByKeyword(keyword);
                if (pids.Count == 0)
                {
                    var stripped = ProcessHelper.TitleMatchKey(keyword);
                    if (stripped.Length > 0
                        && !string.Equals(stripped, keyword.Trim(), StringComparison.OrdinalIgnoreCase))
                    {
                        pids = ProcessHelper.WindowPidsByKeyword(stripped);
                    }
                }

                if (pids.Count > 0)
                {
                    AddUnique(leftovers, $"窗口「{keyword}」仍在（PID {string.Join("/", pids)}）");
                }
            }
        }

        if (leftovers.Count == 0)
        {
            // 名单空 → 这次复查什么也没查（它查的就是这份名单），不能报 ✓。
            if (planNameCount == 0)
            {
                Log($"{scopeLabel}：⚠ 本次清理名单为空（进程关键词、主进程、游戏进程都没填），"
                    + "无法确认目标是否已退出 —— 请到任务编辑页补「游戏进程」，或改用带预设的任务。");
                return;
            }

            Log($"{scopeLabel}：✓ 已复查确认，任务相关的进程都已结束。");

            // 名字有、但没有一个是"游戏本体"：这类名单最容易让人误以为"游戏也会被关掉"。
            if (includeWindowKeywords && gameStageCount == 0)
            {
                Log($"{scopeLabel}：⚠ 名单里没有「游戏/扩展进程」，游戏本体不在本次清理范围内"
                    + "（若任务没配游戏窗口关键词，游戏会继续运行）。");
            }

            return;
        }

        Log($"{scopeLabel}：⚠ 复查发现 {leftovers.Count} 项仍在运行：{string.Join("、", leftovers)}");
        Log($"{scopeLabel}：⚠ 它们多半带反作弊 / 驱动级保护，普通权限结束不了。"
            + "改成在目标会话里以管理员身份运行代理、或手动结束；"
            + "若日志里一直没有「按目标进程关键词兜底结束」这类记录，把真实镜像名（如 YuanShen、StarRail）补进任务的「进程关键词」。");

        // 单独点名"漏网的是脚本自己的子进程"：这是最容易被误判成"已经停止"的一种，
        // 而且它会自己把游戏重新拉回来（2026-09-27 真机事故）。
        if (descendantLeftovers.Count > 0)
        {
            Log($"{scopeLabel}：⚠ 其中「启动脚本派生的子进程」没结束掉：{string.Join("、", descendantLeftovers)}"
                + " —— 它是真正干活的引擎，很可能会自己把游戏重新拉起来，建议在目标会话里手动结束它再确认。");
        }
    }

    private void TerminateProcessesByWindowKeyword(string keyword, string scopeLabel, string taskName)
    {
        var text = keyword.Trim();
        if (text.Length == 0)
        {
            return;
        }

        // 无条件先记一条 —— 这一层跑没跑到必须能一眼看出来。
        // （真机上出现过"配置里有窗口关键词、日志里却一条相关记录都没有"的情况，
        //   当时无法判断是没执行、还是执行了但两条分支都没命中。）
        Log($"{scopeLabel}：任务「{taskName}」按游戏窗口关键词「{text}」查找残留进程…");

        try
        {
            // 窗口标题里不会出现 .exe：关键词写成「原神.exe」时按原样比标题必然落空，
            // 而这条路正是清掉游戏本体（YuanShen / StarRail）的主要手段。剥掉扩展名再比。
            var pids = ProcessHelper.WindowPidsByKeyword(text);
            if (pids.Count == 0)
            {
                var stripped = ProcessHelper.TitleMatchKey(text);
                if (stripped.Length > 0 && !string.Equals(stripped, text.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    pids = ProcessHelper.WindowPidsByKeyword(stripped);
                }
            }

            if (pids.Count == 0)
            {
                Log($"{scopeLabel}：任务「{taskName}」未发现游戏窗口关键词「{text}」对应的残留窗口。");
                return;
            }

            Log($"{scopeLabel}：任务「{taskName}」发现游戏窗口关键词「{text}」对应 PID：{string.Join(", ", pids)}");
            foreach (var pid in pids)
            {
                KillWithFallback(pid, $"窗口「{text}」");
            }
        }
        catch (Exception ex)
        {
            Log($"{scopeLabel}：按游戏窗口关键词「{text}」清理失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 结束一个进程树并把结果写进日志；强杀被拒时退一步"请它自己退出"。
    ///
    /// 为什么要降级：原神 / 星穹铁道这类游戏带内核级反作弊驱动，普通权限的
    /// <c>taskkill /F</c> 会直接被拒 —— 而失败是**静默**的，旧日志里只留下"正在结束"，
    /// 用户看着还在跑的游戏根本判断不出到底关了没有（真机踩过）。
    /// 强杀被拒后给窗口发 WM_CLOSE：那是应用层消息，反作弊不拦，走游戏自己的退出流程
    /// （有些游戏会弹一个确认退出框，需要在目标会话里点一下）。
    /// </summary>
    private void KillWithFallback(int pid, string label)
    {
        var result = ProcessHelper.TerminateProcessTree(pid);
        Log($"  ↳ {label}：{result.Summary}");

        if (result.Ok)
        {
            return;
        }

        var closed = ProcessHelper.CloseMainWindowsByPid(pid);
        if (closed > 0)
        {
            Log($"  ↳ 强制结束被拒，已向 {label} 的 {closed} 个窗口发送关闭请求（走游戏自己的退出流程，可能需要在目标会话里确认）。");
            return;
        }

        Log($"  ↳ 强制结束被拒，且没有可发送关闭请求的窗口。{result.Hint}");
    }

    /// <summary>关键词里是否含非 ASCII 字符（中文等）：这类词不可能出现在进程镜像名里。</summary>
    private static bool HasNonAscii(string text)
    {
        foreach (var ch in text)
        {
            if (ch > 127)
            {
                return true;
            }
        }

        return false;
    }

    private void TerminateProcessKeyword(string keyword)
    {
        var text = keyword.Trim();
        if (text.Length == 0)
        {
            return;
        }

        // 先把"确实在跑"的候选挑出来再动手：taskkill 哪怕报「没有找到进程」也要花近 2 秒，
        // 而清理名单里绝大多数名字在停止那一刻早就退出了（真机日志里 4 次白等 = 8 秒）。
        var tried = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var running = new List<string>();
        foreach (var candidate in ProcessHelper.ProcessNameCandidates(text))
        {
            if (tried.Add(candidate) && ProcessHelper.IsProcessNameRunning(candidate))
            {
                running.Add(candidate);
            }
        }

        if (running.Count == 0)
        {
            Log($"结束进程名「{text}」：未在运行，跳过");
        }

        foreach (var candidate in running)
        {
            var byName = ProcessHelper.TerminateProcessName(candidate);
            Log($"结束进程名「{candidate}」：{byName.Summary}");
        }

        try
        {
            // 关键词要剥掉扩展名才能匹配镜像名（理由见 ProcessHelper.TitleMatchKey）。
            // 这一路兜底是把游戏本体（YuanShen / StarRail）清掉的手段 —— 按名字 taskkill
            // 用的是中文关键词，注定打不中英文镜像名。
            var lower = ProcessHelper.TitleMatchKey(text);
            if (lower.Length == 0)
            {
                return;
            }

            var currentPid = Environment.ProcessId;
            var matched = 0;
            foreach (var proc in Process.GetProcesses())
            {
                try
                {
                    if (proc.Id == currentPid)
                    {
                        continue;
                    }

                    var procName = proc.ProcessName;

                    // 【只按镜像名匹配，不再看窗口标题】2026-10-30 真机：关键词「星穹铁道」
                    // 命中的其实是 **StarRail 的窗口标题**（进程名里没有中文）。保留标题匹配的代价是
                    // "任何标题含这几个字的进程都会被当成目标杀掉" —— 当时若开着浏览器搜攻略、
                    // 或挂着直播页标题带游戏名，Edge 会被一起关掉。
                    // 按窗口标题清理有专门的一条路（window_keywords → TerminateProcessesByWindowKeyword，
                    // 先反查 PID 再杀树），职责分清楚，这里不再兼任。
                    if (!procName.ToLowerInvariant().Contains(lower))
                    {
                        continue;
                    }

                    matched++;
                    Log($"按目标进程关键词兜底结束：{text} -> PID {proc.Id} ({procName})");
                    KillWithFallback(proc.Id, procName);
                }
                catch
                {
                    // 进程已退出或拒绝访问
                }
            }

            // 纯中文关键词在这里注定一个都匹配不到（中文不会出现在镜像名里）。
            // 必须明确说出来 —— 否则用户只看到「未在运行」，会以为程序根本没认真清。
            if (matched == 0 && HasNonAscii(lower))
            {
                Log($"按目标进程关键词「{text}」没有匹配到任何进程镜像名（中文不会出现在 exe 名里）；"
                    + "要按窗口标题清理，请把它填进「游戏窗口关键词」。");
            }
        }
        catch (Exception ex)
        {
            Log($"按目标进程关键词「{text}」兜底清理失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 统一清理顺序：
    /// 1. 启动进程（脚本 exe + 任务名 + launcher_process）
    /// 2. 目标进程（wait_process_name + main_process + process_keywords）
    /// 3. 游戏/扩展进程（game_process + window_keywords 中的 .exe）
    ///
    /// 清理与复查**共用这一份计划** —— 只清不查会漏，只查不清会误报，
    /// 两边用的名字必须来自同一个函数（2026-09-27 假 ✓ 事故的教训）。
    /// </summary>
    /// <summary>清理计划里「游戏/扩展进程」这一层的阶段名（清理、复查、告警三处共用一份）。</summary>
    private const string GameCleanupStage = "游戏/扩展进程";

    private static List<(string Stage, List<string> Names)> BuildCleanupPlan(TaskConfig task, string? scriptPath)
    {
        var launcherNames = new List<string>();
        var mainNames = new List<string>();
        var gameNames = new List<string>();

        if (scriptPath is not null && Path.GetExtension(scriptPath).Equals(".exe", StringComparison.OrdinalIgnoreCase))
        {
            AddUnique(launcherNames, Path.GetFileName(scriptPath));
        }

        // 任务名本身也纳入「启动脚本进程」。
        // 为什么必须加：漏网事故里活下来、并把游戏重新拉起来的那个进程
        // （`March7th Assistant.exe`），名字与**任务名**一致，却既不是 script_path 的文件名、
        // 也不在 wait_process_name 里 —— 于是清理和复查两边都看不见它，复查才会打出假的 ✓。
        if (ProcessHelper.LooksLikeImageName(task.Name))
        {
            AddUnique(launcherNames, task.Name.Trim());
        }

        // 三栏都按分号展开：一个游戏往往要连同平台进程 / Unity 崩溃处理器一起收尾，
        // 只写一个名字会留下"游戏关了、崩溃处理器还占着文件"的尾巴。
        // 单值配置展开后仍是原来那个名字，行为不变。
        foreach (var item in KeywordHelper.Normalize(task.LauncherProcess))
        {
            AddUnique(launcherNames, item);
        }

        AddUnique(mainNames, task.WaitProcessName);
        foreach (var item in KeywordHelper.Normalize(task.MainProcess))
        {
            AddUnique(mainNames, item);
        }

        foreach (var item in KeywordHelper.Normalize(task.ProcessKeywords))
        {
            AddUnique(mainNames, item);
        }

        foreach (var item in KeywordHelper.Normalize(task.GameProcess))
        {
            AddUnique(gameNames, item);
        }
        foreach (var item in KeywordHelper.Normalize(task.WindowKeywords))
        {
            if (item.Trim().EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                AddUnique(gameNames, item);
            }
        }

        var plan = new List<(string, List<string>)>();
        if (launcherNames.Count > 0)
        {
            plan.Add(("启动脚本进程", launcherNames));
        }

        if (mainNames.Count > 0)
        {
            plan.Add(("目标进程", mainNames));
        }

        if (gameNames.Count > 0)
        {
            plan.Add((GameCleanupStage, gameNames));
        }

        return plan;
    }

    private static List<string> ProcessNamesForTask(TaskConfig task, string? scriptPath)
    {
        var names = new List<string>();
        foreach (var (_, items) in BuildCleanupPlan(task, scriptPath))
        {
            foreach (var item in items)
            {
                foreach (var candidate in ProcessHelper.ProcessNameCandidates(item))
                {
                    AddUnique(names, candidate);
                }
            }
        }

        return names;
    }

    // ---------------- 监控目标 ----------------

    public static MonitorSpec BuildMonitorSpec(TaskConfig task)
    {
        var windowKeywords = KeywordHelper.Normalize(task.WindowKeywords);
        var processKeywords = KeywordHelper.Normalize(task.ProcessKeywords);
        var legacyKeyword = (task.WaitProcessName ?? string.Empty).Trim();

        if (windowKeywords.Count > 0)
        {
            return new MonitorSpec
            {
                Kind = MonitorKinds.Window,
                Label = "游戏窗口关键词",
                Keywords = windowKeywords,
                Source = "window_keywords",
            };
        }

        if (processKeywords.Count > 0)
        {
            return new MonitorSpec
            {
                Kind = MonitorKinds.Process,
                Label = "目标进程关键词",
                Keywords = processKeywords,
                Source = "process_keywords",
            };
        }

        if (task.WaitMode == WaitModes.WindowTitle && legacyKeyword.Length > 0)
        {
            return new MonitorSpec
            {
                Kind = MonitorKinds.Window,
                Label = "窗口标题关键词",
                Keywords = new List<string> { legacyKeyword },
                Source = "legacy_wait_mode",
            };
        }

        if (task.WaitMode == WaitModes.CmdlineKeyword && legacyKeyword.Length > 0)
        {
            return new MonitorSpec
            {
                Kind = MonitorKinds.Cmdline,
                Label = "命令行关键词",
                Keywords = new List<string> { legacyKeyword },
                Source = "legacy_wait_mode",
            };
        }

        if (task.WaitMode == WaitModes.ProcessName && legacyKeyword.Length > 0)
        {
            return new MonitorSpec
            {
                Kind = MonitorKinds.Process,
                Label = "目标进程关键词",
                Keywords = new List<string> { legacyKeyword },
                Source = "legacy_wait_mode",
            };
        }

        return new MonitorSpec
        {
            Kind = MonitorKinds.Direct,
            Label = "直接启动进程",
            Keywords = new List<string>(),
            Source = "direct_process",
        };
    }

    private static (bool Present, string Keyword) IsMonitorPresent(MonitorSpec spec)
    {
        foreach (var keyword in spec.Keywords)
        {
            var text = keyword.Trim();
            if (text.Length == 0)
            {
                continue;
            }

            var hit = spec.Kind switch
            {
                MonitorKinds.Window => ProcessHelper.IsWindowTitlePresent(text),
                MonitorKinds.Process => ProcessHelper.IsProcessKeywordPresent(text),
                MonitorKinds.Cmdline => ProcessHelper.IsCommandlineKeywordRunning(text),
                _ => false,
            };

            if (hit)
            {
                return (true, text);
            }
        }

        return (false, string.Empty);
    }

    // ---------------- 辅助 ----------------

    /// <summary>
    /// 轮询用的等待：<b>等满 <paramref name="milliseconds"/>，或收到停止信号时立刻返回</b>。
    ///
    /// 为什么不用 <c>Thread.Sleep</c>：轮询周期是 200～350ms，点「停止」后最坏要等满
    /// 一整个周期才走到循环顶部的 <c>_stopRequested</c> 检查。而停止路径上每一拍都很贵 ——
    /// 进程还在跑、进度还在涨，用户看到的就是"点了停止没反应"。
    /// 用事件等待后，停止信号一到所有轮询线程同拍醒来，CPU 开销与 Sleep 完全相同。
    /// </summary>
    private void WaitPoll(int milliseconds)
    {
        // 已经停了就别等 —— 那样只会把"停止"的响应拖后一整拍
        if (_stopRequested)
        {
            return;
        }

        // 事件等待：CPU 开销与 Thread.Sleep 相同，但停止信号一到立刻返回
        _stopSignal.Wait(milliseconds);
    }

    private void WaitIfPaused()
    {
        while (_pauseRequested && !_stopRequested)
        {
            WaitPoll(250);
        }
    }

    /// <summary>Wait for capacity before beginning a task, without making a stop request wait for a slot.</summary>
    private bool WaitForTaskThreadSlot()
    {
        while (!_stopRequested)
        {
            if (s_taskThreadSlots.Wait(250))
            {
                // Wait 成功后可能立刻收到停止请求；此时归还已取得的槽位。
                if (_stopRequested)
                {
                    s_taskThreadSlots.Release();
                    return false;
                }

                return true;
            }

            WaitIfPaused();
        }

        return false;
    }

    private void TrackProcess(IMonitoredProcess process, List<string> processNames, TaskConfig task)
    {
        lock (_sync)
        {
            _currentProcesses.Add(process);
            if (processNames.Count > 0)
            {
                _processNamesByPid[process.Pid] = new List<string>(processNames);
            }

            _tasksByPid[process.Pid] = task;
        }
    }

    private void UntrackProcess(IMonitoredProcess process)
    {
        lock (_sync)
        {
            if (!_currentProcesses.Remove(process))
            {
                return;
            }

            _processNamesByPid.Remove(process.Pid);
            _tasksByPid.Remove(process.Pid);
            EnqueueProcessForDisposeLocked(process);
        }

        DrainProcessDisposalQueue();
    }

    /// <summary>在登记锁内读取进程状态，避免停止线程同时终止或释放同一进程句柄。</summary>
    private int? PollTrackedProcess(IMonitoredProcess process)
    {
        lock (_sync)
        {
            // 清理线程可能还拿着摘除前的快照；已摘除的进程不再触碰其句柄。
            return _currentProcesses.Contains(process) ? process.Poll() : null;
        }
    }

    /// <summary>在登记锁内确认仍受跟踪后终止，和轮询、释放句柄互斥。</summary>
    private bool TerminateTrackedProcess(IMonitoredProcess process)
    {
        lock (_sync)
        {
            if (!_currentProcesses.Contains(process) || process.Poll() is not null)
            {
                return false;
            }

            process.Terminate();
            return true;
        }
    }

    private void EnqueueProcessForDisposeLocked(IMonitoredProcess process)
    {
        if (process is IDisposable)
        {
            _processDisposalQueue.Enqueue(process);
        }
    }

    /// <summary>锁外释放已摘除的句柄；绝不在这里终止或 Kill 进程。</summary>
    private void DrainProcessDisposalQueue()
    {
        while (true)
        {
            IMonitoredProcess? process;
            lock (_sync)
            {
                process = _processDisposalQueue.Count > 0 ? _processDisposalQueue.Dequeue() : null;
            }

            if (process is null)
            {
                return;
            }

            try
            {
                ((IDisposable)process).Dispose();
            }
            catch (Exception ex)
            {
                Log($"释放脚本进程 PID：{process.Pid} 的句柄失败：{ex.Message}");
            }
        }
    }

    /// <summary>运行结束且所有任务 worker 退出后，释放尚未由任务路径摘除的句柄。</summary>
    private void QueueAllTrackedProcessesForDisposeIfFinished(bool markRunFinished = false)
    {
        lock (_sync)
        {
            if (markRunFinished)
            {
                _runFinished = true;
            }

            if (!_runFinished || Volatile.Read(ref _liveTaskWorkers) != 0)
            {
                return;
            }

            foreach (var process in _currentProcesses)
            {
                EnqueueProcessForDisposeLocked(process);
            }

            _currentProcesses.Clear();
            _processNamesByPid.Clear();
            _tasksByPid.Clear();
        }

        DrainProcessDisposalQueue();
    }

    /// <summary>
    /// 停止时的统一收尾兜底 —— 保证"停止请求到达的那一刻，无论在哪个阶段，目标进程都会被清掉"。
    ///
    /// 为什么必须有它：把整轮关键词清理从 <see cref="RequestStop"/> 里挪走之后（那里同步跑十几秒
    /// 会把工作线程堵死，进度与心跳全停），清理就只剩 RunOneTask 主循环里那一条路径。
    /// 而「启动确认 / 接力确认阶段被停止打断」走的是另一条分支 —— 那里只放弃等待、不做清理，
    /// 于是目标进程（包括提权 / 单实例接力起来的新实例）会活下来。
    ///
    /// 范围按**已登记过的任务**定（<c>_tasksByPid</c>）：只覆盖真的启动过的那批，
    /// 免得顺手把用户另外开着、只是关键词撞上的程序也杀掉。紧急停止才扩大到全部任务配置。
    ///
    /// 幂等：正常路径上 RunOneTask 已经清过、并把进程解除登记了，这里会拿到空集合直接返回，
    /// 不会白跑一遍十几秒的清理。
    /// </summary>
    private void CleanupForStop()
    {
        List<IMonitoredProcess> processes;
        List<TaskConfig> tasks;
        lock (_sync)
        {
            processes = new List<IMonitoredProcess>(_currentProcesses);
            // 范围取全部任务配置：停止只有一个档位，界面上没有"更彻底的那个"可点第二次，
            // 所以这里必须一次清到位（含「启动后不等待」那类已脱离本轮的进程）。
            tasks = new List<TaskConfig>(_tasks);
        }

        if (processes.Count == 0 && tasks.Count == 0)
        {
            return;
        }

        Log($"停止执行：统一收尾 —— 复查 {tasks.Count} 个任务配置、{processes.Count} 个登记进程。");
        CleanupProcessesForTasks(tasks, processes, "停止执行", includeWindowKeywords: true);
    }

    private void SchedulePressEnter(int delaySeconds, string taskName)
    {
        _ = SchedulePressEnterAsync(delaySeconds, taskName);
    }

    private async Task SchedulePressEnterAsync(int delaySeconds, string taskName)
    {
        try
        {
            var remainingMilliseconds = (long)Math.Max(0, delaySeconds) * 1000;
            while (remainingMilliseconds > 0)
            {
                var waitMilliseconds = (int)Math.Min(remainingMilliseconds, int.MaxValue);
                await Task.Delay(waitMilliseconds, _stopCancellation.Token).ConfigureAwait(false);
                remainingMilliseconds -= waitMilliseconds;
            }

            if (_stopRequested)
            {
                return;
            }

            try
            {
                ProcessHelper.PressEnter();
                Log($"任务「{taskName}」已按配置发送一次 Enter 确认键。");
            }
            catch (Exception ex)
            {
                Log($"任务「{taskName}」发送 Enter 失败：{ex.Message}");
            }
        }
        catch (OperationCanceledException) when (_stopCancellation.IsCancellationRequested)
        {
            // 延迟 Enter 到期前队列已停止。
        }
    }

    private string? CaptureTimeoutScreenshot(string taskName, string stage)
    {
        var directory = Path.Combine(_logger.LogDir, "screenshots");
        var safeName = Regex.Replace(taskName, @"[^0-9A-Za-z_\-一-鿿]+", "_").Trim('_');
        if (safeName.Length == 0)
        {
            safeName = "task";
        }

        var safeStage = Regex.Replace(stage, @"[^0-9A-Za-z_\-]+", "_").Trim('_');
        var fileName =
            $"{_logger.SessionLabel}_{DateTime.Now:HHmmss}_{safeName}_timeout_{safeStage}.png";

        var path = ScreenshotService.CaptureFullScreen(directory, fileName);
        if (path is null)
        {
            Log("超时截图失败：未能完成全屏抓图。");
            return null;
        }

        Log($"超时现场截图已保存：{path}");
        return path;
    }

    private void MarkTaskError(string message, string taskName = "")
    {
        RecordTaskIssue(message, taskName, severity: "error", notifyError: true);
    }

    private void MarkTaskWarning(string message, string taskName = "")
    {
        RecordTaskIssue(message, taskName, severity: "warning", notifyError: false);
    }

    private void RecordTaskIssue(string message, string taskName, string severity, bool notifyError)
    {
        if (severity == "error")
        {
            HadTaskError = true;
            TaskErrorCount++;
        }

        _abnormalEvents.Add(new AbnormalEvent
        {
            Time = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            TaskName = taskName.Length > 0 ? taskName : GuessTaskNameFromMessage(message),
            Message = message,
            Severity = severity,
            LogFile = Path.GetFileName(_logger.LogPath),
            LogSession = _logger.SessionLabel,
        });

        if (notifyError)
        {
            InvokeSafely(TaskError, message, nameof(TaskError));
        }
    }

    private static string GuessTaskNameFromMessage(string message)
    {
        const string marker = "任务「";
        var index = message.IndexOf(marker, StringComparison.Ordinal);
        if (index < 0)
        {
            return string.Empty;
        }

        var rest = message.Substring(index + marker.Length);
        var end = rest.IndexOf('」');
        return end < 0 ? string.Empty : rest.Substring(0, end);
    }

    private void SaveAbnormalReport()
    {
        try
        {
            var directory = Path.GetDirectoryName(AbnormalReportPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            if (_abnormalEvents.Count == 0)
            {
                if (File.Exists(AbnormalReportPath))
                {
                    File.Delete(AbnormalReportPath);
                }

                return;
            }

            var errorCount = 0;
            var warningCount = 0;
            foreach (var item in _abnormalEvents)
            {
                if (item.Severity == "warning")
                {
                    warningCount++;
                }
                else
                {
                    errorCount++;
                }
            }

            var report = new AbnormalReport
            {
                CreatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                LogFile = Path.GetFileName(_logger.LogPath),
                LogSession = _logger.SessionLabel,
                Summary = $"上次运行发现 {errorCount} 个异常项，{warningCount} 个提醒项。",
                Events = new List<AbnormalEvent>(_abnormalEvents),
            };

            var options = new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            };

            File.WriteAllText(AbnormalReportPath, JsonSerializer.Serialize(report, options), new UTF8Encoding(false));
        }
        catch (Exception ex)
        {
            Log($"保存异常报告失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 保存本次运行的耗时统计。
    ///
    /// 统计只是"锦上添花"：写失败（多账户共用副本时目录权限不够、文件名撞车都可能）
    /// 绝不该把整轮任务判成异常 —— 所以这里吞掉异常，只留一条日志。
    /// </summary>
    private void SaveStatsQuietly(string label)
    {
        try
        {
            var paths = _stats.SaveSession();
            if (paths.Count > 0)
            {
                Log($"{label}耗时统计已保存：{paths[0]}");
            }
        }
        catch (Exception ex)
        {
            Log($"{label}耗时统计保存失败（不影响任务结果）：{ex.Message}");
        }
    }

    private void Log(string message)
    {
        var line = _logger.Log(message);
        InvokeSafely(Logged, line, nameof(Logged));
    }

    private void RaiseStatus(string status) => InvokeSafely(StatusChanged, status, nameof(StatusChanged));

    private void RaiseProgress(int current, int total) => InvokeSafely(ProgressChanged, (current, total), nameof(ProgressChanged));

    private void RaiseElapsed(int seconds) => InvokeSafely(ElapsedChanged, seconds, nameof(ElapsedChanged));

    private void RaiseTaskStarted(string name, int index, int total) => InvokeSafely(TaskStarted, (name, index, total), nameof(TaskStarted));

    // 注意：并发组下本回调会在工作线程上触发，订阅方需自行切回 UI 线程。
    private void RaiseTaskCompleted(string name, int index, int total, int elapsedSeconds, string status, bool abnormal)
    {
        InvokeSafely(TaskCompleted, new TaskCompletedInfo
        {
            Name = name,
            Index = index,
            Total = total,
            ElapsedSeconds = elapsedSeconds,
            Status = status,
            IsAbnormal = abnormal,
            FinishedAt = DateTimeOffset.Now,
        }, nameof(TaskCompleted));
    }

    private void RaiseFinished(bool stoppedOrError)
    {
        StopAllExternalLogTails();
        InvokeSafely(Finished, stoppedOrError, nameof(Finished));
    }

    /// <summary>隔离订阅者异常，避免后台任务线程因事件回调抛出而退出进程。</summary>
    private void InvokeSafely<T>(EventHandler<T>? handler, T args, string eventName)
    {
        if (handler is null) { return; }

        foreach (EventHandler<T> subscriber in handler.GetInvocationList())
        {
            try
            {
                subscriber(this, args);
            }
            catch (Exception ex)
            {
                try { _logger.Log($"事件「{eventName}」订阅者异常：{ex.Message}"); }
                catch { /* 日志设施异常也不能逃出后台 worker。 */ }
            }
        }
    }

    private static void AddUnique(List<string> target, string? value)
    {
        var text = (value ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            return;
        }

        foreach (var item in target)
        {
            if (string.Equals(item, text, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }

        target.Add(text);
    }

    private enum RunOutcome
    {
        Continue,
        StopRequested,
        StopAll,
    }
}
