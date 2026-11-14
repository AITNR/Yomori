# AVC444 低色度处理模式首阶段实施计划书

- 日期：2026-11-14（北京时间）
- 状态：首阶段实施计划；目标是单批完成低色度功能、配置/ABI 接线、自动化验证和构建，形成可提交候选。本轮只修改计划书，不改产品代码
- 基线：`repo` 主分支 `f769c97`（FreeRDP 3.32.0；当前工作树保留用户既有未提交文件）
- 范围：内嵌 RDP GFX 的 AVC444 / AVC444v2 收包、解码、重建与 BGRA 输出
- 目标：在保留 AVC444/AVC444v2 协商、RDP GDI / BGRA / WinUI 显示链和会话语义的前提下，完成一个默认关闭、每会话生效、可诊断可回退的低色度模式；首阶段交付完整代码与自动化验证，达到可提交候选
- 非目标：修改远端服务器策略、强制协商 AVC420、重写 GPU 表面合成或改变 RDP 输入/音频/会话收尾行为

## 1. 结论摘要

| 设想 | 可行性 | 结论 |
|---|---|---|
| 客户端低色度模式 | 有条件可行，首要闸门是确认主流/aux 的 H.264 参考序列可独立维护 | `op=0/1` 可尝试对主流 YUV420 直接转 BGRA；`op=0` aux 是否能完全不解，取决于参考序列和 surface 更新安全性。`op=2` 是色度-only 主流包，不能当 aux 丢弃；若共享 H.264 context 需要该包推进参考状态，则要继续解码而只跳过色度重建/输出。相关色度-only 区域会保留旧色值。历史“只跳 aux”开关无可辨收益并已撤回；新模式必须使用不同实现并通过完整验收。 |
| 跳过逐像素色度重建 | 可行，且是丢 aux 后的自然优化 | `AVC444_LUMA` 当前路径会把主流的 YUV420 色度上采样/填到 YUV444 工作平面，再交给 YUV444→RGB。若直接以已解码主流 YUV420 数据走现有 YUV420→BGRA helper，可省去 YUV444 平面扩展及 aux 色度合成。 |
| 完全跳过 YUV→RGB 转换 | 不能在当前显示链中单独跳过 | FreeRDP GFX surface、GDI 主表面、原生帧缓冲、C# 渲染器目前都以 BGRA/RGB 像素为接口。跳过颜色转换而不改变这些接口，会得到无法正确显示的数据。可行的长期路线是让 YUV420 surface 一直保留到 GPU shader 转换，但这属于独立的渲染架构项目；不能把它与“丢 aux”混为一项小改动。 |

**建议：**首阶段直接完成一条纵向闭环：设置开关 → 版本化连接选项 → 每会话 FreeRDP 解码策略 → 按 op 与 dirty rect 安全生成现有 BGRA surface → 诊断 → 自动化测试 → 构建/运行库验证。开关默认关闭，AVC444 协商保持不变。`op=0/1` 主流数据尝试 YUV420→BGRA；`op=0` 是否能跳过 aux 解码，必须先证明不会破坏后续主流 H.264 P 帧参考。`op=2` 为色度-only 主流输入：若其压缩数据参与后续参考，则继续解码以推进状态，但跳过色度重建与画面更新。色度-only 更新区域可能保留旧颜色，必须明确接受并真机验收。GPU YUV surface 不进入首阶段。历史简单 aux-drop 已因未测得可辨收益而撤回，所以本方案必须证明直接 YUV420→BGRA 与安全省略 aux 的完整路径相对基线有收益；没有可测收益时，不宣称降低 CPU，也不把该模式作为性能功能提交。

## 2. 当前实现与调用链

### 2.1 当前工作树事实

- `winui3/src/Yomori.RdpNative/ycn_rdp.c` 的 `ycn_gfx_surface_command()` 对 GFX SurfaceCommand 做会话级统计后调用原 FreeRDP `SurfaceCommand`；当前主分支没有 AVC444 aux 丢弃逻辑。
- 原生桥接按 codec ID 11/14/15 计数；其中 14/15 分别是 AVC444 / AVC444v2。是否正在使用 AVC444 必须以实际收到的 codec 统计确认，不能仅凭 `GfxAVC444` 设置值推断。
- FreeRDP 3.32.0 源码位于本机 vcpkg source tree，作为定位依据；项目通过 `winui3/native-deps/ports/freerdp/portfile.cmake` 将 `native-deps/patches/*.patch` 集合并进固定 FreeRDP 包。

### 2.2 AVC444 解码路径

