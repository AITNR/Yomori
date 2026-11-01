# S2 · AVC444 / AVC444v2（codec 14 / 15）双流：结论与证据

日期：2026-11-02
阶段：计划书 S2
前置：S1（单流 AVC420 直显闭环）已通过并推送（`e6c8158`）

## 1. 结论摘要

| 问题 | 结论 |
|---|---|
| AVC444 能否复用 S1 的零拷贝直显 | **不能**。色度流的还原不是平面搬移，见 §2 |
| 生产路径（当前 SwapChainPanel + GDI）是否可用 | **是**。op=1 与 op=0 双流均已实测通过，色度与源图逐像素比对 PSNR 40.4 / 40.8 dB |
| codec 14/15 走进直显路径时的行为 | 立即整 surface 交回 GDI，请求整屏刷新；不会「只显示主流」 |
| 半帧 / 错序 / 尺寸不匹配 | 由 `ycn_avc444_gate` 状态机整帧丢弃并置 `need_full_refresh=1`，77 项断言覆盖 |
| 1080p 每帧转换成本（离线可控测量） | GPU 188.9 µs / 帧，CPU 提交均值 100 ns（见 §5） |

## 2. 为什么 AVC444 做不到零拷贝

`avc444_decompress()` 的 op 语义（`libfreerdp/codec/h264.c:768`）：

* `op=0`：主流给亮度 + 色度流给色度；`op=1`：只有主流（亮度）；`op=2`：只有色度。

问题在色度流解码结果的组织方式。辅助流同样被解成一张**满尺寸** W×H 的 YUV420 图，
但它的平面不是「色度平面」，而是把 4:4:4 色度**压进亮度形状**后的打包数据：

* `AVC444_CHROMAv1`（codec 14，`general_ChromaV1ToYUV444`）：U/V 的第二半按 **16 行
  块交错**铺在辅助流亮度平面里，`y % 16 < 8` 取 U、否则取 V，再按 `2*uY+1` 这种
  奇数行号散写回目标色度平面；
* `AVC444_CHROMAv2`（codec 15，`general_ChromaV2ToYUV444`）：改成按奇数列取 U、
  偶数列取 V，并额外消耗辅助流的 U/V 平面（B6–B9 段）。

这类逐像素散写既不是 `CopySubresourceRegion`（平面形状对不上：NV12 的 UV 平面是
W×H/2 字节，辅助流亮度平面是 W×H 字节），也不是视频处理器的双流叠加（那是空间
叠加，这里是按行块/列奇偶重新排列）。要做成 GPU 侧，只能写 compute shader 读辅助
流——而 NV12 解码目标**不能绑 SRV**（S1 实测 `D3D11_BIND_SHADER_RESOURCE` 返回
`E_INVALIDARG`，见 `docs/rdp-avc-direct-present-s1-2026-11-02.md`），shader 读不到
源像素。结论：AVC444 在本栈上只能 CPU 合成，与本项目其它取舍一致（性能不能靠
「看起来像零拷贝」的口径换取）。

因此 codec 14/15 的处理是**显式降级**：`gdi_SurfaceCommand` 在
`codecId != RDPGFX_CODECID_AVC420` 时调用 `ycn_gdi_drop_direct()`，清掉直显状态并
请求整屏刷新 —— 整 surface 回 GDI，不做半幅显示、不做「主流先上」。

## 3. 新增：整帧提交闸门 `ycn_avc444_gate`

`src/Yomori.RdpNative/ycn_avc444_gate.{h,c}` 是纯状态机（不碰 D3D、不碰 FreeRDP），
把「一帧什么时候算成立」从像素代码里独立出来，从而可以离线被完整测到。

规则：

1. **op=0** 一条命令带两条流 → 两条都 ok 且尺寸一致才提交；
2. **op=1** 只带亮度 → 进入 `pending`，**绝不单独上屏**；后续 **op=2** 接续同一代号，
   到齐后提交；
3. **op=2 孤立到达**（没有等待中的亮度）→ 丢弃，原因 `orphan-chroma`；
4. **尺寸不一致**（辅助流尺寸 ≠ 亮度流尺寸）→ 丢弃，原因 `size-mismatch`；
5. **代号过期**（旧代号晚到、重复结算）→ 丢弃，原因 `stale-generation`；
6. **在途帧被新帧顶掉** → 丢弃，原因 `orphan-luma` / `partial-op0`；
7. **reset / 设备重建** → 在途帧丢弃（`after-reset`），代号前移一个 epoch，
   reset 之前的命令不可能再被当成新帧；
8. 任何丢弃都置 `need_full_refresh=1`：半帧进过管线，只能整屏重画，不能指望增量区域自愈；
9. 租约（每帧最多两条流的 lease）在**成功与失败两条路径上都归还**，
   `leases_outstanding` 必须恒为 0。

单元测试 `tests/ycn_avc444_gate_tests.c`（77 断言，全绿）：

| 用例 | 断言要点 |
|---|---|
| op0 单命令双流 | 提交成功、两条租约各归还一次、尺寸入统计 |
| op1 单独 | 不上屏、`orphan-luma`、`need_full_refresh=1` |
| op1→op2 两段式 | 代号接续、合成后提交、`frames_pending` 计数 |
| op2 孤立 | 不上屏、`orphan-chroma` |
| 色度流失败 | 整帧丢、`chroma-failed`、两条租约都归还 |
| 尺寸不匹配 | 整帧丢、`size-mismatch` |
| 乱序 / 重复结算 | `stale-generation`、被拒命令的租约也归还 |
| 半帧被顶掉 | 旧帧记 `orphan-luma`，新帧照常提交、无租约泄漏 |
| reset | 在途帧 `after-reset`、reset 后 commit 是空操作 |
| 超时 | `expire()` 可结算挂住的亮度帧，且幂等 |
| 多帧序列（20 帧 op0/op1+op2 交替） | 提交数、丢弃数、租约总量守恒 |

