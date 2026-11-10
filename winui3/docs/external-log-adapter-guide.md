# 外部脚本实时日志逐脚本深度适配指南

文档版本：2026-11-10
适用范围：`repo/winui3/src/Yomori.App` 的外部脚本实时日志采集与通道页显示
首个适配脚本：March7th Launcher / March7th Assistant（`m7a`，提交 `2d65404`）
第二个适配脚本：BetterGI 更好的原神（`bgi`，2026-11-10）
第三个适配脚本：MaaEnd（`maaend`，2026-11-10，MaaFW 内核日志专属适配）

## 0. 必要条件（先读）

1. 深度适配**只作用于实时显示**。Yomori 的外部日志归档、`raw` 原文视图必须保持原始行；`node` 节点档保留全部业务行。事后排障要能对上脚本自身保存的日志。
2. 未识别到已适配脚本时，必须**逐行等价**地退回原有粗略链路（`ExternalLogFilter.Parse` → `ExternalLogProjector`）。该分支的语句顺序与逻辑不得改动。
3. 适配器位于 `Models/` 层，为纯逻辑实现：无文件 IO、无 WinUI 依赖、不得出现 `Microsoft.UI.Xaml` 任何类型。冒烟工程以显式 `<Compile Include>` 链接所需 Models 源码，新增文件必须补行。
4. 状态按适配器实例持有。多通道并行时阶段名、节流计数、合并组不得互相污染；`Reset()` 清空全部状态。
5. 解析永不抛异常。不认识的行返回 `ExternalLogProjection.Pass` 原样放行；关键字命中、WARNING、ERROR 不得因降噪规则被丢弃。
6. 输出必须幂等。主控端会对老代理发来的行再投一次，已精简文本不得被二次加工。
7. 先取得真实日志样本并完成行形状统计，再编写规则。每条规则须能指向样本中的实测行。
8. 修改代理链接复用的 `Models/`、`Services/`、`Helpers/` 后，必须给 `Yomori.Agent.csproj` 的 `<AgentVersion>` +1。否则升级时代理副本不刷新，新功能不生效且程序侧无异常。
9. 实现类需求先提交计划书；改完先自测（编译 + 冒烟）再汇报；等待指示后才提交。

## 1. 术语与边界

| 术语 | 定义 |
|---|---|
| 粗略适配 | 与脚本无关的通用处理：`launch_presets.json` 的日志位置/模式/编码预设，`ExternalLogFilter` 的去转义、规整、级别识别与关键字命中，以及非 MaaFramework 日志的原样放行 |
| 深度适配 | 针对某个脚本的日志文法，把原始行解释成阶段、步骤、结果、异常，并重写成单行短句 |
| 面板档 / 节点档 / 原文 | `panel`（默认，精简）/ `node`（全业务行，排障）/ `raw`（原始行）三档视图 |
| Compact 行 | 由深度适配器产出、界面以单行渲染的日志行 |
| 适配器注册表 | 白名单式脚本识别，返回 `null` 即表示走粗略兜底 |

非目标：不改脚本自身日志内容、不改 Yomori 归档格式、不新增通知、不改变任务完成判定与清理名单、不改动其它脚本的既有行为。

## 2. 现状架构

### 2.1 三层职责

| 层 | 位置 | 职责 |
|---|---|---|
| 注册表与接口 | `Models/ExternalLogAdapter.cs` | 声明 `ExternalLogAdapterHint` / `IExternalLogAdapter` / `ExternalLogAdapterRegistry`，按线索判定脚本 |
| 脚本适配器 | `Models/March7thLogAdapter.cs` 等 | 该脚本的文法、清洗、节流、阶段状态；状态按实例持有 |
| 分派点 | `Services/ExternalLogTail.cs::AddLine` | 读到的每一行先问注册表，命中走适配器，未命中走原粗略链路 |

### 2.2 数据流