```text
RDPGFX SurfaceCommand codec 14 / 15
  → libfreerdp/gdi/gfx.c::gdi_SurfaceCommand_AVC444
  → 拆出 LC(op)、主流 bitstream/meta、aux bitstream/meta
  → libfreerdp/codec/h264.c::avc444_decompress
  → 按 op 调 avc444_process_rects
  → H.264 解码到 H264_CONTEXT 的 YUV420 缓冲
  → libfreerdp/codec/yuv.c::yuv444_context_decode
  → YUV420CombineToYUV444（亮度或色度重建）
  → YUV444ToRGB 写入 GFX surface BGRA
  → FreeRDP GDI 合成 → ycn_rdp native BGRA 帧 → WinUI 渲染
```

关键分支（`libfreerdp/codec/h264.c::avc444_decompress`）：

- `op=0`：包内包含主流更新和 aux 色度更新。当前 FreeRDP 依次对主流、aux 调用 H.264 decoder 并重建色度。
- `op=1`：主流亮度/基础 YUV420 更新，只处理主流。
- `op=2`：主流 bitstream 携带色度-only 更新；它不是“第二条 aux 数据”的同义词。若低色度模式要跳过色度效果，仍需先确认该包是否属于后续主流 P 帧的 H.264 参考序列；若需要推进状态，应解码但不做色度合成/输出，区域保留旧色值。该协议/decoder 事实必须通过 3.32.0 源码核查与连续帧 fixture 验证，不能仅从函数名假定。
- `libfreerdp/primitives/prim_YUV.c` 中 `general_LumaToYUV444` 的 B2/B3 会从主流 4:2:0 色度生成全分辨率色度样本；随后 `libfreerdp/codec/yuv.c::avc444_yuv_to_rgb` 仍逐像素执行 YUV444→RGB。
- 标准 AVC420 解码路径 `avc420_decompress()` 已用 `yuv420_context_decode()` 将解码后的 YUV420 数据直接写成目标 RGB/BGRA 格式。可评估复用这条转换 helper，避免为低色度模式先扩成 YUV444 再转 RGB。

## 3. 语义与正确性约束

### 3.1 不能只把 aux 指针设空

上游 `gdi_SurfaceCommand_AVC444()` 会解析两组 metablock，并在成功后分别对两组矩形通知 `UpdateSurfaceArea`。若只跳过 aux 解码但仍通知 aux 矩形，GFX surface 上这些矩形会保留旧像素，造成局部陈旧色块；如果跳过时返回失败，也可能进入错误处理路径。实现必须把解码策略、有效 dirty rect 和呈现通知作为一个整体设计。

### 3.2 op 处理规则

| op | 正常含义 | 低色度模式建议 |
|---|---|---|
| 0 | 主流 + 第二条 aux 同时有更新 | 主流解码一次并直接 YUV420→BGRA；aux 不交给 H.264 decoder、不提交 aux dirty rect。若输出区域/元数据不能安全验证，整个 command 在开始解码前选择原始 AVC444 路径 |
| 1 | 主流更新 | 解主流并按主流区域输出 YUV420→BGRA |
| 2 | 只有色度 rect | 解码该压缩包以推进主 H.264 参考状态，但不做 AVC444 色度重建、不写 BGRA、不提交该 rect 为 dirty | 区域颜色保持旧值，直到后续 op=0/1 主流更新覆盖；残色是明确的有损行为，须在连续真实帧中验收可接受 |

`op=2` 不是 aux 的别名：它仍是主流 H.264 输入序列的一部分。若连压缩包也不解码，可能破坏后续主流 P 帧参考，因此计划只跳过色度重建和输出，仍调用主流 H.264 解码器。

必须核实 FreeRDP 的 SurfaceCommand/Frame ACK、错误返回与 GFX invalid-region 语义，保证 `op=2` 虽不输出仍能完成主流 H.264 解码、且不触发多余 surface update。RDP 图形 SurfaceCommand 正常完成回执仍由上游流程负责；不得在桥接层直接吞掉整个 command 或破坏事件循环。

### 3.3 H.264 参考状态

主流与 aux 在当前实现中进入同一个 H.264 context。低色度模式只解主流：`op=0/1` 解主流后输出 YUV420→BGRA，辅助流不提交给 decoder；`op=2` 解主流压缩包但跳过色度重建/呈现。模式必须在连接/解码器初始化前确定，连接中途修改配置不得热切换。必须验证连续 IDR/P 帧、暂停恢复、丢包和尺寸重置；无法证明参考状态可靠时须回退整条会话到完整 AVC444，不能混用半截 4:4:4 状态与低色度输出。

### 3.4 色彩与画质预期

