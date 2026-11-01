# RDP AVC 直显接管 — S1 单流 AVC420 直显最小闭环（2026-11-02）

本文件是《RDP AVC 直显接管实施计划书》S1 阶段的交付物。S0 已判定
`D3D11VA → D3D11_DIRECT_PASS`、`DXVA2 → CPU_READBACK_ONLY`；S1 在这条判定上
把「解码 → 转色 → 上屏」整条链做成可运行的闭环，并给出可复现的验收证据。

S1 的边界：**单会话、单路 AVC420（codec 11）、整帧呈现**。AVC444（14/15）、
脏区增量、缩放、光标、多视图属于 S2/S3。

## 1. 结论

| 验收项 | 结果 | 依据 |
| --- | --- | --- |
| 解码 surface 与呈现纹理同一个 `ID3D11Device` | PASS | `ycn_dp_present` 每次 `GetDevice()` 比对，不一致即拒帧并写 `texture-device-mismatch` |
| 解码帧零 CPU 回读（`av_hwframe_transfer_data` + `sws_scale` 全跳过） | PASS | 直显帧在 `libavcodec_decompress` 里 `goto fail` 提前收敛，`return 2` 交给 GDI |
| GPU 转色（`ID3D11VideoProcessor`） | PASS | BT.601/709 × limited/full 四组已知像素自检，最大误差 ≤ 2/255 |
| 交换链挂在既有 `SwapChainPanel` 上 | PASS（代码路径）/ 待联机确认 | `ycn_dp_ensure_swapchain` 用 `CreateSwapChainForComposition` + `ISwapChainPanelNative::SetSwapChain` |
| 帧租约生命周期 | PASS | 每帧一个 `AVFrame` 引用；`present` 结束即释放；未呈现的旧帧被丢弃并计数 |
| 解码线程不等 UI 线程 | PASS | `ycn_dp_publish` 只存指针（不碰 D3D、不等锁以外的任何东西） |
| 失败可回退（不黑屏） | PASS | 原生侧任一步失败 → 继续走 GDI；托管侧看门狗 90 拍无上屏 → 关直显、交还托管渲染器 |
| C ABI 契约（size 版本化、非法会话拒绝） | PASS | `native_decode_checks.py::direct_contract` |
| 运行依赖 / 导出完整性 | PASS | `verify_native_runtime.py` 报「原生层验证通过」，16 个 `ycn_*` 导出齐全 |

**S1 未覆盖（如实记录）**：没有真机 RDP 会话的端到端截图。本机没有可用的测试凭据，
而 S1 需要「服务器推 AVC420 + 一个 WinUI 窗口」两个前提同时成立。离线可验的部分
（设备/格式/视图/转色/契约/回退判据）全部通过；端到端留待联机验收，见 §6。

## 2. 链路结构

```
FreeRDP GFX SurfaceCommand(codec 11)
  └─ avc420_decompress                     [patched: h264.c]
       └─ libavcodec_decompress            [patched: h264_ffmpeg.c]
            ├─ avcodec_send/receive_packet → AVFrame(format=d3d11, sw_format=nv12)
            ├─ ycn_h264_try_direct()  ──┐   命中：av_frame_ref 后交给 sink
            └─ av_hwframe_transfer_data │   未命中：原 CPU 回读 + sws_scale
                                        │
                    ycn_direct_frame_sink（桥）→ ycn_dp_publish（只存指针）
                                        │
GFX EndFrame → ycn_dp_end_frame（pending → ready，整帧边界）
                                        │
UI 线程 16ms tick → ycn_rdp_direct_present → ycn_dp_present
                                        └─ VideoProcessorBlt → SwapChainPanel
```

关键设计（与 S0 结论一一对应）：

1. **一条链只有一个 device。** 共享句柄在本驱动栈上不可用（S0：`E_NOINTERFACE`），
   因此 device 由桥创建，并在 FFmpeg 建 hwdevice 之前**注入**
   （`ycn_h264_set_external_d3d11_device`）。注入点只认 `AV_HWDEVICE_TYPE_D3D11VA`，
   DXVA2 会话完全不受影响。
2. **只有直显武装时才切后端。** `h264->ycnDirectPresent` 为真才把 `sys->type`
   改成 D3D11VA；用户在设置里选「软件解码」时策略为 `RdpDecodePolicy.Software`，
   桥根本不武装，行为与改动前一致。
3. **解码线程绝不进入 D3D。** `ycn_dp_publish` 在解码线程只做结构体拷贝；
   `pending` 里已有未取走的帧时直接丢弃旧帧并计数，不排队。
4. **整帧边界在 RDPGFX EndFrame。** `pending → ready` 只在 `ycn_gfx_end_frame`
   发生，半个帧永远不会上屏。

