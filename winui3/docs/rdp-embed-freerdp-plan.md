# Yomori · 远程桌面内嵌（FreeRDP 方案）计划书

- **版本**：草案 v2（2026-09-25）
- **状态**：⚠️ 仅为规划文档，未动工。所有代码改动以本计划书评审通过为前提。
- **v2 变更**（相对 v1，按新需求补齐）：
  1. **音频输出重定向**：远端会话的桌面声音在主控端播放（rdpsnd），新增 §5.8；
  2. **全屏键盘接管**：原 M6 可选的「全屏 + 低级键盘钩子」升级为 M3 核心项（§5.3）；
  3. **鼠标控制**：确认已在核心范围（§5.3 / M3），验收标准补全。
- **适用范围**：`repo/winui3/` 下的 WinUI 3 工程（`Yomori.App`）

---

## 1. 背景与动机

### 1.1 现状：mstsc 独立窗口模式（可用但体验割裂）

当前远程桌面链路是「**外部客户端 + 代理桥接**」：

```text
主控端（账户 A / Yomori）                目标会话（账户 B / 另一台机器）
──────────────────────────────        ──────────────────────────────
写 command.json（任务快照）
RdpSessionService.Connect() 启动
mstsc（独立窗口，cmdkey 供凭据） ──────►  账户 B 登录
                                        公共启动目录拉起 --rdp-agent
轮询 status.json（1Hz）◄──────────────  代理执行任务、回写进度/事件
```

这套机制的**会话侧**（代理、指令桥、心跳、收尾）已经过真机验证、状态稳定；  
问题只出在**画面侧**：mstsc 是个游离在 Yomori 之外的黑盒窗口，程序对它只有  
「启动 / 传分辨率」两个控制点。

### 1.2 历史：ActiveX 内嵌（阶段五）已尝试并放弃

仓库里还留着当时的遗物，写本计划书前已逐一核实：

| 遗物                         | 位置                                | 现状                                                                                                        |
| -------------------------- | --------------------------------- | --------------------------------------------------------------------------------------------------------- |
| `Yomori.RdpHost` 工程    | `src/Yomori.RdpHost/`         | **孤儿工程**：不在 `Yomori.sln` 里，`Yomori.App` 无任何引用                                                     |
| `embed_remote_desktop` 配置键 | `Models/RdpModels.cs` `RdpConfig` | 已标注废弃，仅做旧配置兼容                                                                                             |
| ActiveX 承载 hack            | `RemoteDesktopHost.cs`            | 隐藏 WinForms Form 养出控件 → 摘出控件树 → `SetParent` 挂进 WinUI 的 `DesktopChildSiteBridge` → 手动 `SetWindowPos` 抢 Z 序 |

当时被迫写下的补丁本身就说明了这条路的先天缺陷（代码注释里全是这些坑）：

- ActiveX 只能 STA 实例化，view model 里一 `await` 跑到 MTA 线程就灰屏；
- 与 WinUI 合成器**抢 Z 序**，每次窗口尺寸变化都要重新「抬到自己前面」；
- `ClearTextPassword` 受系统策略限制，凭据链路脆弱（灰屏的第一根因）；
- 依赖 `net40` 的 AxMSTSCLib 互操作程序集（NU1701 警告压制）。

**结论：不要复活 ActiveX 路线。** FreeRDP 方案与它的本质区别在于——  
**不借宿主窗口、不做 SetParent**。像素流由自己解码、自己用 `SwapChainPanel`  
（正规的 XAML 合成表面）渲染，上述四类 hack 从根上不存在。

### 1.3 换 FreeRDP 的收益

| 维度      | mstsc（现状）      | ActiveX（已放弃）                        | FreeRDP（本方案）                        |
| ------- | -------------- | ----------------------------------- | ----------------------------------- |
| 画面位置    | 独立窗口           | 内嵌（靠 SetParent hack）                | 内嵌（SwapChainPanel，原生支持）             |
| 凭据      | cmdkey 间接供     | 明文密码属性，受策略限制                        | 连接参数直接喂，与凭据管理器解耦                    |
| 分辨率控制   | 写 .rdp 钉死三项才生效 | 控件属性 + UpdateSessionDisplaySettings | 连接参数 + DisplayControl 通道**动态调**     |
| 连接状态感知  | 几乎为 0（只知进程在）   | 有事件但隔着 COM                          | 回调直通（连接/断开/首帧/错误码）                  |
| 断开/注销语义 | 用户在 mstsc 窗口里选 | 同 mstsc                             | **程序自己决定**（对应现有 SessionFinishModes） |
| 二进制依赖   | 无（系统自带）        | 无                                   | freerdp3.dll + winpr3.dll（需随包分发）    |
| 维护面     | 系统管            | 系统管（但 hack 自管）                      | 协议栈自管（升级跟着上游走）                      |

---

## 2. 目标与非目标

### 2.1 目标

1. 远程桌面画面以 `SwapChainPanel` 形式嵌入 RdpPage，与现有 UI 同窗共存；
2. **鼠标完整控制**：移动、左/右/中键、滚轮、按住拖拽，全部直通远程会话（§5.3）；
3. **键盘输入直通**：普通键、扩展键、组合键从 Yomori 窗口落到远端（§5.3）；
4. **全屏模式 + 键盘接管**：全屏且焦点在画面上时，由低级键盘钩子把**全部击键（含 Win 键、Alt+Tab 等系统组合）**&#x8F6C;投远端，本地桌面不再响应（§5.3）；
5. **远端声音重定向**：目标会话的桌面声音（游戏、提示音、媒体）在主控端扬声器播放（§5.8）；
6. 凭据直接来自现有 `RdpCredentialStore`（CredRead），不再依赖 mstsc/cmdkey 行为；
7. 会话收尾三态（保持/断开/注销）语义与现状完全一致；
8. **代理、指令桥、心跳、通知机制零改动**（内嵌只替换「画面 + 输入 + 音频 + 连接发起」）;
9. mstsc 模式保留为回退路径，用户可在设置里切换。

