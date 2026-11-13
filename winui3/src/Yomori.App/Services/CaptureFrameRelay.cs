// S1 第七批：采集端工作器 —— 把帧源、共享内存通道、发布器串成一条常驻循环。
// 纯 .NET（只依赖 ICaptureFrameSource 接口），因此 _smoke 能用 fake 源做端到端回环。
// 真实 DD 源是 DxgiFrameSource（含 Vortice），由 Agent 进程注入。
using System;
using System.IO.MemoryMappedFiles;
using Yomori.Models;

namespace Yomori.Services
{
    public sealed class CaptureFrameRelay : IDisposable
    {
        private readonly MemoryMappedFile _mmf;
        private readonly CaptureFrameChannel _channel;
        private readonly CaptureFrameProducer _producer;
        private bool _disposed;

        public string ChannelId { get; }
        public string MapName { get; }
        public SharedFrameRingLayout Layout { get; }
        public bool Enabled => _producer.Enabled;
        public ulong PublishedCount => _producer.PublishedCount;

        /// <summary>
        /// NC5：构造后默认关闭，必须显式启用。
        /// <para>S1 第八批新增：mapFactory 供 Agent 侧注入带 ACL 的 Global\ 映射创建
        /// （CaptureSharedMemoryAcl.TryCreateOrOpen）；mapNameOverride 供「尺寸后缀」映射名
        /// 绕过默认的 ForChannel 命名。两者都不传时行为与第七批完全一致（_smoke 回环用）。</para>
        /// </summary>
        public CaptureFrameRelay(string channelId, SharedFrameRingLayout layout,
                                 ICaptureFrameSource source, bool global = true,
                                 Func<string, long, MemoryMappedFile?>? mapFactory = null,
                                 string? mapNameOverride = null)
        {
            ChannelId = channelId ?? throw new ArgumentNullException(nameof(channelId));
            Layout = layout ?? throw new ArgumentNullException(nameof(layout));
            MapName = mapNameOverride ?? CaptureChannelNaming.ForChannel(channelId, global);
            MemoryMappedFile mmf = mapFactory is not null
                ? mapFactory(MapName, layout.TotalBytes)
                  ?? throw new IOException("创建/打开共享内存映射失败：" + MapName)
                : MemoryMappedFile.CreateOrOpen(MapName, layout.TotalBytes);
            _mmf = mmf;
            _channel = new CaptureFrameChannel(_mmf, layout, MapName);
            _producer = new CaptureFrameProducer(_channel, source);
        }

        /// <summary>NC5：构造后默认关闭，必须显式启用。</summary>
        public void SetEnabled(bool enabled) => _producer.Enable(enabled);

        /// <summary>由采集端常驻循环按节拍调用；返回本拍是否发布了帧。</summary>
        public ProducerTickResult Tick(long nowMs) => _producer.Tick(nowMs);

        /// <summary>
        /// 生产端心跳：静态桌面下一帧都发布不了时也要让消费端知道「我还活着」。
        /// 由采集循环每轮（约 1 Hz）调用一次，不推进 seq。
        /// </summary>
        public void Heartbeat(long nowMs) => _channel.WriteHeartbeat(nowMs);

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _channel.Dispose();
            _mmf.Dispose();
        }
    }
}
