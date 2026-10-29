// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Yomori.Helpers;
using Yomori.Models;

namespace Yomori.Services;

/// <summary>
/// RDP 目标会话里的执行代理。
///
/// 为什么需要它：两个 Windows 用户会话之间无法直接通信，主控端也不能跨会话启动进程。
/// 因此目标账户登录后，由共享启动目录里的快捷方式以 --rdp-agent 拉起Yomori，
/// 读取主控端下发的 command.json 执行任务队列，再把进度与事件写回 status.json，
/// 主控端轮询 status.json 即可看到实时进度并弹通知。
///
/// Agent 全程隐藏窗口运行，任务结束后按指令处理会话（保持 / 断开 / 注销）。
/// </summary>
public sealed class RdpAgentRunner
{
    /// <summary>Agent 模式的命令行开关（实现在 <see cref="RdpTargets"/>，便于脱离 WinUI 单独测试）。</summary>
    public const string AgentArgument = RdpTargets.AgentArgument;

    /// <summary>
    /// 运行期回传"脉搏"事件的间隔（秒）。
    /// 主控端靠它确认任务确实在推进；太密会挤掉真正重要的事件，太疏又看不出动静。
    /// </summary>
    private const int PulseIntervalSeconds = 60;

    /// <summary>
    /// 检查 stop.json 的间隔（毫秒）。
    ///
    /// 为什么是独立的 500ms 循环而不是继续挂在"每秒计时回调"上：
    /// 计时回调只在任务主循环里触发，一旦执行流程停在启动确认、接力确认、并发组等待、
    /// 或者正在做进程清理这些不产生计时回调的地方，停止请求就没人读 ——
    /// 真机日志里"点停止后代理卡住不动"就是这么来的。
    /// 独立循环与任务执行并行，任何阶段都能在半秒内响应。
    /// </summary>
    private const int StopPollIntervalMs = 500;

    /// <summary>指定指令桥目录的命令行前缀。</summary>
    public const string BridgeArgumentPrefix = RdpTargets.BridgeArgumentPrefix;

    private readonly RdpCommand _command;
    private readonly FileLogger _logger;
    private readonly RuntimeStatsManager _stats;

    /// <summary>本代理读写哪座桥 —— Agent 启动时由 --bridge:（或按账户名派生）确定，见 App.OnLaunched。</summary>
    private readonly RdpBridge _bridge;

    /// <summary>停止请求只处理一次（击键连点 / 多拍重复读到同一个请求时不要反复下发）。</summary>
    private bool _stopHandled;

    private readonly object _liveLogGate = new();
    private readonly object _liveLogFlushGate = new();
    private readonly List<RdpLiveLog> _pendingLiveLogs = new();
    private long _liveLogSeq;
    private Timer? _liveLogTimer;
    private bool _liveLogWriteFailureReported;

    /// <summary>
    /// 累计被省略的 TRACE/DEBUG 行数（见 <see cref="ExternalLogFilter.ShouldArchive"/>）。
    /// 每满 <see cref="OmittedLogReportBatch"/> 行往归档里补一条摘要，
    /// 让人知道「日志变少是因为筛掉了冗长行」，而不是脚本没输出。
    /// </summary>
    private int _omittedLogLines;

    private const int OmittedLogReportBatch = 500;

    public RdpAgentRunner(RdpCommand command)
    {
        _command = command;
        _logger = new FileLogger(AppPaths.LogDir);
        _stats = new RuntimeStatsManager(AppPaths.StatsDir);
        _bridge = RdpBridge.Agent;
    }

    /// <summary>命令行里是否带了 --rdp-agent。</summary>
    public static bool IsAgentInvocation(IReadOnlyList<string> args) => RdpTargets.IsAgentInvocation(args);

    /// <summary>从命令行里取出 --bridge: 后面的桥目录（没有则为 null）。</summary>
    public static string? ExtractBridgePath(IReadOnlyList<string> args) => RdpTargets.ExtractBridgePath(args);

    /// <summary>生成 Agent 快捷方式的命令行参数（带桥目录时一并带上）。</summary>
    public static string BuildAgentArguments(string? bridgePath) => RdpTargets.BuildAgentArguments(bridgePath);

    public event EventHandler<string>? Logged;

