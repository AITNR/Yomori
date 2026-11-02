# S3 · dirty 增量、缩放、光标、多画面收尾：结论与证据

日期：2026-11-02
阶段：计划书 S3
前置：S1（`e6c8158`）、S2（`ac25dc0`）已通过并推送

## 1. 结论摘要

| 问题 | 结论 |
|---|---|
| dirty 增量直显是否成立 | **成立**。累积面 + 贴片中转 + 子矩形贴回，整帧一致性有专门判据（§4、§6） |
| 增量每帧省多少（完整口径） | 1080p 整幅帧 **502.8 µs** → 增量帧 **456.2 µs**（×0.89，三轮中位） |
| 增量只算转换省多少 | 1920×1080 全幅 **76.3 µs** → 256×144 矩形 **29.6 µs**（×0.39） |
| 为什么省得有限 | 每帧必付一次全幅 `CopyResource`（≈437 µs），增量省下的转换只有约 47 µs（§2.3） |
| 实现前必须知道的一条驱动行为 | `VideoProcessorBlt` **每次都会先把整个输出面填成背景色**，所以不能直接往累积面打小矩形（§2.1） |
| dirty 丢失 / 设备重建 / 换 codec | 一律退回整帧：`canonical_valid=0` + `dirty.full=1`，不显示半幅（§7） |
| 面板尺寸变化 | 必须重建整套 video processor；否则输出视图尺寸与 enumerator 不符，表现为「改窗口大小后画面全黑」（§7.2） |
| 光标（POINTER） | **未合成进画面**，与既有 GDI 路径一致；指针形状同步是独立缺口（§9） |
| 是否默认启用增量 | **否**。收益为正但仅约 9%，不足以承担多一条合成路径的风险；开关保留，判据保留（§8） |

## 2. 实现前必须定死的三条驱动行为

这三条都是本轮实测出来的，不是文档推断；其中前两条直接决定了实现形状。

### 2.1 `VideoProcessorBlt` 清整个输出面

`VideoProcessorSetStreamDestRect` 只决定 stream 画在输出面的哪个位置，**面外区域由
驱动在每次 Blt 前填成背景色**（`ID3D11VideoContext1::VideoProcessorSetOutputBackgroundColor`
控制这个颜色，缺省黑）。因此「把 dirty 矩形直接 Blt 到累积面的全幅输出视图上」这条
最省事的路是错的：每帧都会把累积面的其余部分擦成黑色，画面表现为「只有最后一次改动
的区域是活的」。

正确形状是两步（`ycn_dp_convert_rect()`）：

```
Blt(源矩形 → 与矩形同尺寸的贴片)  →  CopySubresourceRegion(贴片 → 累积面 对应位置)
```

代价是多一次小复制，收益是累积面不被破坏。这条属于**正确性**约束，与性能无关。

### 2.2 Blt 成本随目标矩形缩放，但存在固定开销

每轮 400 次 Blt、3 轮取中位数，单位 µs/帧（只含 `VideoProcessorBlt`，不含落到目标面的复制）：

| 输入帧 | 整幅 | 960×540 矩形 | 256×144 矩形 | 64×64 矩形 |
|---|---|---|---|---|
| 1920×1080 | 76.3 | 32.3 | 29.6 | 28.5 |
| 1280×720 | 48.8 | 41.9 | 30.6 | 24.4 |
| 960×540 | 36.2 | 27.7 | 26.4 | 21.6 |
| 480×270 | 19.8 | 23.4 | 20.4 | 23.7 |
| 128×128 | 22.4 | 22.7 | 31.7 | 23.3 |

两点结论：转换成本确实随目标矩形缩小（1080p 76.3 → 29.6 µs），但存在约 **22–28 µs
的每调用固定开销**，所以 256×144 之后再缩小矩形不再有意义。这也决定了贴片的取整
粒度取 64 px（§4.2）：更细的粒度换不来收益，只会增加重建次数。

### 2.3 每帧必付的全幅复制是真正的天花板