- 结果不是灰度画面，而是由主流 YUV420 的 Y/U/V 生成完整 RGB 图像；相对于 AVC444，彩色文字、细线和高饱和边缘会出现 4:2:0 色度损失。
- 亮度与几何画面应保持，彩色边缘允许变软；不得出现大面积黑屏、旧色块、错位、花屏或持续停帧。
- 窗口文字通常仍可读，但不可声称与 AVC444 视觉等价。模式文案应明确“降低色度精度以减少解码/合成开销”。

## 4. 首阶段交付范围：一次完成可提交候选

本计划不把实现拆成“先做一个只丢 aux 的小实验、以后再补 UI/ABI/测试”。首阶段作为一个完整纵向交付批次，做完后应能在设置中启用、经连接参数进入每会话 native 解码路径、按安全规则处理图形区域、查看实际诊断，并具备可重复的自动化验证。该批次仍不保证 CPU 一定降低；性能 A/B 是是否值得提交为优化功能的硬验收项。

### 4.1 本轮范围与明确不做

- **协议不变：**仍广告/协商现有 AVC 能力，若服务器选择 AVC444/AVC444v2，实际 codec 仍为 14/15；不改服务器策略、不强制退 AVC420、不减少网络带宽承诺。
- **格式折中：**启用时将适用的 AVC444 主流更新按 YUV420 精度解码并写入现有 BGRA surface；跳过 aux 与色度-only更新带来的细节损失，在开关说明中明确。
- **仅每会话：**模式在建立连接时确定；一个会话启用不会改变其他会话。连接建立后修改设置只影响下一次连接，不在解码序列中途热切换。
- **默认安全：**默认关闭，旧配置缺字段时按关闭处理。原始 AVC444 路径保持不变，作为用户随时可用的回退。
- **不纳入：**GPU YUV surface、跨设备零拷贝、改服务器端、改变全局 GFX 协商、运行期从低色度切回 4:4:4、降低分辨率/帧率。

### 4.2 实现设计与连接闭环

1. **配置模型：**在 `RdpConfig` 增加 `low_chroma_avc444` 布尔项，默认 `false`；`Sanitize()` 对缺省/非法旧配置安全归一；`Clone()`、JSON 持久化及用户设置导入导出路径一并覆盖。只有用户开启且 GFX AVC 能力可用时才请求该模式；禁止通过隐式替换全局 `decode_backend` 语义实现。
2. **设置 UI：**在 Settings 的内嵌 RDP 设置区域添加 `ToggleSwitch`，标签建议“AVC444 低色度模式（降低颜色精度）”。开关下方说明：只影响新建连接；协议仍为 AVC444；可能降低色彩细节，网络流量不变；默认关闭。开关切换时持久化，保存失败须将控件状态恢复，避免界面与配置不一致。
3. **ABI 兼容：**不修改已冻结的 `ycn_rdp_params`、`ycn_rdp_connect()`、`ycn_rdp_connect_ex()` 布局/签名。新增版本化 `ycn_rdp_connect_ex2(..., decode_policy, graphics_options)`（或同等有 size/version 的扩展结构）；定义独立 `YCN_GFX_OPTION_LOW_CHROMA_AVC444` 位，并提供能力位表明 DLL 支持该选项。C# 仅在能力确认后调用新入口；能力不匹配时要明确失败/日志，不可静默按“已启用”继续跑原路径。
4. **Native 会话绑定：**连接线程将 graphics option 写入该会话自己的 `YcnSession`；FreeRDP 的 GDI context 建成后、首个 GFX SurfaceCommand 处理前，将不可变的模式设置到该 context。参考已有 `ycn_gdi_set_session_paused()` 的每 `rdpContext` 隔离方式；不得使用进程级 env flag 或全局可变开关充当正式 per-session 配置。
5. **FreeRDP 补丁边界：**在锁定 3.32.0 的 overlay patch 中加入 per-context option 查询与 AVC444 low-chroma 分支；禁止为此直接修改本机已安装 vcpkg 包而不产出仓库补丁。patch 需通过 `native-deps/ports/freerdp/portfile.cmake` 自动应用，并由补丁哈希纳入 vcpkg ABI/cache 身份。
6. **设置解释与日志：**连接日志记录请求值、DLL/patch capability 和 decode mode；仅当实际收到 codec 14/15 时记录“低色度路径命中”。服务器若未选择 AVC444，显示/日志应报告“未命中 AVC444，模式未生效”，而不是报错或虚构节省。

### 4.3 安全的 op 与 dirty-rect 算法

默认路径必须完全走原始 `avc444_decompress()` 与上游 dirty-region 行为。低色度分支按以下决策实现，并保持 codec 11 与全部非 AVC codec 不变：

