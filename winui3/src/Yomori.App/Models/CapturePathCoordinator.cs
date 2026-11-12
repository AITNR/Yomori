// 双路径状态机的控制面出口：把 BlackFrameGuard 的判定翻译成「要调哪个 RDP 出口」。
// 纯逻辑，不引用 RdpEmbeddedClient、不做 P/Invoke、不碰 UI 线程 —— 上层拿到动作后
// 在 UI 线程调用 SetDisplayUpdates(false) / RequestFullRefresh()（S1-3 已提供）。
// 依据：计划书 4.7 状态机、4.3.3 双源比对闸门、S0 实测前导 2 帧空帧。
using System;

namespace Yomori.Models;

/// <summary>控制面动作。只描述意图，不执行任何调用。</summary>
public enum ControlPlaneAction
{
    /// <summary>无需控制面动作。</summary>
    None,
    /// <summary>已进 CAPTURE_PENDING：仍由 RDP 帧流供画面，等首帧通过再停帧（禁止先切后验）。</summary>
    WaitForFirstCaptureFrame,
    /// <summary>进入 CAPTURE_ACTIVE：调用 SetDisplayUpdates(false)（Suppress Output PDU）。</summary>
    SuppressDisplayUpdates,
    /// <summary>回退：调用 RequestFullRefresh()（SuppressOutput(1) + 全屏 RefreshRect）。</summary>
    AllowDisplayUpdatesAndRefresh,
    /// <summary>明确留在 RDP 帧流（闸门未过 / 逃生后）。</summary>
    KeepRdpFrame,
}

/// <summary>
/// 按 channelId 隔离的双路径协调器。包装 BlackFrameGuard，输出控制面动作；
/// 时间由调用方注入，无 IO、无线程、无 DateTime.Now。
/// </summary>
public sealed class CapturePathCoordinator
{
    private readonly GuardConfig _cfg;
    private readonly BlackFrameGuard _guard;
    private readonly int _warmupFrames;
    private int _warmupSeen;

    public CapturePathCoordinator(string channelId, GuardConfig? cfg = null,
                                  int warmupFrames = WarmupPolicy.FramesToDiscard)
    {
        if (warmupFrames < 0) throw new ArgumentOutOfRangeException(nameof(warmupFrames));
        _cfg = cfg ?? new GuardConfig();
        _cfg.Validate();
        _guard = new BlackFrameGuard(channelId, _cfg);
        _warmupFrames = warmupFrames;
    }

    public string ChannelId => _guard.ChannelId;
    public GuardState State => _guard.State;
    public bool Enabled => _guard.Enabled;
    public int WarmupFrames => _warmupFrames;
    public int WarmupSeen => _warmupSeen;

    /// <summary>
    /// 启用前双源比对：DD 与 RDP 同时非黑才允许进 CAPTURE_PENDING。
    /// 此时 RDP 帧流仍开着，闸门不过就明确留在 RDP_FRAME，根本不切换。
    /// </summary>
    public ControlPlaneAction TryEnable(in FrameSample dd, in FrameSample rdp, long nowMs)
    {
        if (!_guard.Enabled || _guard.State != GuardState.RdpFrame) return ControlPlaneAction.None;
        if (!BlackFrameGuard.PassDualSourceGate(dd, rdp, _cfg)) return ControlPlaneAction.KeepRdpFrame;
        _warmupSeen = 0;
        return _guard.TryEnableCapture(nowMs)
            ? ControlPlaneAction.WaitForFirstCaptureFrame
            : ControlPlaneAction.None;
    }

    /// <summary>
    /// 每帧 DD 观测。预热期帧直接丢弃（不喂 guard），避免 DuplicateOutput 的
    /// 前 2 帧全 0 空帧触发误回退或误上屏。
    /// </summary>
    public ControlPlaneAction OnFrame(in FrameSample s)
    {
        if (!_guard.Enabled) return ControlPlaneAction.None;
        if (_warmupSeen < _warmupFrames)
        {
            _warmupSeen++;
            return ControlPlaneAction.None;
        }
        return Translate(_guard.OnFrame(s));
    }

    public ControlPlaneAction OnTick(long nowMs) => Translate(_guard.OnTick(nowMs));

    public ControlPlaneAction OnRetryProbe(in FrameSample s) => Translate(_guard.OnRetryProbe(s));

    /// <summary>逃生开关 / 连接异常：立即回 RDP；若此前已停帧，必须复原图形输出。</summary>
    public ControlPlaneAction Disable()
    {
        bool wasCapturing = _guard.State != GuardState.RdpFrame;
        _guard.Disable();
        _warmupSeen = 0;
        return wasCapturing
            ? ControlPlaneAction.AllowDisplayUpdatesAndRefresh
            : ControlPlaneAction.KeepRdpFrame;
    }

    public ControlPlaneAction Enable(long nowMs)
    {
        _guard.Enable(nowMs);
        _warmupSeen = 0;
        return ControlPlaneAction.KeepRdpFrame;
    }

    /// <summary>
    /// 核心映射：只有真正进入 CAPTURE_ACTIVE 才停 RDP 帧流；
    /// CAPTURE_PENDING 阶段仍显示 RDP 帧，避免「先切后验」的历史 3 秒白屏。
    /// </summary>
    public static ControlPlaneAction Translate(GuardAction action) => action switch
    {
        GuardAction.EnterCapturePending => ControlPlaneAction.WaitForFirstCaptureFrame,
        GuardAction.EnterCaptureActive => ControlPlaneAction.SuppressDisplayUpdates,
        GuardAction.FallbackToRdp => ControlPlaneAction.AllowDisplayUpdatesAndRefresh,
        _ => ControlPlaneAction.None,
    };
}
