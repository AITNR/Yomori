// DD 抓帧通路的黑帧检测与「回退 RDP」状态机（S1 地基）。
// 纯数值逻辑、无 WinUI / WinRT 类型 —— _smoke 直接链接本文件做行为断言，
// 代理工程（Models\**\*.cs 链接复用）也能安全编译。
//
// 来源：S0 探针 _probe/S0/BlackFrameGuard/Guard.cs（49 条自测全过）。
// 阈值标定：2026-11-12 真机样本，见 _probe/S0/results/S0-RESULT.md 8.2
//   （CalibrateVerifier 回放 56 帧零误报、注入 N 帧检出 / N-1 帧不误触发）。
namespace Yomori.Models
{
    /// <summary>一帧的观测值。全部为纯数值，由采集端填充。</summary>
    public readonly record struct FrameSample(
        long TimestampMs,
        double MeanLuma,
        double ZeroRatio,
        double ChangeRatio,
        bool HadDirtyRect,
        bool ProtectedContentMaskedOut = false);

    public readonly record struct LumaStats(double MeanLuma, double ZeroRatio);

    public enum GuardState { RdpFrame, CapturePending, CaptureActive }

    public enum GuardAction
    {
        None,
        RequestRetryProbe,
        EnterCapturePending,
        EnterCaptureActive,
        FallbackToRdp
    }

    /// <summary>
    /// 阈值配置。默认值即 S0 真机标定值（勿凭感觉改）：
    /// 正常帧 meanLuma 57~126 / zeroRatio ≤0.0022；真黑帧 0 / 1.0；
    /// 静止帧 changeRatio ≤0.0013，大幅变化帧 ≥0.0528；前导空帧 1~2 帧。
    /// </summary>
    public sealed record GuardConfig
    {
        public const string CalibratedAt = "2026-11-12 S0";

        public int BlackStreakLimit { get; init; } = 3;
        public int StaticStreakLimit { get; init; } = 90;
        public double MeanLumaThreshold { get; init; } = 8.0;
        public double ZeroRatioThreshold { get; init; } = 0.90;
        public double ChangeRatioThreshold { get; init; } = 0.002;
        public long CooldownMs { get; init; } = 5_000;
        public long RetryProbeIntervalMs { get; init; } = 30_000;
        public int RetrySuccessLimit { get; init; } = 3;

        public void Validate()
        {
            if (BlackStreakLimit < 1) throw new System.ArgumentOutOfRangeException(nameof(BlackStreakLimit));
            if (StaticStreakLimit < 1) throw new System.ArgumentOutOfRangeException(nameof(StaticStreakLimit));
            if (RetrySuccessLimit < 1) throw new System.ArgumentOutOfRangeException(nameof(RetrySuccessLimit));
            if (RetryProbeIntervalMs < 1) throw new System.ArgumentOutOfRangeException(nameof(RetryProbeIntervalMs));
            if (CooldownMs < 0) throw new System.ArgumentOutOfRangeException(nameof(CooldownMs));
            if (MeanLumaThreshold < 0 || MeanLumaThreshold > 255) throw new System.ArgumentOutOfRangeException(nameof(MeanLumaThreshold));
            if (ZeroRatioThreshold is < 0 or > 1) throw new System.ArgumentOutOfRangeException(nameof(ZeroRatioThreshold));
            if (ChangeRatioThreshold is < 0 or > 1) throw new System.ArgumentOutOfRangeException(nameof(ChangeRatioThreshold));
        }
    }

    /// <summary>BGRA32 帧的亮度 / 全 0 统计与帧间变化率。采样步进避免逐像素全扫。</summary>
    public static class LumaAnalyzer
    {
        public static LumaStats Compute(System.ReadOnlySpan<byte> pixels, int width, int height, int stride, int sampleStep = 16)
        {
            ValidateShape(pixels, width, height, stride, sampleStep);
            long sum = 0, zero = 0, count = 0;
            for (int y = 0; y < height; y += sampleStep)
            {
                int row = y * stride;
                for (int x = 0; x < width; x += sampleStep)
                {
                    int i = row + x * 4;
                    byte b = pixels[i], g = pixels[i + 1], r = pixels[i + 2];
                    sum += (299 * r + 587 * g + 114 * b) / 1000;
                    if ((b | g | r) == 0) zero++;
                    count++;
                }
            }
            return count == 0 ? new LumaStats(0, 0) : new LumaStats((double)sum / count, (double)zero / count);
        }