| 输入 | 低色度分支 | GFX surface/dirty 更新规则 |
|---|---|---|
| codec 非 14/15 | 不进入新逻辑 | 完全沿用当前实现 |
| op=0，主流与 aux 都有 rect | 解主流并走 YUV420→BGRA 输出主流 rect；不解码 aux，不提交 aux rect | aux-only 色度变化是模式明确舍弃的画质损失；不把 aux rect 标为 dirty，不复制旧像素。模式连接起始固定；不能在跳过多帧后逐 command 切回 AVC444 |
| op=1，只有主流 rect | 走 YUV420→BGRA 直接输出 | 仅更新主流 rect；空 rect/延迟解码依照上游 status 语义处理 |
| op=2，只有色度 rect | 先在 P0 验证其是否参与主流 H.264 参考序列。若参与，则解码压缩包推进状态，但跳过色度重建/BGRA 写入/dirty 提交；否则可跳过解码 | 区域颜色保持旧值，直到后续 op=0/1 主流更新覆盖。连续 P 帧、残色及恢复范围必须实测；未过闸时整个 session 保持原始 AVC444 |
| 连接前能力/初始化/格式前置条件不满足 | 在处理首个 SurfaceCommand 前，把整个 session 固定为原始 AVC444 模式 | 只允许在尚未进入低色度解码前选择原路径；模式开启后不可逐 command 切换 |
| 主辅元数据非法、rect 越界或解码错误 | 走现有失败/恢复语义；不可把失败包再送到另一解码路径“补救” | 不伪造成功/低色度命中统计；按上游 GFX/H.264 错误处理或受控重连，防止重复消费与状态分叉 |

**参考状态与 dirty-region 判据是首阶段 Go/No-Go 的关键：**先用 3.32.0 源码和连续多帧 fixture 确认 op=0 主流与 aux 是否共享/依赖同一参考状态，能否不解 aux 而连续解主流 P 帧；再核对主流 rect、aux rect、surface bounds 和上游 `invalidRegion`。不得假定两类 rect 相同或互相包含，也不得跳过 aux 后仍对 aux rect 调 `UpdateSurfaceArea`。低色度策略在连接开始时固定；能力、尺寸或格式不满足时必须在首个 command 前将 session 锁定为原始 AVC444，不能对已消费过的 command 逐包切换/重放。若运行期发现无法继续保证状态，按现有 decoder failure recovery/受控重连处理并记录原因，不让部分输出路径与部分 decoder 状态混合。

低色度模式连接期间固定：`op=0/1` 主流更新在验证通过后可直写 BGRA；是否跳过 op=0 aux 解码取决于 P0 确认的参考依赖。`op=2` 若为后续主流参考所需则解码但不重建色度、不写 BGRA。禁止同一 session 混合直出 BGRA 与旧持久 YUV444 planes 合成；能力、格式、H.264 reference 行为和 decoder 初始化检查必须在首个 command 前完成并锁定路径。

### 4.4 单一首阶段内的工作包与顺序

#### W0：冻结验收基线（先做，仍属于本批）

- 核验实际构建基线、FreeRDP `3.32.0`、patch manifest、运行时诊断版本、codec 统计和硬/软件 decoder；保存修改前 `op=0/1/2` 的离线参考输出。
- 以同一 H.264 输入验证现有 `avc444_decode_probe.py` 在 codec 14/15、software/DXVA2 两种策略下正常；记录现有全路径耗时基线。若离线 fixture 或运行 DLL 不匹配，先修测试环境/锁定 DLL，不进入性能结论。
- 获取至少一个真实负载基线（动态画面 + 静态桌面），读取现有 SurfaceCommand QPC 聚合、实际 codec 计数、FPS 与客户端 CPU；没有真正 AVC444 流时不把模拟数据冒充真实收益。

#### W1：FreeRDP per-context 低色度核心

- 新增默认关闭的低色度 mode 和按 `rdpContext` 隔离的 setter/getter；连接建立与 GFX context 初始化阶段完成设置并锁定路径，在 context teardown 清理槽位，覆盖重连、失败连接、资源 resize 与多会话。
- 在 AVC444 command/decompress 边界保留原始实现为默认分支；只有能力位与 per-context mode 都匹配才进低色度支路。完整原始路径与低色度路径在该 session 第一个 SurfaceCommand 前选择，开始解码后不逐包切换。P0 未证明主流/aux 参考可隔离时，低色度模式保持不激活并明确记录原因。
- 实现主流 H.264 解码 → YUV420→现有目标 BGRA 格式写入 → dirty rect 检查/提交的整条路径。低色度 mode 在连接开始前锁定；禁止“解码成功但沿用 aux dirty rect 刷旧 surface”以及跳过转换后将 YUV 数据误交给 BGRA consumer。
- 正确处理 op 0/1/2、空 rect、尺寸变化、decode 返回 0（无新帧）与负值错误；op2 若属于参考序列则保留压缩解码、只抑制颜色输出。不重放失败包，不在 RDP/FreeRDP 事件线程同步等待 UI。
- 低色度命中、fallback、op2 skip、每种 fallback reason 采用内存计数；日志由现有周期诊断汇总，禁止逐帧磁盘 I/O。

