# Yomori · 任务执行页改造：按「执行通道」编排任务 计划书

- **版本**：v1（2026-09-27）
- **状态**：✅ **已拍板并实施**（M0–M5 已落地：编译通过 + 冒烟全过），剩真机走查（§6）。
- **三项待拍板**：2026-09-27 与 AITNR 确认，**全部选 A**（三栏 / 去掉下拉 / 删通道默认移回本地），见 §8。
- **适用范围**：`repo/winui3/src/Yomori.App`（WinUI 3 主工程）
- **起因**：AITNR 反馈「现在是先选任务、再选执行通道，应该改为按执行通道设置任务」。
- **前置**：多会话通道已落地（M0–M6），见 `rdp-multi-channel-plan.md`

---

## 0. 一句话目标

任务执行页从「**一张扁平任务表 + 每个任务各自选通道**」改为「**先选执行通道 → 在这条通道下编排任务**」。
通道是组织单位，任务是通道里的条目；执行顺序、归属、启停都在通道维度上完成。

---

## 1. 现状盘点（代码定位）

| 位置 | 现状 | 问题 |
| --- | --- | --- |
| `Views/TasksPage.xaml` L14–69 | 左侧一张**全局扁平** `ListView`（绑 `VM.Tasks`，按全局 `order` 排） | 本地任务与远程任务混在一张表里，看不出"这条通道一共跑哪些" |
| `Views/TasksPage.xaml` L56 | 每个任务项的第三行小字显示 `ChannelDisplay` | 通道归属靠肉眼扫描小字，任务一多就糊 |
| `Views/TasksPage.xaml` L94–106 | 表单里一个 `ComboBox ChannelBox`（执行通道，选项 = `VM.ChannelChoices`） | 「先选任务、再选通道」的入口就在这里，是本次要拆掉的东西 |
| `ViewModels/MainViewModel.cs` L251–326 | `AddTask` / `DeleteSelectedTask` / `MoveSelectedUp` / `MoveSelectedDown` / `NormalizeOrders` 全在**全局表**上操作 | 上移/下移会跨通道插队：挪的是全局序号，通道内的实际执行次序跟着乱 |
| `ViewModels/MainViewModel.cs` L1421 `RefreshChannelChoices` | 生成下拉选项 + 反查每个任务的 `ChannelDisplay` | 只有"下拉项"这一种消费方式，没有"通道 → 任务"的分组视图 |
| `Models/TaskConfig.cs` L119 `ChannelId` | 落盘 `channel_id`，空串 = 本地执行 | **字段本身没问题，这次不动** |
| `Models/RdpChannelPlanner.cs` L64 `Plan` | 按全局 `order` 升序 → 按 `channel_id` 分桶 → 按**通道配置顺序**决定启动与并发名额 | 执行链路是对的，本次不碰（见 §3.2 等价性论证） |
| `ViewModels/MainViewModel.cs` L1776 `RemoveChannel` | 删通道后引用它的任务留着旧 id，只在日志里说一句"需要到任务页重新选" | 任务会变成「未知通道」被静默跳过执行，界面上还找不到它们 |
| `Views/ChannelsPage.xaml` L22 | 顶部说明写着"任务在「任务执行」页各自选走哪条通道" | 文案要跟着改 |

### 1.1 具体痛点（为什么要改）

1. **看不出全貌**：想知道「1 号机这条通道跑哪些任务、什么顺序」，得逐个点开任务看下拉。
2. **改归属要来回切**：给一条通道排 5 个任务 = 点 5 次任务 + 改 5 次下拉。
3. **顺序容易搞乱**：通道内执行次序其实来自**全局** `order`，而全局表里本地/远程任务交错排列，拖一行动了别处的顺序。
4. **删通道后任务失踪**：变成「未知通道」，执行时被跳过，界面上无处可改派。
5. **并发组语义含糊**：`ScriptRunner` L235–250 只在**本批次任务列表**里扫描相邻同组任务；跨通道同名组根本不会并在一起，界面却没有任何提示。

---

## 2. 决策表（待拍板，见 §8）

