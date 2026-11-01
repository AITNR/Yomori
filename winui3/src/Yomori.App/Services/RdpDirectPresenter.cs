// RdpDirectPresenter：S1 直显（计划书 §S1）的托管侧外壳。
//
// 与 RdpD3DRenderer 的关系（**互斥，改前必读**）：
//   · RdpD3DRenderer 自己建 device + 交换链，再 SetSwapChain 到面板上；
//   · 本类不建任何 D3D 资源 —— device、交换链、VideoProcessor 全在原生层
//     （ycn_direct_present.c）。它只做三件事：启用/挂牌、让原生层挂上本面板的
//     交换链、每拍请求一次 present。
//
// 因此**一个面板上只能挂一条链**：启用直显成功，就绝不能再建托管渲染器。
// 一旦直显被关掉（失败或看门狗超时），原生层会先把面板解绑并释放自己的链，
// 托管渲染器才允许接管（见 ycn_dp_detach 的注释）。
//
// 线程纪律：
//   · TryArm 可以在建连后的任意线程调（此时还没挂面板，不碰 XAML）；
//   · AttachPanel / Present / Dispose 必须在 UI 线程（碰 SwapChainPanel）。
using System;
using System.Runtime.InteropServices;
using System.Text;
using WinRT;

namespace Yomori.Services
{
    internal sealed class RdpDirectPresenter : IDisposable
    {
        private readonly int _session;
        private IntPtr _panelPtr;
        private Microsoft.UI.Xaml.Controls.SwapChainPanel? _panel;
        private bool _armed;

        internal RdpDirectPresenter(int session, bool armed = false)
        {
            _session = session;
            _armed = armed;
        }

        /// <summary>会话是否已武装（原生层已建 device、注入解码器、打开直显闸门）。</summary>
        internal bool Armed => _armed;

        internal string? LastError { get; private set; }
        internal ulong FramesPresented { get; private set; }
        internal ulong FramesDropped { get; private set; }
        internal ulong LeasesOutstanding { get; private set; }

        /// <summary>
        /// 建连后立即调用：建 device、把 device 注入 FreeRDP、注册直显 sink、
        /// 打开本会话的直显闸门。必须早于第一帧 AVC420 —— H264 上下文的解码后端
        /// 在创建那一刻就定死了（D3D11VA vs DXVA2），晚了这一会话就永远没有直显帧。
        /// 失败时原生层保持原样，调用方按普通路径继续。
        /// </summary>
        internal bool TryArm(bool enable)
        {
            if (!RdpNativeInterop.SupportsDirectPresent)
            {
                LastError = "native exports missing";
                return false;
            }

            try
            {
                var rc = RdpNativeInterop.ycn_rdp_direct_enable(_session, enable ? 1u : 0u);
                if (rc != 0)
                {
                    LastError = $"direct enable failed: {rc}";
                    return false;
                }
                _armed = enable;
                return enable;
            }
            catch (Exception ex) when (ex is EntryPointNotFoundException
                                       || ex is DllNotFoundException
                                       || ex is BadImageFormatException)
            {
                LastError = ex.GetType().Name;
                RdpNativeInterop.MarkDirectPresentUnsupported();
                return false;
            }
        }

        /// <summary>
        /// 把本画面板交给原生层（创建交换链 + SetSwapChain）。
        /// ⚠️ 必须在 UI 线程调。
        /// </summary>
        internal bool AttachPanel(Microsoft.UI.Xaml.Controls.SwapChainPanel panel)
        {
            if (!_armed)
            {
                LastError = "not armed";
                return false;
            }

            try
            {
                _panel = panel;
                _panelPtr = ((IWinRTObject)panel).NativeObject.ThisPtr;
                var rc = RdpNativeInterop.ycn_rdp_direct_attach_panel(_session, _panelPtr);
                if (rc != 0)
                {
                    LastError = $"attach panel failed: {rc}";
                    DetachPanel();
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                LastError = "attach panel: " + ex.Message;
                DetachPanel();
                return false;
            }
        }

        /// <summary>呈现一拍。返回 YcnDirectStatus 之一。</summary>
        /// <remarks>
        /// 只有 <see cref="RdpNativeInterop.YcnDirectStatus.Ok"/> 才算真的上屏；
        /// NoFrame / NotReady 是「这一拍没帧」，只丢本拍，绝不能当成故障去关 D3D。
        /// </remarks>
        internal uint Present(out uint width, out uint height, out long totalTicks)
        {
            width = 0;
            height = 0;
            totalTicks = 0;
            if (!_armed || _panelPtr == IntPtr.Zero)
            {
                return RdpNativeInterop.YcnDirectStatus.Disabled;
            }

            var result = new RdpNativeInterop.YcnDirectPresentResult
            {
                Size = (uint)Marshal.SizeOf<RdpNativeInterop.YcnDirectPresentResult>(),
            };

            var start = System.Diagnostics.Stopwatch.GetTimestamp();
            int rc;
            try
            {
                rc = RdpNativeInterop.ycn_rdp_direct_present(_session, ref result);
            }
            catch (Exception ex)
            {
                LastError = "present: " + ex.Message;
                return RdpNativeInterop.YcnDirectStatus.Failed;
            }
            totalTicks = System.Diagnostics.Stopwatch.GetTimestamp() - start;

            FramesPresented = result.FramesPresented;
            FramesDropped = result.FramesDropped;
            LeasesOutstanding = result.LeasesOutstanding;
            width = result.Width;
            height = result.Height;

            if (rc != 0)
            {
                LastError = $"present rc={rc} hr=0x{result.LastHresult:X8} {DetailOf(ref result)}";
            }
            else if (result.Status == RdpNativeInterop.YcnDirectStatus.Failed)
            {
                LastError = $"present failed hr=0x{result.LastHresult:X8} {DetailOf(ref result)}";
            }

            return result.Status;
        }

        private static unsafe string DetailOf(ref RdpNativeInterop.YcnDirectPresentResult result)
        {
            fixed (byte* detail = result.Detail)
            {
                var length = 0;
                while (length < 96 && detail[length] != 0)
                {
                    length++;
                }
                return length == 0 ? string.Empty : Encoding.UTF8.GetString(detail, length);
            }
        }

        private void DetachPanel()
        {
            _panelPtr = IntPtr.Zero;
            _panel = null;
        }

        /// <summary>⚠️ UI 线程：原生层会解绑并释放面板上的交换链。</summary>
        public void Dispose()
        {
            if (_armed)
            {
                _armed = false;
                try { RdpNativeInterop.ycn_rdp_direct_enable(_session, 0u); }
                catch (Exception ex) when (ex is EntryPointNotFoundException
                                           || ex is DllNotFoundException
                                           || ex is BadImageFormatException)
                { /* 旧 DLL：本来就什么都没开 */ }
                catch (Exception ex) { LastError = "disable: " + ex.Message; }
            }
            DetachPanel();
        }
    }
}
