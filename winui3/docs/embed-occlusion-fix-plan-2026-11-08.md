# 内嵌画面「窗口恢复后不呈现」修复计划书

- 日期：2026-11-08
- 状态：**已实施并自测通过（2026-11-08），未提交，等用户指示再提交**。真机复测 S1–S4 待用户重新部署安装版后执行。
- 适用工程：`repo/winui3/src/Yomori.App`（Yomori 主控端，WinUI 3 / net8.0-windows / unpackaged）
- 冒烟工程：`_smoke/Smoke.csproj`（位于 git 根 `整合/repo` 之外，断言不入库）
- 需求来源：用户反馈「一开始正常，跑一段时间后画面卡住，切换页面刷新后变黑」；截图 `PixPin_2026-11-08_11-33-58.png`；用户实测「收进托盘再双击图标回来就正常了」
- 排障记录：`.workbuddy/memory/2026-11-08.md`（完整证据链）

---

## 0. 必要条件与待确认事项

本节前置。以下三项默认按推荐方案执行；如有异议在动手前指出，否则不阻塞。

| 编号 | 事项 | 推荐方案 | 备选 |
|---|---|---|---|
| A | 恢复窗口的兜底信号 | 新增 `Window.Activated` 订阅 + 所有「把窗口叫回来」的入口统一调一个幂等判定方法 | 仅放宽 `AppWindow.Changed` 的判定条件，不加新事件订阅 |
| B | 闸门状态是否写诊断日志 | 写。闸门由「暂停→恢复」或「恢复→暂停」变化时各记一行 `[embed]`，只记一次，不逐拍刷屏 | 不加日志，保持现状静默 |
| C | 是否一并修多画面页的订阅/定时器缺口 | 一并修。属同一次「切页刷新」路径上的相邻缺陷，改动量小 | 只修主因，另立任务 |

**约束（不可协商）**：修复不得破坏「窗口隐藏期间停止 Present 以便系统裁剪工作集」这一既有行为（`MainWindow.xaml.cs:338-340`、`RdpView.xaml.cs:808-814`）。任何兜底都必须按窗口**实际状态**判定，不能一有事件就无条件恢复。

---

## 1. 问题定义

### 1.1 现象与复现路径

1. 启动主控端，两路会话通道并行执行，多画面页两格内嵌画面正常。
2. 运行约 10 分钟后，画面定格（保留最后一帧，不再刷新）。
3. 切换页面试图刷新，两格变为全黑。
4. 将窗口收进状态栏再双击图标恢复，画面立即正常。

### 1.2 已确认的根因

呈现节拍器 `RdpView.OnPresentTick` 开头被全局闸门 `s_windowOccluded`（`RdpView.xaml.cs:50`，`static volatile bool`）挡住，逐拍早退，不再抓帧、上传、Present。该标志由 `SuspendAllForHiddenWindow()` / `ResumeAllForHiddenWindow()` 设置，目前只在以下两处置位/复位：

| 位置 | 动作 | 是否对称 |
|---|---|---|
| `MainWindow.xaml.cs:345`（`OnAppWindowChanged`） | 仅当 `args.DidPresenterChange` 且 `OverlappedPresenter.State == Minimized` 时 Suspend | 复位依赖同一事件的非最小化分支 |
| `MainWindow.xaml.cs:567`（`HideToTray`） | Suspend | 由 `RestoreFromTray` 复位 |
| `MainWindow.xaml.cs:608`（`RestoreFromTray`） | Resume | 对称 |

**失败路径**：窗口最小化 → 任务栏点回来。`MainWindow` 只订阅 `AppWindow.Changed` 与 `AppWindow.Closing`（`:108`、`:112`），没有窗口激活/可见性兜底；若恢复时 `DidPresenterChange` 未触发，或事件回调里 `Presenter.State` 读取存在竞态，`ResumeAllForHiddenWindow()` 不会被调用，闸门永久为 `true`。

**成功路径**：收进托盘 → 双击图标。`RestoreFromTray()` 有显式 `ResumeAllForHiddenWindow()`（`:608`），与用户实测一致。

### 1.3 黑屏为何发生在「切页刷新」之后