### 2.2 非目标（本计划明确不做）

- 剪贴板 / **麦克风（音频输入）重定向** / 打印机 / 驱动器重定向（剪贴板列为 M6 可选，其余出范围；音频只做**输出**，见 §5.8）；
- RemoteApp / RAIL（单窗口应用级发布）；
- 多显示器横跨（当前 mstsc 模式就是单屏 `use multimon:i:0`，保持一致）；
- 网关（RD Gateway）、 Restricted Admin 等企业特性；
- 给 Agent 侧加任何东西——Agent 根本不需要知道自己被谁连。

---

## 3. 总体架构

### 3.1 分层图（按本仓库实际工程结构调整）

```text
┌──────────────────────────────────────────────────────────────┐
│ Yomori.App（现有 C# WinUI 3 工程，全改动都在这一层）       │
│                                                              │
│  Views/RdpPage.xaml          ← 新增内嵌视图区 + 工具栏       │
│  Views/Controls/RdpView      ← 自定义控件：SwapChainPanel +   │
│     (新)                        指针/键盘事件 → 输入映射       │
│  Services/Rdp/               ← 新目录：                      │
│    RdpEmbeddedClient.cs        会话生命周期状态机（可单测）    │
│    RdpNativeInterop.cs        C ABI P/Invoke 声明             │
│    RdpFrameRenderer.cs        D3D11 纹理 + SwapChain 呈现     │
│    RdpInputMapper.cs          VK→扫描码、DIP→远程像素（纯函数）│
│  MainViewModel               ← ConnectAndRunAsync 换连接入口  │
└────────────────────────────┬─────────────────────────────────┘
                             │ C ABI（DllImport，函数指针回调）
┌────────────────────────────▼─────────────────────────────────┐
│ Yomori.RdpNative（新增 C++ 原生 DLL，新工程）             │
│   freerdp 生命周期：new → settings → connect → 事件循环线程   │
│   帧回调：BeginPaint/EndPaint 之间收脏矩形，交出 BGRA 缓冲    │
│   输入注入：mouse_event / keyboard_event(ex) 的薄封装        │
│   音频：rdpsnd 通道解码后直接放给系统默认设备（不进 C#，§5.8） │
│   输出极简 API（约 10 个导出函数，见 §4.2）                   │
└────────────────────────────┬─────────────────────────────────┘
                             │ 动态链接
┌────────────────────────────▼─────────────────────────────────┐
│ freerdp3.dll + winpr3.dll（上游 FreeRDP 3.x，二进制随包分发）  │
│   协议栈 / NLA(CredSSP) / TLS(OpenSSL) / 编解码（见 §5.1）    │
└──────────────────────────────────────────────────────────────┘
```

### 3.2 桥接形态决策：C ABI 原生 DLL + C# P/Invoke（而非 C++/WinRT 组件）

参考文档推荐 C++/WinRT Runtime Component，本项目**有意偏离**，理由：

| 考量         | C++/WinRT 组件                                          | C ABI DLL（推荐）     |
| ---------- | ----------------------------------------------------- | ----------------- |
| XAML 自定义控件 | C++ 侧写 WinUI 3 XAML 控件支持很差，最后还是得 C# 写                 | 控件留在 C# 侧，天然      |
| 回调         | WinRT 事件，类型系统受限（字节数组要 IBuffer）                        | 函数指针，直白           |
| 依赖注入现有模式   | 现工程全部 C# + CsWin32 风格 P/Invoke（NativeMethods.cs 已是先例） | 完全同构              |
| ABI 稳定性    | WinRT 投影随 SDK 变                                       | C ABI 冻结后不随 SDK 变 |
| 帧缓冲传递      | 受 WinRT 封送约束                                          | 指针 + 长度直传，零封送开销   |

FreeRDP 的结构体（`rdpSettings` 等）**一律不跨 ABI**：原生侧包干，  
C ABI 只交换「连接参数结构体 + 帧元数据 + 输入事件」这类扁平数据。  
这正是参考文档「不要在 C# 里 P/Invoke 几百个 C 结构体」的本意——  
只是把那层封装做成普通 C++ DLL，而不是 WinRT 组件。

### 3.3 线程模型（沿用参考文档的硬约束，落到本工程）

```text
UI 线程（STA）                    RDP 工作线程（原生 DLL 内部）
─────────────────                ─────────────────────────────
SwapChainPanel 创建              WaitForMultipleObjects(
D3D11 Device 创建                  freerdp_get_event_handles()) 循环
      │  UpdateSubresource ◄────  解码 → 帧回调（脏矩形 + BGRA）
      │  Present（节流）          收到输入注入请求（线程安全队列）
输入事件 ── SendInput ──────────►  转协议包发出
```

