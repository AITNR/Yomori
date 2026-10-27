// -*- coding: utf-8 -*-
using Microsoft.UI.Dispatching;
using Yomori.Models;

namespace Yomori.Services;

/// <summary>
/// 「UU 远程修复」的延迟调度器。
///
/// <para>为什么需要延迟：修复要重启 UU 的投屏进程。若在连接或开始执行的**同一瞬间**动手，
/// 会和正在建立的会话抢时序，轻则白断一次，重则让连接流程读到中间状态。
/// 因此规则定为「最后一次相关操作之后静置一小段时间再执行」。</para>
///
/// <para>状态机（只有三个状态）：</para>
/// <list type="bullet">
/// <item><b>空闲</b>：收到操作 → 等待 18 秒。</item>
/// <item><b>等待中</b>：再收到操作 → 重置为 18 秒。</item>
/// <item><b>执行中</b>：收到操作 → 记一个待执行（30 秒后）；若这 30 秒内又来，
/// 剩余 ≤18 秒就重置为 18 秒，否则不动。</item>
/// </list>
///
/// <para>后两条合起来等价于：执行时刻 = max(原定时刻, 最后一次操作 + 18 秒)。
/// 同一时刻最多一个等待、一个执行；执行中来的多次操作合并成一次。</para>
///
/// <para>所有状态变更都在 UI 线程上做 —— <see cref="Notify"/> 可从任意线程调用，
/// 内部会自己编组。</para>
/// </summary>
internal sealed class UuSessionFixScheduler : IDisposable
{
    /// <summary>最后一次操作之后至少静置这么久，才去动 UU 的投屏进程。</summary>
    private const int SettleSeconds = 18;

    /// <summary>执行中被触发时，先按这个间隔排下一次（给当前这次留出完成时间）。</summary>
    private const int BusyDelaySeconds = 30;

    /// <summary>到期后允许的最小提前量，避免 Interval 取 0 时行为不确定。</summary>
    private const int MinArmMilliseconds = 50;

    private readonly DispatcherQueue _dispatcher;
    private readonly Action<string> _log;
    private readonly Func<bool> _isEnabled;
    private readonly Func<Task<UuFixResult>> _apply;

    /// <summary>必须挂字段保活：局部变量的 DispatcherQueueTimer 会被 GC，Tick 从此不再来。</summary>
    private DispatcherQueueTimer? _timer;

    /// <summary>等待到期时刻（Environment.TickCount64 基准，不受系统时间调整影响）。</summary>
    private long _waitingUntil;

    /// <summary>执行中收到的那一次待执行。</summary>
    private bool _hasPending;
    private long _pendingUntil;

    private bool _running;
    private bool _disposed;

    public UuSessionFixScheduler(
        DispatcherQueue dispatcher,
        Action<string> log,
        Func<bool> isEnabled,
        Func<Task<UuFixResult>> apply)
    {
        _dispatcher = dispatcher;
        _log = log;
        _isEnabled = isEnabled;
        _apply = apply;
    }

    public bool IsRunning => _running;

    public bool IsWaiting => _timer?.IsRunning == true;

    /// <summary>
    /// 报告一次「可能改变控制台归属」的操作。重复调用按状态机合并，不会叠加执行。
    /// </summary>
    public void Notify(string source)
    {
        if (_disposed || !_isEnabled())
        {
            return;
        }

        if (!_dispatcher.HasThreadAccess)
        {
            _dispatcher.TryEnqueue(() => Notify(source));
            return;
        }

        var now = Environment.TickCount64;

        if (_running)
        {
            if (!_hasPending)
            {
                _hasPending = true;
                _pendingUntil = now + BusyDelaySeconds * 1000L;
                _log($"[UU 修复] {source}：正在执行，已排到当前这次之后（{BusyDelaySeconds} 秒）。");
            }
            else if (_pendingUntil - now <= SettleSeconds * 1000L)
            {
                _pendingUntil = now + SettleSeconds * 1000L;
                _log($"[UU 修复] {source}：排队中的执行剩余不足 {SettleSeconds} 秒，重置为 {SettleSeconds} 秒。");
            }
            else
            {
                var left = (_pendingUntil - now + 999) / 1000;
                _log($"[UU 修复] {source}：排队中的执行还剩 {left} 秒，不再顺延。");
            }

            return;
        }

        // 空闲或等待中：一律重置。日志要区分这两种情况 ——
        // 「开始计时」说明这轮静置刚开始，「重置计时」说明上一个操作把等待推后了，
        // 排查"为什么迟迟不执行"时看的就是这两行的先后。
        var wasWaiting = _timer?.IsRunning == true;

        _waitingUntil = now + SettleSeconds * 1000L;
        ArmTimer(_waitingUntil);

        _log(wasWaiting
            ? $"[UU 修复] {source}：重置计时，再等 {SettleSeconds} 秒。"
            : $"[UU 修复] {source}：开始计时，{SettleSeconds} 秒后执行。");
    }

