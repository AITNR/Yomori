# RDP 解锁下载链路改造计划书：GitHub 加速前缀

- 日期：2026-11-06
- 状态：待评审（本文件不含代码改动，评审通过后实施）
- 适用范围：`repo/winui3/src/Yomori.App/` 的 RDP 解锁下载链路
- 关联文档：`docs/rdp-unlock-review-2026-10-26.md`、`README.md §七`

---

## 一、必要条件与约束

### 1.1 本次已确认的决策

| 编号 | 决策 |
|---|---|
| D1 | 请求经加速前缀重写后，不附 GitHub 访问令牌 |
| D2 | 同意以静态发布元数据绕开 `api.github.com`（与 C2 冲突，见 4.3） |
| D3 | 仅对两个 exe 引入内置 SHA-256 基线；`rdpwrap.ini` 会更新，不引入固定哈希 |

### 1.2 既有硬约束（实施时不得违反）

| 编号 | 约束 | 来源 |
|---|---|---|
| C1 | 两个 exe 不随 Yomori 安装包分发（已被微软报毒）；只在用户确认后按需下载 | 设置页文案、`RdpUnlockService` 门禁 |
| C2 | 每次真正需要下载时强制重新解析 `releases/latest`，**不写死历史版本**、不自比版本号 | `RdpReleaseResolver.cs:19-24, 57-58`；计划书 4.1；检查记录 P1 |
| C3 | 下载地址必须是 HTTPS | `RdpToolDownloader.cs:135-139`；计划书 4.1 |
| C4 | 不猜资产名；缺失或多个无法区分则整体失败 | `RdpReleaseModels.cs:100-107` |
| C5 | 设置项一律为勾选开关；开关被取消或执行失败必须拨回原位 | 项目约定 |
| C6 | 改完先编译 + 冒烟自测再汇报，等指示才提交 | 项目约定 |
| C7 | 代理工程以「链接 + 排除」复用 `Services/**`、`Models/**`；改共享文件须额外发布代理工程 | `Yomori.Agent.csproj:72-89` |
| C8 | 冒烟直接编译 `Models/*.cs`，该层不得出现 `Microsoft.UI.Xaml` 类型 | 项目约定 |
| C9 | 校验能力不足时必须在步骤行如实标注，不得呈现为「已校验」 | `RdpToolDownloader.cs:214-228` |

### 1.3 范围界定

- 加速前缀**只作用于 RDP 解锁链路**：发布解析、两个 exe 下载、`rdpwrap.ini` 下载。
- **不作用于**主程序「检查更新」。该链路有自己的代理与令牌逻辑，本次不碰。
- 不自建国内镜像、不托管任何二进制、不修改 `termsrv.dll`、不做注入。
- 不引入断点续传、不重构重试策略（列为后续议题）。

---

## 二、问题与目标

### 2.1 现状

RDP 解锁的下载链路依赖三个 GitHub 主机：

| 主机 | 用途 | 现有缓解 |
|---|---|---|
| `api.github.com` | 解析 `releases/latest` | 1 小时缓存、过期缓存降级、发布页 tag 兜底、可选 GitHub 令牌 |
| `github.com/.../releases/download` → `objects.githubusercontent.com` | 下载 `RDPW_Installer.exe` / `RDPW_Uninstaller.exe` | 下载代理（系统 / 自定义 / 直连）、资产重试 1 次 |
| `raw.githubusercontent.com` | 下载 `rdpwrap.ini` | 下载代理、用户手动指定、`RDPWInst -w` 回退 |

`RdpReleaseResolver.cs:142` 明确记录「不自动换镜像、不重试到别的源」。

### 2.2 失败分布

用户诊断：主要失败原因是**连不上 GitHub**，不是重试或超时策略。本计划书据此把网络可达性列为首要目标，重试策略放宽降级为后续议题。

### 2.3 目标与非目标

**目标**：在完全不托管文件的前提下，使三个主机的请求都能经国内可达的加速前缀完成，且不扩大凭据与完整性风险。

**非目标**：自建镜像、随包分发、断点续传、重试策略重构、主程序检查更新链路改造。

---

## 三、已核实的上游事实

