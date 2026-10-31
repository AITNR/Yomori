// RdpView：内嵌远程桌面画面控件。
//
// M4 生产渲染：D3D11 SwapChainPanel（交换链 = 远端分辨率；缩放/居中由 CompositionScale
// + 布局完成，GPU 组合器采样）。D3D 初始化失败自动回退 M2 SoftwareBitmap 路线。
// D3D 采用**惰性初始化**：第一帧到达时按真实帧尺寸创建（Connect/AttachClient 两用）。
//
// 输入：指针/键盘事件经 RdpInputMapper 转发；全屏接管见 ChannelsPage 的 LL 钩子。
using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using Yomori.Models;
using Yomori.Services;

namespace Yomori.Views
{
    public sealed partial class RdpView : UserControl
    {
        private RdpEmbeddedClient? _client;
        private SoftRenderState? _softRenderState;
        /// <summary>
        /// 软渲染复用的 SoftwareBitmap（由 SoftRenderState 持有；尺寸不变就原地更新内容）。
        /// 🔑 2026-10-16 修：原来是每帧 <c>CreateCopyFromBuffer</c> new 一个 8.3MB 对象，
        /// 每秒 ~200MB 内存压力全涌进 <c>GC.AddMemoryPressure</c>，UI 线程最终堵死在该调用里
        /// （dump 实证：栈顶 <c>System.GC._AddMemoryPressure</c>，LOH 16.7MB = 2×8.3MB 帧缓冲）。
        /// 现在整条连接只建一次，内存压力从「每帧 8.3MB」降到「每连接一次」。
        /// </summary>
        private Microsoft.UI.Dispatching.DispatcherQueue? _dispatcher;
        private RdpD3DRenderer? _renderer;
        private bool _d3dFailed;               // D3D 初始化失败后本实例不再重试（回退软渲染）
        private bool _initQueued;              // 惰性初始化已排队（防高频帧重复排队泄漏渲染器）
        private bool _renderSuspended;         // 全屏时页面内渲染器挂起，避免双路渲染
        private long _lastFrameArrivedTick;    // 最近一次 end_paint（原生线程写，帧节奏统计用）
        private Microsoft.UI.Dispatching.DispatcherQueueTimer? _presentTimer;  // 16ms 呈现节拍器（≈60Hz）
        private static long s_liveSoftRenderStates;
        private static long s_softRendersInFlight;

        /// <summary>
        /// 渲染世代号。每次 DetachClient / Disconnect 自增；
        /// AwaitSoftRenderAsync 在 await 前后比对自己那一代的号，不匹配就立即放弃。
        ///
        /// 🔑 为什么必须有（2026-10-16 实测）：原来 OnPresentTick 用
        /// <c>_ = RenderFrameAsync(...)</c> 即发即弃，而旧的全局防重入标志无法区分连接世代。
        /// 页面切走 / 弹出窗口 / 每次重连都会留下一批卡在 await SetBitmapAsync 的任务，
        /// 它们持着已废弃的 SoftwareBitmapSource / SoftwareBitmap 无法回收。
        /// dump 实证：健康进程仅运行 3 分钟就堆到 36 个
        /// AsyncStateMachineBox&lt;RenderFrameAsync&gt;（而 RdpView 只有 14 个），卡死现场是 32 个。
        /// 这些僵尸任务持续调 SetBitmapAsync → XAML 反复重绑合成表面
        /// （卡死栈顶正是 WUCBrushManager::GetSurfaceBrush →
        /// CompositionSurfaceBrush::SetSurface），把渲染线程挤占干净。
        /// </summary>
        private int _renderGeneration;

        /// <summary>
        /// 「还没呈现过第一帧」标记（2026-10-23 修「切页回来黑屏」）。
        ///
        /// 原生侧只在 end_paint（服务器真的画了东西）时回调 FrameArrived，
        /// **远端桌面完全静止时一帧都不来**。而 D3D 渲染器与首次呈现都挂在「新帧到达」上，
        /// 于是：页面切走 → 解挂并 Dispose 渲染器；切回来 → 远端静止 → 没有新帧 →
        /// 渲染器永远不建、画面永远空着，只有断开重连（重连必有一次全屏重绘）才恢复。
        ///
        /// 原生侧始终保留最后一帧（<c>s-&gt;frame</c> + <c>frame_valid</c>），
        /// 所以这里只要允许节拍器在「没有新帧」时也主动抓一次即可恢复画面。
        /// </summary>
        private bool _pendingFirstFrame;

        /// <summary><see cref="_pendingFirstFrame"/> 的重试次数（抓帧失败时的退避上限）。</summary>
        private int _pendingFirstFrameTries;

        /// <summary>首帧重试上限：约 10 秒（16ms 一拍）。超过就停手，避免会话已断时空转 P/Invoke。</summary>
        private const int PendingFirstFrameMaxTries = 600;

        /// <summary>本实例当前在飞（已发出、尚未结束）的渲染任务数；仅供诊断。</summary>
        private int _renderInFlight;

        /// <summary>当前尚未释放的软渲染资源状态数，供 VM 的周期诊断采样。</summary>
        public static long LiveSoftRenderStates => System.Threading.Interlocked.Read(ref s_liveSoftRenderStates);

        /// <summary>所有 RdpView 当前在飞的软渲染任务数，供 VM 的周期诊断采样。</summary>
        public static long SoftRendersInFlight => System.Threading.Interlocked.Read(ref s_softRendersInFlight);

        /// <summary>
        /// 一次连接的软渲染资源。断开时先使状态失效并取消 WinRT 操作；若仍有
        /// SetBitmapAsync 在飞，则由最后一个退出的渲染任务释放其 bitmap。
        /// </summary>
        private sealed class SoftRenderState
        {
            private int _inFlight;
            private int _rendering;
            private int _retired;
            private int _disposed;

            public SoftRenderState(SoftwareBitmapSource source)
            {
                Source = source;
                System.Threading.Interlocked.Increment(ref RdpView.s_liveSoftRenderStates);
            }

            public SoftwareBitmapSource? Source { get; private set; }
            public SoftwareBitmap? Bitmap { get; set; }
            public int BitmapWidth { get; set; }
            public int BitmapHeight { get; set; }
            public System.Threading.CancellationTokenSource Cancellation { get; } = new();
            public bool IsRetired => System.Threading.Volatile.Read(ref _retired) != 0;

            public bool TryBeginRender() =>
                !IsRetired && System.Threading.Interlocked.CompareExchange(ref _rendering, 1, 0) == 0;

            public void EndRender() => System.Threading.Volatile.Write(ref _rendering, 0);

            public void Enter()
            {
                System.Threading.Interlocked.Increment(ref _inFlight);
                System.Threading.Interlocked.Increment(ref RdpView.s_softRendersInFlight);
            }

