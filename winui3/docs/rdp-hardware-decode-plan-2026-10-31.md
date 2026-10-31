# 内嵌 RDP 优先硬件解码实施计划

- 日期：2026-10-31（北京时间）
- 状态：首阶段已实施并通过本机构建与解码回归；完整 RDP 会话和多路 CPU 验收待执行
- 基线：当前仓库 `00a7246`；Windows x64、WinUI 3、FreeRDP 3.32.0
- 范围：内嵌客户端的视频解码、相关诊断、构建和发布依赖
- 实施记录：[代码审查与验证结果](rdp-hardware-decode-implementation-2026-10-31.md)

## 1. 目标与结论

让内嵌画面默认优先使用本机 GPU 解码服务器发送的 H.264 图形数据；设备、驱动、编解码格式或服务器能力不满足时，自动使用软件路径，并说明实际路径及回退原因。

推荐以现有 FreeRDP 3.32.0 的 H.264 解码框架为接入点，先验证 Windows 下 FFmpeg 默认的 DXVA2 解码路径，并对照测试 D3D11VA，再开启受能力探测保护的 AVC 协商。第一阶段继续输出现有 BGRA 帧，保留 GDI 合成和当前 WinUI 显示接口；如果 GPU 回读、YUV 转换及整帧复制仍占主要 CPU，再进入第二阶段的 GPU 表面合成与显示改造。

**不能把 `GfxH264=TRUE`、编译时支持硬件加速、D3D11 显示成功中的任意一项，当成实际硬解成功。** 验收必须包含实际解码后端、成功输出帧计数和 GPU Video Decode 活动等证据。

用户已批准开始实施。首阶段接入 AVC420 + FFmpeg/DXVA2，保留 BGRA 显示链；不修改服务器策略和用户安装目录。当前 CPU 占用的具体贡献尚未完成端到端采样，不能承诺整体 CPU 降幅。

## 2. 当前实现与证据

以下行号对应上述基线；本地构建缓存只能证明该目录曾经的配置，不能证明当前运行进程加载的 DLL 来自同一构建。

| 已核查事实 | 证据位置 | 对方案的影响 |
| --- | --- | --- |
| GFX 已开启，但 H.264 被明确关闭；同时设置 Progressive=FALSE、Planar=TRUE | `src/Yomori.RdpNative/ycn_rdp.c:1993–1996` | 当前不走 H.264 硬解；只调整渲染器无法打开硬解 |
| 使用 `gdi_init(..., PIXEL_FORMAT_BGRA32)` | `src/Yomori.RdpNative/ycn_rdp.c:2021` | FreeRDP 图形最终进入 CPU 端 BGRA 合成表面 |
| EndPaint 将 GDI 整帧复制进 native 会话缓冲 | `src/Yomori.RdpNative/ycn_rdp.c:294–354` | 即使 H.264 硬解成功，仍保留整帧 CPU 拷贝 |
| `ycn_rdp_copy_frame` 在锁内复制整帧 | `src/Yomori.RdpNative/ycn_rdp.c:2921–2938` | 不能为省拷贝直接交出可被并发覆盖的指针 |
| `TryCopyFrame` 先查询尺寸，再拷贝至复用托管数组 | `src/Yomori.App/Services/RdpEmbeddedClient.cs:261–309` | 每次呈现仍有 native→managed 拷贝 |
| D3D 渲染器使用 CPU 可写 staging，Map 后拷入 BGRA，再 CopyResource、Present | `src/Yomori.App/Services/RdpD3DRenderer.cs:201–215、256–310` | 这是 GPU 显示，未提供视频硬解；存在 CPU→GPU 上传 |
| 呈现计时器约 16ms 一次，已有新帧序号判定及全屏宿主挂起 | `src/Yomori.App/Views/RdpView.xaml.cs:484–497、757–843` | 已避免静止画面重复呈现；托盘隐藏与离页连接仍需另行测量 |
| `render_backend=software` 和 `--embed-nod3d` 是现有故障逃生入口 | `src/Yomori.App/Models/RdpModels.cs:679–690`；`Services/RdpD3DRenderer.cs:41–64` | 新硬解策略必须明确这些开关的优先级 |
| C ABI 结构与 C# P/Invoke 布局固定 | `src/Yomori.RdpNative/ycn_rdp.h:53–92`；`Services/RdpNativeInterop.cs` | 不得直接给旧参数结构追加字段并继续复用旧入口 |
| CI 固定 vcpkg 提交、安装 `freerdp[client]:x64-windows` | `../.github/workflows/build.yml:58–61、123–127` | 需要显式固定解码依赖和构建特性 |
| native 缓存只看桥接三份源码；vcpkg 缓存只看提交和 triplet | `../.github/workflows/build.yml:80、121、138、221` | 修改解码特性后可能继续复用旧 DLL，必须同步调整缓存身份 |
| 本地旧构建缓存开启 FFmpeg/HWACCEL，关闭 Media Foundation/OpenH264 | 本机 `C:/Users/AITNR/WorkBuddy/_tools/vcpkg/buildtrees/freerdp/x64-windows-rel/CMakeCache.txt` | 本地与 CI 不一致；不能根据本地成功推断发布包具备同样能力 |
| 现有打包守卫只明确要求原先八个 native DLL | `src/Yomori.RdpNative/CMakeLists.txt:60–68`；`tools/package_release.py:262–271` | 引入 FFmpeg 后须覆盖其实际传递依赖 |

