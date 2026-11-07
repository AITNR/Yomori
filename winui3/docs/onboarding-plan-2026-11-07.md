# 首次启动引导（Onboarding）实施计划书

- 日期：2026-11-07
- 状态：已确认并实施（2026-11-07）。C 项按用户指示改为「结束后回到首页」，其余按推荐方案。
- 适用工程：`repo/winui3/src/Yomori.App`（Yomori 主控端，WinUI 3 / net8.0-windows / unpackaged）
- 冒烟工程：`_smoke/Smoke.csproj`
- 需求来源：用户口述 + 截图 `PixPin_2026-11-07_13-20-20.png`

---

## 0. 必要条件与待确认事项

本节前置。以下六项未经确认不进入实现。

| 编号 | 事项 | 推荐方案 | 备选 |
|---|---|---|---|
| A | 首页四个快捷入口的聚焦粒度 | 逐个聚焦，每次点击推进一个入口；全流程共 7 步 | 一次高亮整排入口、一张气泡列四条说明；全流程共 4 步 |
| B | 引导期间「解锁 RDP」按钮能否真实点击 | 不可点击。遮罩为只读介绍层，任意点击统一解释为「下一步」；按钮在文案中点名并做描边高亮 | 把按钮挖成可交互热区，点击穿透到真实按钮且不推进步骤（与「点一次下一步」相冲突，需另定义手势） |
| C | 完成或跳过后的停留页面 | 停在「通道管理」页，便于用户接着建通道 | 回到首页 |
| D | 首次启动判据 | `ConfigManager.Load()` 记录 `WasCreated`（`config.json` 不存在 = 全新安装）。老用户升级不弹；引导中途强退，因 `config.json` 已生成，下次不再弹 | 增加 `onboarding.json` 状态文件，记录 `completed` / `pending`，支持强退后续播与将来引导版本升级 |
| E | 截图隐私处理 | 对左上角账户名与邮箱做打码或裁切后再随包分发 | 原图直接使用 |
| F | 导航辅助控件 | 仅提供「跳过」与进度角标（如 `3/7`），不提供「上一步」；`Esc` 等同跳过 | 增加「上一步」按钮 |

确认结果：**A 逐个聚焦（7 步）、B 引导中按钮只读介绍、C 结束后回到首页、D 只用 `ConfigManager.WasCreated`、E 截图打码、F 只要「跳过」+ 进度角标**。已按此实施。

---

## 1. 需求定义

### 1.1 触发条件

1. 全新安装后第一次正常启动主控端，自动弹出引导。
2. 设置页提供手动入口，可随时重新观看一次。
3. 以下启动路径不弹：`--rdp-agent` 代理模式、`--deploy-agent` 安装期部署、`--embed-vm` / `--embed-auto` 自检、单实例转发激活。
4. 老配置（升级安装）不自动弹。

### 1.2 交互契约

- 引导为**全窗口覆盖层**，暗化非聚焦区域，聚焦元素保持原亮度并加描边。
- 覆盖层任意位置点击一次 = 进入下一步；最后一步点击 = 结束。
- 右上角「跳过」随时结束，等价于完成，不再自动弹出。
- 引导期间用户不能操作被遮挡的界面；换页由引导自身调用 `MainWindow.NavigateTo(tag)` 完成。
- 结束时不留下事件订阅、不留下动画专用变换对象。

### 1.3 步骤清单（按推荐方案 A，共 7 步）

| 步 | 页面 tag | 聚焦目标（需新增 x:Name） | 内容 | 配图 |
|---|---|---|---|---|
| 1 | `home` | 首页「任务执行」入口卡 | 编排任务 | — |
| 2 | `home` | 首页「运行耗时统计」入口卡 | 查看上次运行时间与平均执行时间 | — |
| 3 | `home` | 首页「运行日志」入口卡 | 查看 Yomori 日志 | — |
| 4 | `home` | 首页「通道管理」入口卡 | 添加并编辑用于执行任务的通道 | — |
| 5 | `settings` | 「远程桌面解锁（RDP）」整张卡片 | 全绿前提 + 解锁按钮 + 失败可重试 | — |
| 6 | `channel-mgmt` | 「新建通道」按钮 | 添加通道的两个前提 | — |
| 7 | `channel-mgmt` | 无（居中大卡片） | 如何添加 Windows 本地用户 | `assets/onboarding/add_local_user.png` |