            public void Exit()
            {
                var remaining = System.Threading.Interlocked.Decrement(ref _inFlight);
                System.Threading.Interlocked.Decrement(ref RdpView.s_softRendersInFlight);
                if (remaining == 0 && IsRetired)
                {
                    DisposeResources();
                }
            }

            public void Retire()
            {
                if (System.Threading.Interlocked.Exchange(ref _retired, 1) != 0)
                {
                    return;
                }

                Cancellation.Cancel();
                if (System.Threading.Volatile.Read(ref _inFlight) == 0)
                {
                    DisposeResources();
                }
            }

            private void DisposeResources()
            {
                if (System.Threading.Interlocked.Exchange(ref _disposed, 1) != 0)
                {
                    return;
                }

                var bitmap = Bitmap;
                Bitmap = null;
                BitmapWidth = 0;
                BitmapHeight = 0;
                Source = null;
                try
                {
                    bitmap?.Dispose();
                }
                finally
                {
                    try
                    {
                        Cancellation.Dispose();
                    }
                    finally
                    {
                        System.Threading.Interlocked.Decrement(ref RdpView.s_liveSoftRenderStates);
                    }
                }
            }
        }
        private bool _renderPathLogged;        // 「当前渲染路径」只写一次日志
        private long _frameArrivals;           // 统计窗口内的帧到达次数（原生线程写）
        private long _maxArrivalGapMs;         // 统计窗口内最大帧间隔（原生线程写）
        private long _statsWindowStart;        // 统计窗口起点（UI 线程写）
        private int _frameSeq;                 // 帧到达序号（原生线程递增）
        private int _presentedSeq;             // 已呈现到的序号（UI 线程写）
        private long _windowStartFrames;       // 统计窗口起点时的 FramesRendered（算呈现帧率用）
        private readonly RdpFramePhaseStats _phaseStats = new(); // 5 秒窗口的分阶段计时/字节聚合
        private int _lastGfxState = -1;        // 上次打印过的 GFX 状态
        private uint _lastDvcCount;            // 上次打印过的通道数
        private double _remoteWidth;
        private double _remoteHeight;
        private bool _inputLocked;             // 锁定：鼠标穿透 + 键盘映射全部停用

        private const ulong VkNumLock = 0x90;  // 见 OnKeyDown：切换后需补发锁状态快照

        /// <summary>画面刷新次数（诊断/验收计数）。</summary>
        public ulong FramesRendered { get; private set; }

        /// <summary>已转投远端的按键次数（诊断/验收计数）。</summary>
        public ulong KeysForwarded { get; private set; }

        /// <summary>会话断开（含原生 reason 与详情）。</summary>
        public event EventHandler<(int Session, int Reason, string Detail)>? SessionDisconnected;

        /// <summary>全屏切换请求（F11）。由页面/宿主决定是否进入全屏。</summary>
        public event EventHandler? FullScreenToggleRequested;

        /// <summary>诊断日志出口（页面注入 VM.AppendLog）。</summary>
        public Action<string>? DiagnosticLog;

        public RdpView()
        {
            InitializeComponent();
            Loaded += (_, _) =>
            {
                _dispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
                IsTabStop = true;
                IsHitTestVisible = true;
            };

            // ---- 指针（M3）----
            PointerMoved += OnPointerMoved;
            PointerPressed += OnPointerPressed;
            PointerReleased += OnPointerReleased;
            PointerWheelChanged += OnPointerWheel;
            PointerExited += OnPointerReleased; // 拖拽中移出画面 → 释放按键，防远端卡键

            // ---- 键盘（M3）----
            KeyDown += OnKeyDown;
            KeyUp += OnKeyUp;

            // 画面上的双击**不做**任何本地动作：双击要原样转投远端（双击图标、选中文字），
            // 全屏只留「F11 + 全屏按钮」两个显式入口。
        }

        /// <summary>连接目标。返回 false 表示发起失败（错误经状态文本显示）。</summary>
        public bool Connect(RdpConnectInfo info)
        {
            DetachClient();

            // 渲染后端选择必须在任何判断之前同步进来（DisableD3D 本来只看命令行，
            // 现在覆盖 render_backend 配置项）。
            Yomori.Services.RdpD3DRenderer.PushRenderBackend(
                RenderBackends.Normalize(App.ViewModel.RdpSettings.RenderBackend)
                == RenderBackends.Software);

            PrepareRenderSurface();

            _dispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
            _client = new RdpEmbeddedClient();
            HookClient(_client);
            MarkPendingFirstFrame();
            StartPresentTimer();

            ShowOverlay("连接中…", progress: true);
            try
            {
                return _client.Connect(info);
            }
            catch (Exception ex)
            {
                ShowOverlay($"连接失败：{ex.Message}", progress: false);
                return false;
            }
        }

        /// <summary>接管外部 client（全屏窗口复用同一会话时用）。</summary>
        public void AttachClient(RdpEmbeddedClient client)
        {
            DetachClient();

            // 同 Connect：入口处同步一次渲染后端选择。
            Yomori.Services.RdpD3DRenderer.PushRenderBackend(
                RenderBackends.Normalize(App.ViewModel.RdpSettings.RenderBackend)
                == RenderBackends.Software);
            PrepareRenderSurface();

            _client = client;
            HookClient(_client);

            _dispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
            // 切页回来 / 全屏接管：远端可能自始至终没画过任何东西（静止桌面），
            // FrameArrived 一次都不会来，必须允许节拍器主动抓一次原生缓存的最后一帧。
            MarkPendingFirstFrame();
            StartPresentTimer();
            ShowOverlay("全屏会话", progress: false, hide: true);
            UpdatePanelLayout();
        }

        /// <summary>
        /// 按当前渲染后端选择准备画面。
        ///
        /// 🔑 <b>这个方法必须在连接前调</b>（2026-10-15 修）。
        ///
        /// 之前的疏漏：软渲染只被当成“回退方案”—— <c>_bitmapSource</c> 只在
        /// <c>OnFrameArrived</c> 的 D3D 初始化分支里创建（初始化失败 / Present 失败两处）。
        /// 用户主动选 <c>render_backend=software</c> 时，这两条分支都不走，
        /// <c>_bitmapSource</c> 永远是 null，而 <c>OnPresentTick</c> 的软渲染分支要求
        /// <c>_bitmapSource is not null</c> —— 结果就是「帧一直在来（日志 40+ 次/秒）但呈现 0 帧、黑屏」。
        ///
        /// 现在把它提前到入口：选了软渲染就立即把 SoftwareBitmapSource 挂好，
        /// 不再依赖 D3D 先失败。
        /// </summary>
        private void PrepareRenderSurface()
        {
            if (Yomori.Services.RdpD3DRenderer.ShouldUseSoftware)
            {
                // 软渲染：直接把 Image 挂上 SoftwareBitmapSource（不用等 D3D 先失败）
                if (_softRenderState is null || _softRenderState.IsRetired)
                {
                    CreateSoftRenderState();
                }

                PartFrame.Visibility = Visibility.Visible;
                PartPanel.Visibility = Visibility.Collapsed;
                PartViewbox.Visibility = Visibility.Collapsed;

                LogRenderPathOnce("SoftwareBitmap（主动选择的软渲染）");
                return;
            }

            // D3D 路线：画面由 OnFrameArrived 惰性初始化（要用第一帧的真实尺寸），
            // 这里只确保 Image 不会把交换链面板遮住。
            PartFrame.Source = null;
            PartFrame.Visibility = Visibility.Collapsed;
        }