```
脚本日志文件
  → ExternalLogTail 逐行读（BOM / 编码 / \r 进度刷新 / 超长行）
  → ExternalLogFilter.Parse（规整、级别、关键字）
  → [view == raw 时原样返回]
  → 注册表：显式线索 → 内容探针（前 32 行）
  → 命中：adapter.Project(raw) → ExternalLogLine（Compact / SourceTime / NodeText=原始行）
    未命中：ExternalLogProjector.Project（MaaFW 投影或 Pass），逐行等价
  → ScriptRunner 发 TaskExternalLogLine
  → ① 归档（写 NodeText，保持原始行）
    ② RdpAgentRunner（远程）/ MainViewModel（本地）过桥或直达
  → RdpLiveLog → RdpChannelSession.ExternalLogs
  → RdpChannelPage 单行或双行模板
```

### 2.3 与 MaaFramework 投影器的关系

`ExternalLogProjector` 是 MaaFW 内核日志的深度投影器，依赖 `[msg=Tasker.*]` 事件与 `details.focus`。它对三月七、BetterGI 等脚本不适用，因此这些脚本原先一律 `Pass`。逐脚本适配器与它并列：注册表命中时用脚本适配器，未命中且判定为 MaaFW 时用原投影器，两者都不命中时 `Pass`。两者共用 `ExternalLogProjection` 与 `ExternalLogKind` 符号体系（`▶ ✔ ✘ ▸ ⚠ ⋯`），保证界面观感一致。

## 3. 契约

### 3.1 Models 层 API

```csharp
public sealed class ExternalLogAdapterHint
{
    public string PresetId   { get; init; } = string.Empty;  // "m7a:main"
    public string EntryName  { get; init; } = string.Empty;  // "March7th Launcher.exe"
    public string LogPath    { get; init; } = string.Empty;
    public string LogPattern { get; init; } = string.Empty;  // "20??-??-??.log"
}

public interface IExternalLogAdapter
{
    string Id { get; }
    ExternalLogProjection Project(string rawLine, long nowMs);
    ExternalLogProjection? Tick(long nowMs);
    void Reset();
}

public static class ExternalLogAdapterRegistry
{
    public const string March7thId = "m7a";
    public const string BetterGIId = "bgi";
    public static IExternalLogAdapter? Select(ExternalLogAdapterHint? hint);
    public static IExternalLogAdapter? DetectByContent(IReadOnlyList<string> lines);
}
```

`ExternalLogProjection` 既有成员保持默认：`Emit` / `UseRaw` / `PanelText` / `NodeText` / `Kind` / `Drop` / `Pass` / `New` / `PanelOnly` / `NodeOnly`。深度适配新增 `bool Compact`、`string SourceTime`，以及工厂 `CompactLine(panel, node, kind, sourceTime)`；`node` 传原始行，`sourceTime` 传脚本自带的 `HH:mm:ss`。

第二期（BetterGI）追加两个**可选、默认空**的字段，只被已适配分支读取：`ExternalLogLevel? Level`（适配器从头文件带来的真实级别）与 `bool MetaOnly`（结构/元数据行，语义已并入紧随的业务行），以及静态工厂 `MetaDrop`。原因见 §3.2 与 §11.2：BetterGI 的级别在头行、正文在下一行，跨行合成后当前行的 `Classify` 恒为 `Unknown`。既有静态工厂的默认值不变，MaaFW 投影与未适配分支逐行不受影响。

`ExternalLogLine` 新增 `bool Compact`、`string SourceTime`；`ArchiveText` 仍优先取 `NodeText`。

### 3.2 采集侧分派

`ExternalLogTail` 构造函数追加第 8 个可选参数 `ExternalLogAdapterHint? adapterHint = null`。`AddLine` 顺序不可调整：

1. `ExternalLogFilter.Parse` 得到 `Level` 与 `KeywordHit`；
2. `view == raw` 原样返回；
3. 未绑定适配器时把前 32 行喂给 `DetectByContent`，命中即绑定，不回溯改写已上屏的行；
4. 未命中时执行原封不动的 MaaFW 投影链路；
5. 命中时调用 `adapter.Project`：`UseRaw` 走粗略输出；`!Emit` 时**关键字命中无条件原样放行**，Warn/Error 仅在 `MetaOnly` 为 false 时强制原样放行，其余丢弃；成功则构造带 `Compact` / `SourceTime` / `Raw` 的 `ExternalLogLine`，`Level` 优先取 `adapted.Level ?? line.Level`。

`Tick()` 返回 `IReadOnlyList<ExternalLogLine>`，循环排空适配器内部队列；`ScriptRunner.ExternalLogTailLoopAsync` 用 `foreach` 消费。单次只返回一条会积压输出并可能扣住最后一行。