| # | 决策点 | 推荐结论 |
| --- | --- | --- |
| **D1** | 落盘结构 | **不变**：仍是 `config.tasks[]` 一张扁平表 + `channel_id`。改造只落在 UI 与视图层 |
| **D2** | `order` 语义 | **落盘值仍是全局唯一整数**；通道内序号由视图层重编显示；排序规则与现在**完全等价** |
| **D3** | 页面布局 | **三栏**（已拍板）：执行通道 / 该通道的任务 / 任务详情 |
| **D4** | 换通道操作 | 「移到通道…」按钮 + 下拉（拖放列为可选增强，本轮不做） |
| **D5** | 删除通道时的任务归属 | 弹确认，三选一：移到本地执行 / 移到指定通道 / 保持未知；**默认移到本地执行** |
| **D6** | 孤儿任务（指向已删通道） | 单列一个「未知通道」分组，可见可改派，不再静默跳过 |
| **D7** | 并发组 | 明确"只在通道内生效"，表单加提示；`Start()` 时跨通道同名组给一行日志（不阻断） |
| **D8** | RDP 总开关关闭时 | 通道项置灰但仍可编排；开始执行时全部按本地跑（与现在 `Plan` 行为一致） |

---

## 3. 设计

### 3.1 数据层：只加视图，不改落盘（D1 / D2）

**不动的东西**：`TaskConfig`（含 `ChannelId` / `ChannelDisplay`）、`RdpChannelPlanner`、`ScriptRunner`、桥协议 `RdpCommand.Tasks`、`config.json` 结构、导入导出。

**新增** `Models/ChannelScope.cs` —— 左侧「执行通道」列表的一项：

```csharp
public sealed class ChannelScope
{
    public string Id { get; init; } = string.Empty;  // "" = 本地执行
    public string Name { get; init; }                // "本地执行（当前会话）" / 通道 DisplayName
    public string Subtitle { get; init; }            // "当前账户会话" / "host / user · 已停用"
    public bool IsLocal { get; init; }               // 本地执行组
    public bool IsUsable { get; init; }              // 本地恒 true；通道 = Enabled && IsConfigured
    public bool IsMissing { get; init; }             // D6：孤儿 id（指向不存在的通道）
    public bool HasCredential { get; init; }
}
```

**`MainViewModel` 新增**（放在现有 `RefreshChannelChoices` 旁边）：

| 成员 | 说明 |
| --- | --- |
| `ObservableCollection<ChannelScope> ChannelScopes` | 首项固定「本地执行」，其后按 `Config.Rdp.Channels` 顺序，末尾是「未知通道」组（有孤儿时才出现） |
| `ChannelScope? SelectedScope` | 当前正在编排的通道；重建后**按 Id 复原选中**（对象会整体重建，不能按引用比） |
| `ObservableCollection<TaskConfig> ScopeTasks` | 当前通道的任务，按 `order` 升序。是 `Tasks` 的**派生视图**，不是第二份数据源 |
| `int ScopeTaskCount` / `bool HasScopeSelection` | 空态与按钮可用性 |
| `void RefreshChannelScopes()` | 重建 scopes + 重算每个 scope 的任务数；**取代**现有 `RefreshChannelChoices` 的调用点 |
| `void RefreshScopeTasks()` | 按当前 scope 过滤 + 排序重填 `ScopeTasks`，并写入每个任务的 `ScopeOrder`（见下） |
| `void AddTaskToScope()` | 新建任务，`ChannelId = SelectedScope.Id`，落到本通道末尾 |
| `void DeleteTask(TaskConfig)` | 从全局表删 + 刷新视图 |
| `void MoveTaskInScope(TaskConfig, int delta)` | **只与同通道内相邻项交换 `order`**，不碰别的通道 |
| `void MoveTaskToScope(TaskConfig, string targetId)` | 改归属 + 落到目标通道末尾 + 重编 `order` |
| `void NormalizeOrdersByChannel()` | 按（本地组 → 通道配置顺序 → 通道内当前相对次序）把 `order` 重编为 1..N 连续唯一 |

`TaskConfig` 只加一个**运行期**属性（不落盘）：

```csharp
[JsonIgnore]
public int ScopeOrder { get; set; }   // 通道内显示序号，由 RefreshScopeTasks() 写入
```

### 3.2 为什么执行顺序不变（等价性论证，重要）

现在 `RdpChannelPlanner.Plan` 的行为：
1. 所有启用任务按**全局 `order` 升序**排成一条序列；
2. 按 `channel_id` **稳定分桶**（桶内保持上一步的相对次序）；
3. 通道之间的启动顺序 / 并发名额 = **通道在 `Config.Rdp.Channels` 里的配置顺序**（`ordered` 遍历），与任务的 `order` 无关。