        /// <summary>只解除事件绑定与画面，不断开会话（交还另一处 RdpView 时用）。</summary>
        public void DetachClient()
        {
            if (_client is not null)
            {
                _client.Connected -= OnClientConnected;
                _client.FrameArrived -= OnFrameArrived;
                _client.DesktopResized -= OnDesktopResized;
                _client.Disconnected -= OnClientDisconnected;
                _client = null;
            }
            // 推进世代号：让已在飞的软渲染任务在下一个检查点自行退出
            System.Threading.Interlocked.Increment(ref _renderGeneration);
            _renderer?.Dispose();
            _renderer = null;
            _initQueued = false;
            _d3dFailed = false;
            _renderPathLogged = false;
            _statsWindowStart = 0;
            System.Threading.Interlocked.Exchange(ref _frameArrivals, 0);
            System.Threading.Interlocked.Exchange(ref _maxArrivalGapMs, 0);
            _phaseStats.Reset();
            RetireSoftRenderState();
            _presentTimer?.Stop();
            _presentTimer = null;
            _pendingFirstFrame = false;
            _pendingFirstFrameTries = 0;
            _lastFrameArrivedTick = 0;
            _frameSeq = 0;
            _presentedSeq = 0;
            _windowStartFrames = 0;
            _lastGfxState = -1;
            _lastDvcCount = 0;
            PartFrame.Source = null;
            PartFrame.Visibility = Visibility.Collapsed;
            PartPanel.Visibility = Visibility.Collapsed;
            PartViewbox.Visibility = Visibility.Collapsed;
            FramesRendered = 0;
            KeysForwarded = 0;
            ShowOverlay("未连接", progress: false);
        }

        /// <summary>断开会话并复位画面。幂等。</summary>
        public void Disconnect()
        {
            if (_client is null)
            {
                return;
            }
            UnhookClient(_client);
            _client.Dispose();
            _client = null;
            // 推进世代号：让已在飞的软渲染任务在下一个检查点自行退出
            System.Threading.Interlocked.Increment(ref _renderGeneration);
            _renderer?.Dispose();
            _renderer = null;
            _initQueued = false;
            _d3dFailed = false;
            _renderPathLogged = false;
            _statsWindowStart = 0;
            System.Threading.Interlocked.Exchange(ref _frameArrivals, 0);
            System.Threading.Interlocked.Exchange(ref _maxArrivalGapMs, 0);
            _phaseStats.Reset();
            RetireSoftRenderState();
            _presentTimer?.Stop();
            _presentTimer = null;
            _pendingFirstFrame = false;
            _pendingFirstFrameTries = 0;
            _lastFrameArrivedTick = 0;
            _frameSeq = 0;
            _presentedSeq = 0;
            _windowStartFrames = 0;
            _lastGfxState = -1;
            _lastDvcCount = 0;
            PartFrame.Source = null;
            PartFrame.Visibility = Visibility.Collapsed;
            PartPanel.Visibility = Visibility.Collapsed;
            PartViewbox.Visibility = Visibility.Collapsed;
            FramesRendered = 0;
            KeysForwarded = 0;
            ShowOverlay("未连接", progress: false);
        }

        /// <summary>暂停本渲染器（全屏时页面内实例用），恢复用 ResumeRendering。</summary>
        public void SuspendRendering() => _renderSuspended = true;

        public void ResumeRendering() => _renderSuspended = false;

        private void RetireSoftRenderState()
        {
            var state = _softRenderState;
            _softRenderState = null;
            state?.Retire();
        }

        /// <summary>
        /// 标记「本实例还没呈现过画面」——节拍器会因此在没有新帧的情况下也主动抓一帧。
        /// 接管一条已经连上的会话（切页回来 / 全屏接管）时必须调，否则远端静止就永远黑着。
        /// </summary>
        private void MarkPendingFirstFrame()
        {
            _pendingFirstFrame = true;
            _pendingFirstFrameTries = 0;
        }

        /// <summary>
        /// 用给定帧尺寸建 D3D 渲染器并挂到面板上；已建好则直接返回 true。
        ///
        /// ⚠️ <b>必须在 UI 线程调用</b>（<see cref="RdpD3DRenderer.Initialize"/> 内部要 SetSwapChain 碰 XAML 面板）。
        /// ⚠️ <b>幂等</b>：OnFrameArrived 的排队体和呈现节拍器两条路都可能走到这里。
        /// </summary>
        private bool EnsureRendererOnUi(uint width, uint height)
        {
            if (_renderer is { Initialized: true })
            {
                return true;
            }

            if (_d3dFailed || Yomori.Services.RdpD3DRenderer.ShouldUseSoftware)
            {
                return false;
            }

            _renderer = new RdpD3DRenderer();
            if (_renderer.Initialize(PartPanel, width, height))
            {
                PartPanel.Visibility = Visibility.Visible;
                PartViewbox.Visibility = Visibility.Visible;
                PartFrame.Visibility = Visibility.Collapsed;
                DiagnosticLog?.Invoke($"[embed] D3D 渲染器初始化成功（{width}×{height}）");
                LogRenderPathOnce("D3D11 / SwapChainPanel");
            }
            else
            {
                DiagnosticLog?.Invoke($"[embed] D3D 初始化失败（{_renderer.LastError}），回退 SoftwareBitmap");
                _renderer.Dispose();
                _renderer = null;
                _d3dFailed = true;
                CreateSoftRenderState();
                PartFrame.Visibility = Visibility.Visible;
                PartPanel.Visibility = Visibility.Collapsed;
                PartViewbox.Visibility = Visibility.Collapsed;
            }

            // 面板 DIP 尺寸 = 帧尺寸；Viewbox(Uniform) 负责缩放与居中
            PartPanel.Width = width;
            PartPanel.Height = height;
            PartViewbox.Visibility = PartPanel.Visibility;
            // Connected 事件可能被错过（HookClient 晚于连接），这里补写尺寸 ——
            // 必须写，否则 UpdatePanelLayout / 坐标映射会一直拿到 0（M5 黑屏同源问题）。
            _remoteWidth = width;
            _remoteHeight = height;

            return _renderer is { Initialized: true };
        }

        private bool IsCurrentClient(RdpEmbeddedClient client, int generation) =>
            ReferenceEquals(_client, client) &&
            generation == System.Threading.Volatile.Read(ref _renderGeneration);