#### W2：C ABI、配置与 UI 全链路

- 新增版本化连接入口及 graphics option capability；旧入口维持原 ABI 与原图像行为，新增参数采用 size/version 或独立 `_ex2`，不得给旧平铺 struct 尾部直接加字段后复用旧 DLL。
- 将 `RdpConfig.low_chroma_avc444` 接至 `RdpEmbeddedClient` → `RdpNativeInterop` → `YcnSession` → FreeRDP context；保证每会话独立，关闭默认。
- Settings 页添加 ToggleSwitch 与影响说明；开关只对新连接生效，现有会话继续使用建立时锁定的 mode。保存/能力验证异常时还原开关并记录原因；不弹确认对话框代替开关。
- 设置项只对 embedded RDP 生效；mstsc 模式不应调用 native option；AVC420、非 AVC codec、禁用 GFX 的原行为保持不变。
- 配置导入/导出、缺少键的旧配置反序列化、Clone 和 Sanitize 全覆盖。低色度设置是可导出的普通偏好，不涉及任何凭据或秘密字段。

#### W3：自动化测试与可复现实验

- 扩展 `avc444_decode_probe.py`：支持生产 low-chroma option、两种 codec ID、software/DXVA2、主流/aux 负载及输出 BGRA 导出；增加主流 YUV420 direct 结果与参考结果的逐像素比较。对因上采样取整导致的差异，经校准后才允许预设误差门限。
- fixture 扩展到多帧连续流，至少包含 IDR 后连续 P 帧；主流、aux 按真实 AVC444 编码关系构造，不能拿两个互不相关的 packet 证明“连续流通过”。覆盖 op=0/1/2、主辅 rect 相同/不相同、空区域、resize/reset，并验证 aux 不送 decoder 后主流 P 帧仍能成功。
- 为 dirty-region 与 op 分派提供原生测试 seam/纯函数测试，断言：aux rect 不造成 stale copy；op2 若属于主流参考序列则 decoder 状态推进但无 BGRA/dirty 输出；模式不在连接中切换；参考/区域语义不安全时整 session 保持原始 AVC444；decode failure 不记作低色度成功；会话槽位重用不继承旧 mode。
- 扩展 `tests/native_decode_checks.py` 校验新 API 版本、capability bits、无支持 DLL 时的安全行为和旧 `connect/connect_ex` ABI 未变；扩展 `_smoke` 配置默认值、Clone/Normalize、版本化入口接线、设置 UI 行为和“仅新连接生效”静态断言。
- 跑 FreeRDP overlay patch clean apply/build、native CMake Release x64、独立 AVC444 decode probe、native decode checks、主工程 build 和 `_smoke`；每条测试必须对应当前产物，确认 DLL/Smoke mtime，避免跑旧 binary 得到假绿。

#### W4：真实会话 A/B、CPU 与画质验收

- 在相同机器、GPU/驱动、电源、目标分辨率、画面内容、帧率、窗口尺寸和 decoder policy 下比较：A 原始 AVC444；B 低色度模式。每组至少 3 轮、每轮稳定采样不少于 60 秒；记录中位数及各轮范围。
- 每组确认实际收到 codec 14/15，不能仅看设置或 advertising；记硬/软件帧数、每 codec SurfaceCommand count/bytes/ticks、客户端进程 CPU 时间/墙钟、有效 Present FPS、帧延迟及画面截图。
- 画质覆盖桌面文字、彩色细字体、色条、窗口拖动/滚动、视频/动态区域、颜色只变化的动画；重点检视 op2-only 更新残色和 aux rect 不同于 main rect 的边界。
- 生命周期覆盖 1/2/4 路、并行不同模式会话、快速连接取消、断开重连、切页暂停/恢复、分辨率变化、设置翻转后旧会话不变且新会话生效。
- 性能结论要分三层：decoder/surface 处理耗时、客户端总 CPU、服务端/网络；该功能只处理客户端，预期不减少服务器发来的 aux 字节，也不保证总 CPU 必降。

#### W5：提交候选审查闸门（全部通过才算首阶段完成）