说明：上表仓库路径除工作流外均相对 `winui3/`。旧版 `docs/rdp-embed-freerdp-plan.md` 的首期策略为关闭 H.264；该文档是历史设计背景，本计划若获实施应更新对应章节。后续 GPU 改造不能照搬历史文档中“零 UI 线程参与”等已被源码纠正的说法。

当前数据流：

```text
服务器图形更新（当前未启用 AVC）
  → FreeRDP 软件解码/图形处理
  → GDI BGRA 主表面
  → native BGRA 快照
  → 托管 byte[]
  → D3D11 staging 上传
  → SwapChainPanel
```

硬解能降低其中的视频解码成本，但不自动消除 GDI 合成、色彩转换、内存复制和服务器编码成本。环回连接下，同一台机器上的 RDP 服务端和目标程序也消耗 CPU；测量必须分别记录客户端、服务端所在进程和目标程序。

## 3. 技术路线与边界

### 3.1 第一阶段：沿用现有显示接口，验证并接入硬解

1. 保持 FreeRDP 版本为 3.32.0，主路线采用 FFmpeg + Windows 默认 DXVA2，先复用现成接入点验证稳定性和收益。
2. 对照测试 D3D11VA；只有稳定性、适配器行为及性能至少不差时再替换默认后端。现有 FreeRDP 没有 D3D11VA→DXVA2→软件的自动链式回退，不把该链路当作现成功能，也不为首版引入多次硬解重连。
3. 本地解码器具备可靠输出能力后，再向服务器宣告 H.264/AVC 能力；同时保留 Planar / 原有图形路径。
4. 硬件解码输出先适配现有 GDI/BGRA 管线，不改输入、音频、会话代理、任务执行或收尾语义。
5. 硬解优先策略作用于每个会话。正式实现应将硬解启用/禁用传递到 H.264 上下文选项，不能通过关闭 `SoftwareGdi` 来强制软件解码，因为现有显示链依赖软件 GDI。后端选择若只保留进程级统一策略，必须启动时一次初始化且不覆盖用户明确的诊断设置；需要会话级 DXVA2/D3D11VA 选择时，应增加受控上游补丁，不能在连接过程中反复切换全局环境变量。

