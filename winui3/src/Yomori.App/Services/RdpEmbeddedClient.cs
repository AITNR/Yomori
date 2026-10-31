// RDP 内嵌客户端：ycn_rdp.dll 的 C# 生命周期封装（计划书 §6 M2）。
// 纯 .NET、不碰 XAML —— 帧数据以「原生指针 + 尺寸」形式经 FrameArrived 事件外送，
// 由 UI 层（RdpView）负责调度到 UI 线程并渲染。
//
// 线程约定：
//   - Connected/FrameArrived/Disconnected/ErrorOccurred 可能来自原生事件循环线程；
//   - 消费方若要碰 UI，必须自行切 DispatcherQueue；
//   - 回调里禁止调用 Disconnect()（原生侧死锁保护，见 ycn_rdp.h）。
using System;
using System.Runtime.InteropServices;
using Yomori.Models;
using Yomori.Services;

namespace Yomori.Services
{
    /// <summary>连接参数（结构化，避免散参）。</summary>
    public sealed record RdpConnectInfo
    {
        public required string Host { get; init; }
        public ushort Port { get; init; } = 3389;
        public required string Username { get; init; }
        public string? Domain { get; init; }
        public required string Password { get; init; }
        public uint DesktopWidth { get; init; } = 1280;
        public uint DesktopHeight { get; init; } = 720;
        public bool AllowSelfsigned { get; init; } = true;
        public bool EnableAudio { get; init; } = true;
        public bool UseGfx { get; init; }
        public string DecodeBackend { get; init; } = DecodeBackends.Auto;
        public string RenderBackend { get; init; } = RenderBackends.Auto;
    }

    /// <summary>一帧就绪的通知（携带可立即 grab 的会话）。</summary>
    public sealed class RdpFrameEventArgs : EventArgs
    {
        public int Session { get; init; }
        public uint Width { get; init; }
        public uint Height { get; init; }
        public uint Stride { get; init; }
    }

    /// <summary>
    /// 内嵌 RDP 客户端。一次实例对应一条会话；Disconnected/Failed 后可重用实例再次 Connect。
    /// </summary>
    public sealed class RdpEmbeddedClient : IDisposable
    {
        // ---- P/Invoke 回调必须保活，否则 GC 回收后原生回调野指针 ----
        private YcnRdpCallbacks? _nativeCallbacks;
        private YcnCallbacks.OnConnected? _onConnected;
        private YcnCallbacks.OnFrameReady? _onFrameReady;
        private YcnCallbacks.OnDesktopResize? _onResize;
        private YcnCallbacks.OnDisconnected? _onDisconnected;
        private YcnCallbacks.OnError? _onError;

        private readonly object _gate = new();
        private RdpClientState _state = RdpClientState.Idle;
        private int _session;
        private bool _disconnectRequested;
        private string _decodeBackend = DecodeBackends.Auto;
        private string _renderBackend = RenderBackends.Auto;
        private uint _requestedDecodePolicy;
        private uint _decodeApiVersion;
        private uint _decodeCapabilities;
        private bool _usedVersionedDecodeEntry;
        private static readonly bool s_forceSoftwareFromCommandLine = Array.Exists(
            Environment.GetCommandLineArgs(), a => a == "--embed-nod3d");
        // 最近一次「静音意图」。原生 ycn_rdp_set_muted 在会话不是 RUNNING 时**直接拒绝**，
        // 而调用方普遍在 Connect() 之前就下发（见 MainViewModel.TryStartEmbeddedConnect /
        // RdpSurfaceCell.SetClient）——那一刻 session 还是 0，必然被拒。
        // 不把意图记住，就会出现「按钮亮着已静音、实际还有声音」，只能靠用户手动关一次再开。
        private volatile bool _requestedMuted;
        // Native callbacks must remain rooted even if asynchronous disposal times out.
        private System.Runtime.InteropServices.GCHandle _callbackRoot;
        private System.Threading.Tasks.TaskCompletionSource _completion = CompletedSource();
        public System.Threading.Tasks.Task Completion { get { lock (_gate) { return _completion.Task; } } }

