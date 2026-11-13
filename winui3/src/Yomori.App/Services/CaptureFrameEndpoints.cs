// S1 第五批：采集端（目标会话代理进程）与主控端（显示进程）的帧通路骨架。
//
// ⚠️ 两端**刻意分离**：B2「完全本地」指同一台物理机，但采集端跑在目标登录会话的
//    Yomori.Agent 里，主控端跑在 Console 会话，中间只通过共享内存通信 —— 不能合成一个对象。
//    本文件把两侧各自的循环骨架定下来，真实 DD 采集与 UI 呈现由后续接线注入。
//
// 纯 .NET + 接口注入：ICaptureFrameSource 的真实实现 = IDXGIOutputDuplication（S1 后续），
// ICaptureFrameSink 的真实实现 = RdpView 在 UI 线程的 D3D Present（S1 后续）。
// **两者都默认关闭**（NC5），当前没有任何生产调用方，不影响现有 RDP GFX 帧流。
using System;
using Yomori.Models;

namespace Yomori.Services
{
    /// <summary>一次采集结果。Pixels 为 BGRA32；Rects 为空表示全帧。</summary>
    public readonly record struct CapturedFrame(
        byte[] Pixels,
        uint Width,
        uint Height,
        uint Stride,
        FrameSample Sample,
        FrameRect[] Rects);

    /// <summary>采集端帧源。真实实现 = 目标会话内的 DXGI Desktop Duplication。</summary>
    public interface ICaptureFrameSource
    {
        bool TryCapture(out CapturedFrame frame);
    }

    /// <summary>主控端呈现。真实实现必须保证在 UI 线程调用 RdpD3DRenderer.Present。</summary>
    public interface ICaptureFrameSink
    {
        void Present(ulong seq, in FrameHeader header, byte[] pixels);
    }

    public enum ProducerTickResult
    {
        Disabled,
        NoFrame,
        WarmupDiscarded,
        BlackFrame,
        Published,
        Failed,
    }

    public enum ConsumerTickResult
    {
        Disabled,
        NoFrame,
        Presented,
        Failed,
    }

    /// <summary>
    /// 采集端发布器：驱动帧源 → 丢弃前 2 帧预热空帧 → 构造帧头 → PublishFrame 推进 NextSeq。
    /// 黑帧 / 受保护内容直接丢弃不发布（上层看门狗按「无有效帧」累积并回退 RDP）。
    /// 异常一律吞成 Failed，绝不掀掉常驻代理进程。
    /// </summary>
    public sealed class CaptureFrameProducer
    {
        private readonly CaptureFrameChannel _channel;
        private readonly ICaptureFrameSource _source;
        private readonly GuardConfig _guardConfig;
        private readonly int _warmupFrames;
        private int _warmupSeen;
        private ulong _nextSeq = 1;

        public bool Enabled { get; private set; }
        public ulong NextSeq => _nextSeq;
        public int WarmupSeen => _warmupSeen;
        public ulong PublishedCount { get; private set; }

        public CaptureFrameProducer(CaptureFrameChannel channel, ICaptureFrameSource source,
                                    GuardConfig? guardConfig = null,
                                    int warmupFrames = WarmupPolicy.FramesToDiscard)
        {
            _channel = channel ?? throw new ArgumentNullException(nameof(channel));
            _source = source ?? throw new ArgumentNullException(nameof(source));
            if (warmupFrames < 0) throw new ArgumentOutOfRangeException(nameof(warmupFrames));
            _guardConfig = guardConfig ?? new GuardConfig();
            _guardConfig.Validate();
            _warmupFrames = warmupFrames;
        }

        /// <summary>NC5：默认关闭；停用后重新启用必须重新预热。</summary>
        public void Enable(bool enabled = true)
        {
            Enabled = enabled;
            if (!enabled) _warmupSeen = 0;
        }

        public ProducerTickResult Tick(long nowMs)
        {
            if (!Enabled) return ProducerTickResult.Disabled;
            try
            {
                // 采集源调用也必须在 try 内：DD / COM 抛异常时收敛为 Failed，
                // 绝不能冒泡掀掉目标会话里常驻的代理进程。
                if (!_source.TryCapture(out CapturedFrame frame) || frame.Pixels is null)
                    return ProducerTickResult.NoFrame;
                if (_warmupSeen < _warmupFrames)
                {
                    _warmupSeen++;
                    return ProducerTickResult.WarmupDiscarded;
                }
                // 受保护内容被 DD 填黑，与全 0 帧同等待遇：不发布，交给上层状态机回退。
                if (frame.Sample.ProtectedContentMaskedOut ||
                    BlackFrameGuard.IsBlackFrame(frame.Sample, _guardConfig))
                    return ProducerTickResult.BlackFrame;

                FrameRect[] rects = frame.Rects ?? Array.Empty<FrameRect>();
                FrameKind kind = rects.Length > 0 ? FrameKind.DirtyRects : FrameKind.Full;
                var header = FrameHeader.Create(_nextSeq, nowMs, frame.Width, frame.Height,
                                                frame.Stride, kind, (uint)frame.Pixels.Length,
                                                (uint)rects.Length);
                _channel.PublishFrame(header, rects, frame.Pixels, nowMs);
                _nextSeq++;
                PublishedCount++;
                return ProducerTickResult.Published;
            }
            catch (Exception)
            {
                return ProducerTickResult.Failed;
            }
        }
    }

    /// <summary>
    /// 主控端消费者：按节拍读最新帧 → 交给 sink 呈现 → Acknowledge 后不重复呈现。
    /// 默认关闭（NC5）；sink 的线程约束由调用方保证。sink 抛异常时**不 ack**，
    /// 下一拍可重试，且不掀进程。
    /// </summary>
    public sealed class CaptureFrameConsumer
    {
        private readonly CaptureFrameChannel _channel;
        private readonly ICaptureFrameSink _sink;
        private ulong _lastPresentedSeq;

        public bool Enabled { get; private set; }
        public ulong LastPresentedSeq => _lastPresentedSeq;
        public ulong PresentedCount { get; private set; }

        public CaptureFrameConsumer(CaptureFrameChannel channel, ICaptureFrameSink sink)
        {
            _channel = channel ?? throw new ArgumentNullException(nameof(channel));
            _sink = sink ?? throw new ArgumentNullException(nameof(sink));
        }

        public void Enable(bool enabled = true) => Enabled = enabled;

        public ConsumerTickResult Tick()
        {
            if (!Enabled) return ConsumerTickResult.Disabled;
            if (!_channel.TryReadLatest(out FrameHeader header, out byte[] pixels, out ulong seq))
                return ConsumerTickResult.NoFrame;
            if (seq == _lastPresentedSeq) return ConsumerTickResult.NoFrame;
            try
            {
                _sink.Present(seq, header, pixels);
                _channel.Acknowledge(seq);
                _lastPresentedSeq = seq;
                PresentedCount++;
                return ConsumerTickResult.Presented;
            }
            catch (Exception)
            {
                return ConsumerTickResult.Failed;
            }
        }
    }
}