第 5 步的「全绿」判据与 `Models/RdpUnlockModels.cs:232` 的 `RdpUnlockSnapshot.AllPass` 一致：四项检测结果全部为 `RdpProbeLevel.Pass`（界面文字「通过」、色块为绿）。

---

## 2. 现状核对（已确认的代码事实）

| 事实 | 位置 |
|---|---|
| 启动顺序：`new MainViewModel()`（构造内 `ConfigManager.Load()`）→ `new MainWindow()` → `ViewModel.Initialize()` | `App.xaml.cs:35-64` |
| `ConfigManager.Load()` 在 `config.json` 不存在时立即新建并落盘 `Save(created)` | `Services/ConfigManager.cs:36-43` |
| 配置路径 = `AppPaths.BaseDir/config.json`；`BaseDir` 靠同级 `assets/` 或 `config.json` 回溯 | `Services/AppPaths.cs:47,60-79` |
| 导航统一入口 `NavigateTo(string tag)`，`settings` / `channel-mgmt` / `home` 均已支持；`ContentFrame` 为 Frame | `Views/MainWindow.xaml.cs:1715-1767`、`MainWindow.xaml:242` |
| 窗口根布局为 Grid，两行：标题栏（Row 0）+ NavigationView（Row 1）；无现成覆盖层 | `Views/MainWindow.xaml:12-16` |
| 首页四张入口卡是四个 Button，均无 `x:Name` | `Views/HomePage.xaml:107-149` |
| 设置页 RDP 模块是第 133–197 行的 `Border`，无 `x:Name`；外层 `ScrollViewer` 也无 `x:Name` | `Views/SettingsPage.xaml:10-14,133-197` |
| 「新建通道」按钮无 `x:Name` | `Views/ChannelsPage.xaml:64-65` |
| 设置页「关于」卡片可作为手动入口宿主 | `Views/SettingsPage.xaml:318-439` |
| 资源随包路径：仓库根 `assets/` 由 `tools/package_release.py:662` 整目录拷贝到 `<安装目录>\assets`，Inno `[Files]` 递归打包 | `tools/package_release.py:662`、`tools/installer.iss:144` |
| 冒烟只链接纯 .NET 的 Models / Services / Helpers，禁止 `Microsoft.UI.Xaml` | `_smoke/Smoke.csproj` |

---

## 3. 总体设计

### 3.1 分层

| 层 | 文件 | 职责 | 进冒烟 |
|---|---|---|---|
| Models | `Models/OnboardingPlan.cs`（新） | `OnboardingStep` 数据记录 + 静态步骤表 + 全部文案常量 + 目标元素 key | 是 |
| Services | `Services/OnboardingStateStore.cs`（新，D 备选时必需） | `onboarding.json` 读写，含 `Version` / `Completed` | 是 |
| Services | `Services/ConfigManager.cs` | 新增 `WasCreated` 属性 | 否（随既有链入） |
| ViewModels | `ViewModels/MainViewModel.cs` | 暴露 `IsFreshInstall` → `_configManager.WasCreated` | 否 |
| Views | `Views/OnboardingOverlay.xaml(.cs)`（新） | 覆盖层：暗化挖洞、描边、气泡、进度、跳过、配图 | 否 |
| Views | `Views/OnboardingController.cs`（新） | 步骤状态机、调用 `NavigateTo`、延后测量、目标定位、结束清理 | 否 |
| Views | `Views/MainWindow.xaml(.cs)` | 追加覆盖层、启动触发、暴露目标查找 | 否 |

`OnboardingPlan` 放在 Models 且**不得引用任何 `Microsoft.UI.Xaml` 类型**（冒烟纪律）。步骤表用纯字符串保存页面 tag、目标 key、标题、正文、图片相对路径。

### 3.2 覆盖层结构

在窗口根 Grid 追加一个 `Grid`（`Grid.Row="1"`，`Canvas.ZIndex="100"`，`Visibility="Collapsed"`，`Background="Transparent"`），只覆盖 NavigationView 区域，不动标题栏拖拽区与系统按钮。子元素顺序：

