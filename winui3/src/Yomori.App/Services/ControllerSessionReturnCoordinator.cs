// -*- coding: utf-8 -*-
using Yomori.Models;

namespace Yomori.Services;

/// <summary>
/// 自动切回主控会话的唯一入口与单飞闸门。
///
/// <para>为什么需要协调器而不是在连接回调里直接跑 tscon：</para>
/// <list type="bullet">
/// <item>原生 Connected 回调跑在 RDP 客户端自己的线程上，里面阻塞等待外部进程会拖住整条连接链；</item>
/// <item>批量连接会连着报好几次成功，逐次执行等于反复把控制台抢来抢去；</item>
/// <item>用户停止/退出时，排队中的请求必须能一次性作废，而不是各自判断。</item>
/// </list>
///
/// <para>因此语义定为：<b>一次连接批次最多执行一次</b>，执行中再来的请求直接合并（记 Busy），
/// 不做排队重试。</para>
/// </summary>
internal sealed class ControllerSessionReturnCoordinator : IDisposable
{
    private readonly ControllerSessionReturnService _service;
    private readonly object _gate = new();

    /// <summary>0 = 空闲，1 = 正在执行。用原子交换做单飞，不用锁包住 await。</summary>
    private int _running;

    private CancellationTokenSource? _lifetime;
    private bool _disposed;

    public ControllerSessionReturnCoordinator(ControllerSessionReturnService service)
    {
        _service = service;
    }

    /// <summary>生命周期取消源：Stop / 退出 / 系统结束会话时触发，一次性作废所有排队请求。</summary>
    public CancellationToken LifetimeToken
    {
        get
        {
            lock (_gate)
            {
                _lifetime ??= new CancellationTokenSource();
                return _lifetime.Token;
            }
        }
    }

    /// <summary>该主控进程所在的 Windows 会话（运行期身份，不落盘）。</summary>
    public uint ControllerSessionId => _service.GetControllerSessionId();

    /// <summary>
    /// 停止或退出时调用：丢弃尚未提交的切回请求。
    /// 已经提交给系统的命令无法撤回，这里只负责不再发起新的。
    /// </summary>
    public void CancelPending()
    {
        lock (_gate)
        {
            _lifetime?.Cancel();
            _lifetime?.Dispose();
            _lifetime = new CancellationTokenSource();
        }
    }

    /// <summary>
    /// 请求一次切回。调用方负责选择时机（连接批次收敛之后），这里只保证：
    /// 开关关闭不执行、没有本机通道不执行、已有执行中不并发、取消后不执行。
    /// </summary>
    public async Task<ControllerSessionReturnResult> RequestAsync(
        string source, bool enabled, bool hasLocalChannel, CancellationToken cancellationToken)
    {
        // 先在入口做廉价判定，避免开关关闭时也去抢单飞槽位。
        if (!enabled)
        {
            return Skip(ControllerSessionReturnOutcome.Disabled, source, "自动切回已关闭。");
        }

        if (!hasLocalChannel)
        {
            return Skip(ControllerSessionReturnOutcome.NoLocalChannel, source, "本次没有本机通道，不切回。");
        }

        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
        {
            return Skip(ControllerSessionReturnOutcome.Busy, source, "已有一次切回在执行，本次请求合并。");
        }

        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, LifetimeToken);
            var token = linked.Token;

            if (token.IsCancellationRequested)
            {
                return Skip(ControllerSessionReturnOutcome.Cancelled, source, "请求在提交前已取消。");
            }

            var controllerSessionId = _service.GetControllerSessionId();
            var consoleKnown = _service.TryGetConsoleSessionId(out var consoleSessionId);

            if (controllerSessionId == 0)
            {
                return new ControllerSessionReturnResult
                {
                    Outcome = ControllerSessionReturnOutcome.QueryFailed,
                    Source = source,
                    Detail = "取不到主控进程的会话 ID，未执行命令。",
                };
            }

            return await _service
                .ReturnAsync(source, controllerSessionId, consoleKnown, consoleSessionId, token)
                .ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Exchange(ref _running, 0);
        }
    }

    private static ControllerSessionReturnResult Skip(
        ControllerSessionReturnOutcome outcome, string source, string detail) => new()
        {
            Outcome = outcome,
            Source = source,
            Detail = detail,
        };

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _lifetime?.Cancel();
            _lifetime?.Dispose();
            _lifetime = null;
        }
    }
}