所以：只要"通道内上移/下移"**只在同通道内**交换 `order`，每个通道拿到的任务集合与内部顺序就与改造前**逐一相同**；`Plan` 一行都不用改。

> 结论：**执行链路零改动**，风险全部集中在界面与视图层。

### 3.3 界面：三栏（D3）

```
┌───────────────┬──────────────────┬────────────────────────────┐
│ 执行通道 240  │ 任务（本通道）300 │ 任务详情            *      │
│               │                  │                            │
│ ● 本地执行  3 │ 1 原神一条龙  ✓  │ 基础设置 / 完成判断 / 进阶 │
│   1 号机 · P2 │ 2 星铁日常    ✓  │ （原表单，去掉通道下拉）   │
│   2 号机 · P3 │ 3 鸣潮收菜    ✗  │                            │
│   未知通道   1│                  │ 所属：1 号机 · Player2     │
│               │ [添加][删除]     │ [移到通道…]                │
│ [通道管理 →]  │ [上移][下移]     │                            │
└───────────────┴──────────────────┴────────────────────────────┘
```

- **左栏**：`ListView` 绑 `VM.ChannelScopes`；每项显示名称 + 副标题 + 任务数；不可用的通道（停用 / 未配账户 / RDP 关闭 / 孤儿）用 `Opacity` + 一行小字标出原因。**每行右侧一个开关＝这条通道本轮要不要执行**（见 §9；本地执行与孤儿组没有开关）。底部「通道管理」跳 `ChannelsPage`。
- **中栏**：绑 `VM.ScopeTasks`；序号列绑 `ScopeOrder`；四项操作按钮只作用于当前通道；序号前有抓手，可按住拖动调整本通道内顺序（见 §10）。
- **右栏**：沿用现有表单（`FormHost`），**删掉 `ChannelBox` 下拉**，改为一行只读「所属：X」+ 一个「移到通道…」按钮（D4）。
- **空态**：当前通道没任务 / 一个通道都没有，各自的 `InfoBar` 提示（本地执行永远存在，所以"没有任何通道"不阻塞使用）。

宽度兜底：窗口最小 960×640 时 `264 + 300 + 间距/padding ≈ 600`，表单区余 ~360px，够用；表单区整体已在 `ScrollViewer` 内。（左栏原为 240，§9 的通道开关占了右侧一列，故加宽。）

### 3.4 删除通道的归属处理（D5）

`RemoveChannel`（L1776）改为：若 `users > 0`，先弹 `ContentDialog` 三选一 —— 移到本地执行（默认）/ 移到指定通道 / 保持未知（这些任务本轮不执行）。确认后再 `SaveConfig()` + `ReloadChannels()` + `RefreshChannelScopes()`。已在跑任务时维持现有"先停止执行"拦截。

### 3.5 并发组语义澄清（D7）

- 表单「并发组」项加 ToolTip：**并发组只在同一个通道内生效，不同通道各跑各的，组名相同也不会并在一起**。
- `Start()` 里：若同一组名出现在 ≥2 个通道，追加一行日志 `⚠ 并发组「X」跨了 N 个通道，只在各自通道内生效`（不阻断，只提示）。

---

## 4. 里程碑

| M | 内容 | 涉及文件 | 可独立验证 |
| --- | --- | --- | --- |
| **M0** | 视图层：`ChannelScope` + `ChannelScopes` / `SelectedScope` / `ScopeTasks` + 增删移/改归属 + `NormalizeOrdersByChannel`；`RefreshChannelChoices` 的调用点统一换成 `RefreshChannelScopes` | `Models/ChannelScope.cs`（新）、`Models/TaskConfig.cs`（+`ScopeOrder`）、`ViewModels/MainViewModel.cs` | 编译通过；冒烟断言分组/排序等价 |
| **M1** | 界面三栏改造：通道列表 / 通道内任务 / 详情表单（去掉通道下拉，加"所属"与"移到通道…"） | `Views/TasksPage.xaml(.cs)` | 编译通过 + 真机走查 |
| **M2** | D4 改归属、D6 孤儿「未知通道」分组、D5 删除通道归属确认 | `MainViewModel`（`RemoveChannel` 等）、`TasksPage.xaml.cs` | 冒烟：改派后 `Plan` 不再产生 `Skipped` |
| **M3** | 提示与文案：并发组 ToolTip + 跨通道日志、通道顺序＝并发名额顺序的说明、`ChannelsPage` L22 文案、通道列表加"去配置任务"跳转 | `TasksPage.xaml`、`ChannelsPage.xaml`、`MainViewModel.Start` | 真机走查 |
| **M4** | 冒烟用例 `_smoke/TaskScopeCheck.cs`：① 分组结果与旧排序逐一相同；② 通道内上移后 `Plan` 的该通道顺序随之改变、别的通道不受影响；③ 改归属后原通道/新通道任务数正确；④ 孤儿任务归入"未知通道"且可改派；⑤ 删除通道归派到本后 `Plan.Skipped` 为空 | `_smoke/`（新文件 + `Program.cs` 注册） | `cd _smoke && dotnet run -c Release` |
| **M5** | 文档：`README` 任务页章节、`USER_GUIDE` 配任务步骤、`CHANGELOG` 条目；真机验收清单 | `repo/winui3/*.md` | 人工 |