1. 全屏透明命中层：`Tapped="OnAdvance"`，负责「点一次下一步」；必须显式设置 `Background="Transparent"`，否则 WinUI 空白区域不接收指针事件。
2. `Path x:Name="DimPath"`：`Fill="#B3000000"`，`IsHitTestVisible="False"`；`Data` 为 `GeometryGroup FillRule="EvenOdd"`，含一个覆盖全窗口的 `RectangleGeometry` 与一个聚焦矩形的 `RectangleGeometry`（挖洞）。
3. `Border x:Name="FocusBorder"`：聚焦矩形描边（2px，主题强调色），`IsHitTestVisible="False"`。
4. `Canvas`：承载气泡卡片。气泡为 `Border Style="{StaticResource YomoriCardStyle}"`，内含标题、正文、`Image`（第 7 步）、进度角标；`Canvas.Left/Top` 由代码按聚焦矩形计算（优先下方，空间不足改上方/右侧，并与窗口边界求交）。
5. 「跳过」按钮：单独 `Tapped` 处理器内 `e.Handled = true` 后 `Finish()`，避免冒泡触发 `OnAdvance`。

首版**不做属性动画**。若后续追加淡入，须遵守既有纪律：Storyboard 挂字段保活、`EnableDependentAnimation`、收尾 `RenderTransform = null` 摘掉动画专用变换，否则命中测试会在 `GetLocalTransform` 上踩到失效对象。

### 3.3 目标定位与坐标

- 各页面给目标控件补 `x:Name`：`HomePage` 四个 Button、`SettingsPage` 的 `RdpUnlockCard` 与 `SettingsScroll`、`ChannelsPage` 的 `AddChannelButton`。
- `OnboardingController` 通过 `(ContentFrame.Content as FrameworkElement)?.FindName(key)` 取目标；取不到时降级为「不挖洞 + 气泡居中」，并写一行运行日志，绝不抛异常或卡死引导。
- 坐标：`target.TransformToVisual(overlayRoot).TransformBounds(new Rect(0,0,target.ActualWidth,target.ActualHeight))`；挖洞矩形与 overlay 视口求交后再写入 `HoleGeometry.Rect` 与 `FocusBorder` 的 `Canvas.Left/Top/Width/Height`。
- 设置页 RDP 卡片位于 `ScrollViewer` 内且可能不在视口：先调 `target.StartBringIntoView()`，等滚动稳定后重新测量。

### 3.4 换页与测量时序

`NavigateTo` 之后不立即测量。顺序固定：

1. `OnboardingController.GoTo(step)` 判断页面 tag 与当前不同则 `NavigateTo(tag)`；
2. 订阅一次性 `ContentFrame.Navigated`（或页面 `Loaded`）；
3. `DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, ...)` 延后一拍，等待布局完成；
4. 需要滚动则 `StartBringIntoView()`，再延后一拍（或 `ViewChanged` 后）；
5. 定位目标、计算矩形、刷新覆盖层、显示气泡。

所有步骤在 UI 线程完成。不使用 `await Task.Run` 后在续体中改绑定（既有 `COMException 0x8001010E` → 原生 AV 的复发点）。结束或跳过时解绑 `Navigated` / `Loaded` / `ViewChanged`，控制器本身作为 MainWindow 字段保活，避免局部定时器被 GC。

### 3.5 启动触发

`App.OnLaunched` 已完成窗口构建后，或 `MainWindow` 构造末尾，用一次 `DispatcherQueue.TryEnqueue(Low)` 触发：

```
if (VM.IsFreshInstall
    && Program.EmbedVmArgs is null
    && !Program.IsAgentInvocation(...)
    && !OnboardingStateStore.IsCompleted)   // D 采用推荐方案时此条省略
{
    _onboarding.Start(force: false);
}
```

推荐方案 D 下不需要状态文件：`config.json` 首次启动时即已生成，跳过或完成后下一次启动 `WasCreated == false`，不会重复弹出。

### 3.6 手动重看入口

