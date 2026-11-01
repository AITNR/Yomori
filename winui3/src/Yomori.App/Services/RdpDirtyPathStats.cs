// 阶段 3：dirty 呈现路径的低开销诊断计数。
//
// 只在显式调用 dirty ABI 的代码路径上累加，因此后台看门狗（它走
// TryCopyFrame 的完整帧 API）不会被计入 dirty consumer。所有字段都用
// Interlocked 更新，5 秒日志在 UI 线程读取快照时不会撕裂计数。
// 这里不改变任何复制/上传/呈现行为，也不做逐帧日志。
using System.Threading;

namespace Yomori.Services
{
    internal readonly struct RdpDirtyPathSnapshot
    {
        public long Attempts { get; init; }
        public long Ok { get; init; }
        public long Fail { get; init; }
        public long Empty { get; init; }
        public long Boxes { get; init; }
        public long Full { get; init; }
        public long BoxesTotal { get; init; }
        public long BoxesMax { get; init; }
        public long BytesTotal { get; init; }
        public long AckOk { get; init; }
        public long AckFail { get; init; }
        public long AttachFail { get; init; }
        public long Unsupported { get; init; }
        public long NotAttached { get; init; }
        public long StaleToken { get; init; }
        public long StaleSeq { get; init; }
        public long NotConnected { get; init; }
        public long InvalidArg { get; init; }
        public long NoMemory { get; init; }
        public long SessionNotFound { get; init; }
        public long Other { get; init; }

        public string ToLogString()
            => $"累计 copy={Attempts} ok={Ok} fail={Fail} empty={Empty} boxes={Boxes} full={Full} " +
               $"rects={BoxesTotal} max_rects={BoxesMax} B={BytesTotal} " +
               $"ack_ok={AckOk} ack_fail={AckFail} attach_fail={AttachFail} unsupported={Unsupported} " +
               $"fail=[natt:{NotAttached} stale:{StaleToken} seq:{StaleSeq} nconn:{NotConnected} " +
               $"inval:{InvalidArg} nomem:{NoMemory} nsess:{SessionNotFound} other:{Other}]";
    }

    internal sealed class RdpDirtyPathStats
    {
        private long _attempts;
        private long _ok;
        private long _fail;
        private long _empty;
        private long _boxes;
        private long _full;
        private long _boxesTotal;
        private long _boxesMax;
        private long _bytesTotal;
        private long _ackOk;
        private long _ackFail;
        private long _attachFail;
        private long _unsupported;
        private long _notAttached;
        private long _staleToken;
        private long _staleSeq;
        private long _notConnected;
        private long _invalidArg;
        private long _noMemory;
        private long _sessionNotFound;
        private long _other;

        /// <summary>记录一次 ycn_rdp_dirty_copy_pending 的结果。</summary>
        public void RecordCopy(int rc, uint kind, uint rectCount, long bytes)
        {
            Interlocked.Increment(ref _attempts);
            if (rc == 0)
            {
                Interlocked.Increment(ref _ok);
                switch (kind)
                {
                    case RdpNativeInterop.DirtyKindEmpty:
                        Interlocked.Increment(ref _empty);
                        break;
                    case RdpNativeInterop.DirtyKindFull:
                        Interlocked.Increment(ref _full);
                        if (bytes > 0) Interlocked.Add(ref _bytesTotal, bytes);
                        break;
                    case RdpNativeInterop.DirtyKindBoxes:
                        Interlocked.Increment(ref _boxes);
                        if (rectCount > 0) Interlocked.Add(ref _boxesTotal, rectCount);
                        if (rectCount > 0) InterlockedMax(ref _boxesMax, rectCount);
                        if (bytes > 0) Interlocked.Add(ref _bytesTotal, bytes);
                        break;
                    default:
                        break;
                }
                return;
            }

            Interlocked.Increment(ref _fail);
            switch (rc)
            {
                case -20: Interlocked.Increment(ref _notAttached); break;
                case -21: Interlocked.Increment(ref _staleToken); break;
                case -22: Interlocked.Increment(ref _staleSeq); break;
                case -8: Interlocked.Increment(ref _notConnected); break;
                case -1: Interlocked.Increment(ref _invalidArg); break;
                case -2: Interlocked.Increment(ref _noMemory); break;
                case -4: Interlocked.Increment(ref _sessionNotFound); break;
                default: Interlocked.Increment(ref _other); break;
            }
        }

        public void RecordAck(bool ok)
        {
            if (ok) Interlocked.Increment(ref _ackOk);
            else Interlocked.Increment(ref _ackFail);
        }

        public void RecordAttach(bool ok)
        {
            if (!ok) Interlocked.Increment(ref _attachFail);
        }

        /// <summary>旧 DLL 缺 dirty 导出 / 探测降级：只计数，不抛异常。</summary>
        public void RecordUnsupported()
        {
            Interlocked.Increment(ref _unsupported);
        }

        public RdpDirtyPathSnapshot Snapshot() => new()
        {
            Attempts = Interlocked.Read(ref _attempts),
            Ok = Interlocked.Read(ref _ok),
            Fail = Interlocked.Read(ref _fail),
            Empty = Interlocked.Read(ref _empty),
            Boxes = Interlocked.Read(ref _boxes),
            Full = Interlocked.Read(ref _full),
            BoxesTotal = Interlocked.Read(ref _boxesTotal),
            BoxesMax = Interlocked.Read(ref _boxesMax),
            BytesTotal = Interlocked.Read(ref _bytesTotal),
            AckOk = Interlocked.Read(ref _ackOk),
            AckFail = Interlocked.Read(ref _ackFail),
            AttachFail = Interlocked.Read(ref _attachFail),
            Unsupported = Interlocked.Read(ref _unsupported),
            NotAttached = Interlocked.Read(ref _notAttached),
            StaleToken = Interlocked.Read(ref _staleToken),
            StaleSeq = Interlocked.Read(ref _staleSeq),
            NotConnected = Interlocked.Read(ref _notConnected),
            InvalidArg = Interlocked.Read(ref _invalidArg),
            NoMemory = Interlocked.Read(ref _noMemory),
            SessionNotFound = Interlocked.Read(ref _sessionNotFound),
            Other = Interlocked.Read(ref _other),
        };

        private static void InterlockedMax(ref long target, long value)
        {
            long current = Interlocked.Read(ref target);
            while (value > current)
            {
                long seen = Interlocked.CompareExchange(ref target, value, current);
                if (seen == current) return;
                current = seen;
            }
        }
    }
}