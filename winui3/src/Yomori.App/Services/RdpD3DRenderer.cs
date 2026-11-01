// RdpD3DRenderer：M4 生产渲染（计划书 §5.2 / §6 M4）。
//
// 架构（防退化，改前必读）：
//   - swap chain 尺寸 == 远端帧分辨率（CopyResource 同尺寸，**绝不**让 backbuffer 与
//     staging 尺寸不一致——那是不对齐 CopyResource，未定义行为，表现为竖直条纹）；
//   - 「缩放 + 黑边居中」由 XAML 层完成：SwapChainPanel（DIP 尺寸 = 远端分辨率）包在
//     Viewbox(Stretch=Uniform) 里，框架自动维护 CompositionScale 并由 DWM 组合器采样缩放；
//     不要改回手动 COM SetCompositionScale——它会被 XAML 布局重置为 1，画面变 1:1 裁切；
//   - Present(0,0)：不等待 vsync——vsync 阻塞会卡住 FreeRDP 事件循环线程（网络/输入同线程）；
//   - 🔴 整条管线**在 UI 线程上**跑（Present 由 RdpView 的 DispatcherTimer 驱动，
//     16ms 一拍）。原先这里写着「零 UI 线程参与」——**那是错的**，实现早已是UI 线程驱动，
//     而这条错注释会让下一个人以为可以随便从别的线程调 Initialize/Present/Dispose。
//     D3D11 设备与 immediate context 不是线程安全的：从原生帧线程直接调 ResizeRemote，
//     会与 UI 线程的 CopyResource/Present 撞在一起，驱动内部互锁 →
//     **整窗假死**（进程活着、Responding=False、UI 线程卡在 Wait=LpcReply、CPU 零增长），
//     用户实测为**偶发**（竞态特征）。修法是把尺寸与 D3D 写点全部收口到 UI 线程，
//     并在类内用 _gate 串行化作纵深防御；回归防线见 _smoke/RenderThreadAffinityCheck.cs。
//   - 失败回退 M2 SoftwareBitmap 路线。
using System;
using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using WinRT;

