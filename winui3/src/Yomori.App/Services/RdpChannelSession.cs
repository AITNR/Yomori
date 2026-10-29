// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Yomori.Helpers;
using Yomori.Models;

namespace Yomori.Services;

/// <summary>
/// 一次轮询给通道带来的变化。Session 本身只改状态、不产生副作用，
/// 日志 / 通知 / 收尾这些动作由主控端（MainViewModel）按本结果执行 ——
/// 这样整条判定链能脱离 WinUI 单测（见 _smoke/RdpChannelCheck.cs）。
/// </summary>
public sealed class RdpChannelTick
{
    /// <summary>本次读到的状态快照（收尾文案要用它取 Finished 事件的 Message）。</summary>
    public RdpStatus? Status { get; set; }

    /// <summary>本拍新出现的事件（按序号去重后）。</summary>
    public List<RdpTaskEvent> NewEvents { get; } = new();

    /// <summary>本拍收到了本指令的 Finished 事件。</summary>
    public bool SawFinished { get; set; }

    /// <summary>Finished 事件带异常标记（"有异常"只能靠它区分，Phase 是恒等的）。</summary>
    public bool FinishedAbnormal { get; set; }

    /// <summary>桥里的状态不属于本指令 —— 「等待代理接管」的过渡态，绝不报失联。</summary>
    public bool AwaitingAgent { get; set; }

    /// <summary>本拍是第一次进入「等待接管」（用于只记一条中性日志）。</summary>
    public bool AwaitingAgentFirstTime { get; set; }

    /// <summary>本拍心跳从失联中恢复（用于补一条「已恢复」日志）。</summary>
    public bool HeartbeatRecovered { get; set; }

    /// <summary>本拍刚判定为失联（只报一次，避免每秒刷屏）。</summary>
    public bool JustWentStale { get; set; }

    /// <summary>
    /// 完成信号：两条路任一即可 ——
    ///   ① 收到本指令的 Finished 事件（持久化在 Events 里、跨 idle 心跳保留，最可靠）；
    ///   ② 恰好轮询到终态相位（注销收尾时代理被连带杀掉、done 留盘，这条会命中）。
    /// 都不依赖"恰好轮询到终态相位"这个时序前提。
    ///
    /// <para>**归属不匹配时一律不算完成** —— 桥里躺着的可能是上一轮指令写的 done，
    /// 若直接采信它的相位，新指令会在下发瞬间被判"已完成"（收尾、停轮询、刷统计全被误触发）。</para>
    /// </summary>
    public bool CompletionSignaled =>
        !AwaitingAgent && (SawFinished || Status?.Phase is "done" or "stopped" or "error");

    /// <summary>本次是否异常收尾（同样只在归属匹配时才作数）。</summary>
    public bool Abnormal => !AwaitingAgent && (FinishedAbnormal || Status?.Phase == "error");

}

/// <summary>
/// 一个「执行通道」的运行时状态 —— 也就是原本散在 MainViewModel 里的那一整套单通道字段
/// （进度 / 相位 / 心跳 / 事件 / 指令归属 / 收尾幂等标志）。
///
/// 为什么要有它：多通道并行意味着这些状态必须**每通道一份**。留在 VM 里就只能是全局单例，
/// 两条通道会互相覆盖彼此的 commandId 与事件序号，完成判定也会串台。
///
/// 纯逻辑（只依赖 BCL + Models + RdpBridge），不碰 WinUI —— 内嵌画面（RdpEmbeddedClient）
/// 仍由 VM 按通道持有，那个依赖原生 DLL，不能进冒烟工程。
/// </summary>
public sealed class RdpChannelSession
{
    /// <summary>界面事件列表保留条数的默认值与范围，统一取自 <see cref="ExternalLogDefaults"/>。</summary>
    public const int MaxEventLines = ExternalLogDefaults.EventLineLimit;
    public const int MaxExternalLogLines = ExternalLogDefaults.EventLineLimit;