`RdpMultiViewPage.OnNavigatedFrom` 对每格调 `SetClient(null)` → `RdpSurfaceCell.SetClient` → `RdpView.DetachClient()`（`:352-394`）：销毁 renderer、停 `_presentTimer`。切回时 `OnNavigatedTo` → `LayoutSurfaces()` → `AttachClient()`（`:291-311`）：新建 renderer（`d3d_dev` 累计创建数由 2 涨至 13）、新建 16ms 节拍器、`MarkPendingFirstFrame()` 强制首拍抓帧。

新建的 D3D 交换链内容为空；而 `s_windowOccluded` 是跨重建仍存在的 static 状态，闸门仍挡在节拍器最前面，首帧强制抓帧也执行不到，于是从「定格」变为「全黑」。

### 1.4 证据摘要

| 证据 | 数值 / 位置 |
|---|---|
| 帧与解码全程正常 | `hw_frames` 11:31:45 = 19984 → 11:34:45 = 25942；`decode_fail=0`；`decoder=[dxva2] hwactive=1`；D3D 上载「失败 0」 |
| 呈现停止 | `dirty_api copy_ok` 冻结在 15231（zdh）/ 15118（bh3），11:31:45 起四次采样完全相同 |
| 诊断行消失不是日志降噪 | `MainViewModel.ShouldShowLogLine`（`:2723`）只挡「运行日志」页显示；`AppendLog`（`:2663`）先写 `FileLogger` 再决定是否上页，`developer_mode=False` 不影响落盘 |
| renderer 反复重建 | 11:32:25 起 `D3D 渲染器初始化成功（1920×1080）` 反复出现；`[memory] d3d_dev` 2/0 → 7/5 → 10/8 → 13/11 |
| 排除崩溃与权限 | `crash.log` 无本次异常；WER 11:35:41 为 `AUDIODG.EXE`（音频）崩溃，晚于黑屏；会话/代理/进程均在线 |

---

## 2. 修复目标与验收标准

1. 窗口从最小化、托盘、单实例激活、任务栏点击等**任意路径**恢复可见后，内嵌画面在 1 秒内继续刷新，无需切换页面或重连。
2. 窗口真正隐藏（最小化或收进状态栏）期间，仍停止抓帧与 Present，工作集裁剪行为不回退。
3. 画面正常时切换页面再切回，不出现黑屏。
4. 闸门状态变化在运行日志中可观测，避免同类故障再次「静默黑屏」。
5. 不改变连接、解码、渲染后端配置，不新增界面开关，不改 XAML 与原生层。

验收判据以**外部可观测行为**为准，不以代码内部标志为准：日志中 `[embed] 帧到达节奏` 恢复每 5 秒一行、`dirty_api copy_ok` 继续增长、截图中画面有内容。

---

## 3. 方案设计

### 3.1 单一判定源：`SyncOcclusionGate()`

在 `MainWindow` 新增一个私有幂等方法，按窗口**当前实际状态**决定闸门，不依赖事件参数：

```
private void SyncOcclusionGate()
{
    var minimized = (AppWindow.Presenter as OverlappedPresenter)?.State
                    == OverlappedPresenterState.Minimized;
    if (_hiddenToTray || minimized)
        RdpView.SuspendAllForHiddenWindow();
    else
        RdpView.ResumeAllForHiddenWindow();
}
```

要点：

- 必须同时判 `_hiddenToTray`。`AppWindow.Hide()` 不改变 `OverlappedPresenter.State`，只看 `State` 会把「已收进托盘」误判为可见。
- `SuspendAllForHiddenWindow()` / `ResumeAllForHiddenWindow()` 是纯赋值，重复调用无副作用，因此所有入口可无条件调用。
- 该方法必须在 UI 线程调用；所有调用点均在 UI 线程事件回调内，无需额外封送。

### 3.2 调用入口清单