        public static double ComputeChangeRatio(
            System.ReadOnlySpan<byte> cur, System.ReadOnlySpan<byte> prev,
            int width, int height, int stride, int sampleStep = 16, byte tolerance = 8)
        {
            ValidateShape(cur, width, height, stride, sampleStep);
            ValidateShape(prev, width, height, stride, sampleStep);
            long changed = 0, count = 0;
            for (int y = 0; y < height; y += sampleStep)
            {
                int row = y * stride;
                for (int x = 0; x < width; x += sampleStep)
                {
                    int i = row + x * 4;
                    if (System.Math.Abs(cur[i] - prev[i]) > tolerance ||
                        System.Math.Abs(cur[i + 1] - prev[i + 1]) > tolerance ||
                        System.Math.Abs(cur[i + 2] - prev[i + 2]) > tolerance)
                        changed++;
                    count++;
                }
            }
            return count == 0 ? 0 : (double)changed / count;
        }

        private static void ValidateShape(System.ReadOnlySpan<byte> pixels, int width, int height, int stride, int sampleStep)
        {
            if (width <= 0 || height <= 0) throw new System.ArgumentOutOfRangeException(nameof(width));
            if (sampleStep < 1) throw new System.ArgumentOutOfRangeException(nameof(sampleStep));
            if (stride < width * 4) throw new System.ArgumentException("stride 小于 width*4", nameof(stride));
            if (pixels.Length < stride * height) throw new System.ArgumentException("像素缓冲不足", nameof(pixels));
        }
    }

    /// <summary>
    /// 按 channelId 隔离的双路径状态机：RdpFrame ↔ CapturePending ↔ CaptureActive。
    /// 时间由调用方以 nowMs / TimestampMs 注入，无 IO、无线程、无 DateTime.Now。
    /// </summary>
    public sealed class BlackFrameGuard
    {
        private readonly GuardConfig _cfg;
        private int _blackStreak, _staticStreak, _retryGoodStreak;
        private long _cooldownUntilMs, _lastProbeAtMs;
        private bool _enabled = true;

        public string ChannelId { get; }
        public GuardState State { get; private set; } = GuardState.RdpFrame;
        public int BlackStreak => _blackStreak;
        public int StaticStreak => _staticStreak;
        public bool Enabled => _enabled;

        public BlackFrameGuard(string channelId, GuardConfig? cfg = null)
        {
            _cfg = cfg ?? new GuardConfig();
            _cfg.Validate();
            ChannelId = channelId ?? throw new System.ArgumentNullException(nameof(channelId));
        }

        public static bool IsBlackFrame(in FrameSample s, GuardConfig cfg)
            => s.ProtectedContentMaskedOut
               || (s.MeanLuma < cfg.MeanLumaThreshold && s.ZeroRatio > cfg.ZeroRatioThreshold);

        /// <summary>只有「收到脏矩形却内容不变」才算可疑静止，避免把正常静态桌面误判为黑帧。</summary>
        public static bool IsSuspiciousStatic(in FrameSample s, GuardConfig cfg)
            => s.HadDirtyRect && s.ChangeRatio < cfg.ChangeRatioThreshold;

        /// <summary>启动前双源比对闸门：DD 与 RDP 同时非黑才允许切换。</summary>
        public static bool PassDualSourceGate(in FrameSample dd, in FrameSample rdp, GuardConfig cfg)
            => !IsBlackFrame(dd, cfg) && !IsBlackFrame(rdp, cfg);