        private void CreateSoftRenderState()
        {
            RetireSoftRenderState();
            _softRenderState = new SoftRenderState(new SoftwareBitmapSource());
            PartFrame.Source = _softRenderState.Source;
        }

        /// <summary>启动 16ms 呈现节拍器（静默检测 + 兑底节流，见 OnPresentTick）。</summary>
        private void StartPresentTimer()
        {
            if (_presentTimer is not null)
            {
                return;
            }
            _presentTimer = _dispatcher?.CreateTimer();
            if (_presentTimer is null)
            {
                return;
            }
            _presentTimer.Interval = TimeSpan.FromMilliseconds(16);
            _presentTimer.Tick += OnPresentTick;
            _presentTimer.Start();
        }

        /// <summary>当前绑定的客户端（全屏窗口接管同一会话时读取）。</summary>
        public RdpEmbeddedClient? CurrentClient => _client;

        /// <summary>注入鼠标事件（flags 取 YcnPtrFlags 常量）。</summary>
        public bool SendMouse(uint flags, ushort x, ushort y) => _client?.SendMouse(flags, x, y) ?? false;

        /// <summary>注入键盘事件。</summary>
        public bool SendKey(bool down, bool extended, ushort scancode)
            => _inputLocked ? false : (_client?.SendKey(down, extended, scancode) ?? false);

        /// <summary>
        /// 锁定 / 解锁本画面的输入转发。
        /// 锁定时鼠标穿透（指针事件不再转投远端）与键盘映射（含全屏钩子里的 SendKey）都停用，
        /// 但画面照常接收与渲染 —— 只是本机不再往里送输入。
        /// 事件用来让宿主刷新顶部条（锁定按钮的文案/图标）。
        /// </summary>
        public bool IsInputLocked
        {
            get => _inputLocked;
            set
            {
                if (_inputLocked == value)
                {
                    return;
                }

                _inputLocked = value;

                // 锁定瞬间把「按下的键」补一个释放，免得远端卡键（拖拽中的鼠标同理）
                if (value)
                {
                    ReleaseStuckButtons();
                }

                InputLockChanged?.Invoke(this, value);
            }
        }

        /// <summary>锁定状态变化（参数 = 新的锁定状态）。</summary>
        public event EventHandler<bool>? InputLockChanged;

        /// <summary>锁定前把可能卡住的按键 / 鼠标按钮释放掉，避免远端一直按着。</summary>
        private void ReleaseStuckButtons()
        {
            var client = _client;
            if (client is null)
            {
                _leftDown = _rightDown = _middleDown = false;
                return;
            }

            if (_leftDown)
            {
                client.SendMouse(YcnPointerFlags.Button1, 0, 0);
            }
            if (_rightDown)
            {
                client.SendMouse(YcnPointerFlags.Button2, 0, 0);
            }
            if (_middleDown)
            {
                client.SendMouse(YcnPointerFlags.Button3, 0, 0);
            }
            _leftDown = _rightDown = _middleDown = false;
        }

        private void HookClient(RdpEmbeddedClient client)
        {
            client.Connected += OnClientConnected;
            client.FrameArrived += OnFrameArrived;
            client.DesktopResized += OnDesktopResized;
            client.Disconnected += OnClientDisconnected;
        }

        private void UnhookClient(RdpEmbeddedClient client)
        {
            client.Connected -= OnClientConnected;
            client.FrameArrived -= OnFrameArrived;
            client.DesktopResized -= OnDesktopResized;
            client.Disconnected -= OnClientDisconnected;
        }

        private void OnClientConnected(object? sender, (int Session, uint Width, uint Height) e)
        {
            if (sender is not RdpEmbeddedClient client || !ReferenceEquals(_client, client))
            {
                return;
            }

            var generation = System.Threading.Volatile.Read(ref _renderGeneration);
            // 锁状态必须在**任何按键之前**同步过去：小键盘与方向键共用扫描码，
            // 差异全靠服务器侧 NumLock（见 KeyboardToggleState 注释）。
            // 事件在原生线程触发，同步是线程安全的入队操作，直接调即可。
            client.SyncKeyboardState();
            // 🔑 尺寸写入与其他 D3D 操作一样排到 UI 线程，与 OnDesktopResized /
            // OnFrameArrived 的写点收口到同一线程 —— 三处曾各写各的，双线程可见性不一致
            // 时 Present 会拿旧尺寸比对帧尺寸，误判成「需要重建交换链」。
            var enqueued = _dispatcher?.TryEnqueue(() =>
            {
                if (!IsCurrentClient(client, generation))
                {
                    return;
                }

                _remoteWidth = e.Width;
                _remoteHeight = e.Height;
                ShowOverlay($"已连接 {e.Width}×{e.Height}", progress: false, hide: true);
            });
        }

        private void OnDesktopResized(object? sender, (int Session, uint Width, uint Height) e)
        {
            if (sender is not RdpEmbeddedClient client || !ReferenceEquals(_client, client))
            {
                return;
            }

            var generation = System.Threading.Volatile.Read(ref _renderGeneration);
            // 🔑 远端分辨率**只在 UI 线程改**。本回调由原生帧线程触发，
            // 而 D3D 设备的其余全部操作（Initialize / Present / 回退时Dispose）都在 UI 线程
            // —— 一个隐式「单线程独占」的资源曾被这里从第二个线程戳了一下，
            // 于是 ResizeBuffers 与 Present/CopyResource 撞在一起（偶发，表现为整个窗口假死）。
            // 原代码把 _remoteWidth/_remoteHeight 也在原生线程直接写，双线程同时改同一组尺寸，
            // 正好让 Present 里的尺寸不一致分支（自己也会 ResizeRemote）与这里互相踩。
            var enqueued = _dispatcher?.TryEnqueue(() =>
            {
                if (!IsCurrentClient(client, generation))
                {
                    return;
                }

                _remoteWidth = e.Width;
                _remoteHeight = e.Height;
                _renderer?.ResizeRemote(e.Width, e.Height);
                UpdatePanelLayout();
            });
        }