| 入口 | 改动 |
|---|---|
| `OnAppWindowChanged`（`:327`） | 将 `SyncOcclusionGate()` 放在方法最前（早于 `DidSizeChange/DidPositionChange/DidPresenterChange` 的 early-return）；删除原 `if (args.DidPresenterChange)` 内的 Suspend/Resume 分支 |
| `HideToTray`（`:541`） | 调整顺序后，在末尾调 `SyncOcclusionGate()`；删除原显式 `SuspendAllForHiddenWindow()` |
| `RestoreFromTray`（`:586`） | 在 `ActivateWindow()` 之后、方法末尾调 `SyncOcclusionGate()`；删除原显式 `ResumeAllForHiddenWindow()` |
| `ActivateWindow`（`:612`） | 在 `try` 块末尾（`Activate()` 之后）调 `SyncOcclusionGate()`，覆盖所有「把窗口叫回来」的路径 |
| `Window.Activated`（新增订阅，构造函数） | 事件参数为 `WindowActivatedEventArgs`；仅当 `WindowActivationState != Deactivated` 时调 `SyncOcclusionGate()` |

`Window.Activated` 的 `Deactivated` 分支**不得**调用 `SuspendAllForHiddenWindow()`。焦点变化不等于窗口隐藏，也不等于会话锁定；该约束与既有「通知投递」结论一致。

### 3.3 关键顺序调整（实现时必须钉死）

`HideToTray` 现有顺序为：

```
SuspendSurfaceRefresh();
RdpView.SuspendAllForHiddenWindow();   // :567
AppWindow.Hide();                      // :569  → 可能触发 AppWindow.Changed
_hiddenToTray = true;                  // :570
```

若按 3.2 在 `OnAppWindowChanged` 里同步闸门，`AppWindow.Hide()` 触发回调时 `_hiddenToTray` 仍为 `false`、`State` 非 Minimized，`SyncOcclusionGate()` 会**把刚设的暂停反向清掉**，隐藏期间继续 Present，破坏内存优化。

因此 `HideToTray` 必须改为：

```
SuspendSurfaceRefresh();
_hiddenToTray = true;        // 先置位
AppWindow.Hide();
SyncOcclusionGate();         // 最后按实际状态判定
UpdateTrayState();
```

`RestoreFromTray` 现有 `_hiddenToTray = false`（`:597`）在 `AppWindow.Show()`（`:599`）之前，Show 触发的回调读到的状态已正确；但显式 Resume（原 `:608`）仍需保留到 `ActivateWindow()` 之后由 `SyncOcclusionGate()` 统一收口，避免 `presenter.Maximize()` / `presenter.Restore()` 触发的回调把闸门又置回暂停。

### 3.4 `RdpView` 侧防御

1. **复位 `_renderSuspended`**（`:34`）。该字段是实例字段，但 `DetachClient()`（`:352`）、`Disconnect()`（`:397`）、`AttachClient()`（`:291`）均未复位，与本次故障同属「跨重建残留状态」隐患。在 `DetachClient()` 与 `Disconnect()` 中置 `false`；`AttachClient()` 入口也置 `false` 以防外部实例复用。

   安全性：全屏协调器 `RdpFullScreenCoordinator` 在进入全屏时调 `_host.SuspendRendering()`（`:141`），退出时 `ResumeRendering()`（`:134`、`:181`，含 `Exit()` 兜底）。宿主被 `DetachClient` 只发生在页面卸载或会话交接，届时宿主已不在可视树，复位不影响全屏期间的双路渲染抑制。

2. **闸门变化写一行诊断**（对应 §0-B）。在 `RdpView.OnPresentTick` 的 `s_windowOccluded` 分支加实例字段 `_occlusionLogged`，仅在状态翻转时经 `DiagnosticLog` 输出：

   - 进入暂停：`[embed] 呈现已暂停：窗口隐藏闸门生效，帧与解码继续`
   - 恢复：`[embed] 呈现已恢复：窗口隐藏闸门解除，补抓首帧`

   `DiagnosticLog` 由页面注入 `VM.AppendLog`，先落盘后过滤，不占「运行日志」页可见行。诊断行不得逐拍输出。

### 3.5 `RdpMultiViewPage` 订阅与定时器

1. `OnSurfaceRefreshRequested` 的订阅从构造函数移到 `OnNavigatedTo`：`VM.SurfaceRefreshRequested -= OnSurfaceRefreshRequested; VM.SurfaceRefreshRequested += OnSurfaceRefreshRequested;`（先退后订，幂等）；`OnNavigatedFrom` 保留 `-=`。构造函数不再订阅。

   现状缺陷：`OnNavigatedFrom`（`:97`）退订后，`OnNavigatedTo` 不重订，切页回来后锁定/静音开关的即时刷新失效。成对订阅/退订同时避免单例 VM 持有已卸页面的引用。