| 项 | 事实 |
|---|---|
| 上游仓库 | `sebaxakerhtc/rdpwrap`（fork 自 `stascorp/rdpwrap`），Apache-2.0，根目录有 `LICENSE`、无 `NOTICE` |
| 最新发布 | `v1.8.9.9`，2023-07-19 发布，至 2026-11-06 未变；`target_commitish = MOD` |
| 两个附件 | `RDPW_Installer.exe` 2,544,128 B；`RDPW_Uninstaller.exe` 1,359,360 B |
| 官方摘要 | 两个资产的 `digest` **均为 `null`** → 现有 `Sha256FromDigest` 分支实际不可达，只有大小 + PE 校验 |
| INI 仓库 | `sebaxakerhtc/rdpwrap.ini`，master，最后 push 2026-10-07，文件头 `Updated=2026-10-07`，约 577 KB 纯文本 |
| 反代 raw | `gh-proxy.com`、`ghfast.top` 均返回真 INI（`Updated=2026-10-07`） |
| 反代 API | `gh-proxy.com` 反代 `releases/latest` 返回完整 JSON（`v1.8.9.9` + 两个 asset） |
| 反代 release 附件 | 两者均返回 HTTP 206，且 `url_effective` 仍停留在反代域名 → **反代内部消化了 302，未把客户端重定向回 `objects.githubusercontent.com`** |
| jsDelivr | `cdn.jsdelivr.net/gh/sebaxakerhtc/rdpwrap.ini@master/rdpwrap.ini` 实测可用；纯文本配置文件，不触碰条款中关于恶意代码与通用文件托管的禁止项 |
| jsDelivr 条款 | 明文禁止 `malware or harmful code` 与通用文件托管，且缓存过的不可变文件不接受移除请求 → **两个 exe 不得经公共 CDN 分发** |

---

## 四、方案

### 4.1 总览

新增一层「加速前缀重写」，位置在 RDP 解锁链路**发起请求之前**，不改动 `UpdateService.DownloadAsync` 内部。理由是该方法同时被主程序检查更新使用，改内部会越出 1.3 的范围。

```
RdpUnlockService / RdpReleaseResolver / RdpToolDownloader
        │  原始 URL（保持从 GitHub 取得，不改写数据源）
        ▼
RdpAcceleration.Rewrite(url, settings)   ← 本次新增，纯函数
        │  重写后的 URL + 是否经过重写
        ▼
UpdateService.DownloadAsync / HttpClient.SendAsync（原样复用）
```

`RdpAsset.DownloadUrl`、`LatestApiUrl`、`IniDownloadUrl` 的**取值来源不变**，重写只发生在发起请求时。这样 C3（HTTPS 校验）仍校验原始上游 URL，缓存与账本里记录的也仍是官方链接。

### 4.2 加速前缀与候选轮换

- 拼接规则：`{prefix}{原始 URL}`，其中 `prefix` 形如 `https://gh-proxy.com/`。内置候选统一采用该形式（已实测两个候选均支持）。
- 前缀规范化：必须以 `https://` 开头、以 `/` 结尾；拒绝带 query、带 userinfo、非 HTTPS 的输入；解析失败视为未配置，回退官方直连并在步骤行标注。
- 内置候选列表写在常量中，不落配置；运行时按顺序轮换。用户可填自定义前缀，优先级高于内置候选。
- 轮换判据：连接失败、超时、非 2xx、下载后空文件、大小不符、内容被识别为网页。命中任一即换下一个候选。
- 官方直连始终作为最后一个候选保留。
- **每个候选都重写为完整的 `https://` URL**，确保 `RdpToolDownloader.cs:135` 的检查在重写前后都成立。

### 4.3 发布解析链路（D2 与 C2 的冲突，需复核）

**冲突**：D2 要求内置静态发布元数据以绕开 `api.github.com`；C2 明确禁止写死 `v1.8.9.9` 之类的历史版本，并要求每次真正下载时 `forceRefresh: true` 重新解析 latest。`RdpReleaseResolver.cs` 的整条三层降级、`RdpTransactionStore` 的 release ID 锁定、`RdpReleaseModels.cs:55-57` 的注释都建立在此约束上。

**若废止 C2 的代价**：