        private static System.Threading.Tasks.TaskCompletionSource CompletedSource()
        {
            var source = new System.Threading.Tasks.TaskCompletionSource(System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously);
            source.SetResult();
            return source;
        }

        private void ReleaseCallbackRoot()
        {
            lock (_gate)
            {
                if (_callbackRoot.IsAllocated) { _callbackRoot.Free(); }
            }
        }

        /// <summary>抓帧复用缓冲（避免每帧分配 8MB 触发 GC）。长度 ≥ 最近一帧的 stride*height。</summary>
        private byte[] _frameBuffer = Array.Empty<byte>();

        public RdpClientState State { get { lock (_gate) { return _state; } } }
        public int Session => _session;

        /// <summary>会话建立（画面尺寸已定）。可能在原生线程触发。</summary>
        public event EventHandler<(int Session, uint Width, uint Height)>? Connected;
        /// <summary>有新帧可取。高频、可能在原生线程触发；用 TryGrabFrame 拷贝。</summary>
        public event EventHandler<RdpFrameEventArgs>? FrameArrived;
        /// <summary>远端桌面分辨率变更（M4：D3D 交换链需要跟着 Resize）。</summary>
        public event EventHandler<(int Session, uint Width, uint Height)>? DesktopResized;
        /// <summary>断开（每会话恰好一次）。reason 见 YcnDisconnectReason。</summary>
        public event EventHandler<(int Session, int Reason, string Detail)>? Disconnected;
        /// <summary>非致命错误通报。</summary>
        public event EventHandler<(int Session, int Code, string Message)>? ErrorOccurred;

