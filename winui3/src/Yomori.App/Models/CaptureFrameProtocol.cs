// 抓帧数据面的帧契约与共享内存环形布局（S1 地基，计划书 4.4 / 4.5 / 4.8）。
// 纯 .NET 值类型与算术：不引 WinUI / WinRT / WindowsAppSDK，不做 P/Invoke，
// 不碰文件与线程 —— _smoke 直接链接本文件做布局与流控断言。
// 形状参考 ycn_rdp.h:239-268 的 YcnDirtyFrameInfo / ycn_rdp_dirty_rect。
using System;
using System.Runtime.InteropServices;

namespace Yomori.Models;

/// <summary>帧类型（计划书 4.4：全帧 / 脏矩形 / 光标 / 尺寸变更）。</summary>
public enum FrameKind : uint
{
    Empty = 0,
    Full = 1,
    DirtyRects = 2,
    Cursor = 3,
    SizeChanged = 4,
}

/// <summary>帧标志位。受保护内容被填黑时置位，直接对接黑帧看门狗。</summary>
[Flags]
public enum FrameFlags : uint
{
    None = 0,
    CursorAttached = 1 << 0,
    ProtectedContentMaskedOut = 1 << 1,
}

/// <summary>脏矩形（半开区间），与 ycn_rdp_dirty_rect 同形状：x/y 有符号，w/h 无符号。</summary>
[StructLayout(LayoutKind.Sequential, Pack = 8)]
public readonly struct FrameRect
{
    public readonly int X;
    public readonly int Y;
    public readonly uint W;
    public readonly uint H;

    public FrameRect(int x, int y, uint w, uint h)
    {
        X = x;
        Y = y;
        W = w;
        H = h;
    }

    public ulong PixelCount => (ulong)W * H;

    public bool IsWithin(uint width, uint height)
        => X >= 0 && Y >= 0 && W > 0 && H > 0
           && (ulong)X + W <= width
           && (ulong)Y + H <= height;
}

/// <summary>
/// 共享内存里每槽开头的帧头。固定 64 字节、显式 Pack=8，
/// 采集端与主控端按同一布局读写；Magic / Version 用于拒绝错位或旧版缓冲。
/// Seq 从 1 开始单调递增（0 保留给「无帧」）。
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 8)]
public readonly struct FrameHeader
{
    public const uint Magic = 0x59434D46u; // "YCMF"
    public const uint ProtocolVersion = 1u;
    public const int Size = 64;

    private const uint KnownFlagsMask = 0x3u;

    public readonly uint MagicValue;
    public readonly uint Version;
    public readonly uint HeaderSize;
    public readonly FrameKind Kind;
    public readonly ulong Seq;
    public readonly long TimestampMs;
    public readonly uint Width;
    public readonly uint Height;
    public readonly uint Stride;
    public readonly uint PayloadBytes;
    public readonly uint RectCount;
    public readonly FrameFlags Flags;
    public readonly uint Reserved0;
    public readonly uint Reserved1;

    /// <summary>
    /// 公开构造以便接收端从共享内存读出的原始字段构造并校验（IsValid 会逐项拒绝错位/旧版头）。
    /// 正常产出请用 Create。
    /// </summary>
    public FrameHeader(uint magic, uint version, uint headerSize, FrameKind kind,
                        ulong seq, long timestampMs, uint width, uint height, uint stride,
                        uint payloadBytes, uint rectCount, FrameFlags flags)
    {
        MagicValue = magic;
        Version = version;
        HeaderSize = headerSize;
        Kind = kind;
        Seq = seq;
        TimestampMs = timestampMs;
        Width = width;
        Height = height;
        Stride = stride;
        PayloadBytes = payloadBytes;
        RectCount = rectCount;
        Flags = flags;
        Reserved0 = 0;
        Reserved1 = 0;
    }

    public static FrameHeader Create(ulong seq, long timestampMs, uint width, uint height,
                                     uint stride, FrameKind kind, uint payloadBytes,
                                     uint rectCount = 0, FrameFlags flags = FrameFlags.None)
        => new(Magic, ProtocolVersion, (uint)Size, kind, seq, timestampMs,
               width, height, stride, payloadBytes, rectCount, flags);

    /// <summary>只校验帧头自身的自洽性；跨槽容量校验见 SharedFrameRingLayout.ValidatePayload。</summary>
    public bool IsValid(out string reason)
    {
        if (MagicValue != Magic) { reason = "Magic 不匹配（缓冲错位或非本协议共享内存）"; return false; }
        if (Version != ProtocolVersion) { reason = "协议版本不匹配"; return false; }
        if (HeaderSize != Size) { reason = "HeaderSize 与布局不符"; return false; }
        if (Kind is not (FrameKind.Empty or FrameKind.Full or FrameKind.DirtyRects
                         or FrameKind.Cursor or FrameKind.SizeChanged))
        { reason = "未知帧类型"; return false; }
        if (Seq == 0) { reason = "Seq 从 1 开始"; return false; }
        if (Width == 0 || Height == 0) { reason = "尺寸为 0"; return false; }
        if (Stride < (ulong)Width * 4) { reason = "Stride 小于 Width*4（BGRA32 放不下）"; return false; }
        if (PayloadBytes > (ulong)Stride * Height) { reason = "PayloadBytes 超过像素缓冲容量"; return false; }
        if (Kind == FrameKind.DirtyRects && RectCount == 0) { reason = "脏矩形帧却没有矩形"; return false; }
        if (Kind != FrameKind.DirtyRects && RectCount != 0) { reason = "非脏矩形帧却带矩形表"; return false; }
        if (RectCount > SharedFrameRingLayout.MaxRectsPerFrame) { reason = "矩形数超出上限"; return false; }
        if (((uint)Flags & ~KnownFlagsMask) != 0) { reason = "未知标志位"; return false; }
        reason = "";
        return true;
    }

    public void Validate()
    {
        if (!IsValid(out string reason)) throw new InvalidOperationException(reason);
    }
}