    /// <summary>
    /// 停止请求没收到回执时的重发间隔。
    /// 代理每 500ms 查一次 stop.json，正常半秒内就该回执；
    /// 5 秒足够覆盖"代理刚好卡在清理里"这类抖动，又不至于让用户等太久。
    /// </summary>
    public static readonly TimeSpan StopResendInterval = TimeSpan.FromSeconds(5);

    /// <summary>停止请求最多下发几次（含首次）。超了就认定目标代理不认识 stop.json。</summary>
    public const int StopResendMaxSends = 5;

    private readonly RdpBridge _bridge;
    private long _lastEventSeq;
    private long _liveLogByteOffset;
    private long _lastLiveLogSeq;
    private string _liveLogCommandId = string.Empty;
    private int _eventLineLimit = MaxEventLines;
    private readonly int _externalLogLineLimit = MaxExternalLogLines;
    private bool _staleReported;
    private bool _awaitingLogged;
    private bool _completionReported;

    /// <param name="channel">
    /// 通道配置快照；本地执行（未分配通道）传 null —— 那种情况下本对象只用于承载
    /// 界面状态，不与任何桥交互。
    /// </param>
    /// <param name="bridge">该通道的指令桥。</param>
    public RdpChannelSession(RdpChannel? channel, RdpBridge bridge)
    {
        Channel = channel;
        _bridge = bridge;
    }

    // ---------------- 身份 ----------------

    public RdpChannel? Channel { get; }

    public string ChannelId => Channel?.Id ?? string.Empty;

    /// <summary>菜单 / 日志里用的名字。</summary>
    public string DisplayName => Channel?.DisplayName ?? "本地执行";

    public string TargetHost => Channel?.Host ?? string.Empty;

    public string TargetUser => Channel?.User ?? string.Empty;

    public RdpBridge Bridge => _bridge;

    /// <summary>是否是与远程会话打交道（本地执行组为 false）。</summary>
    public bool IsRemote => Channel is not null;

    // ---------------- 运行时状态 ----------------

    /// <summary>当前生效的事件列表保留条数（默认 <c>MaxEventLines</c>，可在设置页改）。</summary>
    public int EventLineLimit => _eventLineLimit;

    /// <summary>
    /// 改事件列表保留条数 —— 超了就把最老的先顶掉，保证列表不会超限。
    /// </summary>
    public void SetEventLineLimit(int value)
    {
        _eventLineLimit = ExternalLogDefaults.NormalizeEventLineLimit(value);
        while (Events.Count > _eventLineLimit)
        {
            Events.RemoveAt(0);
        }

    }

    public ObservableCollection<RdpTaskEvent> Events { get; } = new();

    /// <summary>独立的实时外部日志窗口，不与普通运行事件共享容量或序号。</summary>
    public ObservableCollection<RdpLiveLog> ExternalLogs { get; } = new();
    public string LiveLogStatusText { get; private set; } = "等待本轮日志。";

    /// <summary>当前下发指令的 Id —— 用于校验桥里的 status.json 是不是本指令写的。</summary>
    public string? CommandId { get; private set; }

    /// <summary>下发当前指令的时刻 —— 启动宽限期从这里起算。</summary>
    public DateTimeOffset IssuedAt { get; private set; }

    public int Total { get; private set; }

    public string Phase { get; private set; } = "idle";

    public string StatusText { get; private set; } = "未连接";

    public string CurrentTask { get; private set; } = "－";

    public int Progress { get; private set; }

    public int ElapsedSeconds { get; private set; }

    /// <summary>
    /// 最近一次从代理读到的「本次运行新出现的进程」候选。
    ///
    /// <para>为什么存在 Session 上：代理每次 Update 都会带上它（见 RdpAgentRunner.Update），
    /// 而界面是在"任务页"里读的，没法自己去翻桥文件。存在这里等于把它跟着其余状态一起缓存。</para>
    /// </summary>
    public List<RdpCandidateProcess> LastCandidates { get; private set; } = new();

    /// <summary>心跳描述，例如「3 秒前」「已失联 8 分 12 秒」。</summary>
    public string HeartbeatText { get; private set; } = "－";