        /// <summary>发起连接。成功返回 true 并进入 Connecting；失败返回 false 并进入 Failed。</summary>
        public bool Connect(RdpConnectInfo info)
        {
            if (info is null)
            {
                return false;
            }

            lock (_gate)
            {
                var next = RdpClientStateMachine.Transition(_state, RdpClientEvent.ConnectRequested);
                if (next == _state)
                {
                    return false; // 非法迁移（如重复 Connect）
                }
                if (!_completion.Task.IsCompleted) { return false; }
                _disconnectRequested = false;
                _completion = new(System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously);
                _callbackRoot = System.Runtime.InteropServices.GCHandle.Alloc(this);
                _state = next;
            }

            _onConnected = (user, session, w, h) =>
            {
                lock (_gate)
                {
                    _session = session;
                    _state = RdpClientStateMachine.Transition(_state, RdpClientEvent.NativeConnected);
                }

                // 补发「建连之前」就记下的静音意图。
                //
                // 为什么必须有这一步（2026-10-22 用户报「首次启动任务时按钮显示已静音、实际仍有声，
                // 必须关一次再开」的根因）：
                //   ycn_rdp_set_muted 要求 state == YCN_STATE_RUNNING，否则返回 NOT_CONNECTED
                //   且**不改** s->muted；而 UI 侧三处调用（VM 发起连接前、画面 SetClient、
                //   按钮在 CONNECTING 期间被点）全都可能发生在 RUNNING 之前 → 全部静默失败。
                //   UI 只认自己的 VM 标志（IsSurfaceMuted）去画图标，于是图标说静音、原生说没静音。
                //   用户手动关一次（此时已 RUNNING，成功写 0）再开一次（成功写 1）才对上。
                //
                // 交给线程池而不是在原生回调线程里直接调：这里正处在 FreeRDP 的 PostConnect 里，
                // 就地抢 g_lock 并调设备 SetVolume 属于在别人回调里做副作用；
                // 线程池只多一跳（微秒级），而此时音频通道通常还没开始送数据。
                // 任务体里重新读 _requestedMuted：以「最新意图」为准，避免补发把用户刚做的取消静音盖回去。
                if (_requestedMuted)
                {
                    _ = System.Threading.Tasks.Task.Run(() =>
                    {
                        // 只对「本实例现在这条会话」补发：会话号存在复用可能，
                        // 补发跑晚了就宁可不动，也绝不能把静音送到别的通道上去。
                        if (_session != session)
                        {
                            return;
                        }
                        RdpNativeInterop.ycn_rdp_set_muted(session, _requestedMuted ? 1 : 0);
                    });
                }

                Connected?.Invoke(this, (session, w, h));
            };
            _onFrameReady = (user, session, w, h, stride) =>
                FrameArrived?.Invoke(this, new RdpFrameEventArgs { Session = session, Width = w, Height = h, Stride = stride });
            _onResize = (user, session, w, h) =>
                DesktopResized?.Invoke(this, (session, w, h));
            _onDisconnected = (user, session, reason, detail) =>
            {
                lock (_gate)
                {
                    _state = RdpClientStateMachine.Transition(_state, RdpClientEvent.NativeDisconnected);
                    _session = 0;
                    _frameBuffer = Array.Empty<byte>();
                }
                try { Disconnected?.Invoke(this, (session, reason, detail ?? string.Empty)); }
                finally
                {
                    // The native terminal callback occurs after all native resource teardown.
                    lock (_gate)
                    {
                        ReleaseCallbackRoot();
                        _completion.TrySetResult();
                    }
                }
            };
            _onError = (user, session, code, message) =>
                ErrorOccurred?.Invoke(this, (session, code, message ?? string.Empty));

            _nativeCallbacks = new YcnRdpCallbacks
            {
                Connected = _onConnected,
                FrameReady = _onFrameReady,
                DesktopResize = _onResize,
                Disconnected = _onDisconnected,
                Error = _onError,
            };

            var p = new YcnRdpParams
            {
                Host = info.Host,
                Port = info.Port,
                Username = info.Username,
                Domain = info.Domain,
                Password = info.Password,
                DesktopWidth = info.DesktopWidth,
                DesktopHeight = info.DesktopHeight,
                ColorDepth = 32,
                UseNla = -1,
                AllowSelfsigned = info.AllowSelfsigned ? 1 : 0,
                EnableAudio = info.EnableAudio ? 1 : 0,
                UseGfx = info.UseGfx ? 1 : 0,
            };

            var cb = _nativeCallbacks.Value;
            int session;
            try
            {
                var requestedPolicy = RdpDecodePolicy.Resolve(
                    info.DecodeBackend, info.RenderBackend, s_forceSoftwareFromCommandLine);
                var useVersionedEntry = RdpNativeInterop.TryProbeDecodeApi(
                    out var decodeApiVersion, out var decodeCapabilities);
                lock (_gate)
                {
                    _decodeBackend = DecodeBackends.Normalize(info.DecodeBackend);
                    _renderBackend = RenderBackends.Normalize(info.RenderBackend);
                    _requestedDecodePolicy = requestedPolicy;
                    _decodeApiVersion = decodeApiVersion;
                    _decodeCapabilities = decodeCapabilities;
                    _usedVersionedDecodeEntry = useVersionedEntry;
                }

                if (useVersionedEntry)
                {
                    try
                    {
                        session = RdpNativeInterop.ycn_rdp_connect_ex(
                            ref p, ref cb, IntPtr.Zero, requestedPolicy);
                    }
                    catch (EntryPointNotFoundException)
                    {
                        // 极端情况下探测通过但调用时导出消失/被替换：完整退回旧入口，
                        // 不把失败当成连接失败，也不给旧结构追加任何字段。
                        lock (_gate) { _usedVersionedDecodeEntry = false; }
                        session = RdpNativeInterop.ycn_rdp_connect(ref p, ref cb, IntPtr.Zero);
                    }
                }
                else
                {
                    session = RdpNativeInterop.ycn_rdp_connect(ref p, ref cb, IntPtr.Zero);
                }
            }
            catch
            {
                lock (_gate)
                {
                    _state = RdpClientState.Failed;
                    ReleaseCallbackRoot();
                    _completion.TrySetResult();
                }
                throw;
            }
            if (session <= 0)
            {
                lock (_gate)
                {
                    _state = RdpClientStateMachine.Transition(_state, RdpClientEvent.ConnectRejected);
                    ReleaseCallbackRoot();
                    _completion.TrySetResult();
                }
                var buf = new byte[512];
                RdpNativeInterop.ycn_rdp_last_error(0, buf, (UIntPtr)buf.Length);
                throw new InvalidOperationException(
                    $"ycn_rdp_connect 失败（{session}）：{System.Text.Encoding.UTF8.GetString(buf).TrimEnd('\0')}");
            }

            bool disconnect;
            lock (_gate)
            {
                if (!_completion.Task.IsCompleted) { _session = session; }
                disconnect = _disconnectRequested && !_completion.Task.IsCompleted;
            }
            if (disconnect) { RdpNativeInterop.ycn_rdp_disconnect(session); }
            return true;
        }

