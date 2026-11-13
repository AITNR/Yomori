// 抓帧数据面的共享内存帧通道（S1，计划书 4.4 / 4.5 / 4.8）。
// 纯 .NET（System.IO.MemoryMappedFiles），不引 WinUI / WinRT，_smoke 可链入做单进程回环自测。
//
// ⚠️ 本组件只负责「一段命名映射内的槽读写」，不越界做线程同步 / 进程间通知：
//    - 布局与帧头合法性由 Models/SharedFrameRingLayout、FrameHeader 负责；
//    - seq / ack 背压由 Models/FrameFlowPolicy 负责；
//    - 跨 Windows 会话的命名空间（Global\）与 ACL 授权（NC3）由创建方按真机环境决定，
//      本组件**不硬编码授权策略**，映射名原样传给 MemoryMappedFile。
//    - 单进程回环自测通过注入同一个 MemoryMappedFile 实例来模拟两端。
using System;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using Yomori.Models;

namespace Yomori.Services
{
    /// <summary>一段共享内存环形缓冲的读写端。线程不安全，调用方自行串行化。</summary>
    public sealed class CaptureFrameChannel : IDisposable, ICaptureFrameReader
    {
        private readonly MemoryMappedFile _mmf;
        private readonly MemoryMappedViewStream _stream;
        private readonly SharedFrameRingLayout _layout;
        private readonly bool _ownsMmf;

        public SharedFrameRingLayout Layout => _layout;
        public string? MapName { get; }

        /// <summary>
        /// 注入已有映射（自测用匿名映射 / 调用方自建带 ACL 的映射）。
        /// CreateViewStream(0, TotalBytes) 本身就是容量校验：映射小于布局会抛。
        /// </summary>
        public CaptureFrameChannel(MemoryMappedFile mmf, SharedFrameRingLayout layout,
                                   string? mapName = null, bool ownsMmf = false)
        {
            _mmf = mmf ?? throw new ArgumentNullException(nameof(mmf));
            _layout = layout ?? throw new ArgumentNullException(nameof(layout));
            MapName = mapName;
            _ownsMmf = ownsMmf;
            _stream = _mmf.CreateViewStream(0, _layout.TotalBytes, MemoryMappedFileAccess.ReadWrite);
        }

        /// <summary>创建生产端命名映射。映射名如需跨会话请由调用方带 Global\ 前缀并配置 ACL。</summary>
        public static CaptureFrameChannel CreateProducer(string mapName, SharedFrameRingLayout layout)
        {
            if (string.IsNullOrWhiteSpace(mapName)) throw new ArgumentException("映射名不能为空", nameof(mapName));
            MemoryMappedFile mmf = MemoryMappedFile.CreateNew(mapName, layout.TotalBytes);
            return new CaptureFrameChannel(mmf, layout, mapName, ownsMmf: true);
        }

        /// <summary>打开消费端（须与生产端使用完全相同的布局，否则容量/偏移校验会失败）。</summary>
        public static CaptureFrameChannel OpenConsumer(string mapName, SharedFrameRingLayout layout)
        {
            if (string.IsNullOrWhiteSpace(mapName)) throw new ArgumentException("映射名不能为空", nameof(mapName));
            MemoryMappedFile mmf = MemoryMappedFile.OpenExisting(mapName, MemoryMappedFileRights.ReadWrite);
            return new CaptureFrameChannel(mmf, layout, mapName, ownsMmf: true);
        }

        /// <summary>写共享内存 offset 0 的全局头（生产端初始化 / 每次发布时更新）。</summary>
        public void WriteRingHeader(in FrameRingHeader header)
        {
            header.Validate();
            Span<byte> buf = stackalloc byte[FrameRingHeader.Size];
            MemoryMarshal.Write(buf, in header);
            _stream.Position = 0;
            _stream.Write(buf);
        }

        /// <summary>读全局头；未初始化或错位时返回 false。</summary>
        public bool TryReadRingHeader(out FrameRingHeader header)
        {
            Span<byte> buf = stackalloc byte[FrameRingHeader.Size];
            _stream.Position = 0;
            _stream.ReadExactly(buf);
            header = MemoryMarshal.Read<FrameRingHeader>(buf);
            return header.IsValid(out _);
        }

        /// <summary>
        /// 生产端发布一帧：按 seq 选槽写入，并推进全局头 NextSeq = seq + 1。
        /// 全局头尚未初始化时按当前布局自动创建。返回新的 NextSeq。
        /// </summary>
        public ulong PublishFrame(in FrameHeader frame, ReadOnlySpan<FrameRect> rects,
                                  ReadOnlySpan<byte> pixels, long producerTickMs = 0)
        {
            frame.Validate();
            int slot = FrameFlowPolicy.SlotForSeq(frame.Seq, _layout.SlotCount);
            WriteFrame(slot, frame, rects, pixels);

            if (!TryReadRingHeader(out FrameRingHeader ring))
                ring = FrameRingHeader.Create(_layout.Width, _layout.Height, _layout.Stride,
                                              _layout.SlotCount, nextSeq: 1);
            ring.NextSeq = frame.Seq + 1;
            ring.ProducerTickMs = producerTickMs;
            ring.Flags |= FrameRingHeader.ProducerOnline;
            WriteRingHeader(ring);
            return ring.NextSeq;
        }