        private void OnFrameArrived(object? sender, RdpFrameEventArgs e)
        {
            if (_renderSuspended)
            {
                return; // 全屏期间页面内渲染器挂起，全屏窗口的渲染器接管
            }

            if (sender is not RdpEmbeddedClient client || !ReferenceEquals(_client, client))
            {
                return;
            }
            var generation = System.Threading.Volatile.Read(ref _renderGeneration);

            // 服务器按条带逐步重绘，条带中途快照会撕裂（M5 实测）：这里只记录到达时间，
            // 实际取帧交给呈现节拍器（16ms tick）：等 end_paint 静默 ≥8ms（一轮画完）
            // 或兜底节流 60ms（持续动画 16fps，撕裂均匀化）再抓帧呈现
            var arrival = Environment.TickCount64;
            var prevArrival = System.Threading.Volatile.Read(ref _lastFrameArrivedTick);
            if (prevArrival != 0)
            {
                var gap = arrival - prevArrival;
                if (gap > System.Threading.Interlocked.Read(ref _maxArrivalGapMs))
                {
                    System.Threading.Interlocked.Exchange(ref _maxArrivalGapMs, gap);
                }
            }
            System.Threading.Interlocked.Increment(ref _frameArrivals);
            System.Threading.Volatile.Write(ref _lastFrameArrivedTick, arrival);
            // 用序号而不是 bool：bool 的「读-清」之间有竞争窗口，会丢掉正好落在那里的帧
            // （实测：服务器 37 帧/秒 → 呈现只有 31 帧/秒，就是被这个窗口吃掉的）
            System.Threading.Interlocked.Increment(ref _frameSeq);

            // 🔑 远端尺寸只在 UI 线程写（本回调与 OnDesktopResized 都排到 dispatcher 上），
            // 原生线程**只读不写**。原代码在这里直接赋值，等于两个线程同时写同一组字段，
            // 而 Present 要拿它跟帧尺寸比对后决定是否重建交换链 —— 读到撕裂值就会误判。

            // 惰性初始化 D3D（用事件参数里的尺寸，无需 memcpy）；失败后本实例固定走软渲染。
            // 注意：panel 尺寸直接用帧尺寸 —— VM 路径下 Connected 事件可能在挂钩前
            // 已错过（_remoteWidth 为 0），不能依赖它（M5 黑屏根因）
            if (_renderer is null && !_initQueued && !_d3dFailed && !Yomori.Services.RdpD3DRenderer.ShouldUseSoftware)
            {
                _initQueued = true;
                if (!IsCurrentClient(client, generation))
                {
                    _initQueued = false;
                    return;
                }

                var initW = e.Width;
                var initH = e.Height;
                var enqueued = _dispatcher?.TryEnqueue(() =>
                {
                    if (!IsCurrentClient(client, generation))
                    {
                        return;
                    }

                    EnsureRendererOnUi(initW, initH);
                });
                if (enqueued != true)
                {
                    //排队失败（无dispatcher / 页面已卸载）：必须复位，否则再也不会有初始化，
                    // 且下面那个补写分支会误以为「已排队」而只写尺寸、不建渲染器。
                    if (IsCurrentClient(client, generation))
                    {
                        _initQueued = false;
                        _remoteWidth = e.Width;
                        _remoteHeight = e.Height;
                    }
                }
            }
            else if (_remoteWidth == 0 || _remoteHeight == 0)
            {
                // ⚠️ 走软渲染（--embed-nod3d）或渲染器已存在但尺寸还没落地时，
                // 上面的初始化分支不会执行，尺寸就永远停在 0 —— UpdatePanelLayout 会直接 return，
                // 坐标映射也会拿到 0 视口。
                // 🔑 本方法由**原生帧线程**触发，所以必须排到 UI 线程写 —— 直接赋值正是我们
                // 刚修掉的那个竞态（原生线程与 UI 线程双写尺寸，Present 读到撕裂值会误判成
                // 「帧尺寸变了」而重建交换链）。判据也放进UI 线程里读，保证读写同线程。
                _dispatcher?.TryEnqueue(() =>
                {
                    if (!IsCurrentClient(client, generation))
                    {
                        return;
                    }

                    if (_remoteWidth == 0 || _remoteHeight == 0)
                    {
                        _remoteWidth = e.Width;
                        _remoteHeight = e.Height;
                    }
                });
            }

            if (_renderer is { Initialized: true })
            {
                return; // D3D 路线：呈现交给节拍器（OnPresentTick）
            }

            if (!_d3dFailed && !Yomori.Services.RdpD3DRenderer.ShouldUseSoftware)
            {
                return; // 初始化排队中：下一拍就有了
            }

            // 软渲染回退路线也**不在这里直接渲染** —— 同样交给呈现节拍器节流。
            // （原先这里每帧 TryEnqueue 直渲，等于撕裂修复在回退路线上完全没生效。）
            // 本方法只负责记 _lastFrameArrivedTick，抓帧时机由 OnPresentTick 决定。
        }

        /// <summary>
        /// 呈现节拍器 tick：**固定 60Hz 节拍**（16ms 定时器）。
        ///
        /// 只在「两拍之间收到过新帧」时抓帧呈现（_frameDirty），故实际呈现帧率 =
        /// min(60, 服务器推帧率)；静止画面不重复 Present（重复送同一帧没有意义）。
        ///
        /// 早先那套「等 end_paint 静默 ≥8ms 才算一轮画完」的启发式已删除：服务器持续推帧时
        /// 它永远不成立，只会退化成「每 60ms 兜底抓一次」—— 那正是最后那点撕裂的来源之一。
        /// 「抓到的必然是完整帧」现在由原生侧保证：ycn_rdp_copy_frame 在 g_lock 内整帧拷贝。
        /// </summary>
        private void OnPresentTick(object? sender, object e)
        {
            if (_renderSuspended)
            {
                return;
            }

            // 还没上屏（刚 Attach、布局尚未完成）：交换链和软件位图都要往可视树上挂，
            // 这时候抓帧只会白抓 —— 让 _pendingFirstFrame 留着，下一拍再来。
            if (!IsLoaded)
            {
                return;
            }
            var now = Environment.TickCount64;
            EmitFrameRhythmStats(now);

            var seq = System.Threading.Volatile.Read(ref _frameSeq);

            // 「还没呈现过第一帧」时不受 _frameSeq 约束：远端静止的桌面永远不会触发
            // FrameArrived（原生只在 end_paint 回调），光等新帧就是永久黑屏。
            // 原生缓存里始终有最后一帧，抓它即可。
            if (seq == _presentedSeq && !_pendingFirstFrame)
            {
                return; // 还没有新帧：保留最后一帧即可
            }
            var client = _client;
            if (client is null)
            {
                return;
            }

            // 只计「真正开始取帧之后」的同步工作：EmitFrameRhythmStats 每 5 秒一次，
            // 不属于逐帧渲染成本。tick 计时用于区分 UI 线程忙与等待 SetBitmapAsync。
            var tickStart = System.Diagnostics.Stopwatch.GetTimestamp();
            try
            {
                var grabStart = System.Diagnostics.Stopwatch.GetTimestamp();
                var copied = client.TryCopyFrame(out var w, out var h, out var stride, out var pixels);
                var grabTicks = System.Diagnostics.Stopwatch.GetTimestamp() - grabStart;
                if (!copied)
                {
                    // ⚠️ 这里**不能**推进 _presentedSeq：会话还在 CONNECTING / 尺寸切换瞬间
                    // 抓帧会失败，一旦推进就再也等不到这一帧了（正是切页回来黑屏的另一半原因）。
                    if (_pendingFirstFrame && ++_pendingFirstFrameTries > PendingFirstFrameMaxTries)
                    {
                        _pendingFirstFrame = false; // 会话大概已断：停止空转，等下一条连接
                    }
                    return; // 尺寸切换瞬间等：下一拍再来
                }

                _phaseStats.RecordGrab(grabTicks, w, h, stride);
                _presentedSeq = seq;
                _pendingFirstFrame = false;

                // 渲染器是挂到「本实例的面板」上的，切页回来时新实例还没有渲染器；
                // 上面既然已经抓到帧（拿得到真实尺寸），就地建好，不必再等下一帧触发。
                if (_renderer is null && !_d3dFailed && !Yomori.Services.RdpD3DRenderer.ShouldUseSoftware)
                {
                    EnsureRendererOnUi(w, h);
                }

                if (_renderer is { Initialized: true })
                {
                    var presentStart = System.Diagnostics.Stopwatch.GetTimestamp();
                    var presented = _renderer.Present(pixels, w, h, stride);
                    var presentTicks = System.Diagnostics.Stopwatch.GetTimestamp() - presentStart;
                    // 失败也要进 avg/max（bytes=0），否则慢失败会被成功路径采样掩盖。
                    _phaseStats.RecordD3D(presentTicks, w, h, stride, presented);
                    if (presented)
                    {
                        FramesRendered++;
                    }
                    else
                    {
                        DiagnosticLog?.Invoke($"[embed] Present 失败：{_renderer.LastError}");
                        _d3dFailed = true; // 本帧起回退软渲染（renderer 失效不再重试）
                        _renderer.Dispose();
                        _renderer = null;
                        CreateSoftRenderState();
                        PartFrame.Visibility = Visibility.Visible;
                        PartPanel.Visibility = Visibility.Collapsed;
                        PartViewbox.Visibility = Visibility.Collapsed;
                    }
                }
                else if (_softRenderState is { IsRetired: false } state && state.TryBeginRender())
                {
                    // 软渲染回退路线：同一套节拍（否则回退后撕裂原样回来）。
                    // pixels 是本拍已经抓到的完整帧，直接同步 CopyFromBuffer；
                    // 旧实现会在 RenderFrameAsync 里再 TryCopyFrame 一次，等于每帧白拷一份。
                    LogRenderPathOnce("SoftwareBitmap（软渲染回退，无 D3D）");
                    BeginSoftRender(state, _renderGeneration, pixels, w, h, stride);
                }
            }
            finally
            {
                _phaseStats.RecordTick(System.Diagnostics.Stopwatch.GetTimestamp() - tickStart);
            }
        }