namespace Yomori.Services
{
    [ComImport]
    [Guid("63aad0b8-7c24-40ff-85a8-640d944cc325")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface ISwapChainPanelNative
    {
        [PreserveSig]
        void SetSwapChain(IntPtr swapChain);
    }

    internal enum RdpD3DPresentResult
    {
        Presented,
        /// <summary>这一拍没有可用的完整帧（会话未就绪 / 尺寸竞态）：只丢本拍。</summary>
        NoFrame,
        /// <summary>DLL 实际没有直填导出：本拍改走托管拷贝，D3D 本身仍然可用。</summary>
        Unsupported,
        /// <summary>D3D 资源或呈现真失败：调用方关闭 D3D 并回退软渲染。</summary>
        FatalFailure,
    }

    internal sealed class RdpD3DRenderer : IDisposable
    {
        /// <summary>
        /// 命令行 <c>--embed-nod3d</c> 强制 SoftwareBitmap 路线（诊断开关，优先级最高）。
        /// </summary>
        public static bool DisableD3D { get; } = Array.Exists(
            Environment.GetCommandLineArgs(), a => a == "--embed-nod3d");

        /// <summary>
        /// 配置项的渲染后端选择（render_backend）。
        ///
        /// 与 <see cref="DisableD3D"/> 的关系：
        ///   · 命令行开关优先（早于配置项存在，且用于无人值守验收）；
        ///   · 否则看配置：<c>software</c> = 不建 D3D 设备，直接走软渲染。
        ///
        /// 为什么不是一个属性：此处不能引用 Yomori.Models.RdpConfig（Services → Models 的依赖方向可以，
        /// 但这个类是静态工具类，拿不到当前实例）。因此由 RdpView 在创建渲染器前
        /// 把配置值推进来（见 <c>PushRenderBackend</c>）。
        /// </summary>
        internal static bool ForceSoftware { get; private set; }

        /// <summary>把配置的渲染后端选择同步进来（RdpView 挂画面前调一次）。</summary>
        internal static void PushRenderBackend(bool useSoftware)
        {
            ForceSoftware = useSoftware;
        }

        /// <summary>本次连接到底该不该用 D3D。</summary>
        public static bool ShouldUseSoftware => DisableD3D || ForceSoftware;

        /// <summary>🔒 序列化本实例的全部状态变更（Initialize / ResizeRemote / Present / Dispose）。
        ///
        /// D3D11 设备与 immediate context **不是线程安全的**，而这些方法的调用来源曾经不统一：
        /// 多数在 UI 线程（Present 由 DispatcherTimer 驱动），但 OnDesktopResized 一度从
        /// 原生帧线程直接调 ResizeRemote。ResizeBuffers 与 CopyResource/Present 撞在一起
        /// 表现为偶发的整窗假死（UI 线程 Responding=False，栈上LpcReply 等GPU/合成器回复）。
        ///
        /// 注意：这把锁只保证**本对象内部**一致，**不能**让 D3D 调用变成可跨线程的——
        /// SetSwapChain 仍必须在 UI 线程调。调用方纪律见RdpView 的尺寸/D3D 写点注释。
        /// 锁用 Monitor 是因为 Present 内部有 unsafe/fixed 块（不能跨 await 持锁，Present 也不 await）。
        /// </summary>
        private readonly object _gate = new();

        private ID3D11Device? _device;
        private ID3D11DeviceContext? _context;
        private IDXGISwapChain1? _swapChain;
        private ID3D11Texture2D? _staging;
        private ID3D11Texture2D? _frameTexture;
        private bool _frameTextureValid;
        private bool _dirtyAttachAttempted;
        private bool _dirtyAttached;
        private ulong _dirtyToken;
        private static long s_dirtyTokenSource;
        private readonly RdpNativeInterop.YcnDirtyRect[] _dirtyRects = new RdpNativeInterop.YcnDirtyRect[2048];
        private long _dirtyBoxCalls, _dirtyBoxBytes, _dirtyFullUploads, _dirtyTextureResets;
        private ID3D11Texture2D? _backBuffer;
        private Microsoft.UI.Xaml.Controls.SwapChainPanel? _panel;
        private IntPtr _panelPtr;                 // SwapChainPanel 原生指针（SetCompositionScale 用）
        private uint _remoteWidth;
        private uint _remoteHeight;

        private static int _liveRenderers;
        public static int LiveRenderers => System.Threading.Volatile.Read(ref _liveRenderers);
        public bool Initialized { get; private set; }

        public string? LastError { get; private set; }

        /// <summary>创建设备/交换链（远端分辨率）并绑定到 SwapChainPanel。
        /// ⚠️ **必须在 UI 线程调**：内部 SetSwapChain 要触碰 XAML 面板。</summary>
        public bool Initialize(Microsoft.UI.Xaml.Controls.SwapChainPanel panel,
            uint remoteWidth, uint remoteHeight)
        {
            lock (_gate)
            {
                return InitializeLocked(panel, remoteWidth, remoteHeight);
            }
        }

        private bool InitializeLocked(Microsoft.UI.Xaml.Controls.SwapChainPanel panel,
            uint remoteWidth, uint remoteHeight)
        {
            try
            {
                var rc = D3D11.D3D11CreateDevice(
                    null,
                    DriverType.Hardware,
                    DeviceCreationFlags.BgraSupport,
                    new[] { FeatureLevel.Level_11_0, FeatureLevel.Level_10_1 },
                    out var device);
                if (rc.Failure || device is null)
                {
                    rc = D3D11.D3D11CreateDevice(
                        null,
                        DriverType.Warp,
                        DeviceCreationFlags.BgraSupport,
                        new[] { FeatureLevel.Level_11_0 },
                        out device);
                    if (rc.Failure || device is null)
                    {
                        LastError = "D3D11CreateDevice failed (HW+WARP)";
                        return false;
                    }
                }

                _device = device;
                _context = device.ImmediateContext;

                using var dxgiDevice = device.QueryInterface<IDXGIDevice>();
                using var adapter = dxgiDevice.GetAdapter();
                using var factory = adapter.GetParent<IDXGIFactory2>();

                _remoteWidth = remoteWidth;
                _remoteHeight = remoteHeight;

                var desc = new SwapChainDescription1
                {
                    Width = remoteWidth,
                    Height = remoteHeight,
                    Format = Format.B8G8R8A8_UNorm,
                    Stereo = false,
                    SampleDescription = new SampleDescription(1, 0),
                    BufferUsage = Usage.RenderTargetOutput,
                    BufferCount = 2,
                    Scaling = Scaling.Stretch,
                    SwapEffect = SwapEffect.FlipSequential,
                    AlphaMode = AlphaMode.Ignore,
                };
                _swapChain = factory.CreateSwapChainForComposition(device, desc);
                if (_swapChain is null)
                {
                    LastError = "CreateSwapChainForComposition returned null";
                    return false;
                }

                _panel = panel;
                _panelPtr = ((IWinRTObject)panel).NativeObject.ThisPtr;
                SetPanelSwapChain(_swapChain.NativePointer);

                CreateStaging();
                CreateFrameTexture();
                _backBuffer = _swapChain.GetBuffer<ID3D11Texture2D>(0);
                if (!Initialized) { System.Threading.Interlocked.Increment(ref _liveRenderers); }
                Initialized = true;
                return true;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                return false;
            }
        }

        private unsafe void SetPanelSwapChain(IntPtr swapChain)
        {
            if (_panelPtr == IntPtr.Zero) { return; }
            var iid = new Guid("63aad0b8-7c24-40ff-85a8-640d944cc325");
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(_panelPtr, ref iid, out var native));
            try
            {
                // Balance QueryInterface directly; an RCW would hold another COM reference
                // until finalization, keeping the panel and its swap chain alive.
                var vtable = *(IntPtr**)native;
                var setSwapChain = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)vtable[3];
                Marshal.ThrowExceptionForHR(setSwapChain(native, swapChain));
            }
            finally
            {
                Marshal.Release(native);
                GC.KeepAlive(_panel);
            }
        }