Windows Media Foundation 本次保持关闭。源码中确实有客户端 H.264 decoder，但它创建固定系统 MFT、请求 CPU IYUV 输出，未见显式 D3D manager 绑定；这不足以保证硬件 MFT。当前后端注册顺序为 Media Foundation→OpenH264→FFmpeg，选择首个初始化成功者，运行中不会自动改选后端；贸然开启 MF 可能抢在 FFmpeg 前面。OpenH264 属于软件解码候选，不能计作硬解方案。

已核查的 FreeRDP 3.32.0 源码位于本机 `C:/Users/AITNR/WorkBuddy/_tools/vcpkg/buildtrees/freerdp/src/3.32.0-55a401784c.clean`（仅作为源码证据，不等同于运行包来源）：

| 源码证据 | 结论 |
| --- | --- |
| `libfreerdp/codec/h264_ffmpeg.c:1224–1232、1249–1278` | `WITH_FFMPEG_HWACCEL` 开启时 Windows 默认 DXVA2；`FREERDP_HWCTX_TYPE=d3d11va` 可在隔离测试进程中选择 D3D11VA，覆盖还受上游配置控制 |
| `libfreerdp/codec/h264_ffmpeg.c:1314、1364–1392` | 硬解受 H.264 上下文选项控制；硬件设备创建失败时有软件初始化回退；后续解码错误仍需额外分类处理 |
| `libfreerdp/gdi/gfx.c:702–718、796–811` | 当前硬解选项取自 `SoftwareGdi`；该命名不能按字面理解为“只允许软件视频解码” |
| `libfreerdp/codec/h264_ffmpeg.c:725–772` | 硬件帧经 `av_hwframe_transfer_data` 回读，必要时由 swscale 转换；现成路线保留 CPU 开销 |
| `libfreerdp/codec/h264.c:741–783` | 后端按注册顺序取首个初始化成功者，不提供运行中的跨后端自动恢复 |
| `libfreerdp/codec/h264_mf.c:548–554、614–630` | 创建系统 H.264 MFT 并要求 IYUV，不能据此认定硬解 |
| `channels/rdpgfx/client/rdpgfx_main.c:368–400` | AVC420 和 AVC444 能力分别与 `GfxH264`、`GfxAVC444` 相关，需要按实际能力包验证 |
| `include/freerdp/settings_types_private.h:761–768` | `GfxPlanar/GfxProgressive` 为 deprecated，不能用它们保证客户端只协商某个 codec |

因此，非 AVC 回退应同时关闭相应 H.264/AVC 能力并保留经过验证的 GFX 路径，再记录服务器实际 codec；不能把 `GfxPlanar=TRUE` 当成服务器必定发送 Planar 的保证。现有 `use_gfx` 还被 `ycn_rdp.c:1854、1993` 的无条件开启覆盖，不能直接拿它充当可靠回退开关。

### 3.2 AVC 格式策略

- 先证明 AVC420 在目标机器稳定且实际硬解，再评估 AVC444 / AVC444v2。
- RDP AVC444 的多流重建、色彩恢复和合成仍可能在 CPU 执行；不能把“支持 H.264”理解为任意 AVC444 格式都能直接用单张 GPU 纹理解码并显示。
- 用桌面细字、彩色字体、游戏画面和鼠标移动验证画质、颜色、延迟及完整帧；同时记录服务器最终选择的 codec 和实际解码调用。
- `auto` 应在已验证兼容的能力集合内协商；优先硬解不能以明显糊字、错误色彩或渲染撕裂为代价。
- 服务器未选 AVC 时照常工作，状态显示真实的 Planar / 其他软件路径，不标记“硬解”。

### 3.3 可同时评估的复制与后台开销

软件显示分支在 `RdpView.xaml.cs:788` 先调用一次 `TryCopyFrame`，随后 `RenderFrameAsync` 在 `:937` 再抓取一次，存在可确认的重复整帧抓取。该点应列入 P0 采样及后续小范围优化；若改为传递现有快照，必须明确数组的拥有权，避免异步使用时被下一帧覆盖。

