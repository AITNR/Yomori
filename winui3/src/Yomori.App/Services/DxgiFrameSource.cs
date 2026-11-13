// S1 第七批：真实采集端帧源 —— DXGI Desktop Duplication（跑在目标会话的代理进程内）。
// 形状来自已实跑通过的 S0 探针 _probe/S0/DxgiProbe/Program.cs（Vortice 3.8.3，
// 2026-11-12 两条 RDP 会话实测 verdict=OK、1080p copyMs 均值 ≈4 ms）。
//
// ⚠️ 本文件依赖 Vortice.DXGI / Vortice.Direct3D11：主工程已引，Agent 工程需补引用。
//    _smoke 不链接本文件（用 fake ICaptureFrameSource 做端到端回环）。
// ⚠️ ACCESS_LOST（分辨率变更 / UAC / TDR / RDP 连接断开）必须重建 duplication + staging。
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Yomori.Models;

namespace Yomori.Services
{
    public sealed class DxgiFrameSource : ICaptureFrameSource, IDisposable
    {
        private const uint DxgiErrorAccessLost = 0x887A0002;

        private readonly int _adapterIndex;
        private readonly int _outputIndex;
        private readonly int _timeoutMs;
        private IDXGIFactory1? _factory;
        private ID3D11Device? _device;
        private ID3D11DeviceContext? _context;
        private IDXGIOutput1? _output1;
        private IDXGIOutputDuplication? _dup;
        private byte[]? _prev;
        private bool _disposed;

        public string? LastError { get; private set; }
        public int AccessLostCount { get; private set; }

        public DxgiFrameSource(int adapterIndex = 0, int outputIndex = 0, int timeoutMs = 100)
        {
            if (timeoutMs < 0) throw new ArgumentOutOfRangeException(nameof(timeoutMs));
            _adapterIndex = adapterIndex;
            _outputIndex = outputIndex;
            _timeoutMs = timeoutMs;
        }

        public bool TryCapture(out CapturedFrame frame)
        {
            frame = default;
            if (_disposed) return false;
            if (!EnsureInitialized()) return false;

            IDXGIResource? resource = null;
            try
            {
                var rr = _dup!.AcquireNextFrame((uint)_timeoutMs, out _, out resource);
                if (rr.Failure)
                {
                    if ((uint)rr.Code == DxgiErrorAccessLost)
                    {
                        AccessLostCount++;
                        ReleaseDuplication(); // 整套重建，下一拍重新 DuplicateOutput
                    }
                    return false; // 超时 / ACCESS_LOST 都只是本拍无帧
                }

                using var tex = resource!.QueryInterface<ID3D11Texture2D>();
                var td = tex.Description;
                int w = (int)td.Width, h = (int)td.Height;
                bool swapRb = td.Format.ToString().Contains("R8G8B8A8", StringComparison.OrdinalIgnoreCase);

                var sd = new Texture2DDescription
                {
                    Width = (uint)w,
                    Height = (uint)h,
                    MipLevels = 1,
                    ArraySize = 1,
                    Format = td.Format,
                    SampleDescription = new SampleDescription(1, 0),
                    Usage = ResourceUsage.Staging,
                    CPUAccessFlags = CpuAccessFlags.Read,
                    BindFlags = BindFlags.None,
                    MiscFlags = ResourceOptionFlags.None
                };
                using var staging = _device!.CreateTexture2D(sd);
                _context!.CopyResource(staging, tex);
                var mapped = _context.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);

                int tightStride = w * 4;
                byte[] buf = new byte[tightStride * h];
                byte[] row = new byte[tightStride];
                int srcPitch = (int)mapped.RowPitch;
                for (int y = 0; y < h; y++)
                {
                    Marshal.Copy(IntPtr.Add(mapped.DataPointer, y * srcPitch), row, 0, tightStride);
                    if (swapRb)
                        for (int x = 0; x < w; x++)
                            (row[x * 4], row[x * 4 + 2]) = (row[x * 4 + 2], row[x * 4]);
                    Buffer.BlockCopy(row, 0, buf, y * tightStride, tightStride);
                }
                _context.Unmap(staging, 0);

                LumaStats stats = LumaAnalyzer.Compute(buf, w, h, tightStride);
                double change = _prev is null ? 1.0
                    : LumaAnalyzer.ComputeChangeRatio(buf, _prev, w, h, tightStride);
                _prev = buf;
                var sample = new FrameSample(System.Environment.TickCount64,
                    stats.MeanLuma, stats.ZeroRatio, change, true);
                frame = new CapturedFrame(buf, (uint)w, (uint)h, (uint)tightStride,
                                          sample, Array.Empty<FrameRect>());
                return true;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                return false;
            }
            finally
            {
                resource?.Dispose();
                try { _dup?.ReleaseFrame(); } catch { /* 已重建 / 已释放则忽略 */ }
            }
        }

        private bool EnsureInitialized()
        {
            if (_dup is not null) return true;
            var adapters = new List<IDXGIAdapter1>();
            var outputs = new List<IDXGIOutput>();
            try
            {
                _factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
                for (uint i = 0; ; i++)
                {
                    var r = _factory.EnumAdapters1(i, out var a);
                    if (r.Failure || a is null) break;
                    adapters.Add(a);
                }
                if (_adapterIndex >= adapters.Count) { LastError = "没有指定的 adapter"; return false; }
                var adapter = adapters[_adapterIndex];
                for (uint i = 0; ; i++)
                {
                    var r = adapter.EnumOutputs(i, out var o);
                    if (r.Failure || o is null) break;
                    outputs.Add(o);
                }
                if (_outputIndex >= outputs.Count) { LastError = "没有指定的 output"; return false; }

                var hrDev = D3D11.D3D11CreateDevice(
                    adapter, DriverType.Unknown, DeviceCreationFlags.BgraSupport,
                    new[] { FeatureLevel.Level_11_0, FeatureLevel.Level_10_1 }, out _device);
                if (hrDev.Failure || _device is null) { LastError = "D3D11CreateDevice 失败"; return false; }
                _context = _device.ImmediateContext;
                _output1 = outputs[_outputIndex].QueryInterface<IDXGIOutput1>();
                _dup = _output1.DuplicateOutput(_device);
                LastError = null;
                return true;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                ReleaseDuplication();
                return false;
            }
            finally
            {
                // device / output1 已各自持有 COM 引用，枚举包装对象可以释放。
                foreach (var a in adapters) a.Dispose();
                foreach (var o in outputs) o.Dispose();
            }
        }

        private void ReleaseDuplication()
        {
            _dup?.Dispose(); _dup = null;
            _output1?.Dispose(); _output1 = null;
            _context?.Dispose(); _context = null;
            _device?.Dispose(); _device = null;
            _factory?.Dispose(); _factory = null;
            _prev = null;
        }

        public void Dispose()
        {
            _disposed = true;
            ReleaseDuplication();
        }
    }
}