        /// <summary>DD 主路径运行时每帧调用。</summary>
        public GuardAction OnFrame(in FrameSample s)
        {
            if (!_enabled) return GuardAction.None;
            switch (State)
            {
                case GuardState.CaptureActive:
                {
                    if (s.ProtectedContentMaskedOut) return Fallback(s.TimestampMs);
                    bool black = IsBlackFrame(s, _cfg);
                    bool stat = IsSuspiciousStatic(s, _cfg);
                    if (!black && !stat)
                    {
                        _blackStreak = 0;
                        _staticStreak = 0;
                        return GuardAction.None;
                    }
                    _blackStreak = black ? _blackStreak + 1 : 0;
                    _staticStreak = stat ? _staticStreak + 1 : 0;
                    if (_blackStreak >= _cfg.BlackStreakLimit || _staticStreak >= _cfg.StaticStreakLimit)
                        return Fallback(s.TimestampMs);
                    return GuardAction.None;
                }
                case GuardState.CapturePending:
                    if (IsBlackFrame(s, _cfg)) return Fallback(s.TimestampMs);
                    State = GuardState.CaptureActive;
                    _blackStreak = _staticStreak = 0;
                    return GuardAction.EnterCaptureActive;
                default:
                    return GuardAction.None;
            }
        }

        /// <summary>驱动冷却期与后台低频重试探测。</summary>
        public GuardAction OnTick(long nowMs)
        {
            if (!_enabled || State != GuardState.RdpFrame) return GuardAction.None;
            if (nowMs < _cooldownUntilMs) return GuardAction.None;
            if (nowMs - _lastProbeAtMs < _cfg.RetryProbeIntervalMs) return GuardAction.None;
            _lastProbeAtMs = nowMs;
            return GuardAction.RequestRetryProbe;
        }

        /// <summary>送入一次后台重试探测帧的结果。</summary>
        public GuardAction OnRetryProbe(in FrameSample s)
        {
            if (!_enabled || State != GuardState.RdpFrame) return GuardAction.None;
            if (IsBlackFrame(s, _cfg))
            {
                _retryGoodStreak = 0;
                return GuardAction.None;
            }
            _retryGoodStreak++;
            if (_retryGoodStreak < _cfg.RetrySuccessLimit) return GuardAction.None;
            _retryGoodStreak = 0;
            State = GuardState.CapturePending;
            return GuardAction.EnterCapturePending;
        }

        /// <summary>配置启用 + 双源比对通过后，进入 CAPTURE_PENDING。</summary>
        public bool TryEnableCapture(long nowMs)
        {
            if (!_enabled || State != GuardState.RdpFrame) return false;
            State = GuardState.CapturePending;
            _blackStreak = _staticStreak = _retryGoodStreak = 0;
            _lastProbeAtMs = nowMs;
            return true;
        }

        /// <summary>逃生开关 / 连接异常：立即回到 RDP_FRAME 并清零所有计数。</summary>
        public void Disable()
        {
            _enabled = false;
            State = GuardState.RdpFrame;
            _blackStreak = _staticStreak = _retryGoodStreak = 0;
            _cooldownUntilMs = long.MaxValue;
        }

        public void Enable(long nowMs)
        {
            _enabled = true;
            State = GuardState.RdpFrame;
            _blackStreak = _staticStreak = _retryGoodStreak = 0;
            _cooldownUntilMs = nowMs;
            _lastProbeAtMs = nowMs;
        }

        private GuardAction Fallback(long nowMs)
        {
            State = GuardState.RdpFrame;
            _blackStreak = _staticStreak = _retryGoodStreak = 0;
            _cooldownUntilMs = nowMs + _cfg.CooldownMs;
            // 冷却结束时刻才作为「上次探测」基准：冷却期内一律不探测，
            // 冷却结束后仍需再等一个完整 RetryProbeIntervalMs 才第一次探测。
            _lastProbeAtMs = _cooldownUntilMs;
            return GuardAction.FallbackToRdp;
        }
    }
}