---

## 5. 风险与回退

| 风险 | 应对 |
| --- | --- |
| 三栏在真机上偏挤 | 回退到「左通道列表 + 右任务表（表内展开表单）」两栏 —— **VM 层不用改，只改 XAML** |
| `ScopeTasks` 派生视图与 `Tasks` 不同步 | 所有写操作集中在 VM 的六个方法里，方法末尾统一 `RefreshScopeTasks()`；`SaveConfig()` 前统一 `NormalizeOrdersByChannel()` |
| `SelectedScope` 在重建后漂移 | 按 `Id` 复原（与现有 `ReloadChannels` 的做法一致） |
| 旧配置 / 导入的配置 | 结构不变，无需迁移；孤儿 id 由 D6 兜底 |
| 改到一半执行链路被带坏 | M0 只加不改，且 `Plan`/`Runner`/桥协议零改动；M4 冒烟覆盖等价性 |

---

## 6. 验收清单（真机）

1. 打开任务执行页：左侧通道列表首项是「本地执行」，其后是各通道，任务数正确。
2. 选中一条通道 → 中间只列出该通道的任务，序号 1..N 连续。
3. 「添加」的任务直接落在该通道下；「上移/下移」只在本通道内生效，别的通道顺序不动。
4. 「移到通道…」把任务搬到另一条通道，目标通道出现在末尾，两边任务数正确变化。
5. 删掉一条有任务的通道 → 弹确认；选"移到本地执行"后，本地执行组里能看到那些任务，点开始执行不再出现"未知通道"跳过。
6. 开始执行：每条通道的任务集合与顺序与改造前一致（对照旧版日志里的任务名顺序）。
7. 窄窗口（960×640）下三栏不重叠、不出现横向滚动条。

---

## 7. 非目标

- 不动执行链路：`RdpChannelPlanner`、`ScriptRunner`、`RdpChannelSession`、桥协议、`Agent` 侧一律不动。
- 不做任务拖放排序（D4 只做按钮 + 下拉）。
- 不改 `config.json` 结构、不做数据迁移。
- 不新增通道管理能力（通道 CRUD 仍在 `ChannelsPage`）。

---

## 8. 三项决策（2026-09-27 拍板：**全 A**）

| # | 问题 | 结论 |
| --- | --- | --- |
| **Q1** | 页面布局 | **A 三栏**：执行通道 / 通道内任务 / 任务详情 |
| **Q2** | 任务详情里还留「执行通道」下拉吗 | **A 去掉**，换只读「所属：X」+「移到通道…」按钮（菜单现填，含「本地执行」） |
| **Q3** | 删除有任务的通道时默认怎么处理 | **A 弹确认**，默认移到本地执行；另有「移到指定通道」「保持未知」两项 |

### 落地情况

| M | 状态 | 说明 |
| --- | --- | --- |
| M0 | ✅ | `Models/TaskScopePlanner.cs`（纯函数：分组 / 移动 / 改派 / 重编 / 孤儿 / 并发组）+ `Models/ChannelScope.cs`；`TaskConfig` 加 `ScopeOrder`；`MainViewModel` 换成 `ChannelScopes` / `SelectedScope` / `ScopeTasks`（旧的 `ChannelChoices` 与 `ChannelChoice` 类已删） |
| M1 | ✅ | `TasksPage` 三栏重写；`RefreshChannelChoices` → `RefreshChannelScopes` |
| M2 | ✅ | 「移到通道…」、孤儿「未知通道」分组、`RemoveChannelAsync` + `PromptReassignAsync`（归属确认） |
| M3 | ✅ | 并发组 ToolTip 与跨通道日志、`ChannelsPage` 文案与「去任务页编排任务」跳转 |
| M4 | ✅ | `_smoke/TaskScopeCheck.cs`（49 项断言，含**与 `RdpChannelPlanner.Plan` 分桶逐一相同**的等价性）；`Program.cs` 里两条旧的 M4 断言（下拉 / `ChannelDisplay`）已换成新形态 |
| M5 | ✅ | `README` / `USER_GUIDE` / `CHANGELOG` 同步；剩真机走查（§6） |