`SettingsPage.xaml` 的「关于」卡片内新增一行：

- 按钮 `Content="重新观看开始引导"`，`Click="OnRestartOnboarding"`；
- 下方说明文字：「按首次启动的顺序重新播放一遍引导，不改变任何配置。」

按用户约定，这是**一次性动作**，不是持续状态，因此用按钮而非 `ToggleSwitch`；不写配置、不需要二次确认弹窗。`OnRestartOnboarding` 调用 `MainWindow` 暴露的 `RestartOnboarding()` → `Start(force: true)`，无视 `IsFreshInstall` 与完成标记。

---

## 4. 逐文件改动清单

| # | 文件 | 改动 |
|---|---|---|
| 1 | `Models/OnboardingPlan.cs` | 新增：`OnboardingStep` 记录、`Build()` 步骤表、文案常量 |
| 2 | `Services/OnboardingStateStore.cs` | 新增（D 备选时）：`onboarding.json` 读写 |
| 3 | `Services/ConfigManager.cs` | 新增 `WasCreated`；`Load()` 文件不存在分支置 `true` |
| 4 | `ViewModels/MainViewModel.cs` | 暴露 `IsFreshInstall` |
| 5 | `Views/OnboardingOverlay.xaml` / `.cs` | 新增覆盖层控件及 `ShowStep` / `Hide` |
| 6 | `Views/OnboardingController.cs` | 新增状态机与测量调度 |
| 7 | `Views/MainWindow.xaml` | 根 Grid 加 `x:Name`；Row 1 追加覆盖层 |
| 8 | `Views/MainWindow.xaml.cs` | 启动触发、`RestartOnboarding()`、暴露 `NavigateTo` 给控制器 |
| 9 | `Views/HomePage.xaml` | 四个入口 Button 补 `x:Name` |
| 10 | `Views/SettingsPage.xaml` | `SettingsScroll`、`RdpUnlockCard` 补 `x:Name`；「关于」卡片加入口按钮 |
| 11 | `Views/SettingsPage.xaml.cs` | `OnRestartOnboarding` |
| 12 | `Views/ChannelsPage.xaml` | 「新建通道」Button 补 `x:Name` |
| 13 | `repo/assets/onboarding/add_local_user.png` | 新增截图资产 |
| 14 | `_smoke/OnboardingCheck.cs`、`Smoke.csproj`、`Program.cs` | 新增断言并注册 |
| 15 | 本文件 `docs/onboarding-plan-2026-11-07.md` | 计划书 |

---

## 5. 图片资产

- 源：`C:\Users\AITNR\AppData\Local\PixPin\Temp\PixPin_2026-11-07_13-20-20.png`
- 目标：`repo/assets/onboarding/add_local_user.png`
- 运行时加载：`Path.Combine(AppPaths.AssetsDir, "onboarding", "add_local_user.png")` → `new BitmapImage(new Uri(path, UriKind.Absolute))`。unpackaged 下不能用 `ms-appx:///` 相对路径（与 `MainWindow.xaml.cs:121-125` 的 logo 处理一致）。
- 打包：仓库根 `assets/` 由 `package_release.py` 整目录拷贝，Inno `[Files]` 递归打包，无需改 csproj 或安装脚本。
- 内容：Windows 设置 →「账户」→「其他用户」→「添加账户」→「为这台电脑创建用户」弹窗；红色手写 1/2/3 分别标注账户菜单、其他用户、添加账户按钮。
- **隐私**：原图左上角含本机账户名与邮箱地址（`AITNR@outlook.jp`）。随包分发前须打码或裁切（待确认 E）。
- 第 7 步展示方式：居中卡片内 `Image Stretch="Uniform"`，最大高度受窗口高度约束；图片加载失败时显示替代文字，不阻塞结束。

---

## 6. 文案定稿（写入 `OnboardingPlan`，冒烟按文字锚点断言）