- `freerdp_check_event_handles` / `freerdp_get_event_handles` 事件循环**绝不上 UI 线程**，封装在原生 DLL 的内部线程；
- 帧回调里**不做渲染**，只拷贝脏矩形进一个双缓冲槽，打条件变量唤醒 C# 渲染线程（`Present` 与 XAML 线程解耦，`ISwapChainPanelNative::SetSwapChain` 绑定后 SwapChain 可在任意线程 Present）；
- 原生→C# 回调用 `[UnmanagedFunctionPointer]` 委托，C# 侧回调内只做入队，不做任何 UI 操作；
- 键盘扫描码表、坐标映射等纯逻辑做成 `RdpInputMapper` 静态纯函数，进 `_smoke` 做确定性单测（延续 `RdpHeartbeat.Evaluate` 的可测性惯例）。

---

## 4. C ABI 设计（原生 DLL 的全部对外表面）

### 4.1 连接参数结构（C# 与 C++ 双端共用一份平铺布局）

```c
typedef struct {
    const char* host;          /* "127.0.0.1" 或 "192.168.1.20:3389" */
    uint16_t    port;
    const char* username;
    const char* domain;        /* 可空 */
    const char* password;      /* 来自 RdpCredentialStore.CredRead */
    uint32_t    desktop_width;  /* 0 = 1280x720 兜底 */
    uint32_t    desktop_height;
    uint32_t    color_depth;    /* 固定 32 */
    int         use_nla;        /* -1 自动 / 0 禁 / 1 强制 */
    int         allow_selfsigned; /* 环回场景首次连接的指纹放行策略 */
    int         enable_audio;   /* 1 = 远端声音在本机播放（rdpsnd），0 = 静默；见 §5.8 */
} ycn_rdp_params;
```

### 4.2 导出函数（约 10 个，冻结后不加不改）

```c
int   ycn_rdp_connect(const ycn_rdp_params*, callbacks*, void* user);
void  ycn_rdp_disconnect(int session);        /* 语义 = mstsc「断开连接」*/
int   ycn_rdp_send_mouse(int session, uint32_t flags, uint16_t x, uint16_t y);
int   ycn_rdp_send_key(int session, int down, int extended, uint16_t scancode);
int   ycn_rdp_grab_frame(int session, ...);   /* 取一帧（宽度/高度/缓冲指针）*/
void  ycn_rdp_last_error(int session, char* buf, size_t buflen);
```

`callbacks` 里含：`on_connected` / `on_login_complete` / `on_disconnected(reason)`  
/ `on_desktop_resize` / `on_error(code, msg)` / `on_frame_ready`。  
**完成信号、断开原因都走回调事件**——沿用本项目已付过学费的原则：  
*状态可被覆盖，事件不会*（参见常驻代理 idle 心跳覆盖 Phase 的教训）。  
C# 侧 `RdpEmbeddedClient` 是一个显式状态机（Connecting → Connected →  
LoginComplete → Disconnected / Failed），状态迁移函数做成纯函数进 `_smoke`。

---

## 5. 关键技术点与已知坑

### 5.1 FreeRDP 二进制：先拿现成，再谈自建

- **版本钉死**：FreeRDP **3.x**（M0 开工时锁定具体小版本，写死在构建脚本与 NOTICE 里）；
- **获取顺序**：① 官方 GitHub Release 的 Windows 附件（若有）→ ② 官方 CI 产物 → ③ 自行 CMake + VS2022 构建（脚本放 `_tools/`，配置项：OpenSSL、zlib、**rdpsnd 音频（默认开，必须保留）**、**关 H.264**）；
- **历史首期编解码策略**（2026-10-31 硬解接入前，现行方案见 `rdp-hardware-decode-plan-2026-10-31.md`）：**不开 GFX/AVC444/H.264**。本产品目标场景是环回与局域网（1080p 帧率要求低），NSCodec / ClearCodec / planar 足够；这条同时把 OpenH264/FFmpeg 的构建与许可复杂度全部推迟到确有需要时；
- **许可证**：FreeRDP/winpr = Apache-2.0，OpenSSL = Apache-2.0 —— 与闭源分发兼容，发布时随包带 LICENSE/NOTICE 文件即可（放 `rdp-embed-freerdp-plan.md` 评审时确认清单）。

### 5.2 渲染：两步走

| 阶段    | 方案                                                                                     | 用途                       |
| ----- | -------------------------------------------------------------------------------------- | ------------------------ |
| M2 原型 | `SoftwareBitmap` + `Image` 控件                                                          | 最小代价验证「解码出来的 BGRA 真的是对的」 |
| M4 生产 | `SwapChainPanel` + D3D11（Vortice.Windows NuGet）+ `ISwapChainPanelNative::SetSwapChain` | 常驻形态                     |

- `ISwapChainPanelNative` 是 WinUI 3 的 COM 接口（不是 WinRT 投影），C# 侧用 ComImport 声明（GUID 从 Windows App SDK 头文件核对），`As<>()` 转换后绑定 SwapChain——CsWin32/手写 interop 均可，属成熟配方；
- 帧率上限 30fps（`Present(1, 0)` 垂直同步 + 帧合并），远程桌面场景无需 60fps；
- 缩放模式与 ActiveX 时代的 `SmartSizing` 等价对齐：**适配窗口**（等比 letterbox）/ **1:1**，用着色器做采样，脏矩形只更新纹理对应区域（`UpdateSubresource` 带行距）。

### 5.3 输入