## 3. 改动的文件

### 3.1 FreeRDP 覆盖补丁（`winui3/native-deps/patches/freerdp-3.32.0-ycn-h264-diagnostics.patch`）

| 文件 | 改动 |
| --- | --- |
| `include/freerdp/codec/h264.h` | 新增 `YCN_H264_DIRECT_FRAME` 描述符与 6 个导出：`ycn_h264_direct_api_version`、`ycn_h264_set_direct_sink`、`ycn_h264_direct_frame_release`、`ycn_h264_set_direct_armed`、`ycn_h264_set/get_external_d3d11_device`；新增上下文选项 `H264_CONTEXT_OPTION_YCN_DIRECT_PRESENT` |
| `libfreerdp/codec/h264.h` | `H264_CONTEXT` 增 `ycnDirectPresent/ycnDirectArmed/ycnDirectFrameValid/ycnDirectFrames` |
| `libfreerdp/codec/h264.c` | sink 注册、武装开关、租约释放委托、`avc420_decompress` 的 CPU 跳过握手（`ycnDirectFrameValid` → `return 2`） |
| `libfreerdp/codec/h264_ffmpeg.c` | `ycn_h264_try_direct`（`av_frame_ref` 一帧做租约）、`ycn_h264_create_hw_device`（注入外部 device 并拒绝被替换）、`libavcodec_decompress` 的提前收敛、`libavcodec_init` 的后端强制 |
| `include/freerdp/gdi/gfx.h` + `libfreerdp/gdi/gfx.c` | 线程级直显开关（`ycn_gdi_set_thread_direct_present`）、`surface->ycnDirectActive`、离开直显时请求全屏刷新（`ycn_gdi_drop_direct`） |

补丁的生成方式未变：改动 → 复制到 pristine 源码树 → `git diff -U3`，可用「原补丁 +
新补丁」在干净 tarball 上逐字节复现 6 个文件。

### 3.2 桥（`winui3/src/Yomori.RdpNative/`）

- `ycn_direct_present.{c,h}`（新增）：进程级 device（`BGRA_SUPPORT | VIDEO_SUPPORT`）、
  每会话 presenter（上限 8）、交换链惰性创建与尺寸跟随、`VideoProcessor` 输入/输出视图
  缓存、双槽帧交接、`present`、四象限转色自检、`detach`、孤儿槽位回收。
- `ycn_rdp.c/.h`：新导出 `ycn_rdp_direct_enable/attach_panel/present/color_test`；
  按名字解析 `freerdp3.dll` 的直显导出（旧 DLL 缺导出时零副作用）；
  `ycn_gfx_surface_command` 用 **TLS** 标记本帧属于哪个会话（多会话不串帧）；
  会话终结时归还 presenter 槽位（session id 单调递增、不复用，不还就会漏光 8 个槽）。
- `CMakeLists.txt` / `tools/build_native.ps1`：新增源文件与缓存键。

### 3.3 托管侧（`winui3/src/Yomori.App/`）

| 文件 | 改动 |
| --- | --- |
| `Services/RdpNativeInterop.cs` | `YcnDirectPresentResult`（`fixed` 缓冲区，热路径零封送分配）、`YcnDirectStatus`、4 个 `DllImport`、`SupportsDirectPresent` 导出探测、`MarkDirectPresentUnsupported` |
| `Services/RdpDirectPresenter.cs`（新增） | 武装 / 挂面板 / 呈现 / 关闸的外壳；不持有任何 D3D 资源 |
| `Services/RdpEmbeddedClient.cs` | 建连成功后立刻武装（H264 上下文的后端在创建那一刻定死，晚一步这个会话就永远没有直显帧）；只记 `DirectPresentArmed` 状态、**不引用** `RdpDirectPresenter`（冒烟工程不引用 WinUI） |
| `Views/RdpView.xaml.cs` | 渲染路线三选一（直显 / 托管 D3D / 软渲染）；`TryPresentDirectOnUi`；90 拍看门狗；`FallBackFromDirect` 与 `TearDownDirectOnUi` 保证「一块面板只挂一条链」 |

### 3.4 测试

- `tests/native_decode_checks.py`：新增 `check_direct_color`（256×128，四组
  BT.601/709 × limited/full，逐块最大误差 ≤ 8）与 `check_direct_contract`
  （非法会话 / 尺寸不足的结构体必须被拒），并镜像 `color_inferred` 字段。
- `tools/verify_native_runtime.py`：导出清单加入 8 个新符号，断言
  `ycn_h264_direct_api_version() == 1`。