| 步 | 标题 | 正文 |
|---|---|---|
| 1 | 任务执行 | 在这里编排任务：给每条通道安排要运行的脚本与顺序。 |
| 2 | 运行耗时统计 | 在这里查看上一次的运行时间，以及平均执行时间。 |
| 3 | 运行日志 | 在这里查看 Yomori 的运行日志，排查任务与连接问题。 |
| 4 | 通道管理 | 在这里添加并编辑用来执行任务的通道。 |
| 5 | 远程桌面解锁（RDP） | 通道功能依赖本机支持多账户同时远程连接。这里四项检测结果全部显示「通过」（全绿）后，通道才能正常使用。点「解锁 RDP」开始；组件需要联网下载，网络波动可能导致解锁失败，可以多点击几次重试。 |
| 6 | 新建通道 | 在这里添加通道。创建前需要两个前提：本机已完成 RDP 解锁，并且已经添加了 Windows 用户账户。 |
| 7 | 如何添加新用户 | 打开 Windows 设置 → 账户 → 其他用户 → 添加账户；在「为这台电脑创建用户」中选择「我没有这个人的登录信息」→「添加一个没有 Microsoft 账户的用户」，填写用户名与密码后点「下一步」。创建完成后回到 Yomori，把该账户填入通道。 |

气泡常驻元素：进度角标（`n/7`）、「跳过」按钮、提示「点击任意位置继续」。

---

## 7. 与既有工程纪律的冲突检查

1. **跨线程**：引导状态与全部 UI 更新在 UI 线程；控制器不引入 `Task.Run` 续体改绑定。
2. **动画变换残留**：首版零动画，规避 `RenderTransform` 未摘除导致的 `GetLocalTransform` 崩溃。
3. **命中测试**：透明命中层必须有 `Background`；`DimPath` / `FocusBorder` 设 `IsHitTestVisible="False"`，否则点击高亮区不会推进。
4. **ContentDialog 抢输入层**：RDP 解锁会弹测速选节点等对话框；采用推荐方案 B（引导期间不开放真实按钮）即不会并发。若后续改为 B 备选，须先 `Hide()` 覆盖层再允许点击。
5. **动态菜单重建**：`NavigateTo("channel-mgmt")` 不改变通道集合，不触发 `RebuildChannelMenuItems()` 的延后一拍重建，时序上无冲突。
6. **Models 层纯净**：`OnboardingPlan` 只含字符串与枚举，供冒烟直接编译。
7. **特殊启动参数**：引导只在主控端正常启动路径触发，不干扰 `--deploy-agent` 与代理进程。
8. **全屏键盘钩子**：引导在主窗口启动阶段，早于任何 RDP 全屏会话，不与 `WH_KEYBOARD_LL` 路径交叉。

---

## 8. 验证方案

### 8.1 编译

- 主控端：`dotnet build repo/winui3/src/Yomori.App/Yomori.App.csproj -p:Platform=x64`（不带 Platform 会触发 `WindowsAppSDKSelfContained` 架构错）。
- 程序正在运行时会因 pri 锁失败，先完全退出。
- 代理工程：本次改动不涉及 `Services/` / `Helpers/` 的链接复用文件，无需额外 publish；若后续把控制器逻辑下沉到这两处，须补 `dotnet publish Yomori.Agent.csproj -c Release -p:Platform=x64`。

### 8.2 冒烟（新增 `OnboardingCheck.cs`，链入 `OnboardingPlan.cs` / `OnboardingStateStore.cs`）

断言按源码文字锚点写，避免注释误报：

- 步骤数为 7，页面 tag 序列为 `home×4 → settings → channel-mgmt×2`；
- 每步目标 key 与 `x:Name` 命名一致；
- 第 5 步正文含「全绿」「解锁 RDP」「多点击几次」，第 6 步含「已经解锁 RDP」「添加了用户」；
- 第 7 步图片文件名与磁盘资产存在性；
- `OnboardingStateStore` 读写往返、缺文件时默认未完成（D 采用备选时）；
- `ConfigManager.WasCreated` 在临时目录新建配置时为 `true`、读已有配置时为 `false`。

执行 `dotnet run --project _smoke/Smoke.csproj`，**不带** `-p:Platform=x64`；跑完确认 `_smoke/bin/Debug/net8.0-windows/Smoke.exe` 的 mtime 已更新，否则断言跑的是旧产物。`Skip()` 不计失败。

### 8.3 手工验证清单