离页或分页外的会话仍保持连接，native 的 EndPaint 仍复制整帧；收进托盘的 `SuspendSurfaceRefresh` 主要停止网格布局重排，不能等同于暂停所有解码和 Present。应分别采集“可见 / 离页 / 最小化 / 托盘”的解码、复制和呈现计数。后台优化若需要暂停快照或呈现，应保留输入/音频/协议 ACK、远端任务和恢复首帧语义，单独验证，不能未经测量声称后台已经省 CPU。

### 3.4 第二阶段：按实测决定 GPU 表面改造

若第一阶段已实际硬解，但 CPU 主要耗在 GPU→CPU 回读、YUV→BGRA、GDI 合成或多次复制，另开实现阶段：

```text
H.264 GPU 解码表面
  → GPU 色彩转换 / RDP 图形表面与更新区域合成
  → 有界 GPU 帧队列或共享纹理
  → UI 线程更新 SwapChainPanel
```

这需要处理整个 RDP 图形表面的缓存、映射、更新和混合 codec；不能只把一个 H.264 解码输出纹理直接交给 WinUI 就宣称完成。须设计 adapter LUID 一致性、纹理格式、跨设备共享、引用计数、同步及尺寸世代号；不能在 UI 线程无限等待 keyed mutex 或 GPU fence。

第二阶段不是第一阶段前置条件，也不预先承诺“零拷贝”。只有分析结果证明现有 BGRA 路径达不到目标，且硬解本身已通过验收，才承担这部分复杂度。

## 4. 配置、能力探测与回退

### 4.1 新增解码策略

建议新增 `decode_backend`，与现有 `render_backend` 分开表达：

| 值 | 行为 |
| --- | --- |
| `auto`（默认） | 优先已验证可用的硬解，失败后软件回退，记录原因 |
| `software` | 禁用硬件视频解码，走明确的软件路径，便于诊断与对照 |

设置界面可显示“解码方式：自动（优先硬件）/ 软件”，会话诊断显示实际后端。硬解失败不应每帧弹窗；同类原因限频记录一次，后续仅计数。

优先级：`--embed-nod3d` 或 `render_backend=software` 的“完全避开 GPU”语义优先，此时解码也使用软件；其次是 `decode_backend=software`；其他情况执行自动硬解策略。旧配置缺少新字段时默认 `auto`，非法值规范化，开关在下次连接生效。现有 GFX 开关与 native 强制开启的行为也应统一说明，不能出现设置与实际能力相反的状态。

### 4.2 能力探测

逐项区分并记录：构建支持的 decoder、运行 DLL 版本与来源、GPU adapter/驱动、视频解码格式与尺寸支持、设备初始化结果、服务器 GFX/AVC 协商结果、首个成功硬解输出帧。

设备创建成功只表示设备可用；必须在成功解码出硬件帧后才把实际状态从“尝试硬解”改为“硬件解码”。没有视频帧时保持“尚未判定”，不把空闲桌面误报为硬解失败。

### 4.3 回退规则

| 场景 | 行为 |
| --- | --- |
| 无 GPU、驱动不支持、硬件 decoder 初始化失败 | 同一连接优先使用已验证的软件 H.264 decoder；否则连接前不宣告 AVC，使用原来的非 AVC 图形路径 |
| 服务器不支持或未选择 AVC | 保持非 AVC 图形路径，不为强求硬解循环重连 |
| 运行中设备移除、资源不足、硬解失败 | 在 FreeRDP 支持且状态完整时重建软件 decoder；否则关闭旧客户端后受控重连，禁用 AVC 回原来的非 AVC 路径 |
| 新路径导致协议/解码错误 | 将本次连接降级标记保持到会话结束，至多一次受控降级重连 |
| 用户主动断开、注销、身份验证失败或网络断开 | 沿用原会话处理逻辑，不误触发硬解重试 |