        private void CreateStaging()
        {
            _staging?.Dispose();
            _staging = _device!.CreateTexture2D(new Texture2DDescription
            {
                Width = _remoteWidth,
                Height = _remoteHeight,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Staging,
                BindFlags = BindFlags.None,
                CPUAccessFlags = CpuAccessFlags.Write,
                MiscFlags = ResourceOptionFlags.None,
            });
        }

        private void CreateFrameTexture()
        {
            _frameTexture?.Dispose();
            _frameTexture = _device!.CreateTexture2D(new Texture2DDescription
            {
                Width = _remoteWidth, Height = _remoteHeight, MipLevels = 1, ArraySize = 1,
                Format = Format.B8G8R8A8_UNorm, SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default, BindFlags = BindFlags.None, CPUAccessFlags = CpuAccessFlags.None,
                MiscFlags = ResourceOptionFlags.None,
            });
            _frameTextureValid = false;
            _dirtyTextureResets++;
        }

        internal bool AttachDirtyClient(RdpEmbeddedClient client)
        {
            lock (_gate)
            {
                if (_dirtyAttached) return true;
                if (_dirtyAttachAttempted) return false;
                if (!client.SupportsDirtyFrameCopy) return false;
                var token = unchecked((ulong)System.Threading.Interlocked.Increment(ref s_dirtyTokenSource));
                if (token == 0) token = unchecked((ulong)System.Threading.Interlocked.Increment(ref s_dirtyTokenSource));
                if (!client.AttachDirtyRenderer(token)) return false;
                _dirtyAttachAttempted = true;
                _dirtyToken = token;
                _dirtyAttached = true;
                return true;
            }
        }

        internal void DetachDirtyClient(RdpEmbeddedClient client)
        {
            lock (_gate)
            {
                if (_dirtyAttached) client.DetachDirtyRenderer(_dirtyToken);
                _dirtyAttached = false;
                _dirtyAttachAttempted = false;
                _dirtyToken = 0;
            }
        }

        internal bool TryPresentDirty(RdpEmbeddedClient client, out uint width, out uint height, out uint stride, out long copyTicks, out long presentTicks)
        {
            lock (_gate) return TryPresentDirtyLocked(client, out width, out height, out stride, out copyTicks, out presentTicks);
        }