2. `OnNavigatedTo` 创建 `_refreshTimer` 前先 Stop 并置空已有实例，防止重复导航累积多个 1Hz 定时器。

### 3.6 明确不改的范围

- 不动 `RdpD3DRenderer`、`RdpEmbeddedClient` 与原生层，不改解码/渲染后端策略。
- 不改 XAML（避免 `.xbf` / `resources.pri` 部署复杂度）。
- 不接回任何界面开关，不改变 `RdpConfig.Enabled` 的既有处理。
- 不在 `Deactivated`、`Window.VisibilityChanged` 的隐藏分支做 Suspend。

---

## 4. 逐文件改动清单

| 文件 | 改动 |
|---|---|
| `Views/MainWindow.xaml.cs` | 新增 `SyncOcclusionGate()`；新增 `Window.Activated` 订阅与处理器；改造 `OnAppWindowChanged`；调整 `HideToTray` 顺序；`RestoreFromTray` / `ActivateWindow` 收口调用；删除两处散落的显式 Suspend/Resume |
| `Views/RdpView.xaml.cs` | `DetachClient` / `Disconnect` / `AttachClient` 复位 `_renderSuspended`；新增 `_occlusionLogged` 与闸门变化诊断行 |
| `Views/RdpMultiViewPage.xaml.cs` | 订阅改到 `OnNavigatedTo` 成对管理；`_refreshTimer` 重建前先清理 |
| `_smoke/EmbedOcclusionCheck.cs` | 新增（仓库根之外） |
| `_smoke/Smoke.csproj` / `_smoke/Program.cs` | 注册新检查（仓库根之外） |
| 本计划书 | 实施完成后补「实施结果」章节 |

改动均为 `Views/*.xaml.cs`，不涉及 `Services/` / `Helpers/` 的「链接 + 排除」复用，因此**不需要额外 `dotnet publish Yomori.Agent.csproj`**。

---

## 5. 冒烟断言设计

新增 `EmbedOcclusionCheck.cs`，沿用源码文字锚点断言（`Contains`），覆盖以下要点：

| 编号 | 断言 |
|---|---|
| 1 | `MainWindow` 存在 `SyncOcclusionGate()`，且其判据同时含 `_hiddenToTray` 与 `OverlappedPresenterState.Minimized` |
| 2 | `OnAppWindowChanged` 中闸门同步调用位于事件参数 early-return **之前**（不得被 `DidPresenterChange` 限定） |
| 3 | `HideToTray` 中 `_hiddenToTray = true` 出现在 `AppWindow.Hide()` **之前**（顺序回归，防 3.3 反向清空） |
| 4 | `RestoreFromTray` / `ActivateWindow` 均调用 `SyncOcclusionGate()` |
| 5 | 存在 `Activated` 订阅，且处理器判据为 `!= WindowActivationState.Deactivated` |
| 6 | 不得出现「在 `Deactivated` 分支调用 `SuspendAllForHiddenWindow()`」的形状 |
| 7 | `RdpView` 的 `DetachClient` / `Disconnect` 均含 `_renderSuspended = false` |
| 8 | `RdpView` 含闸门变化诊断行，且 `_occlusionLogged` 只翻转一次（不逐拍写） |
| 9 | `RdpMultiViewPage.OnNavigatedTo` 重订阅 `SurfaceRefreshRequested`，`OnNavigatedFrom` 退订 |
| 10 | 源码中不再存在散落的 `RdpView.SuspendAllForHiddenWindow()` / `ResumeAllForHiddenWindow()` 直接调用（统一走 `SyncOcclusionGate()`） |

纪律：

- 换锚点不删判据；按源码文字 `Contains` 做断言时，文案调整须同步换锚点而非删断言。
- 新断言当场跑；首跑红多为判据写错或注释字面量误报，须先剔注释再判。
- 判据不得跟着实现走（不得用「实现里写了某标志」代替行为判据）。

---

## 6. 自测与验证

### 6.1 编译与冒烟