    public async Task RunAsync(CancellationToken token)
    {
        Log($"RDP 会话代理已启动：目标账户 {Environment.UserName}，指令 {_command.Id}。");

        // 先补发一次历史快照：本轮跑之前主控端就该看到"这个账户以前跑过什么"，
        // 否则刚重启 / 刚部署完代理时统计页仍是空的，容易被当成故障。
        PublishStatsSnapshot();
        _bridge.PruneLiveLogs(_command.Id);

        var tasks = new List<TaskConfig>();
        for (var i = 0; i < _command.Tasks.Count; i++)
        {
            var task = (TaskConfig)_command.Tasks[i].Clone();
            task.Sanitize(i + 1);
            tasks.Add(task);
        }

        var enabled = tasks.Where(t => t.Enabled).ToList();
        Update(status =>
        {
            status.CommandId = _command.Id;
            status.AgentUser = Environment.UserName;
            status.Phase = "running";
            status.Total = enabled.Count;
            status.Progress = 0;
            status.StatusText = "已接管指令，准备执行";
            return status;
        });

        if (enabled.Count == 0)
        {
            var message = "指令里没有启用的任务，代理结束。";
            Log(message);
            Append(RdpEventKinds.Finished, message);
            FinishSession("done", "没有可执行的任务");
            return;
        }

        var runner = new ScriptRunner(
            enabled,
            _command.ShutdownAfterDone,
            _command.ShutdownDelaySeconds,
            _logger,
            _stats,
            _command.EnableTimeoutScreenshot);

        // 停止监听与任务执行并行：不再依赖计时回调，任何阶段都能响应"停"。
        using var stopWatch = new CancellationTokenSource();
        StartStopWatcher(runner, stopWatch.Token);

        // 上次回传脉搏时的秒数（任务切换时会归零）
        var lastPulseSecond = 0;

        runner.Logged += (_, line) =>
        {
            Log(line);
            // 只有关键节点进事件流，避免把上千行日志塞进 status.json
            if (line.Contains("已超时") || line.Contains("启动失败") || line.Contains("异常"))
            {
                Append(RdpEventKinds.Log, line);
            }
        };

        // 外部脚本实时日志走独立 NDJSON 流，不进入 status.json.Events。
        // 高频日志与任务完成/停止事件分离，避免互相覆盖；本地归档仍照常保留。
        runner.TaskExternalLogLineRead += (_, item) => OnExternalLogLine(item);
        _liveLogTimer = new Timer(_ => FlushLiveLogs(), null, 250, 250);

        runner.StatusChanged += (_, text) => Update(status =>
        {
            status.StatusText = text;
            return status;
        });

        runner.ProgressChanged += (_, info) => Update(status =>
        {
            status.Progress = info.Current;
            status.Total = info.Total;
            return status;
        });

        runner.ElapsedChanged += (_, seconds) =>
        {
            Update(status =>
            {
                // 任务切换时 ScriptRunner 会把计时归零（RaiseElapsed(0)），脉搏也要跟着重置，
                // 否则新任务的脉搏要等很久才会出现。
                if (seconds < lastPulseSecond)
                {
                    lastPulseSecond = 0;
                }

                status.ElapsedSeconds = seconds;

                // 【脉搏】任务运行期间每 60 秒回一条事件。
                // 没有它的话，主控端在任务执行期间只能看到一个静止的"运行中" ——
                // ScriptRunner 在任务跑的过程中只有日志、没有结构性事件，
                // 事件列表会一直空着，用户根本判断不出任务是还在跑还是已经卡死。
                if (seconds - lastPulseSecond >= PulseIntervalSeconds)
                {
                    lastPulseSecond = seconds;
                    var name = string.IsNullOrEmpty(status.CurrentTask) ? "（未知任务）" : status.CurrentTask;
                    status.Events.Add(RdpBridge.MakeEvent(
                        status,
                        RdpEventKinds.Log,
                        $"仍在运行，已耗时 {FormatHelper.FormatSeconds(seconds)}",
                        name,
                        status.Progress,
                        status.Total,
                        seconds));
                }

                return status;
            });
        };

        runner.TaskStarted += (_, info) => Update(status =>
        {
            status.CurrentTask = info.Name;
            status.Progress = info.Index;
            status.Total = info.Total;
            status.ElapsedSeconds = 0;
            return status;
        });

        runner.TaskCompleted += (_, info) =>
        {
            // 每个任务完成都回传一条事件。
            // 这里**故意不发系统通知**：Toast 只在发出它的那个用户会话里显示，
            // 目标会话里弹一条主控端用户根本看不到（纯噪音），通知统一由主控端发。
            Update(status =>
            {
                status.Events.Add(RdpBridge.MakeEvent(
                    status,
                    RdpEventKinds.TaskDone,
                    $"{info.Status}（{FormatHelper.FormatSeconds(info.ElapsedSeconds)}）",
                    info.Name,
                    info.Index,
                    info.Total,
                    info.ElapsedSeconds,
                    info.IsAbnormal));
                return status;
            });
        };

        runner.ShutdownPrompt += (_, delay) =>
        {
            // Agent 没有交互界面，直接按配置排定关机（仍带倒计时，切过去可取消）
            Log($"已启用自动关机，代理将在 {delay} 秒后关机（可在目标会话执行 shutdown /a 取消）。");
            ShutdownService.Schedule(delay);
        };

        var hadError = false;
        runner.Finished += (_, stoppedOrError) =>
        {
            // 「停止」不是「异常」：ScriptRunner 在停止分支同样 RaiseFinished(true)，
            // 只看这个参数会把用户点停止也写成「已完成（有异常）」——
            // 真机日志里出现过：紧急停止 → 两个通道都报「已完成（有异常）」，
            // 界面上只写了"有异常"、没有任何原因，用户根本看不出那是自己按的停止。
            // 真异常以 HadTaskError 为准（启动失败 / 脚本缺失 / 超时强退 / 过早退出都会置它）。
            hadError = stoppedOrError && runner.HadTaskError;

            // 统计是在 RaiseFinished 之前刚写好的（ScriptRunner 先 SaveStatsQuietly 再 RaiseFinished），
            // 所以这里正好能在会话收尾之前把合并后的历史发布到桥目录，供主控端的"耗时统计"页汇总。
            // 必须赶在 FinishSession 之前：收尾方式若是注销，代理进程会被一起杀掉，之后就没机会写了。
            PublishStatsSnapshot();
        };

        try
        {
            try
            {
                await runner.RunAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                var message = $"代理执行异常：{ex.Message}";
                Log(message);
                FlushLiveLogs();
                Append(RdpEventKinds.Finished, message, abnormal: true);
                FinishSession("error", message);
                return;
            }

            FlushLiveLogs(); // Flush before Finished/logoff: the controller may stop polling immediately.

            // 文案要区分三件事：真异常 / 被停止 / 正常跑完。
            // 以前停止也写成「已完成（有异常）」，主控端状态栏只能照抄 —— 于是"停止"变成了"故障"。
            var stopped = runner.WasStopped;
            var summary = hadError
                ? "已完成（有异常）"
                : stopped ? "已停止（本轮未跑完）" : "全部任务已完成";

            // 异常与否写进事件：常驻代理会把 Phase 从 done 迅速改成 idle，主控端来不及看相位，
            // 只能靠这条 Finished 事件的 abnormal 标记来区分"正常结束"与"有异常结束"。
            Append(RdpEventKinds.Finished, summary, abnormal: hadError);

            // 同上：收尾通知也只由主控端发，目标会话里不弹。
            FinishSession(hadError ? "done" : "done", summary);
        }
        finally
        {
            // 停掉监听循环；并把桥里可能残留的停止请求清掉 ——
            // 本轮已经结束，留着它只会污染下一轮（主控端靠 CommandId 兜底能挡住误停，
            // 但排障时看到一份没人消费的 stop.json 非常误导）。
            stopWatch.Cancel();
            _liveLogTimer?.Dispose();
            _liveLogTimer = null;
            FlushLiveLogs();
            _bridge.ClearStop();
        }
    }