        /// <summary>
        /// 消费端读最新已发布且未确认的帧：依据全局头 NextSeq-1 定位槽，
        /// 并核对帧头 Seq 与之一致（槽若已被更新的帧覆盖则拒绝，不显示旧画面）。
        /// </summary>
        public bool TryReadLatest(out FrameHeader frame, out byte[] pixels, out ulong seq)
        {
            frame = default;
            pixels = Array.Empty<byte>();
            seq = 0;
            if (!TryReadRingHeader(out FrameRingHeader ring)) return false;
            if (ring.SlotCount != _layout.SlotCount ||
                ring.Width != _layout.Width || ring.Height != _layout.Height ||
                ring.Stride != _layout.Stride)
                return false;
            if (!FrameFlowPolicy.TryLatestReadable(ring.NextSeq, ring.LastAckedSeq, out ulong latest))
                return false;
            int slot = FrameFlowPolicy.SlotForSeq(latest, _layout.SlotCount);
            if (!TryReadFrame(slot, out FrameHeader got, out byte[] gotPixels)) return false;
            if (got.Seq != latest) return false;
            frame = got;
            pixels = gotPixels;
            seq = latest;
            return true;
        }

        /// <summary>消费端确认已呈现的帧；返回更新后的 LastAckedSeq。</summary>
        public ulong Acknowledge(ulong seq)
        {
            if (!TryReadRingHeader(out FrameRingHeader ring))
                throw new InvalidOperationException("全局头未初始化或非法");
            if (seq <= ring.LastAckedSeq || seq >= ring.NextSeq)
                throw new ArgumentOutOfRangeException(nameof(seq),
                    "seq 必须在 (LastAckedSeq, NextSeq) 区间内");
            ring.LastAckedSeq = seq;
            WriteRingHeader(ring);
            return seq;
        }

        // 接口契约为 void；公开方法保留返回值供现有断言与调用方使用。
        void ICaptureFrameReader.Acknowledge(ulong seq) => Acknowledge(seq);

        /// <summary>
        /// 写一帧到指定槽：帧头 → 矩形表 → BGRA32 像素。
        /// pixels 长度必须等于 header.PayloadBytes，rects 数量必须等于 header.RectCount。
        /// </summary>
        public void WriteFrame(int slot, in FrameHeader header,
                               ReadOnlySpan<FrameRect> rects, ReadOnlySpan<byte> pixels)
        {
            header.Validate();
            long headerOffset = _layout.HeaderOffset(slot); // 内含槽索引越界检查
            if (!_layout.ValidatePayload(header.PayloadBytes, (uint)rects.Length))
                throw new ArgumentException("PayloadBytes / 矩形数超出槽容量");
            if (pixels.Length != header.PayloadBytes)
                throw new ArgumentException("pixels 长度与 PayloadBytes 不一致", nameof(pixels));
            if (rects.Length != header.RectCount)
                throw new ArgumentException("rects 数量与 RectCount 不一致", nameof(rects));

            Span<byte> headerBytes = stackalloc byte[FrameHeader.Size];
            MemoryMarshal.Write(headerBytes, in header);
            _stream.Position = headerOffset;
            _stream.Write(headerBytes);

            if (rects.Length > 0)
            {
                int rectBytes = SharedFrameRingLayout.FrameRectBytes;
                Span<byte> one = stackalloc byte[rectBytes];
                _stream.Position = _layout.RectTableOffset(slot);
                for (int i = 0; i < rects.Length; i++)
                {
                    FrameRect r = rects[i];
                    MemoryMarshal.Write(one, in r);
                    _stream.Write(one);
                }
            }

            if (pixels.Length > 0)
            {
                _stream.Position = _layout.PixelOffset(slot);
                _stream.Write(pixels);
            }
        }

        /// <summary>
        /// 读一帧。帧头非法（错位 / 旧版本 / 容量不符）时返回 false，避免把未初始化槽当成画面。
        /// </summary>
        public bool TryReadFrame(int slot, out FrameHeader header, out byte[] pixels)
        {
            Span<byte> headerBytes = stackalloc byte[FrameHeader.Size];
            _stream.Position = _layout.HeaderOffset(slot);
            _stream.ReadExactly(headerBytes);
            header = MemoryMarshal.Read<FrameHeader>(headerBytes);
            if (!header.IsValid(out _) || !_layout.ValidatePayload(header.PayloadBytes, header.RectCount))
            {
                pixels = Array.Empty<byte>();
                return false;
            }

            pixels = new byte[header.PayloadBytes];
            if (pixels.Length > 0)
            {
                _stream.Position = _layout.PixelOffset(slot);
                _stream.ReadExactly(pixels);
            }
            return true;
        }

        /// <summary>读回指定槽的矩形表（供脏矩形增量应用）。</summary>
        public FrameRect[] ReadRects(int slot, uint count)
        {
            if (count > SharedFrameRingLayout.MaxRectsPerFrame)
                throw new ArgumentOutOfRangeException(nameof(count));
            long offset = _layout.RectTableOffset(slot); // 内含槽索引越界检查
            var result = new FrameRect[count];
            int rectBytes = SharedFrameRingLayout.FrameRectBytes;
            Span<byte> one = stackalloc byte[rectBytes];
            _stream.Position = offset;
            for (uint i = 0; i < count; i++)
            {
                _stream.ReadExactly(one);
                result[i] = MemoryMarshal.Read<FrameRect>(one);
            }
            return result;
        }

        public void Dispose()
        {
            _stream.Dispose();
            if (_ownsMmf) _mmf.Dispose();
        }
    }
}
