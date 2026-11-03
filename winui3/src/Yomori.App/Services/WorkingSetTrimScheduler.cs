// -*- coding: utf-8 -*-
using Microsoft.UI.Dispatching;

namespace Yomori.Services;

/// <summary>
/// 「连接后回收工作集」的延迟调度器。结构与 <see cref="UuSessionFixScheduler"/> 一致，
/// 但状态机只有两态（空闲 / 等待），因为回收是幂等的、不需要"执行中排队"。
///
/// <para>为什么必须延迟而不是连上就立刻做：连接建立时 D3D 设备、交换链、staging 纹理、
/// 解码表面正在陆续分配和首帧上屏。此时刷工作集，等于把刚写热的页立刻赶走，
/// 下一帧全部硬缺页 —— 画面会顿一下，而且马上又涨回来，白做。</para>
///
/// <para>所以规则是「最后一次连接动作之后静置 <see cref="SettleSeconds"/> 秒再执行」：
/// 两路先后连上时，后一次会重置计时，最终只清一次。</para>
/// </summary>
internal sealed class WorkingSetTrimScheduler : IDisposable
{
    /// <summary>最后一次连接动作之后静置这么久才回收（覆盖连接 + 首帧 + D3D 初始化）。</summary>
    private const int SettleSeconds = 10;

    /// <summary>到期后允许的最小提前量，避免 Interval 取 0 时行为不确定。</summary>
    private const int MinArmMilliseconds = 50;

    private readonly DispatcherQueue _dispatcher;
    private readonly Action<string> _log;
    private readonly Func<bool> _isEnabled;
    private readonly Func<bool> _isBusy;

    /// <summary>必须挂字段保活：局部变量的 DispatcherQueueTimer 会被 GC，Tick 从此不再来。</summary>
    private DispatcherQueueTimer? _timer;

    /// <summary>等待到期时刻（Environment.TickCount64 基准，不受系统时间调整影响）。</summary>
    private long _waitingUntil;

    private bool _disposed;

    public WorkingSetTrimScheduler(
        DispatcherQueue dispatcher,
        Action<string> log,
        Func<bool> isEnabled,
        Func<bool> isBusy)
    {
        _dispatcher = dispatcher;
        _log = log;
        _isEnabled = isEnabled;
        _isBusy = isBusy;
    }

    public bool IsWaiting => _timer?.IsRunning == true;

    /// <summary>
    /// 报告一次「连接状态发生变化」。重复调用只会把回收时间往后推，不会叠加执行。
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

        var wasWaiting = _timer?.IsRunning == true;
        _waitingUntil = Environment.TickCount64 + SettleSeconds * 1000L;
        ArmTimer(_waitingUntil);

        _log(wasWaiting
            ? $"[内存] {source}：重置回收计时，再等 {SettleSeconds} 秒。"
            : $"[内存] {source}：开始计时，{SettleSeconds} 秒后回收工作集。");
    }

    /// <summary>关闭开关或退出时调用：取消尚未开始的回收。</summary>
    public void Cancel()
    {
        if (_disposed)
        {
            return;
        }

        void Apply()
        {
            var hadWork = _timer?.IsRunning == true;
            _timer?.Stop();
            if (hadWork)
            {
                _log("[内存] 已取消尚未执行的工作集回收。");
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
    /// <para>与 UU 调度器同理：不要用固定短周期轮询。10 秒的等待若按 200ms 轮询，
    /// 会往 UI 线程插 50 次队，正撞在画面呈现的关键路径上。</para>
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
        if (_disposed)
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

        if (!_isEnabled())
        {
            return;
        }

        // 任务正在跑时不做：刷工作集等于给正在执行的脚本添缺页。
        // 不重排 —— 下一次连接会重新触发，避免在任务执行期间反复试探。
        if (_isBusy())
        {
            _log("[内存] 任务执行中，跳过本次工作集回收。");
            return;
        }

        var result = WorkingSetTrimmer.TryTrim();
        _log(result is { } r
            ? $"[内存] 已请求回收工作集（当前 {r.Before} MiB），提交量不变；实际常驻值见下一行 [memory] 采样。"
            : "[内存] 工作集回收失败（EmptyWorkingSet 返回失败），不影响运行。");
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