1. 上游一旦发新版，内置基线立刻过期，用户会装到三年前的 exe 而不自知；更新基线必须发 Yomori 新版本。
2. `forceRefresh` 语义失效，检查记录 P1 的结论被推翻，需要同步修订多处注释与冒烟断言。
3. 与 `RdpReleaseResolver.cs:142`「不自动换镜像」的设计取向相反。

**推荐方案 R1（默认按此执行）**：不静态化主路径。给 `RdpReleaseResolver` 的两条网络请求也套加速前缀 —— 官方 API（`:93`）与发布页 tag（`:214`）。三层降级、`forceRefresh`、release ID 锁定全部保留。由于 3 节已实测反代 API 可用且发布页同为 `github.com` 域，连不上 GitHub 时解析链路仍可走通，同时保持动态跟随上游。

**备选 R2**：按 D2 废止 C2，内置静态 release 元数据，`Source` 标注「内置基线（离线降级）」，并在步骤行明确提示可能不是最新版。此路径需要显式批准并同步修订相关注释与断言。

> 待确认项 Q1：采用 R1 还是 R2。无异议时按 R1 实施。

### 4.4 令牌隔离（D1）

- 新增 `ShouldSendToken(Uri)` 白名单：仅当目标主机为 `api.github.com` **且该请求未经加速重写**时允许附令牌。
- 修改 `RdpReleaseResolver.cs:99-103`：当前代码无条件从 `GitHubTokenStore.Read()` 取令牌并加 `Authorization: Bearer`，不检查目标主机。重写后目标主机变为反代域名，若不加拦截即把凭据递给第三方。
- `UpdateService.GetGitHubAsync`（`:275`）服务主程序检查更新，不在本次范围内（1.3），但仍复用同一白名单以保持语义一致。
- 认证被拒退回匿名（`:287-297`）的逻辑不变；匿名配额 60 次/小时由现有缓存与降级链吸收。

### 4.5 exe 内置 SHA-256 基线（D3）

- 新增静态基线表，键为 `(tag, fileName)`，值为期望 SHA-256。不修改 `RdpAsset`（它是 GitHub JSON 的映射模型），避免污染字段语义。
- 插入点 `RdpToolDownloader.cs:178-191`，位于现有 `Sha256FromDigest` 之后、`InspectPe` 之前：
  - 命中基线 → 强制比对，不符即删除暂存文件并返回明确错误；
  - 未命中（上游发布新版、基线尚未收录）→ 沿用现有大小 / PE / 官方 digest 校验，并按 C9 在步骤行标注「未按内置基线校验」。
- **基线取得方式**：必须经至少两条独立网络路径分别下载并比对一致后才采信，不得只从单一反代下载后直接写入代码。原因是反代属不可信第三方，若其投毒，单源采集会把恶意文件的哈希固化成「可信值」。采集在可直连官方的环境完成，只下载不执行。
- `RdpToolDownloader.cs:219-221` 的 `limits` 文案改写：区分「官方摘要」「内置基线」「仅大小与 PE」三种情形，不得笼统表述为已校验。
- 仅两个 exe 适用；**INI 不设基线**（D3）。

### 4.6 INI 校验策略（D3）

INI 为滚动更新的纯文本，固定哈希会随上游更新立即失效，并迫使每次更新都发版。因此维持并明确以下判据：

- 非空文件、`SniffHead` 识别网页 / XML（拦截页）；
- `IniCovers` 含当前 `termsrv.dll` 构建号段（`:1711`）；
- 记录文件头 `Updated=` 供步骤行与诊断使用；
- 用户手动指定 INI 的校验（`:1268`）不变。

INI 的国内可达路径优先使用 `cdn.jsdelivr.net/gh/sebaxakerhtc/rdpwrap.ini@master/rdpwrap.ini`（纯文本、合规、实测可用），作为加速前缀之外的独立一路；该 CDN 与 exe 通道互不牵连。

### 4.7 与现有下载代理的关系

