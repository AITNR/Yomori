// S1 第六批：RdpView 的帧源选择器 —— 把「共享内存抓帧」接成可替代 RDP 画面的帧源。
//
// 设计要点（计划书 4.7 / 4.3.3 / NC5）：
//   · 默认关闭；关闭时绝不发 suppress，画面完全走原有 RDP 帧流。
//   · 只有「双源比对闸门通过 + 预热帧丢弃完 + 控制面 suppress 真的成功」才切换，
//     禁止先切后验 —— suppress 失败就保持 RDP 帧，绝不出现无帧可显示的空窗。
//   · 切换后黑帧 / 连续无帧 / 关闭开关 → 立即 RequestFullRefresh 复原 RDP 帧流。
//
// 纯逻辑 + 接口注入：ICaptureFrameReader 由 CaptureFrameChannel 实现，
// ICaptureControlPlane 由 RdpControlPlane 包装 RdpEmbeddedClient。两者都不碰 XAML，
// 因此 _smoke 可以用 fake 完整验证状态机与副作用顺序。
using System;
using Yomori.Models;

namespace Yomori.Services
{
    /// <summary>共享内存读帧契约（CaptureFrameChannel 已实现这两个方法）。</summary>
    public interface ICaptureFrameReader
    {
        bool TryReadLatest(out FrameHeader header, out byte[] pixels, out ulong seq);
        void Acknowledge(ulong seq);
    }

    /// <summary>控制面契约：false = 停 RDP 图形输出，true = 复原并补全屏刷新。</summary>
    public interface ICaptureControlPlane
    {
        bool SetDisplayUpdates(bool allow);
    }

    /// <summary>把 RdpEmbeddedClient 的两个出口适配成控制面契约。</summary>
    public sealed class RdpControlPlane : ICaptureControlPlane
    {
        private readonly RdpEmbeddedClient _client;

        public RdpControlPlane(RdpEmbeddedClient client)
            => _client = client ?? throw new ArgumentNullException(nameof(client));

        public bool SetDisplayUpdates(bool allow)
            => allow ? _client.RequestFullRefresh() : _client.SetDisplayUpdates(false);
    }

    /// <summary>一拍的帧源决定。UseCapture=false 时一律走原有 RDP 帧路径。</summary>
    public readonly record struct CaptureFrameDecision(
        bool UseCapture,
        byte[]? Pixels,
        uint Width,
        uint Height,
        uint Stride,
        ulong Seq);

    /// <summary>
    /// 按 channelId 隔离的帧源选择器。Tick 必须由 UI 线程（OnPresentTick）调用；
    /// 呈现成功后调用 Acknowledge 推进共享内存 ack。
    /// </summary>
    public sealed class CaptureViewPort
    {
        private readonly ICaptureFrameReader _reader;
        private readonly ICaptureControlPlane _control;
        private readonly CapturePathCoordinator _coordinator;
        private readonly int _maxNoFrameStreak;
        private int _noFrameStreak;
        private bool _suppressed;

        public bool Enabled { get; private set; }
        public bool IsSuppressed => _suppressed;
        public GuardState State => _coordinator.State;
        public int NoFrameStreak => _noFrameStreak;

        public CaptureViewPort(string channelId, ICaptureFrameReader reader,
                               ICaptureControlPlane control, GuardConfig? cfg = null,
                               int maxNoFrameStreak = 30)
        {
            _reader = reader ?? throw new ArgumentNullException(nameof(reader));
            _control = control ?? throw new ArgumentNullException(nameof(control));
            if (maxNoFrameStreak < 1) throw new ArgumentOutOfRangeException(nameof(maxNoFrameStreak));
            _maxNoFrameStreak = maxNoFrameStreak;
            _coordinator = new CapturePathCoordinator(channelId, cfg);
            _coordinator.Disable(); // NC5：构造即关闭
        }

        /// <summary>开关（勾选即启用、取消即还原）。关闭时强制复原 RDP 帧流。</summary>
        public void SetEnabled(bool enabled, long nowMs = 0)
        {
            if (enabled == Enabled) return;
            Enabled = enabled;
            if (enabled)
            {
                _coordinator.Enable(nowMs);
                _noFrameStreak = 0;
                return;
            }
            ForceFallback(nowMs);
        }

        /// <summary>UI 线程每拍调用。rdpSample 为当前 RDP 帧的观测值，用于双源比对闸门。</summary>
        public CaptureFrameDecision Tick(long nowMs, in FrameSample rdpSample)
        {
            if (!Enabled) return UseRdp();

            if (!_reader.TryReadLatest(out FrameHeader header, out byte[] pixels, out ulong seq)
                || pixels.Length == 0)
            {
                _noFrameStreak++;
                // 已经停了 RDP 帧流却迟迟没有抓帧：必须复原，否则画面永久静止。
                if (_suppressed && _noFrameStreak >= _maxNoFrameStreak) ForceFallback(nowMs);
                return UseRdp();
            }
            _noFrameStreak = 0;

            FrameSample dd = MakeSample(header, pixels, nowMs);
            ControlPlaneAction action = _coordinator.State == GuardState.RdpFrame
                ? _coordinator.TryEnable(dd, rdpSample, nowMs)
                : _coordinator.OnFrame(dd);

            if (action == ControlPlaneAction.SuppressDisplayUpdates)
            {
                // 禁止先切后验：suppress 没成功就保持 RDP 帧，绝不进入无帧空窗。
                if (!_control.SetDisplayUpdates(false))
                {
                    _coordinator.Enable(nowMs);
                    return UseRdp();
                }
                _suppressed = true;
            }
            else if (action == ControlPlaneAction.AllowDisplayUpdatesAndRefresh)
            {
                _control.SetDisplayUpdates(true);
                _suppressed = false;
            }

            if (_suppressed && _coordinator.State == GuardState.CaptureActive)
                return new CaptureFrameDecision(true, pixels, header.Width, header.Height,
                                                header.Stride, seq);
            return UseRdp();
        }

        /// <summary>呈现成功后确认该帧，避免消费端重复呈现。</summary>
        public void Acknowledge(ulong seq) => _reader.Acknowledge(seq);

        /// <summary>逃生开关 / 连接异常：无条件复原 RDP 帧流。</summary>
        public void ForceFallback(long nowMs = 0)
        {
            if (_suppressed) _control.SetDisplayUpdates(true);
            _suppressed = false;
            _noFrameStreak = 0;
            _coordinator.Enable(nowMs);
        }

        private static CaptureFrameDecision UseRdp()
            => new(false, null, 0, 0, 0, 0);

        private static FrameSample MakeSample(in FrameHeader header, byte[] pixels, long nowMs)
        {
            // 帧已由采集端做过亮度判据；这里只补一份观测值给 Guard 的黑帧/静止闸门。
            // ChangeRatio 固定 > 阈值，避免在没有上一帧时误判「可疑静止」。
            LumaStats stats = LumaAnalyzer.Compute(pixels, (int)header.Width, (int)header.Height,
                                                   (int)header.Stride);
            return new FrameSample(nowMs, stats.MeanLuma, stats.ZeroRatio, 1.0, true);
        }
    }
}