    /// <summary>
    /// 把本账户累积的耗时统计发布到桥目录，让主控端也能看到。
    ///
    /// 代理的统计落在自己那份副本的 runtime_stats\&lt;账户&gt; 下，主控端不知道副本在哪个槽、
    /// 远程主机时更是读不到 —— 不发布出去，主控端的"耗时统计"页就永远是空白。
    /// 发布失败只记日志：统计是锦上添花，绝不能因为它把整轮任务连坐判成异常。
    /// </summary>
    private void PublishStatsSnapshot()
    {
        try
        {
            var history = _stats.LoadHistory();
            if (history.Count == 0)
            {
                return;
            }

            if (_bridge.WriteStatsSnapshot(history))
            {
                Log($"耗时统计快照已发布到桥目录（{history.Count} 个脚本）：{_bridge.StatsPath}");
            }
            else
            {
                Log($"耗时统计快照发布失败（不影响任务结果）：{_bridge.LastError}");
            }
        }
        catch (Exception ex)
        {
            Log($"耗时统计快照发布失败（不影响任务结果）：{ex.Message}");
        }
    }

    /// <summary>
    /// 处理外部脚本日志：写入独立归档文件，并排队写入主控实时日志流。
    /// 不触碰 status.json.Events，避免高频日志影响任务状态事件。
    ///
    /// <para>TRACE/DEBUG 级（MaaFramework 的函数进出、Pipeline 解析、GPU 探测）**不归档也不上屏**，
    /// 理由见 <see cref="ExternalLogFilter.ShouldArchive"/> —— 这两级占实测日志体积七成以上。
    /// 被省略的行按批补一条摘要，避免让人误以为脚本没输出。</para>
    /// </summary>
    private void OnExternalLogLine(TaskExternalLogLine item)
    {
        var line = item.Line;
        if (line.Text.Length == 0 && line.NodeText.Length == 0)
        {
            return;
        }

        try
        {
            var archivePath = ExternalLogStore.TodayFilePath(_bridge.ChannelTag, DateTime.Now);

            if (!ExternalLogFilter.ShouldArchive(line.Level))
            {
                var omitted = Interlocked.Increment(ref _omittedLogLines);
                if (omitted % OmittedLogReportBatch == 0)
                {
                    ExternalLogStore.Append(
                        archivePath,
                        $"[{FormatHelper.NowTimeText()}] [已省略累计 {omitted} 行 TRACE/DEBUG 日志" +
                        "（外部日志只保留 INFO 及以上；需要明细请设 " +
                        ExternalLogFilter.VerboseEnvironmentVariable + "=1 后重启）]");
                }

                return;
            }

            // 归档写节点档：事后排障要的是"跑到哪一步断了"，而不是面板上那几行人话。
            var archiveText = ExternalLogText(line.ArchiveText, line.Level);
            ExternalLogStore.Append(archivePath, $"[{FormatHelper.NowTimeText()}] {archiveText}");

            // 上屏只发当前视图的文本；节点档独有的行（中间层 WRN）不发，免得面板上冒出代码。
            var text = ExternalLogText(line.Text, line.Level);
            if (line.Text.Length == 0) { return; }

            lock (_liveLogGate)
            {
                // Archive remains on disk; bound the retry queue when the bridge is unavailable.
                if (_pendingLiveLogs.Count >= 4000)
                {
                    _pendingLiveLogs.RemoveRange(0, 256);
                    _pendingLiveLogs.Add(new RdpLiveLog
                    {
                        CommandId = _command.Id, Seq = ++_liveLogSeq,
                        Time = FormatHelper.NowTimeText(), TaskName = item.TaskName,
                        Message = "实时日志传输积压，较早内容已从实时队列移除；请查看脚本原始日志。",
                        Level = "warn",
                    });
                }
                _pendingLiveLogs.Add(new RdpLiveLog
                {
                    CommandId = _command.Id,
                    Seq = ++_liveLogSeq,
                    Time = FormatHelper.NowTimeText(),
                    TaskName = item.TaskName,
                    Message = text,
                    Level = line.Level.ToString().ToLowerInvariant(),
                    IsAbnormal = line.Level == ExternalLogLevel.Error,
                });
            }
        }
        catch
        {
            // 日志落盘失败绝不影响任务执行
        }
    }

