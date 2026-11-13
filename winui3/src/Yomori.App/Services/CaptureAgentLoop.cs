// S1 第八批：会话代理侧的抓帧常驻循环。
//
// 与 AgentHost 的指令循环**完全并列、互不阻塞**：指令循环执行任务时会 await 在
// runner.RunAsync 上，若把抓帧塞进那条循环，任务一跑画面就断。这里单独一个 Task。
//
// 开关来源：桥目录里的 capture-enable.json（主控端设置页写）。文件不存在 = 关闭，
// 于是代理平时**完全不创建 D3D 设备、不抓帧、不占 CPU**（NC5 / B3 的前提）。
//
// 纪律：
//   · 任何异常都收敛成「停用 + 记 LastError + 下一轮重试」，绝不掀常驻代理；
//   · 帧源由外部工厂注入（真实是 DxgiFrameSource，_smoke 用 fake），
//     本文件不引用 Vortice，保持纯 .NET 可链入冒烟工程；
//   · 映射工厂可注入（真实是带 ACL 的 Global\ 创建），失败即不上线。
using System;
using System.IO.MemoryMappedFiles;
using System.Threading;
using System.Threading.Tasks;
using Yomori.Models;

namespace Yomori.Services
{
    public sealed class CaptureAgentLoop : IDisposable
    {
        private readonly string _channelTag;
        private readonly string _bridgeDir;
        private readonly Func<ICaptureFrameSource> _sourceFactory;
        private readonly Func<string, long, MemoryMappedFile?>? _mapFactory;
        private readonly int _frameIntervalMs;
        private readonly int _pollIntervalMs;

        private CaptureFrameRelay? _relay;
        private ICaptureFrameSource? _source;
        private string _currentMapName = string.Empty;
        private long _currentTotalBytes;
        // 累计统计：RunAsync 结束会释放 relay，但排障/验收仍要能读到「这一生发过多少帧」。
        private ulong _publishedTotal;
        private string _lastMapName = string.Empty;
        private bool _disposed;

        /// <summary>最近一次失败原因（排障用；正常运行时为空）。</summary>
        public string LastError { get; private set; } = string.Empty;
        public bool Enabled => _relay?.Enabled ?? false;
        /// <summary>累计已发布帧数（含已释放的上一代 Relay）。</summary>
        public ulong PublishedCount => _publishedTotal + (_relay?.PublishedCount ?? 0);
        /// <summary>当前或最后一次使用的映射名。</summary>
        public string MapName => _relay?.MapName ?? _lastMapName;

        public CaptureAgentLoop(string channelTag, string bridgeDir,
                                Func<ICaptureFrameSource> sourceFactory,
                                Func<string, long, MemoryMappedFile?>? mapFactory = null,
                                bool global = true,
                                int frameIntervalMs = 33,
                                int pollIntervalMs = 1000)
        {
            if (string.IsNullOrWhiteSpace(channelTag)) throw new ArgumentException("channelTag 不能为空", nameof(channelTag));
            if (string.IsNullOrWhiteSpace(bridgeDir)) throw new ArgumentException("bridgeDir 不能为空", nameof(bridgeDir));
            _channelTag = channelTag;
            _bridgeDir = bridgeDir;
            _sourceFactory = sourceFactory ?? throw new ArgumentNullException(nameof(sourceFactory));
            _mapFactory = mapFactory;
            Global = global;
            if (frameIntervalMs < 1) throw new ArgumentOutOfRangeException(nameof(frameIntervalMs));
            if (pollIntervalMs < 1) throw new ArgumentOutOfRangeException(nameof(pollIntervalMs));
            _frameIntervalMs = frameIntervalMs;
            _pollIntervalMs = pollIntervalMs;
        }

        public bool Global { get; }

        /// <summary>长期运行；由 AgentHost 在独立 Task 上启动，正常不返回。</summary>
        public async Task RunAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested && !_disposed)
            {
                try
                {
                    var flag = CaptureEnableFlag.Read(_bridgeDir);
                    if (!flag.Enabled)
                    {
                        _relay?.SetEnabled(false);
                        await Task.Delay(_pollIntervalMs, ct).ConfigureAwait(false);
                        continue;
                    }

                    if (!EnsureRelay(flag))
                    {
                        StopRelay();
                        await Task.Delay(_pollIntervalMs, ct).ConfigureAwait(false);
                        continue;
                    }

                    _relay!.SetEnabled(true);
                    LastError = string.Empty;
                    // 连续采帧约 _pollIntervalMs 一段，然后回读开关（关掉能在一秒内生效）。
                    int ticks = Math.Max(1, _pollIntervalMs / _frameIntervalMs);
                    for (int i = 0; i < ticks && !ct.IsCancellationRequested; i++)
                    {
                        _relay.Tick(Environment.TickCount64);
                        await Task.Delay(_frameIntervalMs, ct).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    LastError = ex.GetType().Name + ": " + ex.Message;
                    StopRelay();
                    try { await Task.Delay(_pollIntervalMs, ct).ConfigureAwait(false); }
                    catch (OperationCanceledException) { break; }
                }
            }
            StopRelay();
        }

        /// <summary>按开关参数建立/复用 Relay；尺寸或映射名变化则整套重建。</summary>
        private bool EnsureRelay(CaptureEnableFlag flag)
        {
            string mapName = CaptureChannelNaming.ForChannelSized(
                _channelTag, flag.Width, flag.Height, Global);
            var layout = new SharedFrameRingLayout(flag.Width, flag.Height, flag.Slots);
            if (_relay is not null && _source is not null &&
                string.Equals(_currentMapName, mapName, StringComparison.Ordinal) &&
                _currentTotalBytes == layout.TotalBytes)
                return true;

            StopRelay();
            _source = _sourceFactory();
            _relay = new CaptureFrameRelay(_channelTag, layout, _source, Global,
                                           _mapFactory, mapName);
            _currentMapName = mapName;
            _currentTotalBytes = layout.TotalBytes;
            return true;
        }

        private void StopRelay()
        {
            if (_relay is not null)
            {
                _publishedTotal += _relay.PublishedCount;
                _lastMapName = _relay.MapName;
            }
            try { _relay?.SetEnabled(false); } catch { /* 忽略 */ }
            try { _relay?.Dispose(); } catch { /* 忽略 */ }
            try { (_source as IDisposable)?.Dispose(); } catch { /* 忽略 */ }
            _relay = null;
            _source = null;
            _currentMapName = string.Empty;
            _currentTotalBytes = 0;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            StopRelay();
        }
    }
}