- 原始模式回归与低色度模式测试均全绿；无新警告/ABI 破坏/patch apply 不稳定；FreeRDP、native、WinUI、Agent（如共享源码变更）都完成对应构建。
- 两种模式实际 codec 诊断正确；低色度功能只在明确开启的 AVC444 会话命中；非 AVC、AVC420、未启用模式和其他会话不受影响。
- dirty rect 无残块、错色、撕裂、持续冻结；op2 色度-only 延迟/残色表现满足计划书对低色度的接受范围；失败/禁用能立即回到原路径（下一次连接生效）。
- A/B 得到超过测量噪声的客户端处理耗时或 CPU 改善，且相同 FPS/分辨率下没有不可接受的画质/延迟退化。若收益不显著或 op/dirty rect 状态无法安全封闭，本批未达“可提交的性能优化”标准，应删除实验实现或缩回原始路径，不以“代码能编译”宣布完成。
- 形成计划台账/提交说明：基线 hash、补丁 hash、配置默认值、真实 codec、decoder backend、测试命令/结果、A/B 数据、已知画质限制、回退方式；完成后等待用户指示再提交，不自动 commit。

### 4.5 首阶段明确验收标准

| 类别 | 通过判据 |
|---|---|
| 协议 | 连接仍按原有能力协商；实际 codec 14/15 时低色度模式只改客户端解码/重建，codec/服务器设置不变；服务端仍可发送 aux，网络流量不承诺下降 |
| 默认兼容 | 新配置默认 false；旧连接 API/结构不变；关闭开关时生产路径与基线一致 |
| 画面 | 低色度主流输出有效；亮度和几何无误；主流 rect 更新稳定；只允许预期色度精度退化；无未声明 stale rect、黑块或持久花屏 |
| 会话隔离 | 不同 session 可同时一开一关；连接失败、销毁、槽复用不串 mode；设置更改不热切现有会话 |
| 解码恢复 | 连续 P 帧、暂停恢复、resize/reset、失败回退均无错误循环；aux 不被送入低色度解码器；所有计数按会话归属 |
| 诊断 | Requested / Supported / Active / Fallback 分开记录；非 AVC444 显示未命中；不可支持时明确失败，不冒报已启用 |
| 测试/构建 | clean patch apply、FreeRDP/native Release x64、WinUI build、_smoke、离线双流测试全部通过；确认运行的产物新鲜 |
| 性能/画质 | 至少 3×60 秒同负载 A/B；改善超过噪声且画质/帧率满足项目接受标准，否则不作为性能功能提交 |

## 5. 首阶段预计文件范围与具体改动点

| 文件 | 计划改动 |
|---|---|
| `winui3/native-deps/patches/freerdp-3.32.0-ycnz-avc444-low-chroma.patch`（暂定名） | `libfreerdp/codec/h264.c` 主流 direct YUV420→BGRA 分支；`libfreerdp/gdi/gfx.c` per-context mode 与安全区域检查/提交；需要时扩展内部头文件/测试 seam。只改已验证必要的 FreeRDP 文件 |
| `winui3/native-deps/ports/freerdp/portfile.cmake` | 已按排序自动加载 `patches/*.patch`；核对新增 patch 排序、干净应用与包 ABI 哈希，不无谓改 port |
| `winui3/src/Yomori.RdpNative/ycn_rdp.h` | 新 graphics option 位、能力位、`connect_ex2` 声明；旧 struct / entry points 不动 |
| `winui3/src/Yomori.RdpNative/ycn_rdp.c` | session option 存储、versioned entry point、context 初始化时设置、会话诊断/错误回退；不让 UI 线程直接改 FreeRDP 私有状态 |
| `winui3/src/Yomori.App/Models/RdpModels.cs` | `LowChromaAvc444` 默认 false，JSON key `low_chroma_avc444`，Clone/Sanitize/导入导出路径 |
| `winui3/src/Yomori.App/Services/RdpNativeInterop.cs`、`RdpEmbeddedClient.cs` | capability 检查，ex2 参数传递，模式锁定与连接日志；不支持新入口时 fail-safe |
| `winui3/src/Yomori.App/Views/SettingsPage.xaml`、`.xaml.cs`、必要 ViewModel | ToggleSwitch + 说明，持久化、失败回拨、只新连接生效 |
| `winui3/tests/avc444_decode_probe.py`、`native_decode_checks.py`、必要 native C tests | 真实双流/连续帧/op/rect/软硬解/逐像素与能力测试 |
| `_smoke`（位于工作区 `整合/_smoke`，不在 git 仓库内） | 配置默认值、连接 API 接线、ABI 与 UI 静态断言；记录需在本机单独运行，测试源码不会自动随仓库入库 |
| CI/build/runtime verification | overlay hash 已进入包身份；验证 native DLL 与 patch manifest 一致、发布包不混用旧 FreeRDP DLL |

## 6. 风险与回退