---

## 9. 后续修订：通道行上的「本轮要不要执行」开关（2026-10-24）

**起因**：AITNR 反馈「为每个通道加上单独的开关来确认是否执行该通道任务」——
原先要停一条通道得切到「通道管理…」，还得先在那条通道的编辑卡里找到「启用该通道」，
在"排任务"的现场看不到、也改不了。

**做法**：左栏每行右侧加一个 `ToggleSwitch`，**写回通道配置里已有的 `enabled`**
（与「通道管理」页的「启用该通道」是同一份，两处永远同步）。执行链路一行没动：
`RdpChannelPlanner.Plan` 本来就跳过停用通道。

| 位置 | 改动 |
| --- | --- |
| `Models/ChannelScope` | 加 `Enabled`（镜像 `RdpChannel.Enabled`）与 `CanToggle`；`Subtitle` / `IsUsable` / `HintText` 由 `init` 改为可写，供**就地刷新** |
| `Models/TaskScopePlanner` | `BuildScopes` 填 `Enabled`；抽出 `IsScopeUsable` / `HintFor`，让"就地刷新"与"重建"共用同一套判定与文案 |
| `MainViewModel` | 新增 `SetScopeEnabled(scope, enabled)`：改通道 `enabled` → `SaveConfig()` → 就地刷这一行 + 记日志 |
| `Views/TasksPage.xaml` | 模板右列加开关（`x:Load="{x:Bind CanToggle}"`）；左栏 240 → 264 |

**三条纪律（改这块时别踩）**：

1. **不能在 `Toggled` 里调 `RefreshChannelScopes()`** —— 那会整体重建集合，而事件里控件
   还绑着刚被换掉的旧对象，正是本项目反复出现的「集合整体替换 × 绑定源失效」崩法。
   故 `ChannelScope` 的那几个展示属性必须保持可写，走就地刷新。
2. **`ChannelScope` 不能引 `Microsoft.UI.Xaml`** —— 冒烟工程把这些 `.cs` 直接 `Compile Include`
   进纯 .NET 工程编译，连 `Visibility` 都不行。所以模板里用 `x:Load`（配 `x:Name`，XAML 编译器
   强制要求）而不是绑 `Visibility`。
3. **本地执行与孤儿组不给开关** —— 本地执行恒执行（拨了也没处写），孤儿组的通道配置已不存在。
4. **`Toggled` 里要读控件自己的 `IsOn`，不能读 `scope.Enabled`** —— `Toggled` 早于 TwoWay
   绑定回写，此刻 `scope.Enabled` 还是**旧值**。第一版就是这么写的，症状是：把开关拨到「开」，
   配置却仍是停用、行上还挂着「通道已停用 —— 本轮不会下发」（用户实测截图）。

---

## 10. 后续修订：抓手拖放调整通道内顺序（2026-10-24）

**起因**：AITNR 反馈「每个通道里面的任务的序号前应该加入一个托盘，可以按住拖动以改变顺序」——
「上移 / 下移」一次只能挪一位，排五六条任务时要点很多下。

**做法**：任务行在序号前加抓手（`FontIcon` U+E76F，两条横线），**指针全由页面自己接管**。
拖动时被拖那行追着指针走（跟手，不补间）、其余行上下让位（120ms `CubicEase/EaseOut` 补间）、
一条提示线标出落点 ——
**三件事全是 `TranslateTransform` / 提示线，集合一个字节都不动**；松手那一刻才 `Move` 一次。