        /// <summary>断开会话（语义 = mstsc「断开连接」）。幂等。</summary>
        public void Disconnect()
        {
            int session;
            lock (_gate)
            {
                _disconnectRequested = true;
                session = _session;
                if (session <= 0) { return; }
            }
            RdpNativeInterop.ycn_rdp_disconnect(session);
        }

        /// <summary>拷贝当前帧到托管数组。返回 false 表示当前无帧/未连接。</summary>
        public bool TryCopyFrame(out uint width, out uint height, out uint stride, out byte[] pixels)
        {
            width = height = stride = 0;
            pixels = Array.Empty<byte>();

            // 第一步只问尺寸：拿不到就快速失败，也顺便决定缓冲大小
            var rc = RdpNativeInterop.ycn_rdp_grab_frame(_session, out var w, out var h, out var st, out _);
            if (rc != 0 || w == 0 || h == 0 || st == 0)
            {
                return false;
            }

            var size = checked((int)(st * h));
            var buffer = _frameBuffer;
            if (buffer.Length < size)
            {
                buffer = new byte[size];
                _frameBuffer = buffer;
            }

            // 第二步在原生锁内整帧拷贝：与 end_paint 的写入互斥，
            // 所以绝不会读到「拷贝到一半」的半成品帧（撕裂根源）。
            unsafe
            {
                fixed (byte* dst = buffer)
                {
                    rc = RdpNativeInterop.ycn_rdp_copy_frame(_session, dst, (uint)size, out w, out h, out st);
                }
            }
            if (rc != 0 || w == 0 || h == 0 || st == 0)
            {
                return false;
            }

            width = w;
            height = h;
            stride = st;
            // 注意：pixels 是复用缓冲，长度可能大于 stride*height —— 消费方按 stride/width/height 取值
            pixels = buffer;
            return true;
        }

        /// <summary>
        /// GFX 图形管线状态（诊断）：
        /// 0 未请求 / 1 已订阅但没有任何动态通道连上（服务器没给 GFX）/ 2 已接入 GDI（全帧模式）
        /// / 3 收到了动态通道但没有 GFX 那条 / -1 会话未知。
        /// </summary>
        public int GfxState(out uint dvcCount)
            => RdpNativeInterop.ycn_rdp_gfx_state(_session, out dvcCount);

        /// <summary>已连上的通道名（诊断，逗号分隔）。空串 = 一条都没连上。</summary>
        public string DvcNames()
        {
            var buf = new byte[256];
            RdpNativeInterop.ycn_rdp_dvc_names(_session, buf, (UIntPtr)buf.Length);
            var len = Array.IndexOf(buf, (byte)0);
            if (len < 0)
            {
                len = buf.Length;
            }
            return System.Text.Encoding.UTF8.GetString(buf, 0, len);
        }