- **鼠标**：`PointerMoved/Pressed/Released/WheelChanged` → 控件内 DIP 坐标 × `XamlRasterizationScale` → 按缩放模式反算远程物理像素 → `PTR_FLAGS_*` 组合；
- **键盘**：WinUI 只给 `VirtualKey`，用 `MapVirtualKey(VK → VSC)`，**扩展键**（方向键/小键盘区分/Right Ctrl 等）必须按 `MapVirtualKeyEx` + `KEYEVENTF_EXTENDEDKEY` 口径打扩展位，`freerdp_input_send_keyboard_event_ex(input, down, extended, scancode)`；
- 焦点在 RdpView 时禁用本地输入法（远程会话自带的 IME 会接管），失焦即恢复；

### 5.3.1 全屏模式与键盘接管（v2 新增，M3 核心项）

- **全屏切换**：RdpView 工具栏「全屏」按钮 + 双击画面 + F11（AppWindow 的  
  FullScreen presenter；SwapChainPanel 与普通控件两种渲染形态都必须支持全屏）；
- **键盘接管**：全屏**且**焦点在画面上时，挂 `SetWindowsHookEx(WH_KEYBOARD_LL)`  
  低级键盘钩子，把所有击键——包括 Win 键、Alt+Tab、Ctrl+Esc、Win+D 这类  
  系统组合——转投远端、本地桌面不再响应；这是全屏模式的**核心价值**：  
  玩起来就是一个真的远程机器，不会被本机桌面抢走焦点；
- **Ctrl+Alt+Del 例外（必须在 UI 文案写明）**：它是 Windows 安全注意序列（SAS），  
  任何用户态钩子都拦不到（mstsc 也一样）。行业通行替代是 **Ctrl+Alt+End**。  
  M3 落地时核对上游 `wf_keyboard.c` 的 SAS 发送通道  
  （`freerdp_input_send_sas_event` 一类）：有就把 Ctrl+Alt+End 映射到远端 SAS；  
  没有就写明「任务管理器请用远端 Ctrl+Shift+Esc」；
- **钩子生命周期纪律**：退出全屏 / 失焦 / 断开连接 → **立即摘钩子**。  
  钩子只允许在全屏+聚焦期间存在（这是安全敏感点：全局键盘监听不能泄漏到  
  日常使用），挂/摘动作各写一条日志。任何异常退出路径都要走统一卸钩出口。
- **已知局限（首期不做，已在 UI 文案范围）**：非全屏模式下 Win 键、Alt+Tab 等  
  系统组合仍归本地桌面（普通应用拿不到），要完整接管请进全屏。

### 5.4 与现有会话机制的对接（零改动清单 + 替换清单）

**完全不动**（这是本计划书最重要的一条红线）：

- `RdpBridge`（command.json / status.json / 原子写 / 事件序号）
- `RdpAgentRunner`（目标侧代理，随登录自启、常驻、idle 心跳）
- `RdpHeartbeat.Evaluate` 三段式判定
- `WaitForSession` / `WaitForAgentOnline` / `TryGetReusableSession`
- `RdpCredentialStore`（CredWrite/CredRead 照旧；CredRead 多了一个消费者而已）
- `PendingNotificationStore`、通知双发逻辑
- WTS 断开/注销 API

**替换**（改动集中在主控端连接入口）：

| 现有                                              | 改后                                                             |
| ----------------------------------------------- | -------------------------------------------------------------- |
| `RdpSessionService.Connect()`（起 mstsc + 写 .rdp） | `RdpEmbeddedClient.ConnectAsync()`；`client_mode=embedded` 时走新路 |
| `ConnectSurface`（开独立 mstsc 窗口）                  | 打开内嵌视图（RdpPage 视图区或弹出 Dialog 承载 RdpView）                       |
| `BuildRdpFile` / mstsc 分辨率三件套 hack              | 连接参数直传；M6 可换 DisplayControl 通道动态分辨率，.rdp 文件退役                  |
| 断开语义靠用户在 mstsc 窗口里选                             | 程序调用 `ycn_rdp_disconnect`（天然等于「断开连接」，会话与任务保留）                  |


`ConnectAndRunAsync` 主体流程（预检 → 写桥 → 复用判定 → 连接 → 等 Agent → 轮询）**结构不变**，只把中间 `Connect(host, out msg, w, h)` 一处换成策略分发：
`embedded` 可用走内嵌客户端，否则回落 `mstsc`。

### 5.5 环回多账户场景的画面可见性语义（必须提前讲清的先天限制）

Windows 客户端版**同时只允许一个活动交互会话**，这一条不因客户端是谁而改变
（现有代码注释与 `PendingNotificationStore` 都已实证）：

| 场景 | 内嵌画面表现 |
|---|---|
| **目标是远程主机** | 内嵌画面全程实时可见、可交互——**内嵌的主战场**，价值最大 |
| **目标是本机环回（切账户）** | 连接成功瞬间主控会话被锁定（画面在锁屏后面继续跑）；用户解锁回来看 Yomori 时，目标会话自动转为 Disconnected，内嵌画面显示断开帧（可提供「重连查看」按钮：重连会话会接回同一会话，但会再次锁定主控） |

也就是说：**环回场景下内嵌客户端的价值是「自动登录引导器」+「免开独立窗口」**，
而不是「持续观看」。这不是缺陷，是单会话限制下的物理现实；mstsc 模式下用户
同样看不到（窗口在锁屏后面）。计划书按此预期设定，避免实现后出现「怎么是灰的/
怎么断了」式误解（阶段五已经付过一次学费）。

### 5.6 配置与兼容