### 3.3 桥接与界面

`RdpLiveLog` 追加 `bool Compact`、`string Kind`（追加式，`RdpLiveLogProtocol.Current` 保持 1；老端不写即默认 false / 空串）。Compact 行上屏文本不套 `ExternalLogText` 的 `[警告]/[错误]` 前缀，级别改由 `✗ / ⚠` 与数据里的 `Level`（供筛选器）承载；`Time` 优先取 `SourceTime`。

`RdpLogRow` 的 Compact 分支置空 `Task` 与 `Level`，正文 `NoWrap` + `CharacterEllipsis`，元信息行折叠；非 Compact 分支保持原映射与 `Wrap` / `None`，逐像素不变。

### 3.4 兼容矩阵

| 代理 | 主控 | 结果 |
|---|---|---|
| 新 | 新 | 深度适配单行形态 |
| 旧 | 新 | 老行无 `Compact` → 两行旧形态，不崩 |
| 新 | 旧 | 老主控忽略 `Compact` → 两行旧形态，不崩 |
| 旧 | 旧 | 现状 |

## 4. 识别机制

按优先级判定，任一命中即绑定适配器；全部不命中返回 `null`：

| 优先级 | 信号 | 示例 |
|---|---|---|
| 1 | `TaskConfig.LaunchPresetId` 的 `:` 前段 | `m7a:main` → `m7a`；`bgi:oneDragon` → `bgi` |
| 2 | 入口可执行文件名 | `March7th Launcher.exe` / `March7th Assistant.exe`；`BetterGI.exe` |
| 3 | 日志路径 + 文件名模式 | `…\March7thAssistant_v2.7.0\logs` + `20??-??-??.log`；`…\BetterGI\log` + `better-genshin-impact*.log` |
| 4 | 前 32 行内容探针 | March7th：标准行 + 框线；BetterGI：≥2 条带 `BetterGenshinImpact.` 模块名的头行 + ≥1 条正文，且无 March7th 标准行与 MaaFW 标记 |
| 5 | 以上都不成立 | 粗略兜底 |

三条判定纪律：注册表只登记已适配脚本，未登记一律 `null`；适配器在 tail 实例构造时绑定一次，整份文件生命周期内不切换；内容探针只做加法，文件刚建立时不提前否定，切换前的行不回溯改写。

## 5. 新脚本接入步骤

1. **采集样本**：取得该脚本真实运行的日志文件，记录版本与路径。
2. **统计形状**：执行技能 `scripts/profile_log.py <日志文件>`，得到形状分布、级别分布、高频正文，把统计表写进计划书。
3. **编写计划书**：给出深度适配后的日志形式（前后对照）、规则表、验收判据、待裁定项；等待确认。
4. **实现适配器**：复制技能 `assets/AdapterTemplate.cs` 为 `Models/<Script>LogAdapter.cs`；文法常量追加到 `Models/ExternalLogGrammar.cs` 的独立静态类。
5. **登记识别**：在注册表中加 id 与四类信号，补 `DetectByContent` 的特征与排除标记。
6. **接入分派**：`ScriptRunner.StartExternalLogTail` 组装 hint；确认 `ExternalLogTail` 的三层分派对未适配脚本等价。
7. **确认双端透传**：`RdpModels` / `RdpAgentRunner` / `RdpChannelSession` / `MainViewModel` 的 `Compact` / `Kind` / `SourceTime` 透传完整；归档仍写原始行。
8. **界面**：确认 Compact 行单行渲染，非 Compact 行逐像素不变。
9. **冒烟**：复制技能 `assets/AdapterSmokeTemplate.cs` 为 `_smoke/<Script>AdapterCheck.cs`，内联样本，注册到 `Program.cs` 并补 `Smoke.csproj` 的 `<Compile Include>`。
10. **自测与部署**：编译、单独发布代理、跑冒烟；`<AgentVersion>` +1；更新本文档与 `live-script-logs.md`；汇报并等待提交指示。

## 6. 规则设计方法

### 6.1 从统计到规则

形状分布决定规则优先级。以 March7th 为例，纯分隔线占 17.3%、过程类重复行约占三分之一，因此先处理结构行与节流，再处理正文清洗。每条规则须在计划书里标注对应的实测行。