    /// <summary>关闭开关时调用：取消尚未开始的等待（正在执行的那次无法撤回）。</summary>
    public void Cancel()
    {
        if (_disposed)
        {
            return;
        }

        void Apply()
        {
            var hadWork = _hasPending || _timer?.IsRunning == true;
            _hasPending = false;
            _timer?.Stop();

            // 只在真的取消了什么才写：Stop/退出都会走这里，没等待时不必刷日志
            if (hadWork)
            {
                _log("[UU 修复] 已取消尚未开始的执行。");
            }
        }

        if (_dispatcher.HasThreadAccess)
        {
            Apply();
        }
        else
        {
            _dispatcher.TryEnqueue(Apply);
        }
    }

    private void EnsureTimer()
    {
        if (_timer is not null)
        {
            return;
        }

        _timer = _dispatcher.CreateTimer();
        _timer.Tick += OnTick;
    }

    /// <summary>
    /// 按剩余时间一次性定时。
    ///
    /// <para>早先用 200ms 固定轮询：18 秒要往 UI 线程插 90 次队。
    /// 内嵌画面正在建立 D3D/SwapChain 时，这些插入会挤在渲染关键路径上 ——
    /// 改成「按到期时间只定时一次」，正常情况整段等待只有 1 次 Tick。</para>
    /// </summary>
    private void ArmTimer(long dueAt)
    {
        EnsureTimer();

        var delay = dueAt - Environment.TickCount64;
        if (delay < MinArmMilliseconds)
        {
            delay = MinArmMilliseconds;
        }

        _timer!.Stop();
        _timer.Interval = TimeSpan.FromMilliseconds(delay);
        _timer.Start();
    }

    private void OnTick(DispatcherQueueTimer sender, object args)
    {
        if (_disposed || _running)
        {
            sender.Stop();
            return;
        }

        if (Environment.TickCount64 < _waitingUntil)
        {
            // Interval 精度不足时可能提前来，重新按剩余时间定一次
            ArmTimer(_waitingUntil);
            return;
        }

        sender.Stop();
        _ = ExecuteAsync();
    }

    private async Task ExecuteAsync()
    {
        if (_running)
        {
            return;
        }

        _running = true;
        try
        {
            var result = await _apply().ConfigureAwait(false);
            _log("[UU 修复] 自动执行：" + result.Detail);
        }
        catch (Exception ex)
        {
            var detail = ex.Message.Length == 0 ? ex.GetType().Name : ex.Message;
            _log("[UU 修复] 自动执行异常：" + detail);
        }
        finally
        {
            _running = false;

            // 回到 UI 线程处理排队的那一次，避免和 Tick 抢状态
            if (!_dispatcher.TryEnqueue(AfterExecute))
            {
                // 队列已关（程序正在退出）：放弃排队，不做任何补救
            }
        }
    }

    private void AfterExecute()
    {
        if (_disposed || !_hasPending)
        {
            return;
        }

        _hasPending = false;

        // 保证与刚完成的这次至少隔开 SettleSeconds，避免连续杀两次投屏进程
        var now = Environment.TickCount64;
        var floor = now + SettleSeconds * 1000L;
        _waitingUntil = _pendingUntil > floor ? _pendingUntil : floor;

        var delaySeconds = (_waitingUntil - now + 999) / 1000;
        ArmTimer(_waitingUntil);
        _log($"[UU 修复] 排队的那次开始计时，{delaySeconds} 秒后执行。");
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_timer is not null)
        {
            _timer.Stop();
            _timer.Tick -= OnTick;
            _timer = null;
        }
    }
}