- 新增 `RdpConfig` 键：`client_mode`（`"mstsc"` / `"embedded"`，默认 `"mstsc"`）；
- 新增 `RdpConfig` 键：`audio_enabled`（默认 `true`，远端声音在本机播放，见 §5.8）；
- `embed_remote_desktop` 旧键迁移：真值 → `client_mode="embedded"`，随后废弃；
- 新增可选键（M4 之后）：`view_scale_mode`（`fit` / `pixel`，默认 `fit`）；
- 全部 snake_case，`Sanitize()` 里做归一化（延续现有惯例）；
- **Agent 部署影响**：代理副本是整个 exe 复制到 `%ProgramData%\Yomori\agent\`。
  原生 DLL 采用**惰性加载**（P/Invoke 首次调用才 LoadLibrary），Agent 进程永不触碰
  RDP 客户端代码路径 → 即使代理目录漏带 DLL 也不影响 Agent 启动；但部署逻辑
  （`DeployAgent` 复制处）仍按白名单带上 freerdp3.dll / winpr3.dll，双保险。

### 5.7 本仓库已知环境约束（历次踩坑记录，直接适用）

- **沙箱不可跑 GUI / 不可真连 RDP / 禁 COM 实例化** → 每个里程碑的验证都拆成
  「源码级断言 + `_smoke` 纯函数单测（可跑）」与「真机检查清单（人工）」两栏；
- 运行中的实例锁 exe → 编译验证用 `-p:BaseOutputPath=<临时目录>`，再把
  `Yomori.dll` 覆盖回 bin（老规矩）；
- `AppendLog` 单行为单位，多行拆多次调用；
- 冒烟工程 `_smoke` 直接链接源码文件、不引 WinUI——`RdpInputMapper`、
  `RdpEmbeddedClient` 状态机、配置迁移都要按这个约束设计（不碰 XAML 类型）。

### 5.8 音频输出重定向（v2 新增需求）

- **与现状对齐**：mstsc 模式下远端声音本来就默认在本地播放；
  内嵌路线必须**不倒退**——这是本需求的直接动机（画面进了 Yomori，声音不能丢）。
- **范围**：只做**输出**——远端会话的桌面声音在 Yomori 所在机器的默认播放设备发声
  （等价于 mstsc 的「在此计算机上播放」）。麦克风输入重定向出范围（§2.2）。
- **机制**：FreeRDP 的 `rdpsnd` 虚拟通道 + Windows 侧 winmm/WASAPI 后端，
  格式协商（PCM / Opus 等）与解码播放**全部在原生 DLL 内部完成**——
  不过 C ABI、不进 C#，C# 侧只负责开关（`enable_audio`）与状态显示。
  这是有意为之：音频是最不该跨语言桥的东西，链路越短越稳。
- **构建**：官方 Windows 构建默认带 rdpsnd；若走自建，确保 `WITH_RDPSND=ON`
  （默认即开）。此项写进 `_tools/` 构建脚本的检查清单与 M0 验收。
- **C ABI**：连接参数加 `enable_audio`（默认 1）。连接期间不暴露暂停/音量接口
  ——系统音量键与远端应用音量已经够用，M6 有需要再加。
- **验收口径**：① 远端播放音乐/视频 → 主控端扬声器出声、画面音画同步；
  ② `enable_audio=0` → 全程静默；③ 断开连接 → 无残留音频线程/设备占用；
  ④ 远端**没有**音频设备时不算失败，状态栏显示「远端无音频输出」。
- **环回场景注意**：本地多用户环回连接会顶掉主控桌面（§5.5），此时目标会话
  的声音重定向到主控端播出——这是**特性**：用户守在主控端就能听见目标账户里
  游戏或脚本的提示音。UI 文案顺带说明，避免「怎么我的账户在放别人的声音」的误解。

---

## 6. 实施里程碑

每个里程碑给出**验收标准**与**验证方式**（沙箱可跑 / 真机人工）。串行推进，
M0 是全方案的 Go/No-Go 闸门。

### M0 · 预研 spike（Go/No-Go）

| 项 | 内容 |
|---|---|
| 工作 | 取得 freerdp3/winpr3 二进制；写一个**控制台**程序（不进解决方案）：连 `127.0.0.1` 环回，NLA 凭据来自 CredRead，收到首帧存 PNG，注入一次合成鼠标移动，开启 rdpsnd 后远端播放一段音频，干净断开 |
| 验收 | 连 Win10/11 Pro 真机成功：NLA 通过、PNG 内容正确（非全灰/全黑）、远端音频在本机可闻、断开后 WTS 显示会话 Disconnected 且进程存活、无残留音频线程 |
| 验证 | 全部真机；沙箱内只做「DLL 导出表存在性」静态断言 |
| **失败出口** | 若 NLA/CredSSP 对 Win11 连不通且 3.x 无解 → 冻结方案，保持 mstsc 模式，本计划书归档 |

### M1 · 桥接层 API 冻结

- 新建 `src/Yomori.RdpNative/`（C++ 工程），实现 §4 的 C ABI；
- 回调以「事件不会丢」为验收：以 100Hz 假帧源 + 随机 disconnect 压测回调序列；
- `_smoke` 新增：C ABI 签名与 C# `DllImport` 声明的一致性断言（字符串级比对）。

### M2 · 原型渲染与音频（SoftwareBitmap 路线）

- `RdpView` 控件（`Image` + `SoftwareBitmap`）+ `RdpEmbeddedClient` 状态机；
- `enable_audio` 参数接线（rdpsnd 由原生侧直接播放，C# 只传开关）；
- 验收：真机上 RdpPage 内可见远端画面（10fps 即可）；**远端播放音频、
  主控端扬声器出声**；`enable_audio=0` 静默；状态机单测全绿（`_smoke`）。

### M3 · 输入与全屏接管

- `RdpInputMapper`（VK→扫描码含扩展键表、DIP→远程像素双模式缩放反算）纯函数 + 完整单测表；
- 指针/键盘事件接线：鼠标移动、左/右/中键、滚轮、按住拖拽，键盘普通键 + 扩展键（方向键 / Right Ctrl / 小键盘区分）；
- **全屏模式**（AppWindow FullScreen presenter，双击/F11/按钮三入口）+ **`WH_KEYBOARD_LL` 键盘接管**：全屏且聚焦时系统组合键（Win 键、Alt+Tab、Win+D 等）全部转投远端，退出全屏/失焦/断开立即摘钩（挂/摘写日志），生命周期纪律见 §5.3.1；
- Ctrl+Alt+End → 远端 SAS 的映射按 §5.3.1 核对结果落地（或按退级方案写 UI 文案）；
- 验收：真机上鼠标点击、拖拽、滚轮、方向键/Right Ctrl/小键盘 全部正确落到远端；**全屏后按 Win 键 / Alt+Tab 远端响应、本地桌面无动作，退出全屏钩子即摘除（日志可见）**；音频与画面在输入过程中不卡顿。

### M4 · 生产渲染（SwapChainPanel + D3D11）

- 换 `SwapChainPanel`，脏矩形纹理更新，30fps 封顶，缩放模式两种；**全屏形态在 SwapChainPanel 上复验一遍**（含键盘接管回归）；
- 验收：1080p 局域网真机，CPU 占用与 mstsc 同数量级（参考阈值 < 15%），无撕裂、无明显输入延迟（< 100ms 主观判定）。

### M5 · 主流程集成

- `client_mode` 配置 + `ConnectAndRunAsync` 策略分发 + `ConnectSurface` 内嵌化 +
  `DeployAgent` 复制清单更新 + 断开/注销按钮语义映射（断开 = `ycn_rdp_disconnect`；注销 = 现有 WTS Logoff 不变）+ `audio_enabled` 设置项进 RdpPage UI；
- 环回场景实测一遍完整任务流（下发 → 连接 → 代理接管 → 跑完 → 收尾 → 通知），
  **音频在环回下也要实测一遍**（§5.8 环回注意项）；
- 验收：**新路径全绿 + mstsc 回退路径回归一遍全绿**。

### M6 · 打磨（按需裁剪）

- 证书指纹首连放行 UI、自动重连（对应 §5.5 环回解锁场景）、
  剪贴板（cliprdr）、动态分辨率（DisplayControl）、音频暂停/音量接口。
- 每项独立开关，做完一项验收一项。

---

## 7. 风险与对策

| # | 风险 | 等级 | 对策 |
|---|---|---|---|
| R1 | FreeRDP Windows 侧质量参差（上游主战场是 Linux） | 高 | M0 闸门最先行；钉版本；沙箱/真机验证拆分；mstsc 永久回退 |
| R2 | NLA/CredSSP 对 Win11 24H2+ 的兼容性 | 高 | M0 实测覆盖最新系统；失败即触发失败出口 |
| R3 | 原生 DLL 构建链（CMake/OpenSSL）在后续维护中碎掉 | 中 | 版本钉死 + `_tools/` 一键脚本 + 二进制进库或 Release 制品缓存 |
| R4 | 键盘边缘键位错乱（扩展键、死键、布局差异） | 中 | M3 大单测表（VK/扫描码全枚举）+ 真机手测清单 |
| R5 | 原生崩溃拖垮整个进程（含 Agent 场景？否——Agent 不加载） | 中 | 全部回调 try 封装；崩溃转 MiniDump；连接失败自动回落 mstsc 提示 |
| R6 | 环回场景用户预期落差（画面断开≠故障） | 中 | §5.5 语义写进 RdpPage 文案 + 状态栏明确显示「已断开（会话保留中）」 |
| R7 | 代理部署遗漏原生 DLL | 低 | 惰性加载（§5.6）+ 部署白名单 + 预检项「内嵌组件就绪」 |
| R8 | rdpsnd 格式协商失败 / 远端无音频设备导致无声 | 中 | M0/M2 验收覆盖「有声 / 静默开关 / 远端无设备」三种情况；无声不算失败，UI 显示「远端无音频输出」（§5.8） |
| R9 | `WH_KEYBOARD_LL` 全局钩子被安全软件标记或意外泄漏 | 中 | 钩子仅在全屏+聚焦期间存在，退出/失焦/断开立即摘除并写日志（§5.3.1）；崩溃路径也要走统一卸钩出口；必要时在 UI 明示「全屏时接管键盘」 |

---

## 8. 回退策略

- `client_mode` 默认 `mstsc`：内嵌全部代码都在新文件/新目录里，主流程仅一处策略分发点；
- 任意里程碑失败：revert 分发点一个 if，即回到现状，零残留；
- freerdp DLL 缺失/加载失败：预检报「内嵌组件缺失」，自动以 mstsc 连接并在日志说明；
- `Yomori.RdpHost` 孤儿工程的处置：**不复活、M5 验收通过后删除**（git 历史可寻）。
  注意两点：① `RdpErrorText.cs`（RDP 错误码→中文）当前仍被冒烟测试编译引用，
  删除前先把这部分断言迁到新桥接层或一并退役；② 该工程的挂载手法
  （隐藏 Form 养句柄 / `SetParent` / Z 序抬升）**全部作废**，FreeRDP 路线不使用任何
  原生窗口挂载——这正是本方案与阶段五的本质区别，不要把旧代码搬回来。

---

## 9. 工作量估算（AI 会话口径，含真机往返）

| 里程碑 | 估算 |
|---|---|
| M0 spike（含音频验证） | 1–2 个会话（含真机验证往返） |
| M1 + M2（桥接 + 原型渲染/音频） | 2–3 个会话 |
| M3（输入 + 全屏键盘接管） | 2–3 个会话 |
| M4 | 2 个会话 |
| M5 | 2–3 个会话 |
| M6 | 按选做项另计 |
| **合计（M0–M5）** | **约 9–13 个会话** |

---

## 10. 待确认决策点（动工前需要拍板）

1. **桥接形态**：本计划书推荐 C ABI DLL + C# P/Invoke，偏离参考文档的 C++/WinRT 建议（理由见 §3.2）——是否接受？
2. **首期编解码**：关 GFX/H.264 走 NSCodec/ClearCodec（推荐）——还是一步到位开 H.264？
3. **环回场景预期**：§5.5 的单会话限制下，环回场景内嵌仅作「登录引导器」——符合预期吗？
4. **M6 选做项排序**：自动重连 / 剪贴板 / 动态分辨率，优先哪几个？
5. **二进制分发方式**：freerdp DLL 进 git 仓库（LFS）vs 构建时下载制品——倾向？
6. **`Yomori.RdpHost` 孤儿工程**：按 §8 在 M5 后删除——确认？
7. **音频默认行为**：`audio_enabled` 默认 `true`（连接即出声，本计划书推荐）
   ——还是默认静默、由用户在设置里打开？
8. **Ctrl+Alt+Del 替代口径**：若上游有 SAS 通道，映射 **Ctrl+Alt+End**（推荐，
   与 mstsc 习惯一致）；若上游无此通道，是否接受退级为 UI 文案提示
   （「任务管理器请用远端 Ctrl+Shift+Esc」）？

---

## 附录 A：参考实现看点清单（读上游源码时按图索骥）

- `client/Windows/wf_client.c` —— Windows 客户端主循环、事件泵组织方式；
- `client/Windows/wf_gdi.c` —— 帧到达后的缓冲组织与脏矩形合并口径；
- `client/Windows/wf_keyboard.c` —— **VK→扫描码 + 扩展键**的现成表，M3 的映射表直接对照移植（该文件里的 `WIN32_KEY` 表是唯一权威）；
- `libfreerdp/core/input.c` —— `freerdp_input_send_mouse_event` / `keyboard_event_ex` 的 flags 语义；
- `client/Sample`（freerdp_clientcommon / server）—— 最小化生命周期样板，M1 桥接层的骨架照此裁剪。

## 附录 C：M0 实施记录（2026-09-25/26 深夜，**M0 已过闸：真机实连成功**）

**二进制来源**：官方 Release / pub.freerdp.com / Jenkins / Actions 均无现成 Windows 产物 →
按第③条自建：vcpkg 装 `freerdp[client]:x64-windows`（3.32.0，40 分钟），
MSVC 14.44（BuildTools，x86 路径）+ 自带 CMake。产物：`freerdp3.dll` / `winpr3.dll` /
`freerdp-client3.dll` + OpenSSL 3.6.4，许可 Apache-2.0 / BSD-3 / MIT。
vcpkg 装在 `C:\Users\AITNR\WorkBuddy\_tools\vcpkg`（**纯 ASCII 路径**——vcpkg 在含中文的
`整合` 目录下会因子进程 spawn 失败卡死在编译器探测，踩过）。

**M0 结论（2026-09-26 01:39 真机实连，agent 代跑）**：
连 127.0.0.2:3389（账户 zdh）→ 服务器组策略 SecurityLayer=0（仅标准 RDP 加密、无 TLS/NLA）
→ 协商回退 RDP security 成功 → gdi_init OK → 桌面 1600x900 → 合成鼠标注入 PASS →
首帧 BMP 落盘（像素分布 53%白+46%黑，为 AutoLogon 刚拉起会话的登录过渡画面，
**解码链路确认通畅**）→ 干净断开 → `quser` 核验 zdh 会话转「断开」保留、
主控桌面（console）未被顶掉。exit=0。
**唯一剩余人工项：音频出声**（rdpsnd 通道加载成功、无错误日志，但出声需人耳）。
AutoLogon + 短停留导致画面停在登录过渡期属正常时序，M1 持续收帧不存在此问题。

**spike 产物**：`_probe/rdp-spike/`（spike.c + CMakeLists + REAL-MACHINE.md 真机清单），
不进 sln。沙箱已验证：编译 0 error（仅上游头文件一条无害 C4081）、DLL 全量部署、
通道加载 OK（rdpsnd/rdpdr）、OpenSSL legacy OK、失败路径干净退出（exit 3 + 错误码）。
真机待验：NLA 实连、首帧 BMP、鼠标注入观感、音频出声、断开会话语义。

**踩出的 API 事实（M1 桥接层直接照抄，全部对照 3.32 源码验证）**：

1. `gdi_init(instance, format)` **必须在 PostConnect 回调里调**（3.x 头文件注明：
   依赖连接握手后核心创建的 rdpCache），放 PreConnect 直接段错误；
2. `FreeRDP_ServerPort` 是 **UINT32**（`freerdp_settings_set_uint16` 会被静默拒绝）；
3. `instance->Authenticate` 自 3.25 弃用，用 **`AuthenticateEx`**（多一个 `rdp_auth_reason` 参数）；
4. `freerdp_client_load_addins` 声明在 **`freerdp/client/cmdline.h`**（不在 channels.h）；
5. 必须先 `freerdp_register_addin_provider(freerdp_channels_load_static_addin_entry, 0)`
   注册内建静态通道提供器，否则 rdpdr/rdpsnd 被当外部 DLL 找而失败；
6. **rdpsnd 硬依赖 rdpdr**：load_addins 检测到 rdpsnd 会强制把 DeviceRedirection 拉回 TRUE；
7. **OpenSSL legacy provider（md4 → NTLM/NLA）需要 `OPENSSL_MODULES` 指向 legacy.dll 所在目录**，
   且必须用 `_wputenv_s`（`SetEnvironmentVariableW` 不更新 UCRT `_environ`，OpenSSL 的
   getenv 读不到）——正式版在 C# 侧首次调 FreeRDP 前设置（`Environment.SetEnvironmentVariable`
   同样要确认对 native getenv 可见，必要时在 native 层设）；
8. 中文注释的 C 源文件必须加 `/utf-8` 编译选项，否则 MSVC 按 CP936 解析会啃坏
   `#ifndef` 块（C4005/C1020 连锁，同项目既有 CP936 坑家族）；