### 6.2 规则优先级

1. 结构行：阶段开始、完成、子步骤、纯分隔符丢弃；
2. 级别行：ERROR 与 WARNING 必出，不参与去重与节流；
3. 正文清洗：路径压缩、机制噪声丢弃、版本号与状态值去重、连续重复行去重；
4. 节流：高频过程行、同类告警折叠，窗口到期由 `Tick` 补一条汇总。

### 6.3 输出形态规范

每条日志占一行，形如 `HH:mm:ss  语义正文`。不显示脚本名、级别文字、正文自带时间与级别、纯分隔符号。符号沿用既有 MaaFW 面板档：

| 语义 | 前缀 | `ExternalLogKind` |
|---|---|---|
| 阶段开始 | `▶ `（停止用 `■ `） | `TaskStart` |
| 阶段完成 | `✔ ` | `TaskDone` |
| 子步骤 / 小节 | `▸ ` | `Node` |
| 普通正文 | 两空格缩进 | `Notice` |
| 告警 | `⚠ ` | `Warn` |
| 错误 | `✗ ` | `TaskFail` |
| 心跳 / 节流汇总 | `⋯ ` | `Heartbeat` |

级别数据保留在 `Level` 字段供筛选器使用；颜色维度可用 `Kind` 透传实现，但不得把 `Foreground` 绑定到可能为 `null` 的画笔。

### 6.4 状态、合并与幂等

- 多行消息以冒号结尾时先扣住，后续续行追加，遇任何非续行或超时收尾成一行。使用输入队列 + 输出队列 + `Tick` 收尾，不得使用「暂存上一拍再交换」的方案（会扣住流末尾最后一行）。
- 结构行永远不能被当作续行合并。
- 适配器开头识别已精简形态，直接 `Pass`，保证幂等。
- `Reset()` 清空全部状态；两个实例交替喂行时阶段名与计数不得串台。

## 7. 归档、视图与降级

- 归档写 `NodeText`：深度适配行传原始行，因此 `external_logs/<通道>/<日期>.log` 与脚本自身日志一致。
- `raw` 视图在 `AddLine` 最前面返回，不做任何投影；`node` 视图保留全部业务行，仅去纯分隔符。
- 关键字命中与 Warn/Error 在被降噪丢弃时由分派层强制原样放行。
- TRACE/DEBUG 归档闸、`YOMORI_EXTERNAL_LOG_VERBOSE=1` 逃生口、级别筛选、清空显示、跟随底部等行为不变。

## 8. 验证

### 8.1 构建命令

```
dotnet build repo/winui3/src/Yomori.App/Yomori.App.csproj -c Release -p:Platform=x64
dotnet publish repo/winui3/src/Yomori.Agent/Yomori.Agent.csproj -c Release -p:Platform=x64
dotnet run --project 整合/_smoke/Smoke.csproj
```

主控不带 `-p:Platform=x64` 会报 `WindowsAppSDKSelfContained` 架构错。冒烟**不带** Platform：带 Platform 只更新 `bin\x64\…`，实际执行的仍是 `bin\Debug\net8.0-windows\Smoke.exe` 旧产物。跑完确认 `Smoke.exe` 的 mtime 更新。`Services/` / `Helpers/` 是链接复用到代理工程，改完必须单独发布代理。

### 8.2 冒烟断言清单

至少覆盖十组：识别（四类信号 + 未登记 + 内容探针反例）、兜底等价、形状、字段（`Compact` / `SourceTime` / `NodeText`）、必出（ERROR/WARNING/关键字）、清洗、fail-open、幂等、状态隔离、压缩率。判据按源码文字锚点写，不跟实现走；首跑红先检查判据；`Skip()` 不算失败。

### 8.3 验收判据

1. 面板输出不含脚本名、级别文字、重复时间、纯分隔符；
2. 每条日志占一行，窄栏不折行；
3. ERROR 与 WARNING 全部保留，关键字命中行不被节流吞掉；
4. 未识别脚本逐行输出与改造前一致，差异数为 0；
5. `raw` 视图与归档逐行等于原始日志；
6. 远程通道新旧主控/代理混跑不崩、不丢行；
7. 主控编译、代理发布、冒烟全绿。