| 位置 | 改动 |
| --- | --- |
| `Models/TaskScopePlanner` | 新增 `ApplyScopeSequence`：只重排**本组已占用的 order 值**（取出来升序、按新次序发回），order 的多重集合不变 → 不会跟别的通道撞号，之后 `SaveConfig()` 里的 `NormalizeOrdersByChannel` 再收敛成 1..N |
| `MainViewModel` | 新增 `MoveTaskInScopeTo(task, targetIndex)`：`ScopeTasks.Move(from, to)` → `ApplyScopeSequence` → `RenumberScope` |
| `Views/TasksPage.xaml(.cs)` | 抓手 `Border`（只挂 `PointerPressed`）；`BeginDragVisual` / `FollowPointer` / `UpdateDropTarget` / `ShiftRows` / `ClearShiftedRows` / `EndDrag`；`DropLine`；4px 起拖阈值；ListView 层 `AddHandler(..., handledEventsToo: true)` 接移动/松开/捕获丢失 |

### 走错的五条路（别再走回头路）

| # | 尝试 | 结果 |
| --- | --- | --- |
| ① | 抓手 `CanDrag="True"` + 自己做 `DragStarting`/`DragOver`/`Drop` | **拖不动**：指针被 `ListViewItem` 的按下/选择逻辑接走，子元素的拖拽手势根本不启动 |
| ② | ListView 内置重排（`CanReorderItems` + `CanDragItems` + `ReorderMode`） | **同样按不动**（本页面上这套机制不生效） |
| ③ | 自己接管指针 + **拖动中实时 `Move`** | 能拖了，但**拖过一行就断开**：`Move` 让 ListView 回收/重建那一行的容器 |
| ④ | ③ 但捕获挪到 `ListView` 上 | 仍然断 —— 问题不在捕获，在"行被重建" |
| ⑤ | 只松手排序、拖动中只画一条线 | 稳了，但丢了「其他行让位」的观感 |
| ⑥ | **让位改为纯视觉位移**（当前实现） | 两者兼得 |

**六条纪律**：

1. **`PointerPressed` 里必须 `e.Handled = true`，并在 `TaskList` 上 `CapturePointer`** ——
   否则 `ListViewItem` 随后把指针抢回去，`PointerMoved` 一条都收不到；捕获也别挂在抓手 /
   行容器上（它们会被回收重建）。
2. **拖动过程中绝不改集合** —— `Move` 会让 ListView 回收重建行容器，位移与捕获一起失效，
   症状就是「拖过一行就断开、直接跳到新位置」。排序只在 `PointerReleased` 做一次。
3. **「让位」只能用 `RenderTransform`，且要补间** —— 给中间那几行加 `TranslateTransform ±行高`，
   布局与集合都不动。直接赋值会瞬移，观感很硬；用 `DoubleAnimation` 动 `TranslateTransform.Y`
   是**独立动画**（不触发布局，不需要 `EnableDependentAnimation`），120ms + `CubicEase/EaseOut`
   差不多是 Windows 列表重排的手感。**只给 `To` 不给 `From`**：起始值就是当前值，落点连续变化时能顺滑接力。
   ⚠️ `Storyboard` 必须自己保活（存进 List，`Completed` 里再摘），只写局部变量可能在播到一半被回收。
4. **行位置在起拖时缓存一次**（`_rowTops` / `_rowHeight` / `_dragSlotTop`）—— 拖动中布局不变，
   之后落点计算全是纯算术。别边拖边量：`TransformToVisual` 会把元素自身的 RenderTransform
   算进去，量出来的是「已经位移过的位置」，而且和"其他行也被推开了"叠在一起，越算越乱。
5. **插入点要判断方向** —— 「插到第 i 行之前」在抽走被拖项后要减一，否则往后拖永远差一位
   （A B C D 把 A 拖到 C 上半区，期望 B A C D 而不是 B C A D）；落点与原地相同时不画线。
6. **`EndDrag` 先清状态再 `ReleasePointerCapture`** —— 后者会同步抛 `PointerCaptureLost`，
   状态没清就会再跑一遍 `EndDrag`。让位那几行的变换也要在这里还原（先 `Stop()` 动画，
   再把 `Y` 显式落 0 —— `HoldEnd` 会让动画值长期压住属性，不显式落值之后改不动）。

已知副作用：点抓手不再顺带选中这一行（按下被 Handled 掉了）。抓手只有二十来像素宽，
点行内文字照常选中，可以接受。

与「上移 / 下移」一致，顺序改动和其它任务字段一样**改动即落盘**（2026-10-30 起为自动保存：
600 毫秒去抖窗口后写 `config.json`，落盘前统一按通道重编 `order`；退出前 flush，
见《配置自动保存计划书》）。