- `update_proxy_url` 是 HTTP 代理（`WebProxy`），加速前缀是 URL 重写，语义不同，**另开配置项，不复用**。
- 两者可叠加：请求先经本地代理，再访问反代域名。
- `UpdateNetworkSettings` 本次不改动；加速设置作为独立的纯数据对象传入 RDP 解锁链路，从而不影响 `UpdateService` 与主程序检查更新。
- 加速前缀或开关变化后调用 `UpdateService.InvalidateClients()`（`:161`），避免复用按旧设置建立的 `HttpClient`。

---

## 五、改动清单

| 文件 | 位置 | 改动 | 影响代理工程 |
|---|---|---|---|
| `Models/RdpAccelerationSettings.cs`（新增） | — | 纯数据：开关、自定义前缀、候选索引；不含 WinUI 类型（C8） | 是（`Models/**` 被链接） |
| `Services/RdpAcceleration.cs`（新增） | — | `Rewrite`、`ShouldSendToken`、前缀规范化、候选枚举，均为纯函数 | 是（被链接；不得引 WinUI，已验证 Agent publish 通过） |
| `Services/RdpToolHashes.cs`（新增） | — | 静态基线表 `(tag, name) → sha256` | 是（被链接；纯 C#） |
| `Services/RdpReleaseResolver.cs` | `:93`、`:99-103`、`:214` | 两条请求套前缀；令牌加白名单；保留三层降级与 `forceRefresh`（R1） | 否（已排除） |
| `Services/RdpToolDownloader.cs` | `:155`、`:178-191`、`:219-228` | 下载前重写 URL；插入内置哈希比对；改写校验说明文案 | 否（已排除） |
| `Services/RdpUnlockService.cs` | `:64-65`、`DownloadIniAsync`、`:1949` 步骤 | INI 下载多源；新增步骤行记录所用候选与来源 | 否（已排除） |
| `Services/RdpUninstallService.cs` | `:487`、`:513` | 卸载链路同样传加速快照（避免连不上 GitHub 时连卸载器都下不到）；`RdpUninstallRequest` 增 `Acceleration` 字段 | 否（已排除） |
| `Models/AppConfig.cs` | `:193` 后 | 新增 `rdp_accel_enabled`、`rdp_accel_prefix` | 是（`Models/**` 被链接） |
| `Views/SettingsPage.xaml` | `:350-386` 代理设置区 | 新增勾选开关与自定义前缀输入框 | 否 |
| `Views/SettingsPage.xaml.cs` | `:685`、`:735-817`、`:1562` | 开关提交与回拨、前缀校验、构造加速设置传入 `UnlockAsync`、`InvalidateClients()` | 否 |
| `_smoke` 项目 | 新增断言 | 见 8.2 | 否 |

`UpdateService.cs`、`UpdateNetworkSettings`、`RdpReleaseModels.cs`、`Enums.cs` **本次不改动**。

---

## 六、配置与界面

### 6.1 新增字段

| JSON 键 | 类型 | 默认 | 语义 |
|---|---|---|---|
| `rdp_accel_enabled` | bool | `false` | 是否启用 GitHub 加速前缀 |
| `rdp_accel_prefix` | string | `""` | 自定义前缀；留空则按内置候选轮换 |

内置候选列表写在代码常量中，不落配置，便于随版本更新而无需迁移配置。

### 6.2 勾选开关行为（C5）

- 控件为 `ToggleSwitch`，说明文字置于开关下方，说明「仅影响 RDP 解锁组件下载，不影响检查更新」。
- 勾选即启用并立即生效（调用 `InvalidateClients()`）。
- 取消勾选或前缀校验失败时，**必须把开关拨回原状态**，并给出一行状态说明；不得留下「看着是开的、其实没生效」的状态。
- 开关默认关闭，保持现有行为不变，用户可随时回退。

### 6.3 步骤行与日志

新增步骤行记录：是否启用加速、使用的候选或「官方直连」、是否命中内置哈希基线、INI 来源（jsDelivr / 反代 / 官方 raw / 手动指定 / `-w`）。Warn / Fail 才带完整说明，Ok 仍用 `BriefLine()`。

---

## 七、风险与回退