- **协议分支误判：**把 `op=2` 当成 aux 物理流跳过，或漏掉色度-only更新，会造成后续色度/区域状态错误；按 LC/op 定义逐项验证。
- **更新区域陈旧：**跳过 aux 后若仍发 aux dirty rect 通知，会把旧像素当成新画面提交；op2 不做色度输出会造成颜色滞后。前者禁止，后者属于明确有损语义，须在 UI 说明并实测。
- **H.264 状态污染：**op0 不送 aux 是否会影响共享 decoder 中主流 P 帧参考，当前需 P0 实测；op2 若属主流参考序列则必须继续解码。不能仅依 LC 名称推断包之间独立。
- **硬解输入格式：**DXVA2 输出可能经 NV12/CPU transfer；复用 `yuv420_context_decode()` 前要覆盖硬解后的 plane/stride/色彩格式，禁止假设所有后端输出都是 I420。首阶段不应默认把“op=0 辅助流在同一 H264_CONTEXT 中会破坏主流参考”当作已证事实，必须实测参考关系；若无法独立安全解主流，则低色度模式不能激活，不能冒险跳 aux。
- **ABI/补丁版本：**项目有固定 FreeRDP 3.32.0 与补丁身份检查；更新补丁需完整重建/发布校验，不可混用本机旧 DLL。
- **回退：**设置默认关闭；建立 session 时检查 capability、decoder backend、目标格式和 per-context setter 成功与否，任一失败则从第一条 SurfaceCommand 起锁定原始 AVC444。若低色度路径开始后发生解码/参考状态失败，走现有失败恢复和受控重连，重连时可禁用该 mode；禁止在同一已消费序列中把当前包重放给原路径。不动服务器策略。

## 7. 历史记录与当前状态区分

- 当前 `main` 基线 `f769c97` 没有 aux 丢弃实现。
- 历史提交 `6086a72` 曾引入 `--embed-drop-avc444-aux` 与 `freerdp-3.32.0-ycnz-drop-avc444-aux.patch`，属于诊断/监控试验，不在当前主分支工作树中。历史方案通过进程环境变量开关，仅在 `op=0` 跳过 aux 解码；它没有覆盖本计划建议的 `op=2` 行为、直接 YUV420→BGRA 路径、dirty rect 一致性与端到端验收，因此不能直接视作完成实现。
- AVC420 协商的本机失败和回退到旧 codec 造成低帧率/高带宽的问题已记录于 `memory/2026-11-13.md`；本计划沿用 AVC444 协商，改在客户端解码/色彩管线内部做精度与成本折中，不尝试再强制服务端 AVC420。

## 8. 批次完成定义与后续

本课题按第 4 节 W0–W5 作为**一个完整首阶段批次**推进，而不是只做 aux-drop 补丁后停在半成品状态。该批次完成时应包含配置/UI、版本化 ABI、FreeRDP per-context 实现、诊断、离线/自动化验证、Release x64 构建及真实会话 A/B 结论，并形成代码可审查的提交候选。当前轮只产出计划书，不等于已批准或已实现该代码批次。

提交候选须同时满足：

1. 功能正确与协议兼容验收通过；AVC444 协商仍由原能力协商决定，低色度模式仅改客户端解码输出。
2. 原始模式和低色度模式均可明确启停；旧配置默认关闭；设置失败能回拨；旧 ABI 保持兼容；会话间不串状态。
3. 自动化测试、FreeRDP 补丁 clean apply、native/WinUI build 和 `_smoke` 全通过；发布运行库/patch hash 核对通过。
4. 真实 A/B 提供可复核数据。若没有超过测量噪声的收益，或色度残留不可接受，则该候选不能以“性能优化”提交：应移除低色度功能或收缩为仅内部诊断实验，再由用户决定后续方向。
5. 用户确认后再执行提交；本计划不授权自动提交、推送或改服务器设置。

首阶段不包含 GPU YUV surface。若 P4 后续另立案，必须在 CPU profile 证明 BGRA 转换/上传仍是主要成本后再评估。

## 9. 推进台账