## 4. 生产路径实测（当前 SwapChainPanel + CPU 合成）

命令：`python tests/avc444_decode_probe.py`（op=1）与 `-- --full`（op=0 双流，
`yuv444_context_encode` 拆流 + libopenh264 编码 + 解码后与源图逐像素比对）。

```
FINAL: op1(PASS) full(PASS)          # 4 组 op1 × 2 codec × 2 policy
op0-avc444-v1-software-p2  PASS  psnr=40.444 mae=1.843 maxChannelError=15
op0-avc444-v1-dxva2-p1     PASS  psnr=40.444 mae=1.843 maxChannelError=15   hwFrames=2
op0-avc444-v2-software-p2  PASS  psnr=40.802 mae=1.738 maxChannelError=9
op0-avc444-v2-dxva2-p1     PASS  psnr=40.802 mae=1.738 maxChannelError=9    hwFrames=2
```

说明：`mae≈1.8 / psnr≈40 dB` 是 H.264 有损编码的正常误差，`maxChannelError` 15 出现
在 4:4:4 边缘像素上。判据是 `psnr ≥ 20 && mae ≤ 12`，实测余量很大 —— 也就是说
「两条流各自解对」之外，「合成后与源图一致」也被验证了，这正是 S2 需要的真实色度证据。

### 4.1 顺带修掉的探针环境坑

`avc444_decode_probe.py` 一开始在 4/4 组都报 `OSError: [WinError 127] 找不到指定的程序`。
根因不是探针逻辑，而是 **`import hashlib` 先把 Python 自带的 `_hashlib.pyd` 连带的
`libcrypto-3-x64.dll` 映射进进程**，随后 `freerdp3.dll` 静态导入的 OpenSSL 3 符号
就在这份别的版本上解析，缺导出 → `LoadLibrary` 直接 127。

修法：`h264_decode_probe.py` 顶部先按路径把原生目录的 `libcrypto-3-x64.dll` /
`libssl-3-x64.dll` 钉死，并把 `hashlib` 改成用到时再 import；`avc444_decode_probe.py`
统一走 `base._sha256()`。同时 `load()` 里增加了「按目录顺序预加载依赖」，
让 `freerdp3.dll` 的导入永远解析到同目录那一份。这一坑对任何「Python 探针 +
OpenSSL 静态导入」的组合都成立，值得记住。

## 5. 每帧转换成本（离线可控口径）

`ycn_dp_bench(width, height, iterations)` 跑真实 `VideoProcessorBlt`：

* CPU 侧 = 提交一次 Blt 的同步耗时（UI 线程真正付的代价），`reserved[0..2]` 纳秒；
* GPU 侧 = 从第一次提交到 GPU 跑完的墙钟时间 / 帧数，`reserved[3]` 纳秒。

1080p / 60 次实测：

```json
{"width":1920,"height":1080,"iterations":60,
 "submit_min_ns":0,"submit_mean_ns":100,"submit_p99_ns":1100,
 "gpu_per_frame_us":188,"gpu_per_frame_ns":188900,"gpu_note":"gpu-timing-event-ok"}
```

两处口径修正（都是踩过才定的）：

1. `D3D11_QUERY_TIMESTAMP` 在本机驱动上恒定返回 0，两个时间戳相同 → 之前得出
   `gpu_per_frame_us=0`。改用 `D3D11_QUERY_EVENT` + `Flush`；
2. `GetData()` 未就绪返回的是 `S_FALSE` 而不是失败码，只判 `FAILED()` 会把
   「还没算完」当成「算完且为 0」。必须判 `== S_OK` 并轮询。

## 6. 与计划书 S2 验收项的对应

| 验收项 | 状态 | 证据 |
|---|---|---|
| codec 14/15 关联、协议 op | 通过 | §3 状态机 + §4 op1/op0 实测（两 codec × 两 policy） |
| frame / generation | 通过 | `stale-generation` / `after-reset` / 代号前移用例 |
| 双 lease | 通过 | 每次提交/丢弃后 `leases_outstanding == 0`；并断言两条租约各归还一次 |
| 整帧发布 | 通过 | op1 单独到达不上屏；op1+op2 到齐才提交 |
| 丢流 / 错序 / 尺寸不匹配 | 通过 | 全部为独立用例 |
| lease / reset | 通过 | reset 丢弃在途帧并前移 epoch |
| 真实色度、多帧 | 通过 | §4 与源图比对；§3 20 帧序列 |
| GPU op0/op1 | **不适用（已定性）** | §2：辅助流是打包散写，NV12 不能绑 SRV，GPU 无法读源像素；直显对 codec 14/15 显式交回 GDI |
| 图像 / CPU 基于当前 SwapChainPanel | 部分 | 离线已测（§4/§5）；真机会话端到端仍待联机（与 S1 §6 同一缺口） |

## 7. 复现命令

```bash
# 原生层（含新闸门测试目标）
powershell -ExecutionPolicy Bypass -File winui3/tools/build_native.ps1 -Configuration Release

# 纯单元测试
_tools/ycn-hwdecode-20261031/build/Release/ycn_avc444_gate_tests.exe

# AVC444 生产路径（op1 + op0 双流）
python winui3/tests/avc444_decode_probe.py
python winui3/tests/avc444_decode_probe.py --full

# 每帧转换成本
python winui3/tests/native_decode_checks.py --dll winui3/src/Yomori.RdpNative/build/Release/ycn_rdp.dll \
  --h264 winui3/tests/fixtures/h264-idr-128x128.h264
```