        /// <summary>
        /// 本次连接尝试的解码配置与原生入口探测结果。它只说明“请求了什么/走了哪个入口”，
        /// 不把设备创建或入口调用当成硬解已成功；实际 codec/decoder/成功硬解帧看 Diag()。
        /// </summary>
        public string DecodeStatus()
        {
            lock (_gate)
            {
                var entry = _usedVersionedDecodeEntry ? "connect_ex" : "connect";
                var hwCompiled = (_decodeCapabilities & (uint)YcnDecodeCapabilities.HardwareCompiled) != 0;
                var hwDiagnostics = (_decodeCapabilities & (uint)YcnDecodeCapabilities.HardwareDiagnostics) != 0;
                return $"decode_backend={DecodeBackends.Normalize(_decodeBackend)}" +
                       $", render_backend={RenderBackends.Normalize(_renderBackend)}" +
                       $", requested_policy={RdpDecodePolicy.Label(_requestedDecodePolicy)}" +
                       $", decode_api={_decodeApiVersion}" +
                       $", decode_caps=0x{_decodeCapabilities:X}" +
                       $", hw_compiled={(hwCompiled ? "yes" : "no")}" +
                       $", hw_diagnostics={(hwDiagnostics ? "yes" : "no")}" +
                       $", entry={entry}";
            }
        }

        /// <summary>原生通道加载诊断（一行文本：load_addins 返回值 / 通道计数 / GFX 状态 / 通道名）。</summary>
        public string Diag()
        {
            var buf = new byte[2048];
            RdpNativeInterop.ycn_rdp_diag(_session, buf, (UIntPtr)buf.Length);
            var len = Array.IndexOf(buf, (byte)0);
            if (len < 0)
            {
                len = buf.Length;
            }
            return System.Text.Encoding.UTF8.GetString(buf, 0, len);
        }

        /// <summary>注入鼠标事件（flags 取 YcnPtrFlags）。</summary>
        public bool SendMouse(uint flags, ushort x, ushort y)
            => RdpNativeInterop.ycn_rdp_send_mouse(_session, flags, x, y) == 0;

        /// <summary>注入键盘事件（VSC 扫描码；扩展键如方向键/Right Ctrl 传 extended=true）。</summary>
        public bool SendKey(bool down, bool extended, ushort scancode) => SendKeyRc(down, extended, scancode) == 0;

        /// <summary>注入键盘事件并返回原生返回码（诊断用）。</summary>
        public int SendKeyRc(bool down, bool extended, ushort scancode)
            => RdpNativeInterop.ycn_rdp_send_key(_session, down ? 1 : 0, extended ? 1 : 0, scancode);

        /// <summary>
        /// 把本机键盘 toggle 状态（NumLock/CapsLock/ScrollLock）同步给服务器。
        ///
        /// 为什么必须有：小键盘 0-9 与 Insert/Home/PgUp/End/PgDn/方向键在 Set1 里**共用扫描码**
        /// （Numpad1 与 End 同为 0x4F），只靠 extended 位 + **服务器侧** NumLock 区分。
        /// 服务器不知道本机锁状态时只能按自己那份解释 —— 本机 NumLock 开、服务器关，
        /// 用户按小键盘 1 就变成按 End，光标乱跳。mstsc / xfreerdp 都会发这个同步事件。
        ///
        /// 幂等；连接未就绪返回 false（不抛）。
        /// </summary>
        public bool SyncKeyboardState()
            => SyncKeyboardState(KeyboardToggleState.SnapshotLocal());

        /// <summary>按给定锁状态同步（测试/重连复用；未连接时返回 false）。</summary>
        public bool SyncKeyboardState(KeyboardToggleState state)
            => _session > 0 && RdpNativeInterop.ycn_rdp_send_keyboard_sync(_session, state.ToWireFlags()) == 0;