1. 先**完全退出**正在运行的 Yomori（pri 锁）；不得用 `-p:OutDir=<临时目录>` 规避。
2. `dotnet build -p:Platform=x64` —— 不带 `-p:Platform=x64` 会报 `WindowsAppSDKSelfContained` 架构错。
3. `dotnet run --project _smoke` —— **不带** `-p:Platform=x64`。带 Platform 只更新 `bin\x64\...`，执行的仍是 `bin\Debug\net8.0-windows\Smoke.exe` 旧产物，会造成假验证。
4. 确认 `bin\Debug\net8.0-windows\Smoke.exe` 的 mtime 已更新，且输出 `===== 全部通过 =====`、0 FAIL。

### 6.2 真机复测场景

| 场景 | 步骤 | 期望 |
|---|---|---|
| S1（原失败路径） | 两路任务运行中 → **最小化**主窗口 → 从任务栏点回来 | 1 秒内画面继续刷新；日志 `[embed] 帧到达节奏` 恢复 5 秒一行、`dirty_api copy_ok` 继续增长 |
| S2（原成功路径回归） | 点关闭收进状态栏 → 双击状态栏图标 | 画面正常；隐藏期间不产生 Present（工作集裁剪不回退） |
| S3（切页回归） | 画面正常时切到「任务执行」再切回「多画面（全部）」 | 不黑屏；锁定/静音开关即时刷新有效 |
| S4（长稳） | 持续运行 15 分钟并重复 S1 三次 | 无定格、无黑屏；`d3d_dev` 累计创建数不因切页之外的操作增长 |

### 6.3 部署提醒