9. 沙箱内 `getaddrinfo(127.0.0.1)` 都会被网络隔离拦截（报 DNS_NAME_NOT_FOUND + 乱码
   错误文本）——这是沙箱现象不是 FreeRDP 问题（真机已证实：正常）；
10. **Winsock 必须显式 `WSAStartup`**：winpr 3.x 不会代劳。不调的话 FreeRDP 内部
    `getaddrinfo` 全部返回 WSANOTINITIALISED，日志表现为乱码 + DNS_NAME_NOT_FOUND，
    极易误判为「网络不通」（M0 当晚被它骗了半小时，最后靠进程内探针定位）；
11. **`NetworkAutoDetect` / `Multitransport` / `HeartbeatPdu` 必须关**：服务器处于
    标准 RDP 安全模式（组策略 `SecurityLayer=0`）时，AutoDetect PDU 会把 RC4 解密流
    错位，后续 Demand Active 全部报 `invalid packet signature` 连接失败。
    关掉三项后即通。M1 内嵌版的 settings 模板要带上这三关；
12. **本机 RDP 服务器策略**：`HKLM\SOFTWARE\Policies\...\Terminal Services\SecurityLayer=0`
    （组策略覆盖 RDP-Tcp 的 SecurityLayer=1），即服务器只提供老式 RC4 加密、
    无 TLS/CredSSP。FreeRDP 客户端兼容此模式（关闭 AutoDetect 后正常工作），
    **无需改服务器策略**。注意 NLA 路径在本机不可达，凭据走 INFO 包（AutoLogon）；
    `AVC444ModePreferred=1` 也在同一策略键里（M4 开 GFX 时可研究利用）；