## 9. 部署与版本

`AgentAutoDeploy` 用 `RdpSessionService.IsAgentPayloadCurrent()` 判断是否跳过代理部署，判据是随包代理 exe 与已部署 exe 的 FileVersion，唯一来源是 `Yomori.Agent.csproj` 的 `<AgentVersion>`。项目源码注释已说明：改了代理代码却忘了 +1，升级后代理不会替换。

部署顺序：`<AgentVersion>` +1 → 发布版本 → 用户升级（安装器或 `AgentAutoDeploy` 重铺载荷）→ **注销目标账户重连**。磁盘文件替换后，内存中的旧代理不会自行更换。若升级时任务正在执行，`StopAgentProcess` 会拒绝部署，需先「停止执行」，再到「通道管理」点「部署会话代理」，最后注销重连。`EnsureAgentPayload` 在载荷被占用时会退到备用槽 `%ProgramData%\Yomori\agent_b` 并改自启项。

排障须知：判断 DLL 是否包含新代码用二进制字节搜索或 `VersionInfo.ProductVersion` 尾 commit hash；Git Bash 无 `strings`，`strings dll | grep -c` 会静默得到 0；CI 产物的 mtime 是 UTC，不可作为新旧判据。

## 10. 参考实现：March7th（m7a）

### 10.1 实测统计

样本 `C:\Program Files\March7thAssistant_v2.7.0\logs\2026-11-05.log`，取 **2026-11-05 21:38 的 497 行快照**（该日志在 22:42 后又追加一轮，现为 555 行；接入新脚本须重新切样本并复算）：标准行 389（INFO 353 / WARNING 25 / ERROR 11）、框线 42、框内标题 21、完成线 19、横线子步骤 19、等号小标题 6、续行 1。`切换到：…` / `当前界面：…` 合计 178 行（约 36%）；winotify 通知 25 行、config-watch / config-save 11 行。该快照压缩至约 281 行（43%），冒烟固定样例 38 → 19 行。

### 10.2 规则表

| 输入形状 | 输出 |
|---|---|
| `+---+` 框上/下边、纯分隔符 | 丢弃 |
| `\|  标题  \|` | `▶ 标题`；`停止运行` → `■ 停止运行` |
| `---- 完成 ----` | `✔ 完成：<最近阶段名>` |
| `==== 标题 ====` / `---- 标题 ----` | `▸ 标题`；批量刷副本压成 `▸ 刷 X（N轮×M次）` |
| `yyyy-MM-dd HH:mm:ss,mmm \| INFO \| 正文` | `  正文`（剥日期、毫秒与级别） |
| 同上 WARNING / ERROR | `⚠ 正文` / `✗ 正文` |
| 无时间戳续行 | 并入上一条以冒号结尾的正文 |
| 不认识的行 | 原样放行 |

清洗：绝对路径压成文件名；`GitHub 发现新版本: vX` → `发现新版本 vX` 且同版本只首条；winotify / config-watch / config-save 丢弃；界面状态与识别失败告警 30 秒窗口节流；`开拓力/沉浸器 N/N` 同值只首条；2 秒内同文去重。

### 10.3 前后对照

改造前（界面渲染形态，取自 22:43 通道页截图；每条两行，时间、级别、脚本名三重重复，框线在窄栏折行）：

```
21:32:05  March7th Assistant.exe  信息
+------------------------------
|            开始运行          |
+------------------------------
21:32:05  March7th Assistant.exe  info
2026-11-05 21:32:05,502 | INFO | 游戏启动：D:\mhy\Star Rail Game
\StarRail.exe
```

改造后（按 `_lastHms` 规则与适配器实际输出复算）：

```
21:31:57    发现新版本 v2026.10.31
21:31:57  ▶ 开始检测更新
21:32:05    发现新版本：v2026.10.16  ——→  v2026.10.31
21:32:05  ✔ 完成：开始检测更新
21:32:05  ▶ 开始运行
21:32:05    启动游戏 StarRail.exe
21:32:15    窗口已切换到前台
21:32:15  ✔ 完成：开始运行
21:32:15  ▶ 获取最新兑换码
21:32:48    没有找到有效的兑换码
21:32:48  ✔ 完成：获取最新兑换码
21:32:48  ▶ 开始获取培养目标
21:32:48    当前界面：主界面
21:32:58    识别到副本: ('侵蚀隧洞', '密伶之径')
21:33:01    识别到副本: ('侵蚀隧洞', '霜风之径')
21:33:07    识别到副本: ('饰品提取', '浴火钢心')
21:33:07    培养目标真珠的待刷副本: 侵蚀隧洞 - 密伶之径, 霜风之径 饰品提取 - 浴火钢心
```