- `_smoke/Smoke.csproj`：补上 `RdpDirtyPathStats.cs`（此前缺失导致冒烟工程编译不过，
  与本次改动无关，顺手修好）。

## 4. 转色自检的实际数值

`ycn_rdp_direct_color_test` 把四个象限按被测矩阵/范围的**精确逆变换**填成 NV12
（黑 / 白 / 红 / 中灰），走**与实时路径完全相同**的 VideoProcessor 配置，再拷到
staging 纹理读回 BGRA 逐像素比对：

| 组合 | 最大误差 (0–255) | 读回样本 BGRA（黑/白/红/灰） |
| --- | --- | --- |
| BT.601 limited | 1 | `0,0,0` / `255,255,255` / `1,0,254` / `128,128,128` |
| BT.601 full | 2 | `0,0,0` / `255,255,255` / `0,0,255` / `130,130,130` |
| BT.709 limited | 2 | `0,0,0` / `255,255,255` / `2,0,255` / `128,128,128` |
| BT.709 full | 2 | `0,0,0` / `255,255,255` / `0,0,255` / `130,130,130` |

矩阵或量化范围写错会把这四块推到几十甚至上百的误差，因此这张表不可能「蒙对」。
容差 8 是留给驱动的定点误差，不是给配置错误的余量。

## 5. 两处必须记录的取舍

### 5.1 色域未标注时按分辨率推断，而不是拒帧

RDP 的 AVC420 流普遍不带 VUI 色彩信息，`AVFrame::colorspace` 常常是
`AVCOL_SPC_UNSPECIFIED`。第一版在未知时直接拒帧（把帧交回 GDI），结果是**这条会话
永远上不了直显**（恒 `NoFrame`，最后只能靠看门狗回退），收益归零。

现在的策略：码流给了就用码流的；没给就按 ≥720 行判 BT.709、否则 BT.601，
量化范围缺省 `limited`（MPEG）。这是**推断**，因此结果里 `color_inferred=1`
如实标出，诊断能区分「读到的」和「猜的」。BT.2020 仍然不猜 —— 走到那是上游换了
编码器，继续回退 GDI。

### 5.2 `end_paint` 的整帧快照目前**保留**

直显帧不写 `gdi->primary_buffer`，但 `ycn_end_paint` 仍会从它拷一份快照。这在直显
路线上是纯浪费（1080p 每帧约 8 MB），但它是托管回退路线唯一的帧来源：
一旦直显判死，宿主需要有可用的最后一帧。S1 先保正确性。

收窄条件已想清楚，留给 S3：只有当**同一帧内所有 surface command 都是直显**时才可以
跳过快照（混合帧里非 AVC420 的 surface 仍需 GDI 合成），跳过时把脏区强制置为 FULL。
标记点应是「本帧全部 surface 直显」而不是「直显已启用」。

## 6. 待联机验收（S1 剩余项）

以下需要在有真实终端的机器上跑一次（本机无测试凭据）：

1. 1080p 会话连续 60 秒，记录 `frames_presented / frames_dropped / convert_ticks /
   present_ticks`，与托管 D3D 路线对照 CPU 占用与 UI 线程单拍耗时。
2. 与 mstsc 同屏对照：文字锐度、红色块、灰阶过渡，确认没有色偏。
3. 断链重连、窗口尺寸变化（远端分辨率切换）、`ResetGraphics` 三条路径下不残留黑屏。
4. 设备丢失（`DXGI_ERROR_DEVICE_REMOVED`）时是否落到托管回退而不是崩进程。

第 4 条目前只有「任何 `FAILED(hr)` 都会带 `detail` 标记后交回 GDI」这一层保证，
没有专门的设备重建逻辑 —— 与既有托管渲染器行为一致，不新增风险。

## 7. 复现命令

```powershell
# 原生层（含依赖安装、编译、PE/导出/干净加载验证）
& .\winui3\tools\build_native.ps1 -BuildDir C:\path\to\build

# 离线解码 + 直显 surface 取证 + 四组转色自检 + ABI 契约
python winui3\tests\native_decode_checks.py `
  --dll winui3\src\Yomori.RdpNative\build\Release\ycn_rdp.dll `
  --h264 winui3\tests\fixtures\h264-idr-128x128.h264

# 运行依赖 / 导出完整性
python winui3\tools\verify_native_runtime.py --dir winui3\src\Yomori.RdpNative\build\Release `
  --require-ffmpeg --expect-hwaccel --forbid-system-gpu --allow-vc-runtime-external `
  --capabilities winui3\src\Yomori.RdpNative\build\Release\ycn_rdp.capabilities.json `
  --lock winui3\native-deps\native-deps.lock.json --patches-dir winui3\native-deps\patches `
  --clean-load --expect-ycn-h264-api
```