13. 真机核验 zdh 会话断开后转 Disconnected 且主控桌面不受影响——与 §5.5 预期一致；
14. **PTR_FLAGS_MOVE = 0x0800**（M1 踩坑：冒烟程序误写 0x0008 保留位，服务器对未知
    flags 的鼠标 PDU 直接判「协议流错误」断流——事件日志 RdpCoreTS #97。C# interop
    的 YcnPtrFlags.Move 已按正确值定义，M3 输入接线勿再手写常量，统一从 interop 取）；
15. **输入注入架构（M1 定稿）**：send_* 只入 per-session 队列（上限 64），由事件循环线程
    在每拍 check 前消费发送——跨线程直调 freerdp_input_send_* 与官方事件循环并发有风险，
    队列化同时对 M3 的 UI 线程接入天然安全；
16. **M1 期间系统变更记录（排查弯路副产品，均经用户 UAC 确认）**：组策略 SecurityLayer
    0→1（协商/TLS，建议保留——更安全且 mstsc 无感）、AVC444ModePreferred 1→0（可还原，
    与本 bug 无关）、TermService 已重启、zdh 测试会话已清。

## 附录 B：与参考文档（方案 A 原文）的差异汇总

| 参考文档 | 本计划书 | 原因 |
|---|---|---|
| C++/WinRT Runtime Component 桥接 | C ABI 原生 DLL | §3.2：XAML 控件必须留 C# 侧，WinRT 封送对帧缓冲不友好 |
| 建议 GFX/AVC444 硬解 | 首期关闭，NSCodec/ClearCodec | §5.1：场景是环回+局域网；规避 OpenH264/FFmpeg 构建与许可面 |
| 渲染两方案并列 | 两步走（原型→生产） | 先解耦「解码对不对」与「渲染够不够快」两个问题 |
| （未涉及） | 环回单会话可见性语义 §5.5 | 本项目主场景就是环回，必须先讲清再动手 |
| （未涉及） | 与现有代理/桥/心跳机制的零改动红线 §5.4 | 会话侧机制已真机验证稳定，不重写 |
| （未涉及） | 音频输出重定向 §5.8 | v2 新需求：rdpsnd 原生侧直放，不跨 C ABI 传音频 |
| （未涉及） | 全屏 + 低级键盘钩子接管系统组合键 §5.3.1 | v2 新需求：全屏后 Win 键 / Alt+Tab 全部转投远端 |