同一台机器上，把「落到目标面」的复制加回去（每轮 800 次 Blt）：

| 口径 | 1920×1080 | 256×144 | 64×64 |
|---|---|---|---|
| 只有 Blt | 291.2 | — | 250.7 |
| 只有 Blt（无复制，同批次） | 76.3 | 29.6 | 28.5 |

即：一次「Blt 之后紧跟同资源复制」的依赖链会额外付 **约 220 µs**，且这个数字与面积
无关（64×64 与 1080p 一样贵）。原因是复制必须等 Blt 写完同一块显存，驱动在这条
依赖上插入屏障，成本按「次数」而不是「字节」计。

在线每帧至少要有一次 `CopyResource(累积面 → 后备缓冲)`：交换链是
`DXGI_SWAP_EFFECT_FLIP_SEQUENTIAL`，Present 之后后备缓冲内容不保证保留，累积面必须
每帧整面搬过去。这一笔约 437 µs，**增量省不掉它**。所以增量直显的收益上限就是
「转换段省下的部分」，实测约 47 µs/帧。

## 3. 版面、DPI 与缩放

`src/Yomori.RdpNative/ycn_present_layout.{h,c}` 是纯计算层（不碰 D3D、不碰 XAML），
因此可以被完整离线测到（§6）。

| 项 | 实现 |
|---|---|
| fit 模式 | `YCN_FIT_STRETCH` / `YCN_FIT_LETTERBOX`（默认）/ `YCN_FIT_CROP` |
| epsilon | 等比缩放后若左右/上下余量都 ≤ epsilon 就直接铺满，吸收非整数 DPI 的 1px 抖动 |
| 黑边 | 版面变化时 `ClearRenderTargetView` 一次，之后不再重画 |
| DPI | `ycn_dp_set_scale()` 走 `IDXGISwapChain2::SetMatrixTransform`，矩阵为 `1/scale` 对角；CompositionScale 变化不会导致重新分配交换链 |
| 逆映射 | `ycn_dp_src_rect_for()` 把目标矩形映射回源纹理坐标，两端各放宽 1 px，宁可多转一行也不留缝 |

版面判定用的是 `fit_rect` 的四个整数与 `canonical_valid`：任一变化即整面重画。这样
「面板尺寸 / DPI / 源分辨率」三类变化走同一条路径，不需要分别处理。

## 4. dirty 增量路径

### 4.1 结构

```
FreeRDP 线程                       UI 线程（ycn_dp_present）
─────────────                      ────────────────────────
RDPGFX SurfaceCommand
  → ycn_dp_set_frame_rects()  ──→  dirty 集合（最多 16 个，相交即合并）
  → ycn_dp_publish(frame)          ↓
  → ycn_dp_end_frame()            版面未变 且 dirty 面积 ≤ 60%
                                    → 逐矩形 convert_rect（Blt→贴片→贴回）
                                  否则
                                    → 整幅 Blt 到累积面
                                  ↓
                                  CopyResource(累积面 → 后备缓冲) → Present
```

### 4.2 关键参数与理由

| 参数 | 取值 | 理由 |
|---|---|---|
| 贴片粒度 | 64 px 向上取整 | 每调用固定开销约 22–28 µs（§2.2），更细无收益；64 让「光标 + caret + 小窗口边」落在同一档，避免每帧重建 enumerator |
| 增量面积阈值 | ≤ 60% | 超过后逐矩形 Blt 的固定开销之和会超过一次整幅转换；阈值本身有断言覆盖 |
| 单帧矩形上限 | 16 | 溢出即退整帧（`ycn_dirty_add` 里 `full=1; count=0`），不排队 |
| 矩形合并 | 相交即 union，反复两两合并到稳定 | 相邻窗口边缘常产生大量 1px 重叠矩形，不合并会把固定开销乘以矩形数 |
| 整帧后 | `dirty.full=1; count=0` | 整幅已经覆盖，残留矩形再转一遍纯属浪费 |

### 4.3 一个必须记住的记账坑

