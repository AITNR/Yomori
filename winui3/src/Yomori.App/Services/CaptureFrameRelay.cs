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

        public CaptureFrameRelay(string channelId, SharedFrameRingLayout layout,
                                 ICaptureFrameSource source, bool global = true)
        {
            ChannelId = channelId ?? throw new ArgumentNullException(nameof(channelId));
            Layout = layout ?? throw new ArgumentNullException(nameof(layout));
            MapName = CaptureChannelNaming.ForChannel(channelId, global);
            _mmf = MemoryMappedFile.CreateOrOpen(MapName, layout.TotalBytes);
            _channel = new CaptureFrameChannel(_mmf, layout, MapName);
            _producer = new CaptureFrameProducer(_channel, source);
        }

        /// <summary>NC5：构造后默认关闭，必须显式启用。</summary>
        public void SetEnabled(bool enabled) => _producer.Enable(enabled);

        /// <summary>由采集端常驻循环按节拍调用；返回本拍是否发布了帧。</summary>
        public ProducerTickResult Tick(long nowMs) => _producer.Tick(nowMs);

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _channel.Dispose();
            _mmf.Dispose();
        }
    }
}