中途改 capabilities 不等于协议重新协商；若必须重连，应通过现有生命周期状态机处理，避免临时断连被识别为“任务完成”或触发退出策略。重连不得注销远端会话，不得重发任务指令，不得重置音频/输入/代理绑定。旧 native 会话、delegate、自引用及 GPU 对象释放完成后，才能创建替代客户端。

## 5. 分阶段实施与交付

### P0：可行性验证与性能基线

- 确认实际运行程序路径、加载的 `ycn_rdp.dll` / `freerdp3.dll` 和文件哈希，排除旧 `YukinoChan.RdpNative` 目录、当前 `Yomori.RdpNative` 输出及安装目录混用。
- 查看 FreeRDP 3.32.0 的 decoder 选择代码和对应 FFmpeg 编译特性，确定 Windows 硬解 API、初始化方式、软回退能力及线程模型。
- 用隔离测试程序或隔离构建验证一条真实 RDP AVC 流：硬解创建、正确输出、软件对照以及故意失败后的回退。不自动改变机器组策略或现有登录会话。
- 采集当前客户端 CPU 采样栈、Video Decode 引擎、实际更新率和内存；环回环境同时记录服务端及目标程序成本。
- 产出《后端选择与基线记录》：锁定 decoder、FFmpeg 特性、补丁范围、codec 能力集合及测量结果。

**完成条件**：证明一条 Windows 硬解路径能处理实际 RDP 帧，并清楚定位回读与软件合成成本。若验证失败，停止扩大 AVC 宣告，先解决 decoder 接入。

### P1：统一依赖、构建和可观察性

- 用仓库内固定的依赖配置/overlay port/triplet 管理硬解特性；如需修改上游，补丁纳入仓库并固定版本。
- 同步本地与 CI 的 feature 集合。把 vcpkg 提交、依赖 features、triplet、overlay/补丁及校验脚本纳入两级缓存 key；缓存恢复与保存使用完全相同的身份。
- 明确 FFmpeg 所需最小组件与许可说明。禁止把本机整个 FFmpeg 工具目录或显卡驱动 DLL 无差别带入发布包；系统组件按系统要求探测。
- 显式固定 `WITH_VIDEO_FFMPEG=ON`、`WITH_FFMPEG_HWACCEL=ON`，保持 MF/OpenH264 关闭，并检查构建结果中的 H.264 能力。通用源码的 HWACCEL 默认关闭，不能只添加 ffmpeg feature 就假定硬解开启。扩展 CMake、CI、`package_release.py` 的完整依赖检查，并在干净测试目录执行加载与解码能力自检，避免依赖开发机 PATH。
- 现有 `g_gfx_last_codec` 仅记录且未导出，`gfx_state=2` 只代表接入 GDI；增加按会话聚合的实际 codec、decoder 诊断字段与计数；日志不能在帧锁内进行同步文件 I/O。

**完成条件**：干净构建和发布包均声明一致能力，缺少真实运行依赖时构建/打包失败。

### P2：桥接、协商、配置和回退

- 增加解码策略、能力结果及实际 decoder 状态；先确保后端可用，再开放对应 AVC 能力。
- 为新连接选项提供带 `size/version` 的 V2 入口或独立版本化配置接口，保留旧连接入口及既有结构布局。新旧 DLL 不匹配时明确诊断并安全回退，禁止按错误结构大小读取内存。
- 同步 `ycn_rdp.h`、C# P/Invoke、配置规范化、设置界面和原生测试工具中的 ABI 定义。
- 接入错误分类、硬解失败限次降级和状态机处理；会话间的失败状态隔离。
- 显示“实际 codec / 实际 decoder / 当前渲染方式 / 回退原因”，保留现有全屏、切页、多路及软件渲染恢复行为。

**完成条件**：正常机器默认尝试硬解；不支持的环境可用；主动退出与协议失败不会形成重连循环。

### P3：性能与稳定性验收