        /// <summary>
        /// 本机静音 / 取消静音（远端照常发声，只是本机不放）。
        /// 立即生效、不需要重连；原生侧对 rdpsnd 设备的 SetVolume 做了链式挂钩，
        /// 静音态下服务器下发的音量 PDU 也盖不掉。只影响**本会话**，不波及其他通道。
        /// rdpsnd 通道还没连上时也安全（连上后原生侧会自动装钩子）。
        /// </summary>
        /// <remarks>
        /// ⚠️ 语义是「**意图已被接受**」，不是「已经生效」：会话还没建立（session == 0）时
        /// 原生必然拒绝，这里把意图记进 <see cref="_requestedMuted"/>，等 Connected 补发。
        /// 调用方不要拿返回值当成"现在真的静音了"。
        /// </remarks>
        public bool SetMuted(bool muted)
        {
            _requestedMuted = muted;

            var session = _session;
            if (session <= 0)
            {
                return true; // 会话还没建立：记下意图，连上后补发（见 _onConnected）
            }

            return RdpNativeInterop.ycn_rdp_set_muted(session, muted ? 1 : 0) == 0;
        }

        /// <summary>
        /// 最近一次下发的静音意图（含**尚未生效**的，例如建连前设的那次）。
        /// 诊断/自检用：与 <see cref="IsMuted"/>（原生真实标志）不一致就说明存在漏发。
        /// </summary>
        public bool RequestedMuted => _requestedMuted;

        /// <summary>原生侧当前静音标志。会话尚未建立时返回最近一次下发的意图。</summary>
        public bool IsMuted
        {
            get
            {
                var session = _session;
                if (session <= 0)
                {
                    return _requestedMuted;
                }

                var rc = RdpNativeInterop.ycn_rdp_get_muted(session);
                return rc == 1;
            }
        }

        public static string GetLastError(int session)
        {
            var buf = new byte[512];
            RdpNativeInterop.ycn_rdp_last_error(session, buf, (UIntPtr)buf.Length);
            return System.Text.Encoding.UTF8.GetString(buf).TrimEnd('\0');
        }

        public void Dispose()
        {
            Disconnect();
        }
    }

    /// <summary>
    /// 帧阶段计时窗口聚合（RdpView 每 5 秒诊断日志用）。
    ///
    /// 放在这里是因为纯 .NET、无 XAML/Vortice 依赖，DecodeChecks 可直接链接本文件测试。
    /// 所有 Record/TakeSnapshot 都由 UI 线程调用（OnPresentTick 及其 await 续体），
    /// 所以热路径只用普通 long 累加，不做逐字段原子操作；Stopwatch 只读时间戳，不逐帧分配。
    /// ⚠️ 如果将来真的跨线程调用：TakeSnapshot 是逐字段拷贝+清零，不是跨字段原子快照，
    /// 恰在窗口边界的帧可能被算到相邻窗口（或 count/bytes 落在不同窗口），但单帧计时本身不受影响。
    /// </summary>
    internal sealed class RdpFramePhaseStats
    {
        private RdpPhaseTotals _grab;
        private RdpPhaseTotals _d3d;
        private RdpPhaseTotals _softCommit;
        private RdpPhaseTotals _softAwait;
        private RdpPhaseTotals _tick;
        private long _d3dFailures;
        private long _lastResolution = -1; // (width << 32) | height；-1 = 尚未抓到帧
        private uint _lastStride;
        private int _resolutionChanges;

        public void RecordGrab(long elapsedTicks, uint width, uint height, uint stride)
        {
            _grab.Add(elapsedTicks, (long)stride * height);
            var packed = ((long)width << 32) | height;
            if (_lastResolution >= 0 && _lastResolution != packed)
            {
                _resolutionChanges++;
            }
            _lastResolution = packed;
            _lastStride = stride;
        }