| 风险 | 处置 |
|---|---|
| 公益反代挂掉、限速、加验证页 | 内置多候选轮换；官方直连保底；`SniffHead` 识别网页并判定为失败换源 |
| 反代投毒 / 塞入篡改文件 | exe 强制比对内置 SHA-256；INI 靠内容判据与网页识别；校验不符即删暂存文件 |
| 令牌泄露给第三方反代 | `ShouldSendToken` 白名单；重写后一律不带 `Authorization` |
| 反代共享出口 IP 撞 GitHub 匿名限流 | 保留 1 小时缓存与过期缓存降级；发布页不吃接口配额；R1 下 `forceRefresh` 失败仍有降级链 |
| 官方发布新版 | R1 自动跟随；内置哈希表未命中时按 C9 如实标注，不阻断安装 |
| jsDelivr 国内波动 | INI 仍有反代、官方 raw、手动指定、`-w` 四路；exe 通道与之隔离 |
| 基线采集被投毒污染 | 多源交叉比对后采信；不执行下载文件 |
| 改动波及主程序检查更新 | 范围限定 1.3；`UpdateService.cs` 不改；冒烟覆盖检查更新原有断言 |

---

## 八、验证

### 8.1 编译

- 先完全退出正在运行的程序，否则 pri 锁导致 build 失败。
- `dotnet build` 必须带 `-p:Platform=x64`（不带会报 `WindowsAppSDKSelfContained` 架构错）。
- 因 C7，改完 `Models/**` 与新 `Services/**` 文件后须额外执行：
  `dotnet publish Yomori.Agent.csproj -c Release -p:Platform=x64`
  主控端编得过不代表代理编得过。

### 8.2 冒烟

- 跑冒烟**不要**带 `-p:Platform=x64`（否则执行的是旧的 `_smoke\bin\Debug\net8.0-windows\Smoke.exe`），须确认该 exe 的 mtime 已更新。
- 新增断言（首跑红多为判据写错，按既有纪律复核）：
  1. `Rewrite` 对各候选的拼接结果、前缀规范化、非法前缀拒绝；
  2. `ShouldSendToken`：官方 `api.github.com` 为真，任一反代域名为假；
  3. `RdpToolHashes` 命中 / 未命中的分支；
  4. `Parse` 与 `TagFromUrl` 原有断言全部保持绿（R1 不改解析语义）；
  5. 配置字段默认值与序列化往返；
  6. 检查更新链路原有断言保持绿（回归）。
- 纯函数放在 `Models` 或不含 WinUI 的 `Services`，满足 C8。

### 8.3 真机验证场景

| 场景 | 期望 |
|---|---|
| 开关关闭 | 行为与改造前一致 |
| 开关开启、可直连 GitHub | 经候选完成，官方直连仍可用 |
| 开关开启、屏蔽 `github.com` / `api.github.com` / `raw.githubusercontent.com` | 仍可完成解析、两个 exe 下载、INI 更新，最终解锁成功 |
| 配了自定义前缀且不可达 | 轮换到内置候选，步骤行如实标注 |
| 篡改下载的 exe | 内置 SHA-256 比对失败并给出明确错误，不进入安装 |
| 取消勾选或前缀校验失败 | 开关回拨原位 |

### 8.4 实施前已验证

3 节所列反代 API、raw、release 附件（含 302 消化与 206 Range）均已实测通过。

---

## 九、实施顺序

1. 确认 Q1（R1 / R2）。默认 R1。
2. 多源采集两个 exe 的 SHA-256 基线，写入 `RdpToolHashes`。
3. 新增 `Models/RdpAccelerationSettings.cs`、`Services/RdpAcceleration.cs`、`Services/RdpToolHashes.cs`。
4. 接入 `RdpReleaseResolver`、`RdpToolDownloader`、`RdpUnlockService`。
5. 新增配置字段与设置页勾选开关。
6. 补冒烟断言，按 8.1 / 8.2 编译与自测。
7. 按 8.3 完成真机验证后汇报，等指示再提交。

---

## 十、后续议题（不在本次范围）

- 重试策略放宽：`LooksRetryable` 目前只认「不是可执行文件 / 读不出来」，超时、连接重置、大小不符均不重试；INI 下载零重试；默认 20 秒超时偏紧。
- 断点续传：反代已返回 206，具备前提。
- 自建国内镜像：仅在公益反代长期不可用时评估。