告警与错误的实际形态（取自同日相邻片段）：`21:34:43  ⚠ 未识别出界面，按 ESC 重试`、`21:36:50  ✗ 检测到该次副本未正常运行，重试：1/3`。ERROR 分支只做路径压缩，不改写正文。

### 10.4 已知偏离

续行在该版本带时间戳，故 `INFO` + 列表形状也并入合并组；框内标题保留原题；WARNING 仅对识别失败折叠；`Tick` 合成行无原始行，`NodeText` 用摘要本身；颜色绑定未启用，语义由符号承载；首期未引入专用心跳。完整偏离清单与待裁定项见计划书 §12 与技能 `references/march7th-example.md`。

## 11. 参考实现：BetterGI（`bgi`，2026-11-10 第二期）

### 11.1 形态与实测统计

BetterGI 的日志是 Serilog 风格的**三行一组**：头行 `[11:54:39.123] [INF] [Primary:S2:P70396:T…] BetterGenshinImpact.Service.ScriptService`（只有元数据、无消息体）、中文正文行、空行。样本 `C:\Program Files\BetterGI\log\better-genshin-impact20261110.log`（0.66.0）固定前 9,726 行：头行 / 正文 / 空行各 3,242 行（各 33.3%）；级别 `INF 2,453 / DBG 745 / WRN 28 / ERR 16`；`GameTask.Common.TaskControl` 占 83.8%。高频正文以 `"角色名" 点按元素战技，cd:N.N 秒`（1,382 行）、`到达路径点附近`（255）、`粗略接近途经点`（248）为主，过程行合计约 86%。异常堆栈会出现 6 / 9 / 12 行的多行正文组。

### 11.2 跨行组状态机（与 March7th 的根本差异）

采集层 `AddLine` 会先把空行丢掉，适配器看到的流是「头行 / 正文 / 头行 / 正文 …」，**组边界只认头行**。做法：头行到达时收尾上一组、缓存 `hms` / `lvl` / 模块并返回 `MetaDrop`；紧随的正文行借该元数据合成一条 Compact 行（`SourceTime` = 头行时间、`NodeText` = 正文行原文）；组内后续行只进归档（`PanelText` 为空），组收尾补一条 `⋯ 后续 N 行（原文见归档）`。输入 / 输出双队列 + `Tick` 排空，超时未配对的头行由 `Tick` 原文放行，绝不静默丢行。

**两个必须处理的分派层冲突**：

1. 输出落在正文行 → `ExternalLogLine.Level` 恒为 `Unknown`，「只看错误」筛选器与 TRACE/DEBUG 归档闸错配 → 由 `ExternalLogProjection.Level` 借用头行级别，`AddLine` 已适配分支用 `adapted.Level ?? line.Level`。
2. 头行 `[WRN]` / `[ERR]` 经 `Classify` 得到 Warn/Error → `Drop` 会被强制原样放行，推上一条「只有类名」的空壳行 → 由 `MetaOnly` 标记跳过级别强制放行；**关键字命中仍无条件放行**。

### 11.3 输出规则摘要

| 输入 | 输出 |
|---|---|
| 头行 | 不单独上屏（`MetaDrop`），元数据借给正文行 |
| `→ "任务启动！"` / `一条龙任务执行: 1/8` / `→ 开始执行JS脚本: "X"` / `→ 开始执行地图追踪任务: "X"` | `▶ …` |
| `→ "任务结束"` / `→ 脚本执行结束: "X", 耗时: …` / `配置组 "X" 执行结束` | `✔ …（耗时）` |
| `配置组任务执行: N/M` / `配置组 "X" 加载完成…` | `▸ …` |
| `WRN` / `ERR` 正文 | `⚠ …` / `✗ …`（必出，不去重不节流） |
| `[ONNX]` / `[GpuAuto]` / 窗口与配置加载等一次性启动行 | 丢弃 |
| 高频过程行 | 30 秒窗口按类折叠，`Tick` 补 `⋯ 类名 ×N（最近：…）` |
| `------------------------------` / `==== … ====` | 丢弃 |
| 不认识的孤立行（无 pending 头、不在组内） | `Pass` 原样放行 |