    private void FlushLiveLogs()
    {
        lock (_liveLogFlushGate)
        {
            // Keep timer callbacks bounded even while scripts continuously append.
            for (var i = 0; i < 20; i++)
            {
                List<RdpLiveLog> batch;
                lock (_liveLogGate)
                {
                    if (_pendingLiveLogs.Count == 0) return;
                    batch = _pendingLiveLogs.Take(256).ToList();
                    _pendingLiveLogs.RemoveRange(0, batch.Count);
                }
                if (_bridge.AppendLiveLogBatch(_command.Id, batch))
                {
                    _liveLogWriteFailureReported = false;
                    continue;
                }
                lock (_liveLogGate)
                {
                    _pendingLiveLogs.InsertRange(0, batch);
                    if (_pendingLiveLogs.Count > 4000)
                        _pendingLiveLogs.RemoveRange(0, _pendingLiveLogs.Count - 4000);
                }
                if (!_liveLogWriteFailureReported)
                {
                    _liveLogWriteFailureReported = true;
                    Log($"实时日志流写入失败（不影响任务执行）：{_bridge.LastError}");
                Update(status => status);
                }
                return;
            }
        }
    }

    /// <summary>给日志行加级别前缀（错误 / 警告），其余原样。</summary>
    private static string ExternalLogText(string text, ExternalLogLevel level) => level switch
    {
        ExternalLogLevel.Error => $"[错误] {text}",
        ExternalLogLevel.Warn => $"[警告] {text}",
        _ => text,
    };

