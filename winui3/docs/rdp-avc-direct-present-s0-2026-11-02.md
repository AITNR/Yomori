# RDP AVC 直显接管 — S0 只读取证报告（2026-11-02）

本文件是《RDP AVC 直显接管实施计划书》S0 阶段的交付物。S0 只加诊断、不写渲染代码：
记录真实解码 surface 的类型、格式，并验证它能否被 D3D11 直接消费，据此判定直显路线
在本机是否成立。

## 1. 结论

| 判定项 | 结果 | 依据 |
| --- | --- | --- |
| 解码后端可观测（`av_hwdevice_get_type_name`、AVFrame format、`hw_frames_ctx->sw_format`） | PASS | 见 §3.1、§3.2 的原始字段 |
| DXVA2 分支：surface 可被 D3D11 零拷贝访问 | FAIL | D3D9 解码面无共享句柄，`share_status=0x80004002` |
| DXVA2 分支：只能 CPU 回读 | PASS（判定成立） | `av_hwframe_transfer_data` 每帧真实回读字节被计数 |
| D3D11VA 分支：解码可用 | PASS（本轮修复后） | 修前 `Invalid pixfmt for hwaccel!`，修后 hardware frame 正常产出 |
| D3D11VA 分支：surface 可被 D3D11 直接消费 | PASS | `verdict=d3d11-direct`，`d3d11:vp-input-ok` |
| 软件解码分支：无 GPU 面，维持 CPU 回读 | PASS | `stage=software`、`transfer_bytes=0` |
| 跨设备共享句柄（渲染器自建 device 的备选路径） | FAIL（不需要） | `share_status=0x80004002`（E_NOINTERFACE，纹理非 NT 共享） |
| 关闭诊断后行为不变 | PASS | 只有 sink 旁路观测，GDI 绘制路径与默认后端未改 |

S0 的最终判定是 **`D3D11_DIRECT_PASS`（仅 D3D11VA 后端）** 与 **`CPU_READBACK_ONLY`（DXVA2 后端）** 并存：

- 直显路线可行，但它**必须建立在 D3D11VA 后端上**，不能建立在当前默认的 DXVA2 后端上。
- DAC 结论不靠推断：D3D11 分支是在解码器自己的 `ID3D11Device` 上真实创建
  `ID3D11VideoProcessorInputView` 成功的，也就是硬件转色的输入前提已经成立。

## 2. 本轮发现的第一成败条件

### 2.1 D3D11VA 解码失败的真实原因（上游 bug，非硬件不支持）

FreeRDP 3.32.0 的 `get_hwctx_image_format()` 把 `AV_HWDEVICE_TYPE_D3D11VA` 映射成
`AV_PIX_FMT_D3D11VA_VLD`。该值在 FFmpeg 4.0 之后不再由 D3D11VA hwaccel 提供，于是
`get_format` 请求了一个无效格式：

```
[h264 @ ...] Invalid pixfmt for hwaccel!
[h264 @ ...] Failed setup for format d3d11va_vld: hwaccel initialisation returned error.
[libavcodec_decompress]: Failed to decode video frame (status=-1094995529)
```

这与“本机 GPU 不支持 D3D11VA”完全不同。改为按 FFmpeg 版本返回 `AV_PIX_FMT_D3D11`
（`LIBAVUTIL_VERSION_INT >= 56.0.0`，旧版仍回退 `AV_PIX_FMT_D3D11VA_VLD`）后，
同一台机器、同一段码流解码成功，`successful_hardware_frames` 正常增长。

因此上一版计划书里“D3D11VA 当前解码失败”这一既成事实不再成立，S0 的分支判定随之改变：
D3D11 直显分支从“疑似不可用”变成“可直接使用”。

### 2.2 两个后端的 surface 事实

| 项目 | DXVA2 | D3D11VA |
| --- | --- | --- |
| `AVFrame::format` | `dxva2_vld` | `d3d11` |
| `hw_frames_ctx->sw_format` | `nv12` | `nv12` |
| 平面数（data[] 非空） | 1（`data[3]` = `IDirect3DSurface9*`） | 2（`data[0]` = `ID3D11Texture2D*`，`data[1]` = slice） |
| `av_hwframe_map(READ|DIRECT)` | 成功（拿到 D3D9 面指针） | 成功（拿到纹理指针） |
| VideoProcessor 输入视图 | 不适用（D3D9 面） | 成功，`d3d11:vp-input-ok` |
| 跨设备共享句柄 | 失败 | 失败（E_NOINTERFACE） |
| 判定 | `CPU_READBACK_ONLY` | `D3D11_DIRECT_PASS` |

跨设备共享失败不影响结论：S1 采用“渲染器使用解码器同一 device 上的纹理”
（同进程、同适配器，零拷贝），不需要跨设备打开。

## 3. 原始取证输出

### 3.1 DXVA2 后端（默认）

```json
"direct_surface": {
  "stage": "dxva2",
  "verdict": "cpu-readback",
  "flags": "0x00000047",
  "device": "dxva2",
  "format": "dxva2_vld",
  "sw_format": "nv12",
  "detail": "dxva2:no-shared-handle",
  "width": 128,
  "height": 128,
  "plane_count": 1,
  "dxgi_format": 842094158,
  "share_hresult": "0x80004002",
  "open_hresult": "0x00000000",
  "sample_hresult": "0x80004001",
  "frames": 1,
  "transfer_bytes": 24576,
  "transfer_us": 944.3,
  "probe_us": 2.2
}
```

`share_hresult = 0x80004002`（E_NOINTERFACE：解码纹理不暴露 `IDXGIResource`，不是
D3D9Ex 共享纹理）；`open_hresult = 0x00000000` 表示 D3D9 设备本身是 Ex 设备；
`sample_hresult = 0x80004001`（E_NOTIMPL）表示 D3D9 面没有 VideoProcessor 输入视图。