完整规则表、实测统计、前后对照与待裁定项见 `整合/外部脚本实时日志深度适配计划书-BetterGI.md`。冒烟 `_smoke/BetterGIAdapterCheck.cs` 覆盖十组判据。

### 11.4 已知偏离与实现取舍

1. 多行异常组的后续行只进归档、不上面板（面板由首行 + 摘要承载），保证归档逐行等于脚本自身日志；ERR/WRN 首行仍必出。
2. 头行作为结构行不单独归档，`NodeText` 取正文行原文（与 March7th 丢 ASCII 框线同类）；`raw` 视图仍逐行保留全部原始行。
3. 幂等判定收紧为「恰好两空格 + 非空白字符」，且只在「不在组内」时生效 —— 否则异常堆栈里 `   at …` 的三空格缩进续行会被误判成已精简文本短路，组计数少算。
4. `<AgentVersion>` 由 `2.1.1` 升到 `2.1.2`；部署后仍需注销目标账户重连。

## 12. 待适配脚本

《外部脚本实时日志降噪计划书》§3.6 调研了五个非 MaaFramework 族；三月七（第一期）与 BetterGI（第二期）已适配，其余三族的结论可直接作为规则起点：

| 族 | 现状与处理要点 |
|---|---|
| BetterGI | **已适配（§11）**：三行一组、跨行借头行级别、启动噪声丢弃、过程行折叠 |
| ok-script 系 | 中文任务行 + 大量 http / 截图；丢 `ok.http` / `screenshot` / 心跳，留任务、完成、错误 |
| 一条龙 | 中文，高频「点击 xxx」；折叠连续点击行，保留任务轮次与退出码 |
| AUTO-MAS | `时间 \| 级别 \| 模块 \| 消息`，混 `POST /api/...`；丢 API 请求行，保留配置 / 进程 / 任务 |
| MaaFramework 系（MaaEnd 除外） | 仍由 `ExternalLogProjector` + `LogLabelStore` 深度投影，保持两行旧形态 |
| MaaEnd | **已单独适配（§12.1）**：`maaend` 族专属适配器，单行 Compact 形态；其它 MaaFW 项目不受影响 |

各族没有 `focus` 与 `label` 机制，面板档与节点档差异主要在「是否折叠连续点击 / 一次性初始化行」。

### 12.1 参考实现：MaaEnd（`maaend`，2026-11-10 第三期）

MaaEnd 是 MaaFramework 系里唯一走专属适配的项目：`debug\maafw.log`（MaaFW 5.x 内核日志）由 `MaaEndLogAdapter` 处理，产出单行 Compact 形态；其余 MaaFW 项目仍用 `ExternalLogProjector`。固定 30,000 行快照里 100% 是单行内核行、TRC+DBG 占 79%，语义在 `[msg=…]` 事件与 `[details={…}]` 的 `entry` / `name` / `focus` 里。

三个与前两期不同的实现要点：

1. **中文标签注入**：新增可选接口 `IExternalLogLabelAware.SetLabels(LogLabelMap)`，由 `ExternalLogTail.BeginLoadLabels` 在异步装载完成后透传给适配器；不改 `IExternalLogAdapter` 签名。任务名取 `Resolve(entry)`，节点名取 `ResolveNode(name)`。
2. **内容探针不启用**：MaaFW 内核日志跨项目同形，探针一命中会把 45+ 项目一起劫持。只认 `maaend:*` / `MaaEnd.exe` / 日志路径含 `MaaEnd` 三类显式线索；线索缺失时退回原投影器（fail-open）。`maafw.log` 是通用文件名，不能当独立信号。
3. **专属改进**：`MaafwDetails.SingleFocus()` 补回「focus 是单键对象且键不等于当前事件」的中文提示；WRN 首条必出、同类每满 100 条再出一条，其余只进归档（用 `CompactLine("", 原文, …)` 而非 `Drop`，否则已适配分支会按 Warn 级别强制原样放行）；心跳与日志中断由适配器 `Tick` 自补。