        /// <summary>
        /// D3D Present 无论成功失败都计时；失败时 bytes=0 并单独计失败次数，
        /// 避免只采样成功路径把慢失败从 avg/max 里过滤掉。
        /// </summary>
        public void RecordD3D(long elapsedTicks, uint width, uint height, uint stride, bool uploaded)
        {
            _d3d.Add(elapsedTicks, uploaded ? (long)stride * height : 0);
            if (!uploaded)
            {
                _d3dFailures++;
            }
        }

        public void RecordSoftCommit(long elapsedTicks, uint width, uint height, uint stride) =>
            _softCommit.Add(elapsedTicks, (long)stride * height);

        public void RecordSoftAwait(long elapsedTicks) => _softAwait.Add(elapsedTicks, 0);

        public void RecordTick(long elapsedTicks) => _tick.Add(elapsedTicks, 0);

        public void Reset()
        {
            _grab = default;
            _d3d = default;
            _softCommit = default;
            _softAwait = default;
            _tick = default;
            _d3dFailures = 0;
            _lastResolution = -1;
            _lastStride = 0;
            _resolutionChanges = 0;
        }

        /// <summary>
        /// 取走当前窗口并清零计数。仅每 5 秒由 UI 线程调用一次；分辨率/stride 是
        /// 最近一次成功抓帧的真实值（本窗没抓到帧时沿用上一次），字节数按每帧 stride*height 累加。
        /// </summary>
        public RdpFramePhaseSnapshot TakeSnapshot(long elapsedMs)
        {
            var packed = _lastResolution;
            var snapshot = new RdpFramePhaseSnapshot
            {
                ElapsedMs = elapsedMs,
                Grab = _grab,
                D3D = _d3d,
                SoftCommit = _softCommit,
                SoftAwait = _softAwait,
                Tick = _tick,
                Width = packed >= 0 ? (uint)(packed >> 32) : 0u,
                Height = packed >= 0 ? (uint)(packed & 0xFFFFFFFFL) : 0u,
                Stride = _lastStride,
                ResolutionChanges = _resolutionChanges,
                D3DFailures = _d3dFailures,
            };

            _grab = default;
            _d3d = default;
            _softCommit = default;
            _softAwait = default;
            _tick = default;
            _d3dFailures = 0;
            _resolutionChanges = 0;
            return snapshot;
        }
    }

    /// <summary>一个阶段的累加器：次数/总 ticks/最大 ticks/字节。只在 UI 线程读写。</summary>
    internal struct RdpPhaseTotals
    {
        public long Count;
        public long Ticks;
        public long MaxTicks;
        public long Bytes;

        public readonly double AverageMs =>
            Count > 0 ? Ticks * 1000.0 / System.Diagnostics.Stopwatch.Frequency / Count : 0;

        public readonly double MaxMs => MaxTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency;

        public void Add(long ticks, long bytes)
        {
            Count++;
            Ticks += ticks;
            if (ticks > MaxTicks)
            {
                MaxTicks = ticks;
            }
            Bytes += bytes;
        }

        public readonly double BytesPerSecond(long elapsedMs) =>
            elapsedMs > 0 ? Bytes * 1000.0 / elapsedMs : 0;
    }

    /// <summary>RdpFramePhaseStats 的一次 5 秒窗口快照（值对象，每 5 秒才创建）。</summary>
    internal struct RdpFramePhaseSnapshot
    {
        public long ElapsedMs;
        public RdpPhaseTotals Grab;
        public RdpPhaseTotals D3D;
        public RdpPhaseTotals SoftCommit;
        public RdpPhaseTotals SoftAwait;
        public RdpPhaseTotals Tick;
        public uint Width;
        public uint Height;
        public uint Stride;
        public int ResolutionChanges;
        public long D3DFailures;
    }
}