        private bool TryPresentDirtyLocked(RdpEmbeddedClient client, out uint width, out uint height, out uint stride, out long copyTicks, out long presentTicks)
        {
            width = height = stride = 0; copyTicks = presentTicks = 0;
            if (!_dirtyAttached || !Initialized || _frameTexture is null || _staging is null || _backBuffer is null || _context is null) return false;
            var start = System.Diagnostics.Stopwatch.GetTimestamp();
            var mapped = false;
            try
            {
                var map = _context.Map(_staging, 0, MapMode.Write, Vortice.Direct3D11.MapFlags.None);
                mapped = true;
                var capacity64 = (ulong)(uint)map.RowPitch * _remoteHeight;
                if (capacity64 == 0 || capacity64 > uint.MaxValue) throw new InvalidOperationException("staging capacity overflow");
                var rc = client.CopyPendingDirty(_dirtyToken, map.DataPointer, (uint)capacity64, (uint)map.RowPitch, out var info, _dirtyRects);
                copyTicks = System.Diagnostics.Stopwatch.GetTimestamp() - start;
                if (rc != 0) { LastError = $"dirty copy failed: {rc}"; _frameTextureValid = false; return false; }
                width = info.Width; height = info.Height; stride = info.Stride;
                if (info.ApiVersion != 1 || info.Token != _dirtyToken || info.RectCount > _dirtyRects.Length || width == 0 || height == 0) { LastError = "invalid dirty frame info"; _frameTextureValid = false; return false; }
                if (width != _remoteWidth || height != _remoteHeight) { _context.Unmap(_staging, 0); mapped = false; if (!ResizeRemoteLocked(width,height)) throw new InvalidOperationException("dirty resize failed"); _frameTextureValid=false; return false; }
                if (info.Kind == RdpNativeInterop.DirtyKindEmpty) { _context.Unmap(_staging,0); mapped=false; return true; }
                _context.Unmap(_staging,0); mapped=false;
                if (info.Kind == RdpNativeInterop.DirtyKindFull)
                {
                    _context.CopyResource(_frameTexture, _staging);
                    _frameTextureValid = true;
                    _dirtyFullUploads++;

                }
                else if (!_frameTextureValid)
                {
                    LastError = "dirty D3D texture invalid without FULL snapshot";
                    return false;
                }
                else if (info.Kind == RdpNativeInterop.DirtyKindBoxes)
                {
                    for (uint i=0;i<info.RectCount;i++)
                    {
                        var b=_dirtyRects[i];
                        var box=new Vortice.Mathematics.Box(b.X,b.Y,0,b.X+(int)b.W,b.Y+(int)b.H,1);
                        _context.CopySubresourceRegion(_frameTexture,0,(uint)b.X,(uint)b.Y,0,_staging,0,box);
                        _dirtyBoxCalls++;
                        _dirtyBoxBytes += (long)b.W * b.H * 4;

                    }
                }
                else { LastError="unknown dirty frame kind"; _frameTextureValid=false; return false; }
                _context.CopyResource(_backBuffer,_frameTexture);
                _swapChain!.Present(0,0);
                if (client.AckDirtyFrame(_dirtyToken,info.Seq)) { presentTicks=System.Diagnostics.Stopwatch.GetTimestamp()-start; return true; }
                LastError="dirty frame ack rejected"; _frameTextureValid=false; return false;
            }
            catch(Exception ex) { LastError="dirty D3D failure: "+ex.Message; _frameTextureValid=false; return false; }
            finally { if(mapped) { try{_context.Unmap(_staging,0);}catch(Exception ex){LastError="Unmap: "+ex.Message;} } }
        }

        /// <summary>远端分辨率变化：重建交换链、staging 与持久帧纹理（同尺寸原则）。
        /// ⚠️ 必须在 UI 线程调（会重建 D3D 资源）。锁是 Monitor（可重入），
        /// 因为 Present 在尺寸不一致时会调用本方法。</summary>
        public bool ResizeRemote(uint width, uint height)
        {
            lock (_gate)
            {
                return ResizeRemoteLocked(width, height);
            }
        }

        private bool ResizeRemoteLocked(uint width, uint height)
        {
            if (!Initialized || (_remoteWidth == width && _remoteHeight == height))
            {
                return true;
            }
            try
            {
                _backBuffer?.Dispose();
                _backBuffer = null;
                _swapChain!.ResizeBuffers(0, width, height, Format.B8G8R8A8_UNorm, 0);
                _remoteWidth = width;
                _remoteHeight = height;
                CreateStaging();
                CreateFrameTexture();
                _backBuffer = _swapChain.GetBuffer<ID3D11Texture2D>(0);
                return true;
            }
            catch (Exception ex)
            {
                LastError = "ResizeRemote: " + ex.Message;
                return false;
            }
        }

