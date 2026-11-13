// S1 第八批：主控侧「延迟打开」的共享内存读帧端。
//
// 为什么需要它：CaptureViewPort 构造时目标映射很可能还不存在（Agent 未启动、
// Global\ 未授权、Agent 重启中、尺寸变更换了新映射名）。若直接 OpenExisting
// 抛异常，会让连接流程崩在 UI 线程；若因此不发帧，ViewPort 会保持 RDP 帧流，
// 画面不黑 —— 这正是 NC3 / NC5 要的安全网。
//
// 纪律：
//   · 映射不存在 / 读失败一律返回「无帧」，不抛、不掀进程；
//   · 打开失败按 retryIntervalMs 低频重试，不在每拍 60Hz 里狂试；
//   · Retarget 用于尺寸或映射名变更，旧句柄立即释放；
//   · 读过程中映射被销毁（Agent 重启）→ 丢弃句柄，下一轮重新打开。
using System;
using Yomori.Models;

namespace Yomori.Services
{
    /// <summary>
    /// 实现 <see cref="ICaptureFrameReader"/>，把「打开命名映射」这件事延后到真的有帧时。
    /// 线程安全（内部锁），但读帧与 Retarget 不应并发到同一帧边界上。
    /// </summary>
    public sealed class DeferredCaptureFrameReader : ICaptureFrameReader, IDisposable
    {
        private readonly object _gate = new();
        private readonly long _retryIntervalMs;
        private readonly long _heartbeatTimeoutMs;
        private string _mapName = string.Empty;
        private SharedFrameRingLayout? _layout;
        private CaptureFrameChannel? _channel;
        private long _lastRetryMs = long.MinValue;
        private bool _disposed;

        public bool IsOpen { get { lock (_gate) { return _channel is not null; } } }
        public string MapName { get { lock (_gate) { return _mapName; } } }
        public string? LastError { get; private set; }

        public DeferredCaptureFrameReader(long retryIntervalMs = 1000,
                                         long heartbeatTimeoutMs = 5000)
        {
            if (retryIntervalMs < 0) throw new ArgumentOutOfRangeException(nameof(retryIntervalMs));
            if (heartbeatTimeoutMs < 1) throw new ArgumentOutOfRangeException(nameof(heartbeatTimeoutMs));
            _retryIntervalMs = retryIntervalMs;
            _heartbeatTimeoutMs = heartbeatTimeoutMs;
        }

        /// <summary>
        /// 采集端是否在线：映射打得开 + 全局头合法 + <c>ProducerOnline</c> + 心跳未超时。
        /// <para>与 <c>TryReadLatest</c> 的区别：后者在「环在线但没有新帧」时也返回 false
        /// （静态桌面），本方法用来把这种情况与「采集端真掉线」区分开，避免误撤销 suppress。</para>
        /// </summary>
        public bool IsHealthy(long nowMs)
        {
            lock (_gate)
            {
                if (_disposed || !EnsureOpenLocked(nowMs)) return false;
                try
                {
                    if (!_channel!.TryReadRingHeader(out FrameRingHeader ring)) return false;
                    if ((ring.Flags & FrameRingHeader.ProducerOnline) == 0) return false;
                    long age = nowMs - ring.ProducerTickMs;
                    return age >= 0 && age <= _heartbeatTimeoutMs;
                }
                catch
                {
                    CloseChannelLocked();
                    return false;
                }
            }
        }

        /// <summary>设定/切换目标映射。name 或 layout 变化都会丢弃旧句柄。</summary>
        public void Retarget(string mapName, SharedFrameRingLayout layout)
        {
            if (string.IsNullOrWhiteSpace(mapName)) throw new ArgumentException("映射名不能为空", nameof(mapName));
            if (layout is null) throw new ArgumentNullException(nameof(layout));
            lock (_gate)
            {
                if (_disposed) return;
                bool changed = !string.Equals(_mapName, mapName, StringComparison.Ordinal) ||
                               _layout is null || _layout.TotalBytes != layout.TotalBytes ||
                               _layout.Width != layout.Width || _layout.Height != layout.Height;
                _mapName = mapName;
                _layout = layout;
                if (changed) CloseChannelLocked();
                _lastRetryMs = long.MinValue; // 立刻允许重试一次
            }
        }

        /// <summary>丢弃当前映射（开关关闭 / 通道断开时调用，释放句柄）。</summary>
        public void Clear() { lock (_gate) { CloseChannelLocked(); } }

        public bool TryReadLatest(out FrameHeader header, out byte[] pixels, out ulong seq)
            => TryReadLatest(Environment.TickCount64, out header, out pixels, out seq);

        /// <summary>带时间戳的重载，便于 _smoke 精确控制重试节拍。</summary>
        public bool TryReadLatest(long nowMs, out FrameHeader header, out byte[] pixels, out ulong seq)
        {
            header = default;
            pixels = Array.Empty<byte>();
            seq = 0;
            lock (_gate)
            {
                if (_disposed || !EnsureOpenLocked(nowMs)) return false;
                try
                {
                    return _channel!.TryReadLatest(out header, out pixels, out seq);
                }
                catch (Exception ex)
                {
                    // 映射被销毁 / 容量变化 / 视图失效：丢弃句柄，下一轮重新打开。
                    LastError = ex.GetType().Name + ": " + ex.Message;
                    CloseChannelLocked();
                    return false;
                }
            }
        }

        public void Acknowledge(ulong seq)
        {
            lock (_gate)
            {
                if (_disposed || _channel is null) return;
                try { _channel.Acknowledge(seq); }
                catch (Exception ex) { LastError = ex.GetType().Name + ": " + ex.Message; CloseChannelLocked(); }
            }
        }

        private bool EnsureOpenLocked(long nowMs)
        {
            if (_channel is not null) return true;
            if (_layout is null || _mapName.Length == 0) return false;
            if (_lastRetryMs != long.MinValue && nowMs - _lastRetryMs < _retryIntervalMs) return false;
            _lastRetryMs = nowMs;
            try
            {
                _channel = CaptureFrameChannel.OpenConsumer(_mapName, _layout);
                LastError = null;
                return true;
            }
            catch (Exception ex)
            {
                LastError = ex.GetType().Name + ": " + ex.Message;
                return false;
            }
        }

        private void CloseChannelLocked()
        {
            try { _channel?.Dispose(); } catch { /* 释放失败不影响状态机 */ }
            _channel = null;
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
                CloseChannelLocked();
            }
        }
    }
}