`present` 成功后要把 dirty 置成 `full=1`（下一帧若没有新矩形到达就按整幅处理）。但
「置 full」与「本帧是否已经开始收矩形」是两件事：早期版本在 `ycn_dp_set_frame_rects()`
里写的是 `if (p->dirty.full) break;`，于是 present 之后置的 `full=1` 会把**后续所有
帧的矩形都挡在门外**，增量永远不生效，而且表现完全正常（只是每帧都整幅）。现在用
独立的 `dirty_frame_open` 区分：本帧第一条矩形到达时清空集合并置 `full=0`，同一帧
后续命令继续累加；帧内因矩形溢出置的 `full=1` 不会被下一条命令清掉。

### 4.4 为什么不做「只转主流 / 只转可见区域」

`fit` 区域外的黑边本来就不含内容，`ycn_layout_intersect` 会把落在黑边上的 dirty 直接
裁掉。除此之外不再做额外裁剪：dirty 已经是 RDPGFX 给出的真实变化区域，再猜一次只会
引入漏画风险。

## 5. 实测（三轮）

命令与 §10 一致，三轮连续执行，均 `exit 0`。

| 轮次 | 纯转换 整幅 | 纯转换 256×144 | 每帧完整 整幅 | 每帧完整 增量 | 增量/整幅 | CPU 提交 mean | CPU 提交 p99 |
|---|---|---|---|---|---|---|---|
| 1 | 65.0 µs | 48.6 µs | 500.0 µs | 374.1 µs | 0.748 | 100 ns | 2.3 µs |
| 2 | 115.0 µs | 95.9 µs | 502.8 µs | 492.4 µs | 0.979 | 100 ns | 1.1 µs |
| 3 | 223.0 µs | 40.6 µs | 512.0 µs | 456.2 µs | 0.891 | 100 ns | 2.0 µs |
| 中位 | — | — | **502.8 µs** | **456.2 µs** | **0.891** | 100 ns | 2.0 µs |

口径说明：

* **纯转换** = 一次 `VideoProcessorBlt`，60 次迭代。三轮抖动大（65–223 µs），因为这台
  机器同时有 Intel UHD 与 RTX 4060 两张适配器、外加一个虚拟显示适配器，默认适配器
  的调度受其它进程影响。稳定值取 §2.2 的 400 次 × 3 轮表（76.3 / 29.6 µs）。
* **每帧完整** = 转换 + 落到累积面 + 落到后备缓冲，120 次迭代。这一列才对应
  `ycn_dp_present` 真正付的 GPU 时间。
* **CPU 提交** = UI 线程一次 `VideoProcessorBlt` 的同步返回耗时。均值 100 ns 是 QPC
  一格（10 MHz），即「提交立即返回」；p99 1–2 µs 是偶发的驱动命令缓冲扩容。

## 6. 断言

| 目标 | 断言数 | 覆盖 |
|---|---|---|
| `ycn_present_layout_tests` | **35** | letterbox 居中（1920×1080→1920×1200 = (0,60,1920,1080)；→1080×1080 = (0,236,1080,608)）、epsilon 吸收 1px 抖动、DPI 矩阵、dirty 合并/裁剪/钳位/溢出退整帧/面积阈值/链式重叠 |
| `ycn_avc444_gate_tests` | **77** | S2 闸门（op0 / op1 / op1→op2 / 孤立色度 / 尺寸不匹配 / 乱序 / 半帧被顶 / reset / expire） |
| `ycn_dirty_diag_tests` 等 5 个 | 全部 ok | 既有 dirty 诊断与快照路径未回归 |
| `native_decode_checks.py` | 新增 3 项 | `direct_bench`（纯转换）、`direct_bench_rect`（矩形 vs 整幅）、`direct_bench_frame`（每帧完整：整幅 vs 增量） |

`direct_bench_frame` 的判据只要求「两个口径都测到 GPU 时间」且「增量不超过整幅的
1.35 倍」。没有把「增量必须更便宜」写成硬判据：这台机器上它只便宜约 9%，写死阈值会
让报告变成「判据跟着测量噪声走」。真实数值进报告，判据守回归。