/// <summary>
/// 一段共享内存的布局：全局头 + N 个等长槽；每槽 = 帧头 + 矩形表 + BGRA32 像素。
/// 全部按 64 字节对齐，槽偏移为纯算术（真正的 CreateFileMapping / ACL 属 S1 后续真机工作）。
/// </summary>
public sealed class SharedFrameRingLayout
{
    public const int GlobalHeaderBytes = 64;
    public const int MaxRectsPerFrame = 256;
    public const int Alignment = 64;
    public const int DefaultSlotCount = 3;
    public const int MaxSlotCount = 8;
    public const uint MaxWidth = 7680;
    public const uint MaxHeight = 4320;

    public static int FrameRectBytes => Marshal.SizeOf<FrameRect>();
    public static int RectTableBytes => MaxRectsPerFrame * FrameRectBytes;

    public uint Width { get; }
    public uint Height { get; }
    public uint Stride { get; }
    public int SlotCount { get; }
    public long PixelBytes { get; }
    public long SlotBytes { get; }
    public long TotalBytes { get; }

    public SharedFrameRingLayout(uint width, uint height,
                                 int slotCount = DefaultSlotCount, uint stride = 0)
    {
        if (width == 0 || width > MaxWidth) throw new ArgumentOutOfRangeException(nameof(width));
        if (height == 0 || height > MaxHeight) throw new ArgumentOutOfRangeException(nameof(height));
        if (slotCount < 1 || slotCount > MaxSlotCount) throw new ArgumentOutOfRangeException(nameof(slotCount));

        uint computedStride = stride != 0 ? stride : checked(width * 4);
        if (computedStride < (ulong)width * 4)
            throw new ArgumentException("Stride 小于 Width*4", nameof(stride));

        Width = width;
        Height = height;
        Stride = computedStride;
        SlotCount = slotCount;
        PixelBytes = (long)Stride * height;
        long rawSlot = FrameHeader.Size + RectTableBytes + PixelBytes;
        SlotBytes = Align(rawSlot, Alignment);
        TotalBytes = checked(GlobalHeaderBytes + SlotBytes * slotCount);
    }

    public long SlotOffset(int index)
    {
        if (index < 0 || index >= SlotCount) throw new ArgumentOutOfRangeException(nameof(index));
        return GlobalHeaderBytes + SlotBytes * index;
    }

    public long HeaderOffset(int index) => SlotOffset(index);
    public long RectTableOffset(int index) => SlotOffset(index) + FrameHeader.Size;
    public long PixelOffset(int index) => SlotOffset(index) + FrameHeader.Size + RectTableBytes;

    public bool ValidatePayload(uint payloadBytes, uint rectCount)
        => payloadBytes <= PixelBytes && rectCount <= MaxRectsPerFrame;

    private static long Align(long value, int alignment)
        => checked(((value + alignment - 1) / alignment) * alignment);
}

/// <summary>
/// seq / ack 背压与丢帧判据。seq 从 1 单调递增；lastConsumedSeq 为已确认的最新 seq（初始 0）。
/// 丢帧策略：消费者落后到环形被追上时，生产者直接丢掉新帧，不覆盖未确认槽。
/// </summary>
public static class FrameFlowPolicy
{
    public static int SlotForSeq(ulong seq, int slotCount)
    {
        if (seq == 0) throw new ArgumentOutOfRangeException(nameof(seq), "seq 从 1 开始");
        if (slotCount < 1) throw new ArgumentOutOfRangeException(nameof(slotCount));
        return (int)((seq - 1) % (ulong)slotCount);
    }

    /// <summary>已写未确认的帧数（nextSeq 是「下一个要写」的 seq）。</summary>
    public static ulong InFlight(ulong nextSeq, ulong lastConsumedSeq)
    {
        ulong latest = nextSeq == 0 ? 0 : nextSeq - 1;
        return latest > lastConsumedSeq ? latest - lastConsumedSeq : 0;
    }

    /// <summary>环形已满：生产者必须丢掉这一帧，不写共享内存。</summary>
    public static bool ShouldDrop(ulong nextSeq, ulong lastConsumedSeq, int slotCount)
        => InFlight(nextSeq, lastConsumedSeq) >= (ulong)slotCount;

    /// <summary>消费者此刻应读的最新 seq；false = 没有新帧。</summary>
    public static bool TryLatestReadable(ulong nextSeq, ulong lastConsumedSeq, out ulong seq)
    {
        seq = 0;
        if (nextSeq <= 1) return false;
        ulong latest = nextSeq - 1;
        if (latest <= lastConsumedSeq) return false;
        seq = latest;
        return true;
    }

    /// <summary>指定 seq 是否仍在可读窗口内（未被环形覆盖、且尚未确认）。</summary>
    public static bool IsReadable(ulong seq, ulong nextSeq, ulong lastConsumedSeq, int slotCount)
        => seq > lastConsumedSeq
           && nextSeq > 1
           && seq <= nextSeq - 1
           && (nextSeq - 1) - seq < (ulong)slotCount;
}

/// <summary>
/// S0 实测（results/S0-RESULT.md 8.1）：DuplicateOutput 后前 2 帧为全 0 空帧。
/// 必须丢弃后再参与黑帧判定与上屏，否则首帧就会被当成黑帧故障。
/// </summary>
public static class WarmupPolicy
{
    public const int FramesToDiscard = 2;

    public static bool ShouldDiscard(int zeroBasedFrameIndex)
        => zeroBasedFrameIndex >= 0 && zeroBasedFrameIndex < FramesToDiscard;
}