用户当前运行的是安装版 `C:\Program Files\Yomori\`（日志第 2 行 `配置文件：` 已确认），不是 dev build 输出。本次改动全为 `.cs`，不含 XAML，但**仍需重新构建并重新部署安装版那份**才能生效；仅替换 `Yomori.dll` 前须确认该文件的 mtime 与构建产物一致。真机复测 S1–S4 由用户在重新部署后执行。

---

## 7. 风险与回退

| 风险 | 缓解 |
|---|---|
| 兜底被 `AppWindow.Hide()` 触发的回调反向清空，隐藏期间继续 Present | §3.3 顺序调整 + 冒烟断言 3 |
| `Window.Activated` 高频触发导致闸门反复翻转 | `SyncOcclusionGate()` 按实际状态幂等判定；仅在状态翻转时写诊断行 |
| `Deactivated` 误判为隐藏 | 明确禁止在该分支 Suspend；冒烟断言 5、6 |
| 复位 `_renderSuspended` 影响全屏双路渲染抑制 | 只在页面卸载/会话交接的 `DetachClient` / `Disconnect` 复位；全屏协调器进入全屏时重新置位 |
| 工作树存在并行会话未提交改动 | `git add` 后必须 `git diff --cached` 逐文件检查，避免混入他人 hunk |

回退：本次改动集中在 3 个 `Views/*.xaml.cs` 文件，均为可独立回退的局部修改；不涉及数据格式、配置结构或原生 ABI。

---

## 8. 交付与提交纪律

1. 本计划书确认后再进入实现。
2. 实现完成后先自测（§6.1 编译 + 冒烟），再汇报；**不自动提交**，等用户明确指示。
3. 提交时只入库 `Views/MainWindow.xaml.cs`、`Views/RdpView.xaml.cs`、`Views/RdpMultiViewPage.xaml.cs` 与本计划书的实施结果章节。`_smoke/` 位于 git 根之外，断言改动不入库（与既有惯例一致）。
4. 提交后同步 `.workbuddy/memory/2026-11-08.md`，把状态从「待确认」更新为「已实施 / 已验证」。

---

## 9. 附：证据索引

- 运行日志：`C:\Program Files\Yomori\logs\2026-11-08.log`（11:20:45 启动，11:31:01 最后一组帧诊断，11:32:25 起 renderer 反复初始化）
- 崩溃日志：`C:\Program Files\Yomori\logs\crash.log`（本次无异常记录）
- 排障笔记：`.workbuddy/memory/2026-11-08.md`
- 相关代码：`Views/RdpView.xaml.cs`（`:50`、`:291-311`、`:352-394`、`:801-828`、`:1085`）、`Views/MainWindow.xaml.cs`（`:108`、`:327-351`、`:541-610`、`:612-635`）、`Views/RdpMultiViewPage.xaml.cs`（`:43-106`、`:136-201`）

---

## 10. 实施结果（2026-11-08，已自测，未提交）

### 10.1 §0 三项按推荐方案执行

- A 加 `Window.Activated` 兜底：已实施。
- B 闸门翻转写一行 `[embed]` 诊断：已实施。
- C 一并修多画面页订阅/定时器：已实施。

### 10.2 实际改动

| 文件 | 内容 |
|---|---|
| `Views/MainWindow.xaml.cs` | 新增 `SyncOcclusionGate()`（按 `_hiddenToTray \|\| OverlappedPresenterState.Minimized` 二选一）与 `OnWindowActivated`（仅非 `Deactivated` 时同步）；构造函数订阅 `Activated`；`OnAppWindowChanged` 把闸门同步提到 early-return 之前并删除原 `DidPresenterChange` 分支；`HideToTray` 改为「先置 `_hiddenToTray` → `Hide()` → `SyncOcclusionGate()`」；`RestoreFromTray` 删除散落 Resume、在 `ActivateWindow()` 之后统一收口；`ActivateWindow()` 在 `try/catch` **之外**补一次 `SyncOcclusionGate()`，保证抢焦点抛异常被吞时闸门仍同步。 |
| `Views/RdpView.xaml.cs` | 新增 `_occlusionLogged`；`DetachClient` / `Disconnect` 复位 `_renderSuspended` 与 `_occlusionLogged`（`AttachClient` 首行调 `DetachClient`，天然覆盖，已加注释）；`OnPresentTick` 开头读一次 `occluded` 并在翻转时各记一行诊断，随后所有分支统一使用该局部变量。 |
| `Views/RdpMultiViewPage.xaml.cs` | `SurfaceRefreshRequested` 订阅移到 `OnNavigatedTo`，先退后订；`OnNavigatedFrom` 保留退订；定时器重建前 `_refreshTimer?.Stop()` 并置空。 |
| `_smoke/EmbedOcclusionCheck.cs` | 新增 17 条源码形状断言（仓库根之外，不入库）。 |
| `_smoke/Smoke.csproj` / `_smoke/Program.cs` | 注册 `RunEmbedOcclusionCheck()`（仓库根之外，不入库）。 |

与计划书的**一处微调**：`ActivateWindow()` 的 `SyncOcclusionGate()` 放在 `try/catch` 之外而非 `try` 块末尾。原设计在 `catch` 吞掉异常时会跳过同步，放到外面才能覆盖所有路径；`SyncOcclusionGate()` 幂等，与 `RestoreFromTray` 里的收口重复调用无害。

### 10.3 自测结果

- `dotnet build -p:Platform=x64`：**0 错误**，8 个既有 nullability 警告（`MainViewModel.cs`、`LauncherProfile.cs`、`RdpEmbeddedClient.cs`、`RdpD3DRenderer.cs`）。程序开着未触发 pri 锁。
- `dotnet run --project _smoke`（不带 `-p:Platform=x64`）：`===== 全部通过 =====`，0 FAIL，4 项环境跳过。新增 17 条 `[遮挡闸门]` 断言全 PASS。
- `bin\Debug\net8.0-windows\Smoke.exe` mtime 由 11-07 14:55:42 更新为 **2026-11-08 11:54:58**，确认非假验证。
- 未执行 Agent publish：本次改动全在 `Views/*.xaml.cs`，不涉及 `Services/` / `Helpers/` 的链接复用。

### 10.4 工作树状态（未提交）

本会话改动：`MainWindow.xaml.cs`、`RdpView.xaml.cs`、`RdpMultiViewPage.xaml.cs`、本计划书（untracked）。
历史遗留未提交、本次一律未动：`live-script-logs.md`、7 张 `docs/images/guide-2.4-*.png/jpg`、`external-log-adapter-guide.md`。
`_smoke/` 三个文件在 git 根 `整合/repo` 之外，不入库。

### 10.5 待办

1. 用户重新构建/部署安装版 `C:\Program Files\Yomori\`（本次全为 `.cs`，但安装版那份不是 dev build 输出），执行 §6.2 的 S1–S4 真机复测；S1（最小化 → 任务栏点回来）是原失败路径。
2. 复测通过后按用户指示提交，只入库 3 个 `.cs` 与本计划书；提交前 `git diff --cached` 逐文件检查，避免混入并行会话或历史遗留 hunk。