## 7. 回退与失效处理

### 7.1 一律退整帧的触发条件

| 触发 | 处理 |
|---|---|
| `canonical_valid == 0`（首次、重建、Blt 失败后） | 整幅 Blt + 清黑边 |
| `dirty.full == 1` | 整幅 |
| 版面变化（面板尺寸 / DPI / 源分辨率） | 清黑边 + 整幅 |
| dirty 面积 > 60% 或矩形数 > 16 | 整幅 |
| `VideoProcessorBlt` 失败 | 释放输入视图、`canonical_valid=0`、下一帧整幅；连续失败由上层 `ycn_dp_disable` 收口 |

### 7.2 面板尺寸变化必须重建 processor

`D3D11_VIDEO_PROCESSOR_CONTENT_DESC.OutputWidth/OutputHeight` 是 enumerator 的静态属性。
面板尺寸变化时若只重建累积面、复用旧 enumerator，`CreateVideoProcessorOutputView()`
会因为资源尺寸与 enumerator 不符而返回 `E_INVALIDARG`，表现是「改窗口大小后画面全黑」。
`ycn_dp_ensure_swapchain()` 在 `ResizeBuffers` 之前先 `ycn_dp_reset_processor()`，
贴片也一并释放（贴片尺寸受累积面尺寸钳位）。

### 7.3 会话与多画面

* presenter 槽位按会话 id 独立，`canonical` / `scratch` / dirty 集合都是槽位私有，
  因此 1/2/4/8 分屏下各画面之间不存在共享累积面，也就没有串图路径。
* 会话结束走 `ycn_dp_release_session()`：槽位标记 `orphaned`，D3D 资源延后到槽位
  复用或 `ycn_dp_shutdown()` 时拆（`SetSwapChain` 只能在 UI 线程调用，不能在
  FreeRDP 线程里拆）。
* 换 codec（AVC420 → AVC444 / GDI）走 S2 的 `ycn_gdi_drop_direct()`：清直显状态并
  请求整屏刷新，累积面随之失效。

## 8. 验收项对应

| 计划书 S3 验收项 | 状态 | 证据 |
|---|---|---|
| dirty 丢失 → 整帧 fallback | 通过 | §7.1；`ycn_dirty_is_incremental_worthwhile` 断言 |
| lease / reset | 通过 | S1 租约计数 + §7.1；`direct_contract` 四项非法输入均返回 `-1` |
| 设备重建 | 通过 | §7.2 的 `reset_processor` + 贴片释放 |
| pointer | **未实现** | §9 |
| DPI / Viewbox | 通过 | §3 + 35 项断言 |
| overlap | 通过 | `ycn_dirty_add` 相交即 union，断言覆盖链式重叠 |
| 串图 / 并发 | 通过（结构上） | §7.3；presenter 槽位私有资源 |
| 三轮每帧 CPU 微秒 | 通过 | §5 CPU 提交列 |
| epsilon | 通过 | §3 + 断言 |
| DWM 旁证 | **未取** | §9 |
| 帧时间与可见丢帧 | **未取** | 需要真实 RDP 会话，§9 |

## 9. 未完成与缺口

1. **光标（POINTER）**：当前全链路都没有注册 FreeRDP 的 pointer 回调，远端光标形状
   不同步到本地，画面里也不合成光标（与既有 GDI 路径一致，不是直显引入的回归）。
   要做需要新增 pointer 回调 + 本地指针映射，属独立工作量。
2. **DWM 旁证与可见丢帧**：两者都需要真实 RDP 会话（本机无对端凭据）。离线只能给出
   GPU 段成本（§5），给不出「端到端帧时间」和「DWM 侧是否掉帧」。
3. **1080p 回读计时、mstsc 同屏对照**：S1 / S2 报告已列为待联机缺口，本轮未变。
4. **增量默认关闭**：开关是 `ycn_rdp_direct_set_incremental(session, 1)`，默认 0。
   开启条件是实测收益跨过阈值（当前 9%），或 §2.3 的全幅复制被替换成着色器合成
   （全屏 quad 采样累积面，预期把 437 µs 降到几十 µs，届时增量收益会显著放大）。