        /// <summary>把当前实际渲染路径写一次日志（撕裂排查的第一手依据）。</summary>
        private void LogRenderPathOnce(string path)
        {
            if (_renderPathLogged)
            {
                return;
            }
            _renderPathLogged = true;
            DiagnosticLog?.Invoke($"[embed] 当前渲染路径：{path}");
        }

        /// <summary>
        /// 每 5 秒把「帧到达节奏 / 实际呈现帧率」写一行日志（撕裂与帧率定位用）。
        ///
        /// 判读方式：条带（legacy bitmap）路径下服务器把一次桌面更新拆成大量碎帧，
        /// 到达频率会远高于画面帧率、且间隔参差；走 GFX 全帧管线时每次 end_paint
        /// 就是完整一帧，到达频率贴近真实画面帧率、间隔整齐。
        /// 「呈现」一栏是节拍器真正 Present 出去的帧率，上限就是节拍器频率（16ms ≈ 60Hz）。
        /// </summary>
        private void EmitFrameRhythmStats(long now)
        {
            if (_statsWindowStart == 0)
            {
                _statsWindowStart = now;
                _windowStartFrames = (long)FramesRendered;
                return;
            }
            var elapsed = now - _statsWindowStart;
            if (elapsed < 5000)
            {
                return;
            }
            var arrivals = System.Threading.Interlocked.Read(ref _frameArrivals);
            var maxGap = System.Threading.Interlocked.Read(ref _maxArrivalGapMs);
            System.Threading.Interlocked.Exchange(ref _frameArrivals, 0);
            System.Threading.Interlocked.Exchange(ref _maxArrivalGapMs, 0);
            _statsWindowStart = now;

            var presented = (long)FramesRendered - _windowStartFrames;
            _windowStartFrames = (long)FramesRendered;

            var diagClient = _client;
            if (diagClient is { } gfxClient)
            {
                var st = gfxClient.GfxState(out var dvc);
                if (st != _lastGfxState || dvc != _lastDvcCount)
                {
                    _lastGfxState = st;
                    _lastDvcCount = dvc;
                    var desc = st switch
                    {
                        0 => "未请求图形管线",
                        1 => "已订阅通道事件，但一条动态通道都没连上 —— 服务器没给 GFX",
                        2 => "GFX 通道已接入 GDI（全帧模式）",
                        3 => "动态通道连上了，但没有 GFX 那条（通道名不匹配）",
                        4 => "GFX 通道已连上，但 gdi_graphics_pipeline_init 失败（看 gfx_init / codecs_null）",
                        _ => "会话未知",
                    };
                    DiagnosticLog?.Invoke(
                        $"[embed] GFX 管线状态：{st}（{desc}）｜{gfxClient.Diag()}");
                }
            }

            var decodeStatus = diagClient?.DecodeStatus() ?? "decode_backend=unknown";
            var nativeDiag = diagClient?.Diag() ?? "native_diag=unavailable";
            DiagnosticLog?.Invoke(
                $"[embed] 帧到达节奏：{arrivals} 次 / {elapsed / 1000.0:F0}s" +
                $"（≈{arrivals * 1000.0 / elapsed:F1} 次/秒，最大间隔 {maxGap} ms）｜" +
                $"呈现 ≈{presented * 1000.0 / elapsed:F1} 帧/秒｜{decodeStatus}｜{nativeDiag}");

            // 分阶段聚合：只在 5 秒窗口边界取一次快照；热路径只累加 long，不逐帧打日志/分配。
            // 抓帧=TryCopyFrame（原生抓帧+整帧拷贝）；D3D=Map/Unmap+CopyResource+Present；
            // 软提交=CopyFromBuffer+SetBitmapAsync 发起；软等待=SetBitmapAsync 完成等待；
            // tick=OnPresentTick 同步工作总时长。这样能区分 UI 忙、复制与等待。
            var phase = _phaseStats.TakeSnapshot(elapsed);
            var resolution = phase.Width == 0 || phase.Height == 0
                ? "分辨率未知"
                : $"分辨率 {phase.Width}×{phase.Height} stride {phase.Stride}";
            var resolutionChanges = phase.ResolutionChanges > 0
                ? $"，本窗变化 {phase.ResolutionChanges} 次"
                : string.Empty;
            DiagnosticLog?.Invoke(
                $"[embed] 帧阶段（{elapsed / 1000.0:F1}s，{resolution}{resolutionChanges}）：" +
                $"抓帧 {phase.Grab.Count} 次 avg {phase.Grab.AverageMs:F2} / max {phase.Grab.MaxMs:F2} ms，" +
                $"拷贝 {phase.Grab.BytesPerSecond(elapsed) / 1048576.0:F1} MB/s｜" +
                $"D3D {phase.D3D.Count} 次（失败 {phase.D3DFailures}）avg {phase.D3D.AverageMs:F2} / max {phase.D3D.MaxMs:F2} ms，" +
                $"上载 {phase.D3D.BytesPerSecond(elapsed) / 1048576.0:F1} MB/s｜" +
                $"软提交 {phase.SoftCommit.Count} 次 avg {phase.SoftCommit.AverageMs:F2} / max {phase.SoftCommit.MaxMs:F2} ms，" +
                $"{phase.SoftCommit.BytesPerSecond(elapsed) / 1048576.0:F1} MB/s｜" +
                $"软等待 {phase.SoftAwait.Count} 次 avg {phase.SoftAwait.AverageMs:F2} / max {phase.SoftAwait.MaxMs:F2} ms｜" +
                $"tick {phase.Tick.Count} 次 avg {phase.Tick.AverageMs:F2} / max {phase.Tick.MaxMs:F2} ms");
        }