    private void FinishSession(string phase, string summary)
    {
        Update(status =>
        {
            status.Phase = phase;
            status.StatusText = summary;
            status.CurrentTask = string.Empty;
            return status;
        });

        var mode = SessionFinishModes.Normalize(_command.SessionFinish);
        var session = RdpSessionService.FindSession(Environment.UserName);

        switch (mode)
        {
            case SessionFinishModes.Disconnect:
                if (session is not null)
                {
                    RdpSessionService.DisconnectSession(session.Id, out var message);
                    Log($"会话收尾：{message}");
                }
                else
                {
                    Log("会话收尾：找不到当前会话，跳过断开。");
                }

                break;

            case SessionFinishModes.Logoff:
                if (session is not null)
                {
                    RdpSessionService.LogoffSession(session.Id, out var message);
                    Log($"会话收尾：{message}");
                }
                else
                {
                    Log("会话收尾：找不到当前会话，跳过注销。");
                }

                break;

            default:
                Log("会话收尾：保持连接，方便随时切回来查看结果。");
                break;
        }
    }

    // ---------------- 状态与日志 ----------------

    private void Update(Func<RdpStatus, RdpStatus> mutator)
    {
        _bridge.UpdateStatus(status =>
        {
            status.CommandId = _command.Id;
            status.AgentUser = Environment.UserName;
            status.LiveLogProtocol = RdpLiveLogProtocol.Current;
            status.LiveLogError = _liveLogWriteFailureReported ? "实时日志回传暂时不可用，正在重试。" : string.Empty;
            return mutator(status);
        });
    }

    private void Append(string kind, string message, bool abnormal = false) =>
        _bridge.UpdateStatus(status =>
        {
            status.CommandId = _command.Id;
            status.AgentUser = Environment.UserName;
            status.Events.Add(RdpBridge.MakeEvent(status, kind, message, abnormal: abnormal));
            return status;
        });

    private void Log(string line)
    {
        try
        {
            _logger.Log(line);
        }
        catch
        {
            // 目标账户可能没有日志目录写权限，忽略
        }

        Logged?.Invoke(this, line);
    }

    /// <summary>
    /// 起一条与任务执行并行的停止监听。
    ///
    /// 为什么必须由 Agent 主动查：任务跑在目标会话里，主控端点了「停止执行」之后
    /// 没有任何办法直接去停那个进程 —— 两条会话之间只有桥文件能通信。
    /// 为什么不放进"每秒计时回调"：那个回调只在任务主循环里触发，
    /// 一旦执行流程停在启动确认 / 接力确认 / 并发组等待 / 进程清理等分支，
    /// 停止请求就没人读，主控端只能干等（真机日志里已经复现过）。
    /// </summary>
    private void StartStopWatcher(ScriptRunner runner, CancellationToken token)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                while (!token.IsCancellationRequested)
                {
                    CheckStopRequest(runner);
                    await Task.Delay(StopPollIntervalMs, token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                // 本轮执行结束，正常退出
            }
            catch (Exception ex)
            {
                Log($"停止监听循环异常退出：{ex.Message}");
            }
        });
    }

    private void CheckStopRequest(ScriptRunner runner)
    {
        if (!_bridge.IsStopRequested(_command.Id, out var emergency))
        {
            return;
        }

        // 先把请求文件清掉：ScriptRunner 停止是异步收尾，还会再跑一会儿，
        // 不清掉的话后面每一拍都会重复触发；已经处理过的也要清 ——
        // 否则（例如先点停止、再点紧急停止）后一份请求会永远躺在桥目录里没人收。
        _bridge.ClearStop();

        if (_stopHandled)
        {
            return;
        }

        _stopHandled = true;

        var text = emergency ? "收到紧急停止请求，立即结束任务。" : "收到停止请求，正在收尾。";
        Log(text);
        Append(RdpEventKinds.Log, text);

        // 立刻把相位切成 stopping 作为"回执"：主控端据此知道请求已送达，不再盲目重发，
        // 也能在界面上把状态固定成"正在收尾"，而不是继续显示"运行中"。
        Update(status =>
        {
            status.Phase = "stopping";
            status.StatusText = text;
            return status;
        });

        if (emergency)
        {
            runner.RequestEmergencyStop();
        }
        else
        {
            runner.RequestStop();
        }
    }
}
