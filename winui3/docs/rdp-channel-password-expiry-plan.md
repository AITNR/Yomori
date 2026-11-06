# 会话通道账户密码过期治理方案（计划书）

- 日期：2026-11-06
- 触发材料：`远程桌面-用户密码补丁.bat`（`C:\Users\AITNR\OneDrive\桌面\远程本地多用户桌面1.19r\`）
- 状态：**已批准并实施（2026-11-06）**。原提案与最终实现的差异、改动清单与自测结果见 **§10**；§6.2.2 等处描述的是原始提案，以 §10 为准。

---

## 1. 必要条件与适用范围

本方案的全部结论与后续实施，以下列条件为前提。条件不成立时，第 6 章的自动化部分不适用，只能按第 5 章做人工处置。

1. 目标主机为本机环回地址（`config.json` 中 `rdp.target_host = 127.0.0.2`，通道 `host = 127.0.0.2`），账户是本机 Windows **本地账户**（`PrincipalSource = Local`）。
2. 主控端以管理员权限运行（项目已设 `requireAdministrator`）。会话代理为 `asInvoker`，不得承担账户策略修改。
3. 沿用项目既有纪律：程序**不创建 Windows 用户、不设置或重置账户密码、不修改全局密码策略**（`LocalGroupMembershipService.cs` L18-26、L286-292）。本方案不改变这一边界。
4. 微软账户（`PrincipalSource = MicrosoftAccount`）、域账户、受组策略托管的账户、远程主机上的账户，均不在自动生效范围内。
5. 密码仅存于 Windows 凭据管理器（`Yomori/RDP/<host>|<user>`，`RdpCredentialStore.cs`），配置中只留 `credential_saved` 标记。本方案不引入任何新的密码落盘位置。

---

## 2. 结论摘要

1. 所附补丁**不能解决密码过期**。`net user <user> <password> /expires:never` 中的 `/expires` 设置的是**账户有效期**（Account expires），不是**密码有效期**（Password expires）。
2. 该命令的第二个位置参数会被 `net user` 解释为**新密码**，因此每次运行都会重置一次密码、刷新 `pwdLastSet`，把到期日往后推 42 天。这是它"看起来有效"的真实原因，属于手动续期，不是永不过期。
3. 本机实测已复现该现象，且存在**紧急项**：通道账户 `bh3` 的密码将于 **2026-11-08 11:05:43** 到期，距本计划书撰写时约 37 小时。
4. 正确的一次性处置是对通道账户单独设置 `UF_DONT_EXPIRE_PASSWD`（PowerShell 表现为 `PasswordNeverExpires = True`），**不改密码本身**，因此凭据管理器里已保存的密码继续有效。
5. 工程侧需要补齐三件事：到期状态可见（预检/诊断）、开关化固化（用户偏好）、登录失败时能识别密码过期并给出可操作提示。当前三项均缺失。

---

## 3. 现状与实测证据

### 3.1 本机密码策略

`net accounts` 输出（2026-11-06，只读查询）：

| 项 | 值 |
|---|---|
| 密码最长使用期限 | **42 天** |
| 密码最短使用期限 | 0 天 |
| 锁定阈值 | 10 次 |
| 计算机角色 | WORKSTATION |

### 3.2 通道账户实际状态

`net user <name>` / `Get-LocalUser` 输出（2026-11-06，只读查询）：

| 账户 | 通道用途 | 账户到期 | 上次设置密码 | 密码到期 | 判定 |
|---|---|---|---|---|---|
| `bh3` | `channels[1].user` | 从不 | 2026-09-27 11:05:43 | **2026-11-08 11:05:43** | **约 37 小时后过期** |
| `zdh` | `target_user` / `channels[0].user` | 从不 | 2026-11-02 15:29:59 | 2026-12-14 15:29:59 | 11-02 被重设过一次，续期 42 天 |
| `123` | 对照（非通道） | 从不 | 2026-10-29 17:28:25 | **从不** | 已置 `UF_DONT_EXPIRE_PASSWD` |

三条证据链：

- `bh3` 与 `zdh` 的「账户到期」都已是「从不」，但「密码到期」仍是具体日期 —— **直接证明 `/expires:never` 与密码过期无关**。
- `zdh` 的「上次设置密码」为 11-02，恰好比「密码到期」早 42 天 —— 与「运行补丁导致密码被重置」一致。
- `123` 的「密码到期 = 从不」才是目标状态，其 `PasswordLastSet` 保留原值，说明置位该标志不需要改密码。

### 3.3 工程侧对应位置

| 环节 | 位置 | 现状 |
|---|---|---|
| 通道账户字段 | `Models/RdpChannel.cs` `User` L74-88 | 只存账户名，`config.json` 中为 `zdh` / `bh3` |
| 凭据保存 | `Services/RdpCredentialStore.cs` `Write` L45；`RdpSessionService.cs` `SaveCredential` L457 | `CredWriteW` + 两条 `cmdkey`，密码不落配置 |
| 凭据读取 | `MainViewModel.cs` `TryStartEmbeddedConnect` L4879-4894 | `CredReadW` 取回固定密码 |
| 交给 FreeRDP | `RdpEmbeddedClient.cs` L257-271 → `ycn_rdp.c` L3446-3457 | `FreeRDP_Password` + `AutoLogonEnabled = TRUE` |
| 就绪预检 | `RdpSessionService.cs` `RdpReadiness` L60-82、`CheckReadiness` L2119 | 字段仅 `UserExists` / `RemoteDesktopEnabled` / `AgentDeployed` / `AgentOnline` / `CredentialSaved`，**无到期字段** |
| 错误分类 | `Models/RdpErrorText.cs` L10-90 | 已含 `3338 目标账户密码已过期`、`20 目标账户密码已过期`、`-3 密码需要更新`、`-5 密码即将过期`，但**全仓库无调用点，为 mstsc/ActiveX 时代遗留死代码** |
| 认证失败原因 | `ycn_rdp.c` `ycn_authenticate_ex()` L2946 | L2952 `(void)reason;` **丢弃 `rdp_auth_reason`**；`freerdp_connect` 失败（L3033-3039）只透传原始错误串 |

---

## 4. 根因分析

### 4.1 三个被混为一谈的概念

| 概念 | Windows 字段 | 对应命令 | 与本次问题的关系 |
|---|---|---|---|
| 账户有效期（Account expires） | `AccountExpires` | `net user <u> /expires:never` | 账户本身是否失效。本地账户默认即为「从不」，**不是病因** |
| 密码有效期（Password expires） | `UF_DONT_EXPIRE_PASSWD` (0x10000) + 策略 `maxpwage=42` | `Set-LocalUser -PasswordNeverExpires $true` / `NetUserSetInfo(1008)` | **本次病因** |
| 凭据副本与当前密码失配 | 凭据管理器 `CredentialBlob` | 重新点「保存凭据」 | 并发症状：密码一旦被改（含被补丁改错），程序仍用旧密码自动登录 |

### 4.2 为何密码过期会直接打断通道

连接采用 `AutoLogonEnabled` + 凭据管理器固定密码，全程无交互。密码过期时 NLA/CredSSP 返回 `STATUS_PASSWORD_EXPIRED`，服务端要求改密，FreeRDP 无法在无交互场景下完成改密流程，连接直接失败。即使管理员随后在 Windows 里改了密码，凭据管理器中仍是旧值，通道依旧连不上，必须人工重新「保存凭据」。

当前程序对此**零识别**：认证回调丢弃 reason，连接失败只把 `freerdp_get_last_error_string` 原样透出，运行期唯一相关提示是「该主机还没有保存凭据」（`MainViewModel.cs` L4890，针对凭据缺失）。因此故障表现为「用了一段时间突然连不上，且没有明确原因」。

### 4.3 所附补丁的副作用

1. `set /p password=` 明文回显，密码进入命令行参数、进程命令行与可能的历史记录。
2. 输入内容与当前密码不一致时，会**把密码改成错误值**，凭据管理器里的旧密码立即失配，故障反而被制造出来。
3. 提示语「密码已设置为永不过期」与命令实际效果不符，构成误导，使问题被反复延后而非解决。
4. UAC 提权依赖临时 VBS `ShellExecute runas`，未校验账户存在性、未限定本地账户、未做回滚，且 `if %errorlevel%` 位于 `setlocal` 内未用延迟展开，判定不可靠。
5. 每 42 天必须人工再跑一次，通道越多越容易漏（当前 `bh3` 即漏跑）。

**判定：该补丁应废弃，不纳入产品，也不作为运维手段保留。**

---

## 5. 方案比选

| 方案 | 做法 | 效果 | 影响面 / 风险 | 结论 |
|---|---|---|---|---|
| A | `net accounts /maxpwage:unlimited` | 全局关闭密码有效期 | 影响本机所有本地账户，扩大安全面；与第 1 章第 3 条纪律冲突 | 不采用 |
| B | 对**通道账户单独**置 `UF_DONT_EXPIRE_PASSWD` | 密码永不自动过期，密码本身不变 | 仅作用于指定本地账户，凭据保持有效 | **采用（止血 + 产品化）** |
| C | 定期轮换密码并同步写凭据管理器 | 符合传统密码策略 | 需长期持有明文密码、调度复杂、任何一次不同步即断连 | 不采用 |
| D | 沿用所附补丁定期续期 | 每次续 42 天 | 见 4.3，且必然复发 | 废弃 |
| E | 改用微软账户 | — | 密码由云端策略管理，本地标志无效，NLA 行为更不可控 | 不采用 |

---

## 6. 推荐实施方案

分三期。P0 为独立的一次性运维动作，不依赖代码改动；P1 为产品化；P2 视需要追加。

### 6.1 P0 — 紧急止血（建议 2026-11-08 前执行，待批准）

对 `config.json` 中所有 `host` 为本机的通道涉及的**本地账户**置位，改动仅限账户标志位：

```
Set-LocalUser -Name bh3 -PasswordNeverExpires $true
Set-LocalUser -Name zdh -PasswordNeverExpires $true
```

或以工程方式调用 `NetUserSetInfo` + `USER_INFO_1008`（`usri1008_flags |= 0x10000`），避免依赖 PowerShell 版本差异。执行后逐个复核：

- `net user <name>` 的「密码到期」应为「从不」；
- `Get-LocalUser` 的 `PasswordLastSet` **保持不变**，`PasswordExpires` 为空；
- 凭据管理器 `Yomori/RDP/127.0.0.2|<user>` 条目未变，通道无需重新保存凭据；
- 实际建立一次通道连接，确认可登录。

对 `PrincipalSource = MicrosoftAccount` 的账户跳过并单独列出，不尝试置位。

### 6.2 P1 — 产品化改造

#### 6.2.1 新增账户密码策略服务

- 新增 `Services/LocalUserPasswordPolicy.cs`（或并入 `Services/LocalGroupMembershipService.cs`，复用其 `netapi32.dll` P/Invoke 与账户边界判定）。
- 读取：`NetUserGetInfo` level 1008 取 `usri1008_flags` 判 `UF_DONT_EXPIRE_PASSWD`；`NetUserGetInfo` level 3（或 `Get-LocalUser`）取 `PasswordLastSet`、计算 `PasswordExpires = PasswordLastSet + maxPwdAge`；`NetUserModalsGet` level 0 取 `usrmod0_max_passwd_age`。
- 写入：`NetUserSetInfo` level 1008，只改该标志位，不设置 `usri1008_password`。
- 边界：仅 `RdpTargets.IsLocal(host)` 且账户为本地账户时生效；含 `@` 的 UPN、非本机前缀的 `DOMAIN\user` 一律 `Deny`（复用 `RdpGrantPlanner.Plan()` 的判定，见 `Models/RdpAccountModels.cs` L308-371）；非管理员/远程主机返回明确原因。
- 审计：沿用 `RdpGrantStore` 的 `grants.jsonl` 两段式记账思路，记录 intent / result / 账户 SID，不记录密码。

#### 6.2.2 设置项：勾选开关（按既定交互约定）

> ⚠️ 本节为原始提案。用户批准时改为「默认勾选 / 状态落配置 / 不带说明文字 / 只对已添加进 Yomori 的通道账户生效」，见 §10。

- 位置：设置页新增一个 **`ToggleSwitch`「通道账户密码永不过期」**，勾选 = 对所有本机本地通道账户置位，取消 = 还原。
- 说明文字放在开关下方，不进弹窗：「仅对本机本地账户生效。开启后该账户的密码不再受系统 42 天期限限制；不会修改现有密码，也不需要重新保存凭据。微软账户与远程主机上的账户不适用。」
- **开关状态以 Windows 实际标志位为准，不写进 `config.json`**（与「UU 远程修复」开关一致：持久化布尔值会产生「显示已开、实际已失效」的漂移）。进入页面时读回真实状态，多账户状态不一致时以「部分」态呈现并给出逐账户明细。
- 取消勾选、被用户拒绝或执行失败时，**必须把开关拨回原位**；批量操作中途失败时，已成功的账户需回滚或明确提示，不得留下半状态。
- 不做「按钮 + 二次确认」组合。

#### 6.2.3 到期可见性

- `RdpReadiness`（`RdpSessionService.cs` L60-82）增加 `PasswordNeverExpires`、`PasswordExpiresAt`、`PasswordPolicyNote` 三个字段；`CheckReadiness()` 对本机本地账户填充。
- 分级提示：已过期 → `Problems`（红）；14 天内到期 → `Notes`（黄）；已置永不过期 → 不提示。
- `RdpDiagnoseCli.AppendTargetAccounts()`（L176-249）输出每个通道账户的 `PasswordLastSet` / 到期日 / 标志位，便于远程排障取证。
- 通道列表行可显示只读的到期状态，但不做成第二个可操作开关（账户是系统级对象，逐通道开关会出现同一账户两条通道状态互斥）。

#### 6.2.4 登录失败原因识别

- 最小版（仅托管层，先做）：`YcnCallbacks.OnError` / `RdpEmbeddedClient.Connect` 拿到 `freerdp_get_last_error_string` 与错误码后，映射到 `RdpErrorText.cs` 现有文案，命中密码过期/账户锁定/凭据错误时给出可操作指引「在 Windows 改密码后，回到会话通道页重新点『保存凭据』」。
- 完整版（需动原生层）：`ycn_rdp.c` `ycn_authenticate_ex()` 不再丢弃 `rdp_auth_reason`，经回调把认证失败原因透出到托管层再分类。此项涉及原生 DLL 整套替换与三处副本同步，建议单列一次改动，不与 P1 其余部分混在一起。
- 冒烟需补断言：`RdpErrorText` 被真正调用、密码过期码映射正确（当前该文件是死代码，改动易被回归漏掉）。

#### 6.2.5 文档

- `winui3/USER_GUIDE.md`、`winui3/README.md`、`README.md` 各增「通道账户密码有效期」小节：三种「过期」的区分、`/expires:never` 的误用说明、正确的处置命令、微软账户/域账户/远程主机不适用、密码被改后必须重新保存凭据。
- 排障条目补「密码到期导致通道连不上」的典型表现与检查步骤。
- 明确标注所附第三方 bat 不作为推荐手段。

### 6.3 P2 — 可选：凭据一致性检测（暂不纳入首期）

密码确被外部修改时，程序无法在不发起认证的前提下判断凭据管理器中的副本是否仍有效。可选做法是记录凭据写入时间，与账户 `PasswordLastSet` 比对，若 `PasswordLastSet > 凭据写入时间` 则提示「账户密码可能已变更，请重新保存凭据」。该项属于启发式判断，存在误报，需单独评估后再决定。

---

## 7. 关键风险与对策

| 风险 | 后果 | 对策 |
|---|---|---|
| **取消开关时密码可能已超 42 天** | 清除 `UF_DONT_EXPIRE_PASSWD` 后，账户按 `PasswordLastSet + 42 天` 立即被判定为「密码已过期」，通道当场断连 | 还原前检查 `PasswordLastSet` 与 `maxpwage`；若已超期，拒绝还原、把开关拨回 On，并提示「需先在 Windows 修改密码并重新保存凭据」 |
| 微软账户 | 密码由云端策略管理，本地标志不生效 | `PrincipalSource` 判定后禁用开关并给出说明，不尝试写入 |
| 域环境 / 组策略托管 | 本地设置被策略覆盖或不被允许 | 只读检测 + 提示，失败即回拨开关，不重试 |
| 远程主机通道 | 主控端无法读取对方账户状态 | 开关仅对本机生效；远程通道在预检中沿用现有「需在对方机器上确认」的说明 |
| 权限 | `NetUserSetInfo` 需管理员 | 主控端 `requireAdministrator` 满足；代理侧（`asInvoker`）不承载此逻辑；非管理员时只读 + 明确报错 |
| 安全面 | 关闭密码过期可能被视为降低安全性 | 仅作用于既有的、专用于本机环回会话的通道账户；不改密码复杂度、不改锁定阈值、不动全局策略；改动可审计、可还原 |
| 工程回归 | `Services/Helpers` 为「链接 + 排除」复用，主控端编得过不代表代理编得过 | 改完额外执行 `dotnet publish Yomori.Agent.csproj -c Release -p:Platform=x64`；Models 层不得引入 `Microsoft.UI.Xaml`（冒烟直接编译 `Models/*.cs`） |

---

## 8. 验证方式

1. 编译：完全退出程序后 `dotnet build`（带 `-p:Platform=x64`），避免 pri 文件锁。
2. 冒烟：`dotnet run --project _smoke`，**不带** `-p:Platform=x64`；确认 `_smoke\bin\Debug\net8.0-windows\Smoke.exe` 的 mtime 已更新，避免跑旧产物造成假通过。
3. 新增断言：到期日计算、标志位读回、开关失败回拨、非本地账户跳过、微软账户禁用、错误码映射。首跑判据写错导致的红要与实现错误区分。
4. 系统侧实测（P0 复核项同 6.1）：`net accounts`、`net user <name>`、`Get-LocalUser`、凭据管理器条目、实际通道连接各一次。
5. 回归：新建通道、保存/清除凭据、导入导出（导出剥离密码）、诊断 CLI 输出格式。

---

## 9. 分期与待确认事项

| 期 | 内容 | 依赖 |
|---|---|---|
| P0 | 对 `bh3`、`zdh` 置位并复核 | **需批准；建议 2026-11-08 11:05 前完成** |
| P1-a | 6.2.1 服务 + 6.2.3 到期可见性 + 6.2.5 文档 | 无 |
| P1-b | 6.2.2 开关 + 6.2.4 最小版错误识别 | 依赖 P1-a |
| P1-c | 6.2.4 完整版（原生层透传 auth reason） | 单列一次原生 DLL 改动 |
| P2 | 6.3 凭据一致性检测 | 待评估 |

待确认：

1. 是否同意 P0 立即执行（只改两个账户的标志位，不改代码、不改密码）。
2. 6.2.2 的开关是否设为**默认开启**，以及是否需要对新通道账户自动置位；若自动置位，需在「保存凭据」或「授权为管理员」链路上增加一个明确的提示。
3. 是否需要保留「逐账户覆盖」能力，还是统一由一个总开关管理。
4. 是否纳入 6.2.4 完整版（涉及原生层），还是首期只做托管层映射。
5. 第三方 bat 文件是否需要从随附资料中标注为「不推荐」，避免后续被再次引用。

---

## 附：本次调研的只读操作清单

- 按 GBK 解码读取所附 `.bat`，确认命令与中文提示语原文。
- `net accounts`、`net user bh3|zdh|123`、`Get-LocalUser`（只读查询，未写入）。
- 读取 `config.json` 的 `rdp` 段、`RdpErrorText.cs`、`RdpSessionService.CheckReadiness()`。
- 全仓库检索账户创建 / 密码策略 / 过期检测相关 API 与文案，确认代码与文档中均无对应实现。

**调研阶段（22:14）未执行任何写操作**：未改工程文件、未改 `config.json`、未改 Windows 账户、未改凭据管理器、未执行该 `.bat`。

---

## 10. 实施记录（2026-11-06 22:40–23:10）

### 10.1 用户决策

| 项 | 决策 |
|---|---|
| 方案 | **B**：对通道账户单独置 `UF_DONT_EXPIRE_PASSWD` |
| 开关 | 「通道账户密码永不过期」，**默认勾选** |
| 范围 | **只对已添加进 Yomori 的通道账户有效**，不扫描全机账户 |
| 说明文字 | **不要**（开关下方不放任何 TextBlock） |

### 10.2 与原提案的差异

1. **开关状态落 `config.json`**：新增 `rdp.channel_password_never_expires`，字段初始化器 `= true` 保证默认勾选、旧配置缺键仍为 true；`Sanitize()` 不覆盖，用户显式关闭可持久。原提案（§6.2.2）主张「状态读 Windows 实况、不落配置」，本次改为「配置表示是否自动维护的长期意愿，Windows 标志位由服务幂等对齐」。
2. **不带说明文字**：XAML 只有一个 `ToggleSwitch`，无下方 TextBlock，也无状态文字；执行结果写运行日志。
3. **范围判定**：`CollectChannelAccounts` 只遍历 `rdp.channels[].user`，按 `RdpTargets.IsLocal(host)` + `RdpGrantPlanner.Plan()` 过滤并去重；**不按 `RdpChannel.Enabled` 过滤**（停用通道的账户同样会过期）。微软账户（含 `@`）、域账户、远程主机账户一律跳过。
4. **取消勾选会还原**，但有两道保护：整批先只读预检，任一账户密码年龄已达策略上限（清除标志位会让它立刻判为过期、通道当场断连）则**整批不动**；任何失败都把开关拨回原位且不落配置。
5. **本期顺延**：6.2.3 到期可见性预检、6.2.4 登录失败错误识别（含原生层 `rdp_auth_reason`）、6.2.5 大段文档，未在本次实现，仍属后续工作。

### 10.3 改动文件

| 文件 | 改动 |
|---|---|
| `Services/LocalUserPasswordPolicy.cs` | 新增。`NetUserGetInfo(3)` 读 `usri3_flags` / `usri3_password_age`、`NetUserModalsGet(0)` 读策略、`NetUserSetInfo(1008)` 只写标志位；写后读回校验；只读、幂等、无 UI 依赖 |
| `Models/RdpModels.cs` | `RdpConfig` 新增 `channel_password_never_expires`，默认 true |
| `Views/SettingsPage.xaml` / `.xaml.cs` | 新增默认勾选开关；`OnChannelPasswordPolicyToggled` 先执行后落配置、失败回拨 |
| `ViewModels/MainViewModel.cs` | `GetChannelPolicyAccounts` / `ApplyChannelPasswordPolicyAsync`；启动延迟 3 秒后台巡检、保存凭据后补置 |
| `Yomori.Agent.csproj` | 把新服务加入 `Exclude`（代理 asInvoker 不承载账户策略） |
| `_smoke/ChannelPasswordPolicyCheck.cs`、`Smoke.csproj`、`Program.cs` | 新增 18 条断言：默认值、范围、接线、Win32 只读实况、写入回读（显式开关） |

### 10.4 自测结果

- 主工程 `dotnet build -p:Platform=x64`：**0 错误**，8 个既有警告（均非本次引入）。
- `Yomori.Agent.csproj` Release publish：**成功**。
- 冒烟 `dotnet run --project _smoke/Smoke.csproj`（不带 Platform）：**全部通过**。
- **冒烟抓到并修正一个真 bug**：`NetUserGetInfo` 不支持 level 1008（`USER_INFO_1008` 是 `NetUserSetInfo` 专用写入结构），返回 `ERROR_INVALID_LEVEL(124)`；读标志位改走 level 3 的 `usri3_flags`。若未发现，启动巡检会对每个账户报「读取失败」而永远置不上位。
- Win32 只读实况：`max_passwd_age = 3628800` 秒（42 天），与 `net accounts` 一致，确认 `USER_INFO_3` / `USER_MODALS_INFO_0` 结构布局正确。

### 10.5 未完成：P0 止血需要管理员权限

本会话的命令执行环境**不是管理员**，`NetUserSetInfo` 返回 5（拒绝访问），因此 `bh3`、`zdh` 的标志位**尚未置位**。`bh3` 的密码仍将在 **2026-11-08 11:05:43** 到期。

主控端 `Yomori.exe` 是 `requireAdministrator`，装上新版本后启动巡检会自动完成置位；若要在装新版之前先止血，请在**管理员 PowerShell** 中执行：

```powershell
Set-LocalUser -Name bh3 -PasswordNeverExpires $true
Set-LocalUser -Name zdh -PasswordNeverExpires $true
```

复核：`net user bh3` 与 `net user zdh` 的「密码到期」应变为「从不」，`PasswordLastSet` 不变，凭据管理器 `Yomori/RDP/127.0.0.2|<账户>` 条目不变，无需重新保存凭据。未添加进 Yomori 的账户（如 `123`）不要动。

### 10.6 待确认（原 §9）

Q1–Q5 中与本次实现相关的部分已按用户决策定案：默认勾选、范围限定、不带说明文字。剩余待定：是否纳入 6.2.4 完整版（原生层透传认证失败原因）、是否补做到期可见性预检。