        /// <summary>
        /// 软渲染提交一帧。
        ///
        /// 🔑 <b>复用 SoftwareBitmap，不要每帧 new</b>（2026-10-16 修，dump 实证的假死根因）。
        ///
        /// 原来的写法每帧 <c>SoftwareBitmap.CreateCopyFromBuffer(...)</c>，即每秒新建约 24 个
        /// 8.3MB 的 WinRT 对象。.NET 的 WinRT 投影在 <c>SoftwareBitmap</c> 构造时会调
        /// <c>GC.AddMemoryPressure(8.3MB)</c>（这块内存不受 GC 直接管理，得靠压力通知催它回收）。
        /// 于是每秒约 200MB 的压力涌进 GC，累积到阈值后 <c>AddMemoryPressure</c> 内部强制完整 GC，
        /// 而 GC 又要处理 LOH 上的帧缓冲与大量 WinRT 包装 —— UI 线程最终堵死在该调用里，
        /// 整窗假死、无任何日志（进程内看门狗也一起被拖住，所以它也没记录）。
        ///
        /// 现在：bitmap 尺寸不变就复用同一个实例，每帧只 <c>CopyFromBuffer</c> 原地更新内容，
        /// 内存压力从「每帧 8.3MB」降到「每连接一次 8.3MB」。
        ///
        /// 2026-10-31：只消费 <see cref="OnPresentTick"/> 已经抓到的 pixels，不再二次
        /// <c>TryCopyFrame</c>。CopyFromBuffer/CreateCopyFromBuffer 在第一次 await 之前同步完成；
        /// 之后只 await SetBitmapAsync。pixels（客户端复用缓冲）不会跨 await 被读取，
        /// 因此不会出现复用数组被下一拍覆盖导致的撕裂，客户端生命周期也与旧实现一致。
        /// </summary>
        private void BeginSoftRender(SoftRenderState state, int generation, byte[] pixels, uint width, uint height, uint stride)
        {
            System.Threading.Interlocked.Increment(ref _renderInFlight);
            state.Enter();
            Windows.Foundation.IAsyncAction? operation = null;
            try
            {
                // 世代/生命周期校验：本拍发出后连接可能已被换掉。
                // 这里绝不能 return —— Enter()/renderInFlight 已经计数，必须统一走下方 cleanup。
                if (!state.IsRetired
                    && generation == System.Threading.Volatile.Read(ref _renderGeneration)
                    && state.Source is not null)
                {
                    var commitStart = System.Diagnostics.Stopwatch.GetTimestamp();

                    // pixels 是复用缓冲，长度可能大于本帧实际字节数 —— 必须按 stride*height 切
                    var byteCount = checked((int)(stride * height));
                    var buffer = System.Runtime.InteropServices.WindowsRuntime.WindowsRuntimeBufferExtensions.AsBuffer(
                        pixels, 0, byteCount);

                    // 尺寸变化（远端改分辨率）才重建；否则原地更新，避免每帧触发 AddMemoryPressure
                    if (state.Bitmap is null || state.BitmapWidth != (int)width || state.BitmapHeight != (int)height)
                    {
                        state.Bitmap?.Dispose();
                        state.Bitmap = SoftwareBitmap.CreateCopyFromBuffer(
                            buffer, BitmapPixelFormat.Bgra8, (int)width, (int)height, BitmapAlphaMode.Ignore);
                        state.BitmapWidth = (int)width;
                        state.BitmapHeight = (int)height;
                    }
                    else
                    {
                        state.Bitmap.CopyFromBuffer(buffer);
                    }

                    // SetBitmapAsync 接收的是 SoftwareBitmap 本身。上面的同步拷贝已经完成；
                    // 从这里开始不再读取 pixels，所以复用帧缓冲不会跨 await。
                    operation = state.Source.SetBitmapAsync(state.Bitmap);
                    _phaseStats.RecordSoftCommit(
                        System.Diagnostics.Stopwatch.GetTimestamp() - commitStart, width, height, stride);
                }
            }
            catch
            {
                // 尺寸切换瞬间可能抛参数异常，丢帧即可
            }

            if (operation is null)
            {
                // 没有异步接管：本方法自己收尾（Exit/EndRender 各一次）。
                state.Exit();
                state.EndRender();
                System.Threading.Interlocked.Decrement(ref _renderInFlight);
                return;
            }

            // operation 非空后由 AwaitSoftRenderAsync 的 finally 接管收尾（Exit/EndRender 仍各一次）。
            _ = AwaitSoftRenderAsync(state, generation, operation);
        }

        /// <summary>等待 SetBitmapAsync 完成并收尾软渲染生命周期（与旧 RenderFrameAsync 一致）。</summary>
        private async System.Threading.Tasks.Task AwaitSoftRenderAsync(
            SoftRenderState state, int generation, Windows.Foundation.IAsyncAction operation)
        {
            var awaitStart = System.Diagnostics.Stopwatch.GetTimestamp();
            try
            {
                using (state.Cancellation.Token.Register(() => operation.Cancel()))
                {
                    await operation;
                }

                // await 回来后再校验：等待期间可能已断开 / 换过实例。
                // 不校验的话，后续计数会记到已废弃的对象上（无害但误导）。
                if (state.IsRetired || generation != System.Threading.Volatile.Read(ref _renderGeneration))
                {
                    return;
                }

                FramesRendered++;
            }
            catch
            {
                // 断开取消 / 尺寸切换：丢帧即可
            }
            finally
            {
                _phaseStats.RecordSoftAwait(System.Diagnostics.Stopwatch.GetTimestamp() - awaitStart);
                state.Exit();
                state.EndRender();
                System.Threading.Interlocked.Decrement(ref _renderInFlight);
            }
        }