1. 临时目录删掉 `config.json` 后启动 → 自动弹出，停在第 1 步。
2. 连续点击 7 次 → 每步聚焦正确、第 5 步自动跳设置并把 RDP 卡片滚入视口、第 6 步自动跳通道管理。
3. 任一步点「跳过」→ 覆盖层消失、无残留订阅、下次启动不再自动弹。
4. 设置页「重新观看开始引导」→ 从首页第 1 步重播。
5. 已有 `config.json` 的老配置启动 → 不弹。
6. `--embed-vm` / `--deploy-agent` 启动 → 不弹。
7. 深色 / 浅色主题、窗口缩至 1024×768 时气泡不越界、RDP 卡片超高时挖洞矩形正确裁剪。
8. 目标控件找不到时降级为居中气泡，运行日志留痕，进程不崩。

---

## 9. 风险与降级

| 风险 | 处置 |
|---|---|
| 换页后布局未完成导致测量为 0 或旧坐标 | 强制延后一拍；测量前校验 `ActualWidth/Height > 0`，否则再排一次 |
| `ScrollViewer` 滚动动画未结束时坐标漂移 | `StartBringIntoView()` 后按 `ViewChanged` 或再延后一拍复测 |
| 高亮矩形超出视口 | 与 overlay 视口求交；气泡位置按剩余空间上下翻转 |
| 覆盖层遮挡系统通知 / 更新对话框 | 引导期间不主动触发解锁弹窗；结束态必须先 `Hide()` |
| 图片缺失或被裁剪 | 显示替代文字，不阻塞结束流程 |
| 窗口最小化 / 收进托盘 | 首次启动默认窗口可见；不额外处理最小化场景 |

---

## 10. 实施顺序与交付边界

1. 确认第 0 节六项。
2. 落 `Models/OnboardingPlan.cs` + 冒烟断言，先跑一次冒烟（预期新断言全绿）。
3. 补 `ConfigManager.WasCreated` / `IsFreshInstall` 与状态存储（按 D 结论）。
4. 补各页面 `x:Name` 与设置页入口。
5. 实现 `OnboardingOverlay` + `OnboardingController`，接 `MainWindow`。
6. 图片资产落库（含隐私处理）。
7. 按第 8 节编译 + 冒烟 + 手工清单自测。
8. 汇报结果，**等待指示后才提交**。本次不执行任何 `git commit` / `git push`。

---

## 11. 实施结果（2026-11-07）

按第 0 节确认结果落地，与计划书的偏差如下（均为实现阶段的健壮性补强，不改变需求）：

1. **不引入 `OnboardingStateStore`**：D 采用推荐方案，`WasCreated` 已足够；`Services/OnboardingStateStore.cs` 未创建。
2. **同页步骤即时重画**：`GoTo` 判断页面 tag 未变时下一拍直接渲染，只有跨页才走「定位 → 滚动 → 渲染」两段式；首页四步与最后一步点击后不再等半秒。
3. **定位有限重试**：跨页后 `FindName` 可能落在 namescope 建立之前，`PhaseLocate` 最多重试 3 次，避免设置页 RDP 卡片停在视口外变成「不挖洞」。
4. **滚动稳定时间 260ms → 350ms**，覆盖 `StartBringIntoView()` 的滚动动画。
5. **窗口缩放重算**：覆盖层 `SizeChanged` 通知控制器重画当前步；已有定时器排队时不打断时序。
6. **气泡显式 `MaxWidth`**：Canvas 给子元素无限空间，只靠 `Measure` 入参约束不住宽度。
7. **图片隐私处理**：左上角账户头像 / 账户名 / 邮箱已涂成设置窗口背景色；红色标注、箭头、弹窗完好。资产 `repo/assets/onboarding/add_local_user.png`（1804×1401，157704 字节）。

自测：`dotnet build ... -p:Platform=x64` 0 错误（4 个既有 nullability 警告）；冒烟 `===== 全部通过 =====`、0 FAIL，新增 24 条引导断言全 PASS，强制重建后 `Smoke.exe` mtime 13:54:14 已更新。真机 UI 流程（自动弹出、7 次点击、跳过、设置页重看）需用户启动程序后验证。