    /// <summary>代理是否已停止回传心跳（会话断开 / 被注销时的典型表现）。</summary>
    public bool IsStale { get; private set; }

    /// <summary>最后一次有效心跳的时间点（失联告警里报"最后心跳"用）。</summary>
    public DateTimeOffset? LastHeartbeatAt { get; private set; }

    public bool IsActive => Phase is "running";

    public bool HasCommand => !string.IsNullOrEmpty(CommandId);

    /// <summary>"跑完了"这件事是否已经收尾（保证一轮指令只收尾一次）。</summary>
    public bool CompletionReported => _completionReported;

    // ---------------- 停止请求的投递状态 ----------------

    /// <summary>主控端已经下发过停止请求、且还没到收尾那一刻。</summary>
    public bool StopPending { get; private set; }

    /// <summary>下发的是紧急停止（true）还是常规停止（false）。</summary>
    public bool StopEmergency { get; private set; }

    /// <summary>最近一次下发停止请求的时刻（重发计时的起点）。</summary>
    public DateTimeOffset? StopRequestedAt { get; private set; }

    /// <summary>已经下发了几次（含首次）。</summary>
    public int StopSendCount { get; private set; }

    /// <summary>目标会话的代理已经回执"收到停止请求"。</summary>
    public bool StopAcked { get; private set; }

    /// <summary>重发到上限仍无回执 —— 目标代理多半是旧版本，不再白费力气。</summary>
    public bool StopGiveUp { get; private set; }

    /// <summary>
    /// 本轮主控端下发过停止请求（不论代理是否回执）。
    ///
    /// <para>收尾时用它把"用户点停止"和"真的出故障"分开：代理对停止请求同样以
    /// <c>abnormal=true</c> 收尾（旧副本甚至直接写「已完成（有异常）」），
    /// 不区分开就会把用户自己的操作报成异常。</para>
    /// </summary>
    public bool StopRequested => StopPending || StopAcked;

    public string ElapsedText => FormatHelper.FormatSeconds(ElapsedSeconds);

    public string TaskDisplay => string.IsNullOrEmpty(CurrentTask) ? "－" : CurrentTask;

    // ---------------- 生命周期 ----------------

    /// <summary>
    /// 下发指令时复位全部状态并记下归属与下发时刻。
    /// 事件序号归零 + 桥里的 status 已被 <see cref="RdpBridge.ResetStatus"/> 换成全新一份，
    /// 所以上一轮的 Finished 不会给本轮制造假完成。
    /// </summary>
    public void Begin(string commandId, int total, DateTimeOffset now)
    {
        CommandId = commandId;
        IssuedAt = now;
        Total = total;
        Progress = 0;
        ElapsedSeconds = 0;
        CurrentTask = "－";
        Phase = "idle";
        StatusText = "正在连接目标会话…";
        HeartbeatText = "－";
        IsStale = false;
        LastHeartbeatAt = null;
        Events.Clear();
        ExternalLogs.Clear();
        LiveLogStatusText = "等待代理接管与日志输出。";

        _lastEventSeq = 0;
        ResetLiveLogCursor(commandId);
        _staleReported = false;
        _awaitingLogged = false;
        _completionReported = false;
        ResetStopTracking();
    }

    /// <summary>
    /// 只复位界面状态、不记归属（用于本地执行组 —— 它没有桥，也不需要完成判定）。
    /// </summary>
    public void BeginLocal(int total)
    {
        Total = total;
        Progress = 0;
        ElapsedSeconds = 0;
        CurrentTask = "－";
        Phase = "idle";
        StatusText = "未连接";
        HeartbeatText = "－";
        IsStale = false;
        Events.Clear();
        ExternalLogs.Clear();
        _lastEventSeq = 0;
        ResetLiveLogCursor($"local-{Guid.NewGuid():N}");
        _staleReported = false;
        _awaitingLogged = false;
        _completionReported = false;
        CommandId = null;
        ResetStopTracking();
    }

    /// <summary>清掉"停止请求投递状态"（新一轮指令、或本轮已经收尾时调用）。</summary>
    private void ResetStopTracking()
    {
        StopPending = false;
        StopEmergency = false;
        StopRequestedAt = null;
        StopSendCount = 0;
        StopAcked = false;
        StopGiveUp = false;
    }