`transfer_bytes = 24576 = 128 × 128 × 1.5`，正是 NV12 一帧的字节数：回读是实测的，
不是按格式猜的。128×128 只有 24 KB，1080p 单帧约 3.1 MB，预算差两个数量级，
因此本行**不能**外推成 1080p 的时间成本。

### 3.2 D3D11VA 后端（修复像素格式映射后）

```json
"direct_surface": {
  "stage": "d3d11",
  "verdict": "d3d11-direct",
  "flags": "0x00000061",
  "device": "d3d11va",
  "format": "d3d11",
  "sw_format": "nv12",
  "detail": "d3d11:vp-input-ok",
  "width": 128,
  "height": 128,
  "plane_count": 2,
  "dxgi_format": 103,
  "adapter": "10DE:28E0",
  "format_support": "0xFA82C320",
  "share_hresult": "0x80004002",
  "open_hresult": "0x00000000",
  "sample_hresult": "0x00000000",
  "frames": 1,
  "transfer_bytes": 24576,
  "transfer_us": 1037.2,
  "probe_us": 57.5
}
```

`dxgi_format = 103 = DXGI_FORMAT_NV12`；`adapter = 10DE:28E0`（NVIDIA）。
`flags = 0x61` 表示 HW_FRAME | SAMPLEABLE | PROBED。`share_hresult` 仍是 E_NOINTERFACE，
可以用，但**不需要**：解码纹理与渲染器同进程、同适配器，S1 走同 device 引用即可。

### 3.3 软件解码分支（`--policy 2`）

```json
"direct_surface": {
  "stage": "software",
  "verdict": "cpu-readback",
  "flags": "0x00000040",
  "device": "ffmpeg-software",
  "format": "yuv420p",
  "sw_format": "",
  "detail": "software:no-gpu-surface",
  "width": 128,
  "height": 128,
  "plane_count": 0,
  "transfer_bytes": 0,
  "transfer_us": 0.0,
  "probe_us": 1.3
}
```

软件解码没有 GPU 面，`transfer_bytes = 0` 是正确的：**不存在**被计入的回读。
这条分支同时证明计数器不是恒大于零的摆设。

## 4. 取证方式（可复现）

诊断由三部分构成，全部只读：

1. FreeRDP overlay 补丁（`native-deps/patches/freerdp-3.32.0-ycn-h264-diagnostics.patch`）
   在每帧解码成功后发布 `YCN_H264_SURFACE_INFO`：格式、`hw_frames_ctx->sw_format`、
   平面指针、`av_hwframe_map` 结果，以及 `av_hwframe_transfer_data` 的 QPC 耗时与字节数。
2. `ycn_rdp.dll` 注册 sink，用同线程 TLS 把帧归属到会话，并按 generation 做**一次**
   COM 探测（D3D11：VideoProcessor 输入视图 + 共享句柄；DXVA2：D3D9Ex + 共享句柄）。
   稳态每帧只做结构体拷贝与计数累加。
3. `tests/native_decode_checks.py` 读取 `ycn_rdp_direct_probe_last()`，断言结构完整、
   硬件面回读字节非零，并打印全部字段。

复现命令：

```powershell
python winui3/tests/native_decode_checks.py `
  --dll <dir>\ycn_rdp.dll --freerdp-dll <dir>\freerdp3.dll `
  --h264 winui3/tests/fixtures/h264-idr-128x128.h264 --width 128 --height 128 --policy 1

# D3D11VA 分支（FreeRDP 的硬件设备类型覆盖）
$env:FREERDP_HWCTX_TYPE = 'd3d11va'
```

## 5. 附带修复的构建缺陷

vcpkg 的包 ABI 只覆盖 portfile 与 port 目录内的文件，仓库把补丁放在
`native-deps/patches/`（通过 `../../` 引用）时，**改补丁不会改变 ABI**：vcpkg 会把旧包
判定为最新（实测只花 1.3 秒“安装完成”），发布目录里仍是旧 DLL。本轮改为
`build_native.ps1` 在生成 manifest 快照时把补丁复制进 `ports/freerdp/patches/`，
使补丁内容进入 ABI；portfile 同时保留直接 CMake 构建时读 `../../patches` 的回退。

## 6. 未验证项（不得当作已通过）

- 真实 RDP 会话：本轮全部证据来自离线单帧 AVC420 夹具，未登录真实服务器，
  服务器最终协商的 codec（11/14/15）、AVC444 双流、真实 1080p 回读成本均未测。
- 1080p/高帧率下的每帧回读微秒、GPU 引擎时间、DWM 增量。
- 与 mstsc 的同 codec/同帧负载对照（S1 验收项）。
- `ID3D11VideoProcessorBlt` 的实际转色路径：本轮只验证输入视图可创建，
  未做 601/709、limited/full 的图像比对。

## 7. 对 S1 的约束（由本轮证据确定）

1. S1 的直显分支必须绑定 **D3D11VA** 后端；DXVA2 分支继续保持 GDI + CPU 回读，
   不参与直显，也不做 D3D9→D3D11 的伪互操作。
2. 纹理跨 DLL 传递走“同 device 引用 + AddRef/Release 租约”，不走共享句柄。
3. `av_hwframe_transfer_data` 必须能在直显激活时被整段跳过，否则只是多画一遍，
   CPU 不会下降；S1 需要 overlay 补丁在 `gdi_SurfaceCommand_AVC420/444` 里
   跳过 CPU 转色与 primary buffer 拷贝。
4. 后端默认值本轮**未改**（仍为 DXVA2），改变默认属于 S1 的开关范围，
   需与回退策略一起验收。