        /// <summary>
        /// D3D 直填：原生锁内把帧逐行写入已映射 staging，再 CopyResource/Present。
        /// 不创建托管 byte[]；尺寸竞态或目标不足时按原生返回尺寸重建并重试一次。
        /// 始终 Unmap；只有 D3D 资源重建/呈现真失败才返回 FatalFailure。
        /// </summary>
        internal RdpD3DPresentResult TryPresentFromClient(
            RdpEmbeddedClient client,
            out uint width,
            out uint height,
            out uint stride,
            out long copyTicks,
            out long presentTicks)
        {
            lock (_gate)
            {
                return TryPresentFromClientLocked(client, out width, out height, out stride,
                    out copyTicks, out presentTicks);
            }
        }

        private RdpD3DPresentResult TryPresentFromClientLocked(
            RdpEmbeddedClient client,
            out uint width,
            out uint height,
            out uint stride,
            out long copyTicks,
            out long presentTicks)
        {
            width = height = stride = 0;
            copyTicks = 0;
            var totalStart = System.Diagnostics.Stopwatch.GetTimestamp();
            presentTicks = 0;

            if (!Initialized || _staging is null || _backBuffer is null || _context is null)
            {
                LastError = "renderer not initialized";
                return RdpD3DPresentResult.FatalFailure;
            }

            for (var attempt = 0; attempt < 2; attempt++)
            {
                var mapped = false;
                var needsResize = false;
                var mappedDimensions = false;
                try
                {
                    var map = _context.Map(_staging, 0, MapMode.Write, Vortice.Direct3D11.MapFlags.None);
                    mapped = true; // Map 已成功；任何后续失败和 return 都必须在 finally 中 Unmap

                    var capacity = (ulong)(uint)map.RowPitch * _remoteHeight;
                    if (capacity == 0 || capacity > uint.MaxValue)
                    {
                        LastError = "staging capacity overflow";
                        return RdpD3DPresentResult.FatalFailure;
                    }

                    var copyStart = System.Diagnostics.Stopwatch.GetTimestamp();
                    var copyResult = client.TryCopyFrameToMappedBuffer(
                        map.DataPointer,
                        (uint)capacity,
                        (uint)map.RowPitch,
                        out width,
                        out height,
                        out stride);
                    copyTicks += System.Diagnostics.Stopwatch.GetTimestamp() - copyStart;

                    if (copyResult == RdpMappedFrameCopyResult.ResizeRequired)
                    {
                        needsResize = width > 0 && height > 0;
                    }
                    else if (copyResult == RdpMappedFrameCopyResult.Unsupported)
                    {
                        LastError = "native strided frame copy unavailable";
                        return RdpD3DPresentResult.Unsupported;
                    }
                    else if (copyResult == RdpMappedFrameCopyResult.NoFrame)
                    {
                        // 原生 -4/-8 是会话/尺寸竞态，只丢本拍，不能关闭 D3D。
                        LastError = "native frame unavailable";
                        return RdpD3DPresentResult.NoFrame;
                    }
                    else if (copyResult != RdpMappedFrameCopyResult.Success)
                    {
                        // 其余原生错误表示帧状态或参数真实损坏：回退软渲染，避免永久静默丢帧。
                        LastError = "native strided frame copy failed";
                        return RdpD3DPresentResult.FatalFailure;
                    }
                    else
                    {
                        mappedDimensions = width == _remoteWidth && height == _remoteHeight;
                        needsResize = !mappedDimensions;
                    }

                    if (mappedDimensions)
                    {
                        _context.Unmap(_staging, 0);
                        mapped = false;
                        _context.CopyResource(_backBuffer, _staging);
                        _swapChain!.Present(0, 0);
                        presentTicks = System.Diagnostics.Stopwatch.GetTimestamp() - totalStart;
                        return RdpD3DPresentResult.Presented;
                    }
                }
                catch (Exception ex)
                {
                    LastError = ex.Message;
                    presentTicks = System.Diagnostics.Stopwatch.GetTimestamp() - totalStart;
                    return RdpD3DPresentResult.FatalFailure;
                }
                finally
                {
                    if (mapped)
                    {
                        try { _context.Unmap(_staging, 0); }
                        catch (Exception ex) { LastError = "Unmap: " + ex.Message; }
                    }
                }

                if (!needsResize)
                {
                    LastError = "native frame has no dimensions";
                    presentTicks = System.Diagnostics.Stopwatch.GetTimestamp() - totalStart;
                    return RdpD3DPresentResult.NoFrame;
                }
                if (!ResizeRemoteLocked(width, height))
                {
                    presentTicks = System.Diagnostics.Stopwatch.GetTimestamp() - totalStart;
                    return RdpD3DPresentResult.FatalFailure;
                }
            }

            // 分辨率在两次尝试之间仍在变化：本拍不呈现，下一拍重来；不升级为 D3D 永久故障。
            LastError = "remote resolution changed during direct present";
            presentTicks = System.Diagnostics.Stopwatch.GetTimestamp() - totalStart;
            return RdpD3DPresentResult.NoFrame;
        }