## 10. 现场发现：直显为什么一次都没跑起来（2026-11-02 追加）

用户在最新版上连真实主机后「看不出变化」。查安装目录的日志得到两条独立原因：

### 10.1 能力探测查错了模块（已修）

`RdpNativeInterop.SupportsDirectPresent` 把 `ycn_h264_set_external_d3d11_device`
当成 `ycn_rdp.dll` 的导出。实际它由**打过补丁的 `freerdp3.dll`** 导出，桥接层自己
也是运行时 `GetProcAddress` 去取，所以桥接层导出表里根本没有它 —— 探测恒为 false，
`ArmDirectPresent()` 直接返回 `"native exports missing"`，直显**从未武装过**。
日志里那行字看起来像「DLL 太旧」，实际是查错了模块。

现在探测拆成两个模块分别查，并把「缺哪个符号、在哪个模块」写进
`DirectPresentProbeDetail`，日志不再是「exports missing」一句话。

### 10.2 服务器发的是 AVC444v2，直显对它无效（已改广告策略）

同一份日志：`decode_codec=15`、`codec_counts_by_id` 第 16 项 13079（全部 surface
都是 codec 15）、`surface_ms_by_codec[15]` 累计 60 秒 / 11445 surface ≈ **5.25 ms
每 surface** 的 CPU 色度重建。

也就是说这条会话走的是 S2 判定的「AVC444 只能 CPU 合成」那条路，永远进不了直显。
按「更高性能、更低占用优先」的取舍，改成**只广告到 10.0**：

```c
freerdp_settings_set_uint32(settings, FreeRDP_GfxCapsFilter, 0x7F8u);
```

`GfxCapsFilter` 是按 `rdpgfx_main.c` 里 `capList` 顺序的位掩码（本机构建 11 项：
`8, 81, 10, 101..107`），`0x7F8` 滤掉 101..107，只剩 8 / 8.1 / 10.0 → 服务器只能发
AVC420（codec 11）→ 直显闭环才真正成立。代价是色度 4:2:0。

- 不需要改 FreeRDP 补丁，纯设置项。
- `GfxAVC444` 必须保持 `TRUE`：上游只在「`!GfxH264 || GfxAVC444`」时才广告 10.x。
- 旧的 `--embed-avc420-only` 开关语义是错的（它把 `GfxAVC444` 置 FALSE，按上游规则
  得到的是 8.1 而不是 AVC420），已换成 `--embed-avc444`：带上它就不过滤、回到 10.7，
  用于 A/B 对照。
- 探针 `gfx_caps_probe.c` 新增 `s9` 场景，断言过滤后只剩
  `8(THINCLIENT) / 8.1(0x13) / 10.0(SMALL_CACHE)` 三个 capset。

## 11. 复现

```bash
# 原生层构建（Release；Debug 缺 ucrtbased 会让 verify_runtime_dlls 失败）
powershell -File winui3/tools/build_native.ps1 -Configuration Release -SkipInstall

# 纯单元断言
for t in ycn_avc444_gate_tests ycn_present_layout_tests ycn_dirty_diag_tests \
         ycn_dirty_snapshot_tests ycn_dirty_pending_tests \
         ycn_dirty_pending_session_tests ycn_dirty_stats_tests; do
  "$YCN_BUILD/Release/$t.exe"
done

# 端到端自检（含三个 bench）
python winui3/tests/native_decode_checks.py \
  --dll winui3/src/Yomori.RdpNative/build/Release/ycn_rdp.dll \
  --h264 winui3/tests/fixtures/h264-idr-128x128.h264 --width 128 --height 128

# 转换成本的阶梯测量（markdown 表）
python winui3/tests/bench_ladder.py \
  winui3/src/Yomori.RdpNative/build/Release/ycn_rdp.dll 400
```