| 批次 | 时间 | 产出 | 自测 | 状态 |
|---|---|---|---|---|
| W1 上游补丁 | 2026-11-14 12:20 | 新增 `native-deps/patches/freerdp-3.32.0-ycnz-avc444-low-chroma.patch`：`H264_CONTEXT_OPTION_YCN_AVC444_LOW_CHROMA`（0x59434E02）+ 私有 `ycnAvc444LowChroma`；`avc444_decompress` 在低色度模式下 op0/1 走主流 YUV420→BGRA、op2 只解码不输出；`gfx.c` 加每 `rdpContext` 开关（`ycn_gdi_set/get/release_avc444_low_chroma`）、跳过 aux rect 的 dirty/UpdateSurfaceArea、op2 不做区域更新 | 按 vcpkg 的实际打补丁方式（`git apply --ignore-whitespace --whitespace=nowarn`）在纯净 3.32.0 tarball 上按 sorted 顺序应用 7 个上游 port 补丁 + 4 个 ycn 补丁，全部 OK；CI run 43556540733 用该补丁重建 freerdp3.dll 成功 | 已完成（已进 DLL） |
| W2 桥接 ABI | 2026-11-14 12:25 | `ycn_rdp.h`：`YCN_DECODE_CAP_AVC444_LOW_CHROMA`(0x8)、`YCN_GFX_OPTION_LOW_CHROMA_AVC444`(0x1)、`ycn_rdp_connect_ex2`；`ycn_rdp.c`：会话 `gfx_options`、ex2 未知位拒绝、`gdi_init` 后设置、teardown/abandon 释放登记 | CI 步骤「校验原生层」PASS：能力位与 ex2 存在性互约束通过、未知 graphics option 被拒；打包校验脚本要求的新导出也在产物里 | 已完成（已进 DLL） |
| W2 配置/UI | 2026-11-14 12:28 | `RdpConfig.LowChromaAvc444`（`low_chroma_avc444`，默认 false）、`RdpConnectInfo.LowChromaAvc444`、`MainViewModel` 透传、`RdpEmbeddedClient` 能力位判断 + ex2 调用 + 诊断字段 `avc444_low_chroma`、设置页 ToggleSwitch + 保存失败回拨 | `_smoke` 新增 5 条断言全 PASS（默认关、键名往返、Clone、旧配置缺键、Sanitize 不重置）；整体 2410 PASS / 1 FAIL（失败项为既有的 `[遮挡闸门] 节拍器…`，依赖未提交的 occlusion 工作，与本改动无关） | 已完成 |
| W3 离线测试 | 2026-11-14 13:49 | `avc444_decode_probe.py`：`--low-chroma` 走 `h264_context_set_option`；`--full` 增加「低色度 op0 输出必须与 op1 主流单解逐字节相同」的等价断言 | 用 CI 产物实跑：op1 4 项 PASS；op0 双流 4 项 PASS（PSNR 40.444 / 40.802，MAE 1.843 / 1.738）；低色度 4 项 `equivalent=True`（两侧 sha 完全相同）。首轮因分支返回 `1` 而上游成功约定为 `0` 被判 FAIL，改 `return 0` 后 **`FINAL: op1(PASS) full(PASS)` 12/12 全绿** | 已完成 |
| W3 ABI 测试 | 2026-11-14 13:10 | `native_decode_checks.py`：已知能力位加入 0x8；能力位与 `ycn_rdp_connect_ex2` 存在性互相约束；未知 graphics option 必须被拒 | CI run 43556540733 步骤「校验原生层」PASS（含本项）；另用 pefile 之外的方式直接读产物：`ycn_rdp_decode_capabilities()` 返回 `0xF`，`ycn_gdi_set/get/release_avc444_low_chroma` 与 `ycn_rdp_connect_ex2` 均已导出 | 已完成 |
| W4 A/B | 2026-11-14 15:05 | 交替采样实测（`_probe/ab-lowchroma-2026-11-14/result.md`）：同一时间段内 on/off 快速交替，按日志模式标记分组 | codec 路径 **3.345 → 3.544 ms/surface（−5.6%，四分位不重叠）**，即约 0.68% 单核；进程 CPU 因噪声不可判；**GPU 视频解码/3D 引擎无差异**（`hw_frames/surface` 两模式都是 1.000）；显存无差异 | 已完成，**结论：无可感知收益**，不作性能卖点 |

**当前闸门**：本机沙箱的黑名单拦截 `reg.exe`，vcpkg 在 `pkgconf` 步骤即失败（`PROGRAM BLOCKED BY SECURITY POLICY`），所以**无法在本沙箱内重建带补丁的 freerdp3.dll**。构建一律交给 CI，两轮均已通过：`43556540733`（`3b897a2`）首次用本补丁重建并跑通原生层/能力校验；`43560648588`（`042e192`）在返回值修正后重建同样通过。

离线等价性用 CI 产物在本机实跑，结论成立；过程中确认了一个**本机环境坑**（与产品无关）：本机 Python 自带的 `libcrypto-3-x64.dll` 与 Yomori 用的 vcpkg OpenSSL 同名，`import hashlib` 会把它先载入进程，之后加载 `freerdp3.dll` 得到 `WinError 127`。绕法是启动时先预载同目录的 OpenSSL；CI 的 runner 上没有此冲突。另有依赖不全的问题：只放 CI 的 13 个 DLL 会缺 `avformat-63`/`avfilter-12` 等，需与既有构建目录的 DLL 合并后再跑探针。

真实会话 A/B 与 CPU 收益结论仍未取得，当前不得声称「降低 CPU」。