执行第 7 节矩阵，比较当前实现、新版强制软件、新版自动硬解三组结果，单独分析协议从原非 AVC codec 切到 AVC 带来的影响。只有满足正确性、实际硬解和性能目标，才能把默认自动策略用于正式包。

### P4：有证据后再做 GPU 表面优化

如第一阶段未达 CPU 目标，依据 profile 排序处理回读、色彩转换、整帧复制与全局锁竞争。先评估带帧序号/尺寸世代号的快照 API 和会话级同步；如收益不足，再实施第 3.4 节的 GPU 图形合成。

不得通过直接跨线程使用 D3D immediate context、绕过帧锁、降低帧率、降低分辨率或降低画质，把测量上的变化冒充硬解收益。

## 6. 预计文件范围

| 文件或目录 | 预期变更 |
| --- | --- |
| `winui3/src/Yomori.RdpNative/ycn_rdp.c`、`ycn_rdp.h` | 后端能力、AVC 协商、版本化 API、状态与错误归类 |
| `winui3/src/Yomori.RdpNative/CMakeLists.txt`、`verify_runtime_dlls.cmake` | 固定依赖能力、运行库验证、版本信息 |
| 仓库内新增依赖 manifest / overlay / triplet / 补丁（位置在 P0 确定） | 复现 FreeRDP/FFmpeg 构建，避免只修改本机 vcpkg |
| `.github/workflows/build.yml` | 安装 features、缓存 key、能力自检、依赖校验 |
| `winui3/tools/package_release.py` | 发布包依赖和干净目录加载检查 |
| `winui3/src/Yomori.App/Services/RdpNativeInterop.cs`、`RdpEmbeddedClient.cs` | 新 API、会话状态和诊断 |
| `winui3/src/Yomori.App/Models/RdpModels.cs`、对应设置绑定与文案 | `decode_backend`、默认值和选择入口 |
| `winui3/src/Yomori.App/ViewModels/MainViewModel.cs` 及必要的会话状态代码 | 连接策略透传、受控降级重连 |
| `winui3/src/Yomori.App/Services/RdpD3DRenderer.cs`、`Views/RdpView.xaml.cs` | 第一阶段以保留线程纪律及诊断为主；GPU 表面接入留待 P4 |
| `winui3/tests/native_memory_checks.py` 及相应 ABI/生命周期检查 | V2 API 与新资源生命周期回归 |
| `winui3/README.md`、旧内嵌计划、第三方许可说明 | 构建方式、开关语义、诊断与依赖更新 |

## 7. 验证与验收标准

### 7.1 性能方法

- 固定机器、电源模式、驱动、分辨率、显示缩放、画质、网络及远端内容；以相同脚本重复窗口滚动、视频/游戏动态画面。
- 采用三组对照：当前版本、新版软件模式、新版自动硬解。另用同样 AVC 流进行软/硬解对比，区分 decoder 收益与 codec 改变的影响。
- 以 1080p 的 1/2/4 路为主，补充本机实际使用分辨率及 4K 压力场景。当前 native 会话上限为 8、网格每页显示 4 路；补充 8 路已连接且 4 路可见的后台负载与回退压力测试。
- 每组预热后至少采样 60 秒并重复 3 次。记录 CPU 时间增量、归一化 CPU 使用率、有效呈现 FPS、帧延迟分位数、丢帧、Private Bytes、GPU 内存和各引擎活动。
- CPU 推荐统一为 `进程CPU时间增量 / 采样墙钟时长 / 逻辑处理器数`，明确百分比口径；环回场景分别列客户端和全机成本。
- 建议性能目标：在相同输出质量、分辨率及可比 FPS 的动态画面中，客户端 CPU 相对基线下降至少 25%，呈现 FPS 不下降超过 5%，P95 帧延迟不恶化超过 10%。这些是验收目标，尚无实测结果；低 CPU 空闲场景按绝对值和不退化判定。
- 未达目标时交付 profile 结论，决定是否执行 P4；不能通过关闭统计、改采样口径或隐藏服务器成本宣布达标。