`ExternalLogProjector` 的投影逻辑、`ExternalLogFilter`、桥协议（仍为 1）与界面模板均未改动。完整规则表、前后对照与实测统计见 `整合/外部脚本实时日志深度适配计划书-MaaEnd.md`。

## 13. 检查清单（提交前逐项确认）

- [ ] 计划书已给出深度适配后的日志形式与前后对照
- [ ] 真实样本统计表已附
- [ ] 适配器在 `Models/` 层、无 WinUI 依赖、状态按实例、fail-open、幂等
- [ ] 注册表为白名单，未登记脚本返回 `null`
- [ ] 未适配分支逐行等价，粗略链路未改动
- [ ] 归档与 `raw` 视图保持原始行，`node` 视图保留业务行
- [ ] ERROR / WARNING / 关键字命中强制放行
- [ ] 第二期：`Level` / `MetaOnly` 只影响已适配分支；关键字命中无条件放行，`MetaOnly` 只跳过级别强制放行
- [ ] 第二期：头行不单独上屏、多行堆栈逐行进归档、幂等判定不误吃三空格缩进续行
- [ ] 双端透传完整，`RdpLiveLogProtocol.Current` 保持 1
- [ ] 非 Compact 行界面逐像素不变
- [ ] 冒烟覆盖十组判据且全绿；`Smoke.exe` mtime 已更新
- [ ] `<AgentVersion>` 已 +1
- [ ] 本文档已补新脚本条目，`live-script-logs.md` 已修正「原样显示」类描述
- [ ] 第三期：`IExternalLogLabelAware` 只影响已适配分支；MaaEnd 未启用内容探针；`Reset()` 保留注入的标签
- [ ] 第三期：WRN 折叠用「面板为空、原文进归档」而非 `Drop`；心跳由适配器 `Tick` 自补
- [ ] 编译、代理发布、冒烟结果已汇报，等待提交指示

## 附录 A：代码锚点（按方法名定位，不写行号）

| 位置 | 事实 |
|---|---|
| `Services/ExternalLogTail.cs::AddLine` | 分派点；顺序为 Parse → raw 视图 → 内容探针 → 粗略链路 / 适配器 |
| `Services/ExternalLogTail.cs::Tick` | 返回集合，排空适配器内部输出队列 |
| `Services/ScriptRunner.cs::StartExternalLogTail` | 组装 `ExternalLogAdapterHint` |
| `Models/ExternalLogAdapter.cs` | 接口、Hint、注册表 |
| `Models/March7thLogAdapter.cs` / `Models/BetterGILogAdapter.cs` / `ExternalLogGrammar.cs` | 两个脚本适配器与各自文法 |
| `Models/ExternalLogProjector.cs`（顶部） | `ExternalLogProjection` 定义处 |
| `Services/ExternalLogFilter.cs` / `ExternalLogModels.cs` | 粗略适配与数据结构 |
| `Services/RdpAgentRunner.cs::OnExternalLogLine` / `ViewModels/MainViewModel.cs::DrainLocalLiveLogs` | 归档与上屏透传 |
| `Views/RdpChannelPage.xaml(.cs)` | `RdpLogRow` 与单行模板 |
| `Services/AgentAutoDeploy.cs` / `RdpSessionService.cs::IsAgentPayloadCurrent` | 代理替换判据 |
| `_smoke/Smoke.csproj` | 显式 `<Compile Include>` 列表，新增文件必须补行 |

## 附录 B：相关文档与工具

| 资源 | 位置 |
|---|---|
| 首期计划书 | `整合/外部脚本实时日志深度适配计划书-March7th.md`（仓库外） |
| 第二期计划书 | `整合/外部脚本实时日志深度适配计划书-BetterGI.md`（仓库外） |
| 降噪计划书（含各族调研） | `整合/外部脚本实时日志降噪计划书.md`（仓库外） |
| 用户向功能文档 | `repo/winui3/docs/live-script-logs.md` |
| WorkBuddy 技能 | `~/.workbuddy/skills/yomori-external-log-adapter/`（SKILL.md、契约、踩坑、参考实现、代码模板、统计脚本） |