        private void OnClientDisconnected(object? sender, (int Session, int Reason, string Detail) e)
        {
            if (sender is not RdpEmbeddedClient client || !ReferenceEquals(_client, client))
            {
                return;
            }

            var generation = System.Threading.Volatile.Read(ref _renderGeneration);
            var enqueued = _dispatcher?.TryEnqueue(() =>
            {
                if (!IsCurrentClient(client, generation))
                {
                    return;
                }

                ShowOverlay($"已断开：{e.Detail}", progress: false);
                SessionDisconnected?.Invoke(this, e);
            });
        }

        /// <summary>按 letterbox 计算面板 DIP 尺寸与 CompositionScale（swap 像素↔DIP）。</summary>
        private void UpdatePanelLayout()
        {
            if (_remoteWidth == 0 || _remoteHeight == 0)
            {
                return;
            }
            // 面板 DIP 尺寸 = 远端分辨率（整数）；等比缩放+居中全部交给 Viewbox(Uniform)，
            // CompositionScale 由框架按渲染变换自动维护（手动 COM 设置会被布局重置 → 1:1 裁切）
            PartPanel.Width = _remoteWidth;
            PartPanel.Height = _remoteHeight;
            PartViewbox.Visibility = PartPanel.Visibility;
        }

        private void ShowOverlay(string text, bool progress, bool hide = false)
        {
            PartStatusText.Text = text;
            PartProgress.IsActive = progress;
            PartProgress.Visibility = progress ? Visibility.Visible : Visibility.Collapsed;
            PartOverlay.Visibility = hide ? Visibility.Collapsed : Visibility.Visible;
        }

        // ---- 输入映射（M3）----
        private bool _leftDown;      // 左键按下（拖拽 = 移动时带 Button1 down 位）
        private bool _rightDown;
        private bool _middleDown;

        /// <summary>
        /// 当前指针是否可直接转发远端（画面未就绪/遮罩在时/已锁定时都不转发）。
        /// ⚠️ 锁定也走这里 —— 但**只拦转发**，不拦「锁定」按钮自己（它在格子顶部条上，不走本控件）。
        /// </summary>
        private bool InputForwardingEnabled =>
            _client is not null && !_inputLocked && PartOverlay.Visibility == Visibility.Collapsed;

        private bool TryForwardPointer(Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e, uint extraFlags)
        {
            var client = _client;
            if (client is null || !InputForwardingEnabled)
            {
                return false;
            }
            var pt = e.GetCurrentPoint(this);
            var (x, y) = RdpInputMapper.MapPointerToRemote(
                pt.Position.X, pt.Position.Y, ActualWidth, ActualHeight, _remoteWidth, _remoteHeight);
            return client.SendMouse(extraFlags, x, y);
        }

        private void OnPointerMoved(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            var flags = YcnPointerFlags.Move | (_leftDown ? YcnPointerFlags.Button1 : 0);
            TryForwardPointer(e, flags);
        }

        private void OnPointerPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            Focus(FocusState.Pointer);
            var props = e.GetCurrentPoint(this).Properties;
            uint flags;
            if (props.IsRightButtonPressed)
            {
                flags = YcnPointerFlags.Button2 | YcnPointerFlags.Down;
                _rightDown = true;
            }
            else if (props.IsMiddleButtonPressed)
            {
                flags = YcnPointerFlags.Button3 | YcnPointerFlags.Down;
                _middleDown = true;
            }
            else
            {
                flags = YcnPointerFlags.Button1 | YcnPointerFlags.Down;
                _leftDown = true;
            }
            e.Handled = TryForwardPointer(e, flags);
        }

        private void OnPointerReleased(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            var props = e.GetCurrentPoint(this).Properties;
            uint flags;
            if (props.IsRightButtonPressed || _rightDown)
            {
                flags = YcnPointerFlags.Button2;
                _rightDown = false;
            }
            else if (props.IsMiddleButtonPressed || _middleDown)
            {
                flags = YcnPointerFlags.Button3;
                _middleDown = false;
            }
            else
            {
                flags = YcnPointerFlags.Button1;
                _leftDown = false;
            }
            e.Handled = TryForwardPointer(e, flags);
        }

        private void OnPointerWheel(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            var client = _client;
            if (client is null || !InputForwardingEnabled)
            {
                return;
            }
            var delta = e.GetCurrentPoint(this).Properties.MouseWheelDelta;
            var (flags, units, repeats) = RdpInputMapper.MapWheel(delta);
            if (flags == 0 || repeats <= 0)
            {
                return;
            }
            var pt = e.GetCurrentPoint(this);
            var (x, y) = RdpInputMapper.MapPointerToRemote(
                pt.Position.X, pt.Position.Y, ActualWidth, ActualHeight, _remoteWidth, _remoteHeight);
            // units 是**旋转单位**（1 格 = 120），直接叠在 bit0-8（WheelRotationMask），**不能左移**：
            // bit8=WHEEL_NEGATIVE、bit9=WHEEL，移上去会把方向标志本身改写，
            // 向上/向下会编码成同一个值（表现为两个方向滚出一样的效果）。
            // 也不能发格数：服务器按 120 单位/格解释，发 1 格(=1单位)只会滚 1/120 的距离。
            var wire = flags | units;
            for (var i = 0; i < repeats; i++)
            {
                client.SendMouse(wire, x, y);
            }
            e.Handled = true;
        }

        private void OnKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
        {
            // F11：全屏切换
            if ((ulong)e.Key == 0x7A)
            {
                e.Handled = true;
                FullScreenToggleRequested?.Invoke(this, EventArgs.Empty);
                return;
            }
            if (!InputForwardingEnabled)
            {
                return;
            }
            var (sc, ext) = RdpInputMapper.MapKey((ushort)e.KeyStatus.ScanCode, e.KeyStatus.IsExtendedKey, (ulong)e.Key);
            if (sc == 0)
            {
                return;
            }
            if (_client?.SendKey(true, ext, sc) == true)
            {
                KeysForwarded++;
                e.Handled = true;
                // NumLock 键本身照常转发给服务器（它会跟着切），
                // 随后补一次状态快照，让本机锁状态与服务器对齐 ——
                // 否则用户在本机切了 NumLock，远端仍按旧状态解释小键盘（0-9 ↔ 方向键）。
                if ((ulong)e.Key == VkNumLock)
                {
                    _client.SyncKeyboardState();
                }
            }
        }

        private void OnKeyUp(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
        {
            // 锁定时不转发 —— 但按下时已经发出去的键，在锁定的瞬间由 ReleaseStuckButtons 兜底
            if (!InputForwardingEnabled)
            {
                return;
            }
            var (sc, ext) = RdpInputMapper.MapKey((ushort)e.KeyStatus.ScanCode, e.KeyStatus.IsExtendedKey, (ulong)e.Key);
            if (sc == 0)
            {
                return;
            }
            if (_client?.SendKey(false, ext, sc) == true)
            {
                KeysForwarded++;
                e.Handled = true;
            }
        }
    }
}