### 7.2 功能与故障矩阵

| 类别 | 必测情况 | 验收重点 |
| --- | --- | --- |
| 常规硬件 | 当前实际 GPU；具备条件时 Intel / AMD / NVIDIA 各一种 | 日志确认硬件 decoder 且有成功硬解帧；GPU Video Decode 活动与负载对应 |
| 能力缺失 | 无视频解码能力、WARP/虚拟适配器、硬解初始化失败 | 可靠软件回退，不能把 WARP 计作视频硬解 |
| 协议 | AVC420、可用时 AVC444、服务器只给 Planar/旧图形路径 | 颜色和桌面文字正确；未协商 AVC 时状态真实 |
| 设备变化 | 设备移除、驱动重置、多显卡 adapter 不匹配 | 不阻塞 UI；资源释放；有限次降级 |
| 界面 | 缩放、调整分辨率、全屏转移、切页回来、后台挂起恢复 | 无黑屏、条纹、撕裂或重复呈现；保留首帧恢复 |
| 多路与生命周期 | 1/2/4 路可见、8 路连接/4 路可见、快速连接/取消/断开、反复重连 | 会话计数归零；回调终态仍恰好一次；显存、句柄和 native 对象无持续增长 |
| 其他通道 | 键盘鼠标、声音、静音、任务进行中降级 | 输入/音频正常；不重复执行任务，不误触发收尾 |
| 逃生模式 | `decode_backend=software`、`render_backend=software`、`--embed-nod3d` | 优先级确定，完全避 GPU 模式不再创建视频硬解设备 |
| 发布包 | 干净目录、最小 PATH、缺少 codec 依赖、新旧 DLL 混用 | 可明确诊断；完整包可运行；缺依赖或 ABI 不匹配时安全失败/回退 |

静态 ABI/配置测试、native 生命周期检查和 .NET/原生编译用于拦截结构和资源管理错误；实际 GPU 解码、驱动行为及性能必须在真机完成。CI 无 GPU 时只能验证能力声明和软回退，不能据此给“硬解已验收”的结论。

## 8. 风险与发布回滚

- **首要不确定性**：源码已确认 FFmpeg/DXVA2 接入及初始化软回退，目标 GPU 的实际输出、图形流兼容性和运行中恢复仍需 P0 实证；编译特性名字不等于运行结果。
- **CPU 收益上限**：旧 BGRA 显示链仍保留回读、合成与复制；是否进入 P4 由采样决定。
- **整窗卡死风险**：保留 UI 线程管理交换链的纪律，decoder 工作与 UI 显示通过明确的队列/生命周期交接，不在帧锁里等待 UI 或写大日志。
- **兼容性风险**：AVC420 桌面细字、AVC444 重建、驱动和虚拟显示器差异必须纳入验收；GFX 接通不等于任何 codec 都可靠。
- **可重复性风险**：旧 native 目录、CI 缓存和开发机全量 DLL 拷贝可能掩盖依赖缺失；发布包必须从固定依赖配置构建和自检。
- **回滚方式**：用户可将 `decode_backend` 设为 `software`，或使用现有完全避 GPU 的入口；出现协议兼容问题时禁用 AVC 回到原非 AVC 图形路径（可能是 Planar/ClearCodec 等，以实际 codec 为准）。版本回滚需整体还原桥接 DLL、FreeRDP 与 codec 运行库，避免混装。

预估工作量：P0 1–2 个工作日，P1–P2 3–5 个工作日，P3 1–2 个工作日；若需要上游 decoder 补丁或 GPU 表面改造，P4 另估 5–10 个工作日。硬件与驱动覆盖、上游接入难度会影响排期；该估算不代表硬解可行性已验证。

正式实施顺序：先完成 P0 的可行性与基线证据，再按 P1→P2→P3 交付；P4 根据结果决策。完成后应提交代码差异、依赖清单、实际后端日志、三组性能对照和回退验证记录。