    /// <summary>
    /// 本地执行组的进度入口。
    ///
    /// 本地没有桥，进度来自主控端 <c>ScriptRunner</c> 的回调。之所以要把它记进这同一套字段，
    /// 是为了让**状态面板 / 主页通道概览 / 整轮汇总**只有一个数据源 ——
    /// 否则本地这块在面板上永远是初始值（"跑起来了但面板没反应"，已实际发生过）。
    /// 只传需要改的项，null 表示保持原值。
    /// </summary>
    public void UpdateLocal(
        string? phase = null,
        string? statusText = null,
        int? progress = null,
        int? total = null,
        string? currentTask = null,
        int? elapsedSeconds = null)
    {
        if (phase is not null)
        {
            Phase = phase;
        }

        if (statusText is not null)
        {
            StatusText = statusText;
        }

        if (progress is not null)
        {
            Progress = progress.Value;
        }

        if (total is not null)
        {
            Total = total.Value;
        }

        if (currentTask is not null)
        {
            CurrentTask = currentTask;
        }

        if (elapsedSeconds is not null)
        {
            ElapsedSeconds = elapsedSeconds.Value;
        }
    }

    /// <summary>
    /// 本地执行路径下直接补一条独立实时日志。
    ///
    /// 为什么单独开一个入口：远程通道的事件是从 status.json 里按 Seq 去重收进来的，
    /// 本地没有桥，只能由 VM 拿到行之后就地补一条；序号仍按本次运行批次自增。
    /// </summary>
    public void AddExternalLogLine(string text, bool abnormal, string level = "info", string? taskName = null)
    {
        AddLiveLog(new RdpLiveLog
        {
            CommandId = _liveLogCommandId,
            Seq = _lastLiveLogSeq + 1,
            Time = DateTime.Now.ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture),
            TaskName = taskName ?? CurrentTask ?? string.Empty,
            Message = text,
            Level = level,
            IsAbnormal = abnormal,
        });
    }

    private bool AddLiveLog(RdpLiveLog log)
    {
        if (!string.Equals(log.CommandId, _liveLogCommandId, StringComparison.Ordinal))
        {
            return false;
        }

        if (log.Seq > 0 && log.Seq <= _lastLiveLogSeq)
        {
            return false;
        }

        if (log.Seq > 0)
        {
            _lastLiveLogSeq = log.Seq;
        }

        ExternalLogs.Add(log);
        while (ExternalLogs.Count > _externalLogLineLimit)
        {
            ExternalLogs.RemoveAt(0);
        }

        return true;
    }

    private void ResetLiveLogCursor(string commandId)
    {
        _liveLogCommandId = commandId ?? string.Empty;
        _liveLogByteOffset = 0;
        _lastLiveLogSeq = 0;
    }

    /// <summary>
    /// 已成功读取的完整 NDJSON 行边界，仅由 UI 线程提交。
    /// </summary>
    public long LiveLogByteOffset => _liveLogByteOffset;

    public void ApplyLiveLogs(string commandId, long byteOffset, IEnumerable<RdpLiveLog> logs)
    {
        if (!string.Equals(commandId, _liveLogCommandId, StringComparison.Ordinal)) return;
        _liveLogByteOffset = byteOffset;
        // Only the visible tail enters ObservableCollection; the complete stream stays on disk.
        foreach (var log in logs.TakeLast(_externalLogLineLimit)) AddLiveLog(log);
    }

    public List<RdpLiveLog> ConsumeLiveLogs()
    {
        var result = new List<RdpLiveLog>();
        var commandId = string.IsNullOrWhiteSpace(CommandId) ? _liveLogCommandId : CommandId;
        if (string.IsNullOrWhiteSpace(commandId))
        {
            return result;
        }

        if (!string.Equals(_liveLogCommandId, commandId, StringComparison.Ordinal))
        {
            ResetLiveLogCursor(commandId);
        }

        if (!_bridge.TryReadLiveLogs(commandId, ref _liveLogByteOffset, out var logs))
        {
            return result;
        }

        foreach (var log in logs)
        {
            if (!string.Equals(log.CommandId, commandId, StringComparison.Ordinal))
            {
                continue;
            }

            if (AddLiveLog(log))
            {
                result.Add(log);
            }
        }

        return result;
    }


    /// <summary>
    /// 收尾完成：置幂等标志并清掉归属标记，
    /// 之后点「停止执行」不会再往桥里写一条无主的 stop.json。
    /// </summary>
    public void MarkCompletionReported()
    {
        _completionReported = true;
        CommandId = null;
        ResetStopTracking();
    }

    // ---------------- 轮询 ----------------

    /// <summary>
    /// 应用一次 status.json 快照。返回本拍的变化，由调用方转成日志 / 通知 / 收尾。
    ///
    /// 判定顺序不可换（三段式，理由见 <see cref="RdpHeartbeat.Evaluate"/>）：
    ///   ① 归属校验 → 不是本指令的状态一律不采信（也不报失联）；
    ///   ② 归属一致才应用进度与事件；
    ///   ③ 最后才按时间戳判失联。
    /// </summary>
    /// <param name="connectTimeoutSeconds">连接超时（秒），用于折算启动宽限期。</param>
    public RdpChannelTick Tick(RdpStatus status, DateTimeOffset now, int connectTimeoutSeconds)
    {
        var tick = new RdpChannelTick { Status = status };

        var grace = RdpHeartbeat.ResolveGraceSeconds(connectTimeoutSeconds);
        var beat = RdpHeartbeat.Evaluate(
            status.UpdatedAt,
            status.Phase,
            now,
            CommandId,
            status.CommandId,
            IssuedAt,
            grace);

        // ① 归属不匹配（含本指令尚未在桥上落地）：过渡态，只给中性提示。
        //    绝不动 Events / Progress —— 否则上一轮指令残留的事件会被当成这一轮的进度收进来。
        if (beat.AwaitingAgent)
        {
            tick.AwaitingAgent = true;
            HeartbeatText = beat.Text;
            IsStale = false;

            if (!_awaitingLogged)
            {
                _awaitingLogged = true;
                tick.AwaitingAgentFirstTime = true;
            }

            if (_staleReported)
            {
                _staleReported = false;
                tick.HeartbeatRecovered = true;
            }

            return tick;
        }

        _awaitingLogged = false;

        // ResetStatus writes the new CommandId before the agent has touched the placeholder.
        // An absent capability in that controller-authored file says nothing about agent support.
        LiveLogStatusText = string.IsNullOrWhiteSpace(status.AgentUser)
            ? "等待代理接管与日志输出。"
            : status.LiveLogProtocol < RdpLiveLogProtocol.Current
                ? "正在运行的代理未声明独立实时日志支持，请核对部署副本并重启目标会话代理。"
                : string.IsNullOrWhiteSpace(status.LiveLogError) ? "正在接收本通道日志。" : status.LiveLogError;
        // ② 归属一致 → 应用进度
        Phase = status.Phase;
        Progress = status.Progress;
        Total = status.Total;
        ElapsedSeconds = status.ElapsedSeconds;
        CurrentTask = string.IsNullOrEmpty(status.CurrentTask) ? "－" : status.CurrentTask;
        StatusText = string.IsNullOrEmpty(status.StatusText) ? status.Phase : status.StatusText;

        // 代理采集的「新出现的进程」候选：旧代理不写这个字段，此时保留上一次的值
        // （空 → 空，本来就没有；非空 → 别因为一次空心跳把它抹掉）。
        if (status.CandidateProcesses is { Count: > 0 })
        {
            LastCandidates = new List<RdpCandidateProcess>(status.CandidateProcesses);
        }

        foreach (var ev in status.Events)
        {
            if (ev.Seq <= _lastEventSeq)
            {
                continue;
            }

            _lastEventSeq = ev.Seq;

            // 兼容旧代理但不再把外部高频日志收进普通事件窗口。
            if (ev.Kind == RdpEventKinds.ExternalLog)
            {
                continue;
            }

            Events.Add(ev);
            while (Events.Count > _eventLineLimit)
            {
                Events.RemoveAt(0);
            }

            tick.NewEvents.Add(ev);

            // 只【记录】、不在这里收尾 —— 收尾统一由调用方在循环外做一次，
            // 否则中途停轮询后同一次调用还会继续走到后面的判定，容易重复触发。
            if (ev.Kind == RdpEventKinds.Finished)
            {
                tick.SawFinished = true;
                tick.FinishedAbnormal |= ev.IsAbnormal;
            }
        }

        // ③ 心跳健康度与「失联 / 恢复」的边沿
        if (!beat.Parsed)
        {
            HeartbeatText = "未知";
            return tick;
        }

        HeartbeatText = beat.Text;

        if (!beat.IsStale)
        {
            IsStale = false;
            LastHeartbeatAt = beat.UpdatedAt;

            if (_staleReported)
            {
                _staleReported = false;
                tick.HeartbeatRecovered = true;
            }

            return tick;
        }

        IsStale = true;
        if (!_staleReported)
        {
            _staleReported = true;
            tick.JustWentStale = true;
        }

        return tick;
    }

    /// <summary>连接阶段直接设定状态文案（进度区显示）。</summary>
    public void SetStatusText(string text) => StatusText = text;

    /// <summary>
    /// 标记失联：状态文案切成告警（进度刻意保留，让用户看到停在哪一步）。
    /// </summary>
    public void MarkStale()
    {
        var last = LastHeartbeatAt ?? IssuedAt;
        StatusText = $"⚠ 代理已失联（最后心跳 {last:HH:mm:ss}）";
    }

    // ---------------- 停止 ----------------

    /// <summary>
    /// 把「停」这个意图写进本通道的 stop.json，由代理每秒检查一次。
    /// 远程任务跑在目标会话的代理进程里，本地 ScriptRunner 管不到它 —— 桥是唯一通道。
    /// </summary>
    public bool WriteStop(bool emergency, out string error)
    {
        error = string.Empty;
        var commandId = CommandId;
        if (string.IsNullOrEmpty(commandId))
        {
            return false;
        }

        if (!_bridge.WriteStop(commandId, emergency))
        {
            error = _bridge.LastError;
            return false;
        }

        return true;
    }

    /// <summary>
    /// 记下一次停止请求的投递（首次由「停止执行」按钮触发，之后由重发逻辑调用）。
    /// 界面文案同步成"等待收尾"，让用户立刻看到点击有了着落。
    /// </summary>
    public void MarkStopping(bool emergency, DateTimeOffset now)
    {
        StopPending = true;
        StopEmergency = emergency;
        StopRequestedAt = now;
        StopSendCount++;
        StatusText = emergency ? "已请求紧急停止…" : "已请求停止…";
    }

    /// <summary>目标会话的代理回执了（相位 stopping / 收到停止的日志事件）。</summary>
    public void MarkStopAcked()
    {
        StopAcked = true;
        StatusText = StopEmergency ? "已请求紧急停止，目标会话正在收尾…" : "已请求停止，目标会话正在收尾…";
    }

    /// <summary>记一次重发。</summary>
    public void MarkStopResent(DateTimeOffset now)
    {
        StopRequestedAt = now;
        StopSendCount++;

        // 到了上限还收不到回执，基本可以断定目标代理不认识 stop.json ——
        // 通常是 ProgramData 里那份代理副本还是旧的（改过代理代码后没重新部署）。
        if (StopSendCount >= StopResendMaxSends)
        {
            StopGiveUp = true;
        }
    }

    /// <summary>是否该再补发一次停止请求（没回执、没放弃、没到次数上限、间隔已到）。</summary>
    public bool NeedsStopResend(DateTimeOffset now)
    {
        if (!StopPending || StopAcked || StopGiveUp || StopRequestedAt is null)
        {
            return false;
        }

        return StopSendCount < StopResendMaxSends
            && now - StopRequestedAt.Value >= StopResendInterval;
    }
}