        /// <summary>上传一帧（同尺寸 CopyResource）并呈现。失败返回 false。
        /// ⚠️ 必须在 UI 线程调（本方法整体在一个 GPU 阻塞段里，绝不能拖到后台线程 ——
        /// 那会让 UI 线程与 GPU 互相等，成假死）。</summary>
        public bool Present(ReadOnlySpan<byte> pixels, uint width, uint height, uint stride)
        {
            lock (_gate)
            {
                return PresentLocked(pixels, width, height, stride);
            }
        }

        private bool PresentLocked(ReadOnlySpan<byte> pixels, uint width, uint height, uint stride)
        {
            if (!Initialized || _staging is null || _backBuffer is null || _context is null)
            {
                LastError = "renderer not initialized";
                return false;
            }
            if (width != _remoteWidth || height != _remoteHeight)
            {
                if (!ResizeRemoteLocked(width, height))
                {
                    return false;
                }
            }

            try
            {
                // 上传远端帧：Map/Unmap 手动拷贝。不用 UpdateSubresource 的 span 重载——
                // 每帧 new 的数组若在原生执行期间被 GC 回收会 AV（M4 崩溃 bug），fixed 显式固定
                var mapped = _context.Map(_staging, 0, MapMode.Write, Vortice.Direct3D11.MapFlags.None);
                try
                {
                    unsafe
                    {
                        fixed (byte* src = pixels)
                        {
                            var dst = (byte*)mapped.DataPointer;
                            var rowBytes = (int)(width * 4);
                            if ((uint)mapped.RowPitch == stride)
                            {
                                Buffer.MemoryCopy(src, dst, (long)mapped.RowPitch * height, (long)stride * height);
                            }
                            else
                            {
                                for (var row = 0; row < height; row++)
                                {
                                    Buffer.MemoryCopy(
                                        src + (ulong)(stride * row),
                                        dst + (ulong)(mapped.RowPitch * row),
                                        rowBytes,
                                        rowBytes);
                                }
                            }
                        }
                    }
                }
                finally
                {
                    _context.Unmap(_staging, 0);
                }
                _context.CopyResource(_backBuffer, _staging);
                // SyncInterval=0：立即返回不等待 vsync（vsync 阻塞会卡住 FreeRDP 事件循环线程）
                _swapChain.Present(0, 0);
                return true;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                return false;
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_dirtyAttached) { _frameTextureValid = false; }
                if (Initialized) { System.Threading.Interlocked.Decrement(ref _liveRenderers); }
                Initialized = false;
                try { SetPanelSwapChain(IntPtr.Zero); }
                catch (Exception ex) { LastError = ex.Message; }
                finally
                {
                    _panelPtr = IntPtr.Zero;
                    _panel = null;
                }
                _staging?.Dispose();
                _frameTexture?.Dispose();
                _backBuffer?.Dispose();
                _swapChain?.Dispose();
                _context?.Dispose();
                _device?.Dispose();
                _staging = null;
                _frameTexture = null;
                _frameTextureValid = false;
            _dirtyTextureResets++;
                _backBuffer = null;
                _swapChain = null;
                _context = null;
                _device = null;
                _panelPtr = IntPtr.Zero;
            }
        }
    }
}
