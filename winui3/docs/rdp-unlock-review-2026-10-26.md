# RDP 解锁实现检查记录（2026-10-26）

## 结论

当前 RDP 解锁已经完成主要功能链路：设置页提供解锁、卸载、还原注册表和诊断入口；只读检测覆盖 Wrapper、TermService、RDP-Tcp 监听器和 INI 版本；解锁流程具备下载确认、官方 Release 资产选择、事务目录、INI 备份、服务重启和失败补偿；卸载流程不把 3389 监听作为唯一判据。

本机 2026-10-26 检测输出为 4/4：Wrapper 已安装、TermService 正在运行、RDP-Tcp 被判为正在监听、INI 已覆盖当前版本。输出中可见相关 `_smoke` 断言通过；本次未核实整套冒烟最终汇总，不以日志提取命令的退出码代替冒烟退出码。检测通过不等于安装、卸载和恢复失败路径通过验收。

当前仍不能把“解锁流程已安全闭环”作为验收结论。主要缺口集中在外部工具超时、事务落盘失败、补偿结果确认、未知状态判定和隔离 VM 验收。

## 已确认做得较好的部分

- `RdpReadOnlyDetector` 只使用查询类注册表、SCM、WinStation 和版本资源接口；检测模块没有写注册表、下载、启动服务或加载 Wrapper DLL 的入口。
- Wrapper 判定依据 `TermService\Parameters\ServiceDll`，同时区分原版、`rdpwrap.dll`、第三方接管和未知状态；不以 DLL 文件残留单独判定已安装。
- 监听器主要依据 `WinStationEnumerateW` 查找 `RDP-Tcp`，TCP 3389 只作补充证据。
- INI 同时保留宽松版本号命中和严格 section/架构补丁键命中；版本字符串与固定文件信息不一致时，代码保留诊断信息。
- 解锁前有系统修改确认；缺少组件时，杀软人工确认位于 metadata 查询和下载之前。
- 安装器和卸载器通过 `-i`/`-u` 调用，INI 更新使用 `-w`；工具超时不会直接强杀进程。
- 三类修改动作使用进程内和跨进程信号量；事务目录拒绝非法事务 ID，并保留注册表、INI 和步骤记录。
- 失败后能够识别遗留事务；工具进程仍运行时会阻止新的操作，避免同时写系统文件。

## 仍存在的问题

### P0：第三方接管或未知状态会被送入安装分支

证据：`Services/RdpUnlockService.cs:101` 使用 `before.WrapperState != RdpWrapperState.Installed` 决定需要下载安装；因此 `ThirdParty` 和 `Unknown` 都会继续经过下载确认，并在后续 `:281` 执行安装器。代码没有在第三方接管或读取未知时停止。检测文案 `RdpReadOnlyDetector.cs:1039` 与计划书要求则明确“不自动覆盖陌生接管”。

影响：已有其他组件接管 TermService 时，点击解锁可能覆盖第三方配置；读取失败时也可能把“无法确认”当成“未安装”，启动下载和系统修改。

建议：只有明确 `NotInstalled` 才允许进入安装分支；`ThirdParty` 和 `Unknown` 必须先返回诊断结果，要求用户处理或明确选择后再执行独立流程。

### P0：隐藏桌面安装超时后，流程可能继续与仍在运行的安装器并行

证据：`Services/RdpUnlockService.cs:294-321` 启动隐藏桌面安装器后只轮询 `ServiceDll`。达到 180 秒仍未接管时，流程把事务记为 `Failed` 并返回；它没有确认安装器进程及其子进程已经退出。`RdpHiddenProcess.Start`（`Services/RdpHiddenProcess.cs:34-85`）只返回 PID，随后没有进程存活状态或进程树跟踪。

影响：安装器可能仍在写服务、文件或注册表；用户立即再次解锁、卸载或还原时，当前进程只能看到工具名是否存在，无法可靠覆盖由隐藏桌面启动的实例及其改名/子进程。并发操作可能造成 ServiceDll、INI 或安装目录的中间状态。

建议：为隐藏桌面启动建立可查询的进程记录，至少跟踪安装器 PID、子进程和启动时间；超时后事务保持 `Running/结果不明`，直到确认工具树退出。`ResolveStale` 应按事务记录判断对应进程，不要只按固定进程名全局扫描。

### P1：注册表目标值在写入前没有立即持久化到事务账本

证据：`RdpUnlockService.cs:807-825` 先把 `delta.After` 设置为目标值，随后直接调用 `RdpRegistryService.Write`；直到整个 `ApplyRegistry` 返回后才在 `:847` 返回结果，期间没有 `RdpTransactionStore.Save(manifest)`。如果进程在写入成功后、下一次保存前崩溃，磁盘中的事务仍可能只有 `After == Before`。

影响：崩溃恢复时无法判断目标值是否已写入，也无法可靠执行逆序补偿或冲突判断。

建议：每个目标值写入前保存包含目标值的 manifest，写入后立即保存 `Applied/Verified` 和回读结果；保存失败时保留“系统已动过但账本不完整”的明确状态并阻止自动恢复覆盖。

### P1：补偿调用卸载器后没有验证结果，却可能把事务标为 `Compensated`

证据：`RdpUnlockService.cs:1191-1208` 调用下载的卸载器后只记录退出码，没有重新检测 `ServiceDll`；`RdpUnlockService.cs:1217-1223` 只按前面的 `allOk` 汇总并结束事务。`RdpUnlockService.cs:1164-1187` 的控制台工具路径也没有检查退出码和卸载后 Wrapper 状态。

影响：卸载器被拦截、部分卸载或退出码非零时，事务可能显示“已补偿”，但 Wrapper 仍处于接管状态，用户会继续执行下一步操作。

建议：补偿卸载后重新运行只读检测，至少确认 Wrapper 不再安装、ServiceDll 已解除接管；退出码非零或状态未知应记录 `CompensationFailed/Unknown`，保留事务锁和人工处理入口。

### P0：恢复冲突或服务重启失败仍可能被汇总为成功

证据：`RdpUnlockService.cs:695-715` 将注册表第三值冲突记为 Warn；同文件恢复流程 `:747-752` 将服务重启失败记为 Warn；随后 `:754-760` 以“所有步骤都不是 Fail”计算成功。

影响：冲突没有覆盖、服务没有成功重启时，结果仍可能是 `Success=true`，界面显示绿色完成，违反计划书对“部分恢复不得覆盖成成功”的要求。

建议：恢复冲突、服务重启失败、回读失败和未知状态都必须使操作进入失败或部分恢复状态；结果应保留未恢复项、事务 ID 和下一步操作，不能只用步骤颜色降级为 Warn。

### P0：事务日志写盘失败被吞掉，系统修改后可能没有恢复证据

证据：`RdpTransactionStore.AppendStep`（`:432-443`）和 `Finish`（`:447-460`）捕获所有保存异常后继续返回。系统修改已经发生而 manifest 无法落盘时，调用方仍可能继续操作或报告最终结果。

影响：断电、磁盘满、权限变化或目录损坏时，事务账本可能缺少已执行步骤和最终状态；下一次启动无法可靠识别恢复边界，和“每步落账本、崩溃可识别”的设计目标冲突。

建议：区分诊断性日志与恢复性账本。恢复性 manifest 的关键写入失败必须让操作进入“结果不明/不可自动恢复”，阻止后续系统修改，并把内存中的失败原因返回 UI；只有非关键附加日志可以尽力而为。

### P1：解锁流程仍会自动启动 RDP_CnC.exe

证据：`RdpUnlockService.cs:281-325` 通过 `RdpHiddenProcess.Start` 启动图形安装器；`RdpHiddenProcess.cs:14-18` 明确记录安装器会按上游行为拉起 `RDP_CnC.exe`。隐藏桌面只让窗口不可见，不能改变“自动启动 CnC”的事实，也没有追踪和清理该进程。

影响：成功或异常路径都可能启动并留下 CnC 进程，和计划书 3.3、3.4、7.3“Yomori 不自动启动 CnC”不一致；隐藏桌面还会让用户难以发现该外部进程的实际状态。

建议：优先调用经核实的控制台安装路径并禁止图形安装器拉起 CnC；如果上游资产只能启动图形安装器，应把该行为列为未满足的产品约束，不能用隐藏桌面包装成“不启动”。同时跟踪进程树并在结果中明确报告残留。

### P1：Support 的静态 INI 覆盖与运行态监听被混用，可能错误跳过或重复更新 INI

证据：`RdpReadOnlyDetector.cs:132-149` 先用 `RdpSupportVerdict.Combine` 把 INI 状态和 Listener 合并；`RdpUnlockService.cs:420-439` 再用合并后的 SupportState 判断是否跳过 `-w`。Listener 缺失时，已覆盖的 INI 会被判 `RuntimeNotApplied`，触发再次更新；Listener 未知时，`Combine` 仍可能保留 `FullySupported`，从而跳过更新。

影响：运行态暂时不可用会改变 INI 更新决策。Listener 缺失和 API 读取失败分别可能造成重复联网更新或错误跳过更新，检测展示的“静态覆盖”也失去独立语义。

建议：是否执行 `-w` 只依据明确的 INI 静态分析、INI 更新记录和文件内容；运行态 Listener 只用于最终诊断与“是否需要重启”提示。Listener Unknown 不得成为绿色跳过依据。

### P1：INI 更新超时的结果被降级后仍可能继续重启服务并结束事务

证据：`UpdateIniAsync` 在 `RdpUnlockService.cs:1041-1044` 对超时返回 `Ok=false`；调用方 `:453-477` 把失败降为 Warn，随后 `:481-517` 仍按 `RestartService` 执行 TermService 重启，最后 `:569-572` 根据最终四项状态把事务结束为 Completed 或 Failed。

影响：`-w` 仍在运行或结果不明时，流程可能对服务做下一步操作；如果工具随后继续写 INI，最终检测和事务状态可能与实际落盘时序不一致。

建议：INI 工具超时必须保持事务未完成，确认对应进程树退出并重新读取文件哈希/内容后再允许重试、补偿或重启服务；不能把“结果不明”当作普通 Warn 继续推进。

### P1：卸载把未知状态当作“已解除接管”

证据：`RdpUninstallService.cs:203-206` 使用 `after.WrapperState != RdpWrapperState.Installed` 作为卸载成功条件。`Unknown` 和 `ThirdParty` 也会满足该条件；随后 `:234-258` 可能返回卸载成功。

影响：卸载器执行后如果读取 ServiceDll 失败，或第三方组件已经接管，界面可能显示“已卸载”，但代码没有证据证明 Wrapper 接管已解除。

建议：成功条件只接受明确的 `NotInstalled`，并要求 ServiceDll 读取成功；`Unknown` 返回未知/失败，`ThirdParty` 返回第三方接管，不得归入卸载成功。

### P2：服务重启失败时，已停止的依赖服务没有保证恢复

证据：`RdpServiceControl.Restart` 在 `:349-369` 逐个停止依赖服务；如果某个依赖停止失败，仍继续停止 TermService。若 TermService 停止失败或启动失败，方法在 `:371-388` 直接返回，没有恢复已经成功停止的依赖服务。只有 TermService 启动成功后才执行 `:391-396` 的依赖服务恢复。

影响：服务重启失败可能留下原本运行、但现在停止的 `UmRdpService` 等依赖服务，造成用户会话或重定向功能异常。

建议：任何中途失败都按已记录的 `touched/stoppedDependents` 逆序恢复；恢复结果逐项写入事务并在最终检测中显示，不能只返回 TermService 的错误。

### P2：备份目录 ACL 收紧失败被静默忽略

证据：`RdpTransactionStore.Begin` 在 `:57-61` 调用 `TryHardenDirectory`，但忽略返回值；`TryHardenDirectory` 在 `:525-550` 捕获所有异常并返回 false。

影响：事务快照可能落在未按计划限制写权限的目录中，仍继续执行系统修改。计划书要求备份仅 Administrators/SYSTEM 可写，当前代码没有把该条件作为开始修改的门槛，也没有把失败写入结果。

建议：把 ACL 收紧失败记录到事务和界面；若无法证明目录权限符合要求，在首次系统修改前停止流程。若产品明确允许降级，也必须显示“备份权限未达标，无法保证恢复材料完整性”。

### P2：监听器实现没有落实自身注释中的 Listen 状态契约

证据：`RdpReadOnlyDetector.cs:558-560` 的注释把监听器定义为 `RDP-Tcp` 且状态为 Listen；实际 `:579-604` 找到同名条目后主要依据 `listenerId > 0` 返回 `RdpListenerState.Listening`，没有使用已经读取的 `listenerState` 判断状态值。计划书明确要求查找 `RDP-Tcp`，但没有单独写出状态值要求，因此这里属于实现与自身契约不一致，仍需和上游及实机结果对照。

影响：同名但处于 Active、Disconnected 或其他状态的会话条目可能让检测显示“监听”，掩盖实际不可接受连接的状态。

建议：同时校验名称和状态值；非 Listen 状态不得显示通过。状态异常时保留会话原值，返回 Unknown 或 Absent，并在诊断中列出状态。应在 `_smoke/RdpUnlockDetectCheck.cs` 增加非 6 状态样本断言。

### P2：WinStation 枚举失败时，Support 仍可能显示 FullySupported

证据：`RdpSupportVerdict.Combine`（`Models/RdpUnlockModels.cs:256-261`）只在 `listener == Absent` 时把静态 INI 覆盖降为 `RuntimeNotApplied`。当监听状态为 `Unknown` 时，INI 的 `FullySupported` 会原样返回；`RdpUnlockSnapshot.AllPass` 又要求 `SupportState == FullySupported`，因此 Support 单项仍显示通过；`AllPass` 另有 Listener 必须为 Listening 的条件，所以这里不会导致总结果通过。

影响：精简系统、权限不足或 API 失败时，用户可能看到 Support 通过，实际运行期状态没有证据。

建议：`Unknown` 监听必须传播为 `RuntimeUnknown` 或至少把 Support 降为 Unknown；只有明确发现 `RDP-Tcp` 缺失时才使用 `RuntimeNotApplied`。

### P2：代理工程通过通配链接编译全部 Services，RDP 修改/下载链没有工程级隔离

证据：`src/Yomori.Agent/Yomori.Agent.csproj:43-48` 链接 `Yomori.App\Services\**\*.cs`，只排除了三个文件；其中包含 `RdpUnlockService`、`RdpUninstallService`、`RdpReleaseResolver`、`RdpToolDownloader` 等系统修改和外部下载代码。

影响：当前源码未发现 `rdpwrap.dll` 等 Wrapper 解锁二进制被嵌入代理；问题在于代理工程和部署载荷会编译、携带不属于常驻代理的系统修改与下载实现。后续代理代码误引用时可把高风险能力带到目标账户常驻进程，也会扩大包体和审计范围。

建议：代理项目改为显式列出所需源文件，或增加编译时断言，确保代理不包含 RDP 修改、下载、服务控制和安装器运行链；发布包检查代理目录不含这些实现和不必要依赖。

### P2：原生 GFX 钩子使用全局回调指针，存在多会话数据竞争

证据：`src/Yomori.RdpNative/ycn_rdp.c:368-379` 使用全局 `g_orig_gfx_surface`；`ycn_rdp.c:1771-1781` 为每个动态通道连接覆盖该全局指针，并把同一个 `ycn_gfx_surface_command` 写入当前 GFX context。Reset/Start/End 的原始回调也使用全局指针。回调本身没有通过 `ctx`/session 找回对应会话的原始函数。

影响：多路 RDP 同时连接时，后建立的会话会覆盖前一会话保存的原始回调，形成跨会话竞争。当前不能仅凭源码断言一定已经调用悬挂指针；但单路冒烟无法覆盖该风险，可能表现为画面错乱、回调串路或随机崩溃。

建议：把原始回调和会话上下文放入每个 GFX context 的实例存储，或建立受锁保护的 context→session 映射；断开时先恢复/清除对应实例，不使用跨会话共享的原始函数指针。

### P1：Release 缓存可能让真正下载操作使用一小时内的旧版本

证据：`Services/RdpReleaseResolver.cs:38-39,65-73` 允许 `ResolveLatestAsync` 在一小时内直接返回 `release-cache.json`，不会请求 GitHub latest。`RdpUnlockService.cs:476-489` 的手动工具路径绕过 Release 解析；正常缺组件路径则直接使用该缓存结果。

影响：官方已发布新版本时，用户点击解锁可能仍下载旧版本，和计划书“每次真正需要下载时解析最新正式版”的严格要求不一致。缓存还可能与当前安装状态或同轮安装/卸载资产不一致。

建议：真正开始下载前强制重新解析 latest；网络不可用时明确显示离线降级，不把一小时缓存默认为“最新”。若保留缓存，应由用户显式选择并在结果中显示缓存时间与版本。

### P2：Release 缓存和 tag 回退路径不完全符合动态资产要求

证据：`Services/RdpReleaseResolver.cs:38-39,65-73` 允许使用一小时内的 release-cache；`256-270` 的 tag 回退直接拼接固定安装器/卸载器名称，且没有资产大小和官方 digest。

影响：API 不可用时，回退路径无法确认未来发布的资产命名、大小或摘要，校验能力下降。缓存还可能让真实下载使用旧版本，具体优先级取决于产品是否把“每次真正下载都 fresh latest”作为硬门禁。当前代码会标注能力受限，但与计划书的严格要求有偏差。

建议：下载操作默认重新请求 latest；缓存只作为明确的离线诊断或用户可见的降级方案。没有 metadata 资产清单时停止自动下载，要求用户打开发布页确认，而不是拼接未知资产。

### P2：卸载器版本匹配依赖账本标签，部分本地工具路径缺少版本证据

证据：`RdpUninstallService.cs:349-360,517-540` 优先使用安装目录里的 `RDPWInst.exe`，只记录当前文件 SHA-256，没有把它与安装事务的 release ID/tag 做版本匹配；只有缺少本地工具时，才在 `:435-445` 检查记录版本与 latest tag 是否不同。

影响：手工安装、目录残留或工具被替换时，可能调用与当前 ServiceDll/INI 不匹配的卸载工具。文件哈希只能证明当前文件一致，不能证明它与安装版本配套。

建议：安装目录工具也应关联安装事务的 release/asset 记录并核对来源；无法证明版本匹配时停止自动卸载，给出人工确认或发布页指引。

### P2：C ABI 字符串字段未显式声明 UTF-8 封送

证据：`Services/RdpNativeInterop.cs:15-28` 的 `YcnRdpParams` 使用 `string` 字段；对应 `ycn_rdp.h` 声明为 `const char*`，注释约定 UTF-8。当前结构字段没有显式 `MarshalAs(UnmanagedType.LPUTF8Str)` 或 UTF-8 指针生命周期管理。

影响：非 ASCII 主机名、用户名、域或密码可能按默认 ANSI 代码页封送，与原生层预期的 UTF-8 不一致；单纯 ABI 大小检查不能证明字符编码正确。

建议：采用明确的 UTF-8 封送方式（或手工分配 UTF-8 字节并在调用期间保证生命周期），补充中文及其他非 ASCII 参数的跨边界测试。

## 验证结果与边界

已运行：

```text
dotnet run --project _smoke/Smoke.csproj --no-restore
```

结果：主检查已执行上述命令，输出中可见 RDP 只读检测 4/4 通过，以及 M2-M4 纯逻辑断言通过，包括 `-i`、`-w`、超时状态判定、遗留事务结案、隐藏桌面启动和状态轮询等项目。大段输出被保存为日志；本报告不以随后提取日志的命令退出码证明冒烟进程退出码，也不把整套冒烟表述为全绿。RDP 服务真正停启检查因会打断远程会话而跳过；依赖服务权限检查因冒烟进程不是管理员而跳过。该冒烟工程包含临时文件、Windows 凭据和隐藏命令进程相关检查，后续应在隔离环境运行。

本次未完成的验收：

- Windows 10/11 专业版和家庭版隔离 VM 的首次安装、更新、卸载、注册表还原矩阵。
- 安装器/卸载器在安全软件拦截、网络中断、进程卡死、部分写入和重启失败时的恢复验证。
- 至少 30 分钟的解锁后稳定性观察。
用户确认的排除项（2026-10-26）：不处理事务快照对安装器实际接管范围的覆盖问题；该项不计入待修缺陷、修复优先级或验收要求。

## 修复优先级

1. 先修复外部工具进程跟踪和超时状态机，确保结果不明时不能并发操作。
2. 修复注册表逐项落盘、补偿后状态验证和依赖服务失败恢复。
4. 将卸载的 Unknown/ThirdParty 状态改为未知或失败。
5. 在隔离 VM 完成专业版/家庭版实机矩阵，并补足 30 分钟稳定性观察。

## 修复记录（2026-10-26 晚）

### 已修复

| 原问题 | 处理方式 | 落点 |
|---|---|---|
| P0 第三方接管/未知状态被送入安装分支 | 只有 `NotInstalled` 允许下载安装，`Installed` 才复用；`ThirdParty`、`Unknown` 各自停在诊断并给出可执行下一步 | `RdpUnlockService.UnlockAsync` 的 1b 门禁 |
| P0 隐藏桌面安装超时后可能与仍在运行的安装器并行 | 新增工具进程记录（PID + 启动时间 + 角色）落进账本；超时先查进程树，树未退出则事务保持 `Running`（结果不明）并挡住后续操作；`ResolveStale` 先按账本 PID 判断，仅退回按名扫描兜底 | 新增 `Services/RdpToolProcessTree.cs`、`RdpHiddenProcess.StartTracked`、`RdpTransactionStore.ResolveStale` |
| P1 注册表目标值写入前未落盘 | 写入前先 `TrySaveCritical` 落目标值，写入后立刻落 `Applied`/`Verified`/回读值；`RdpRegistryDelta.ReadBack` 随账本持久化 | `RdpUnlockService.ApplyRegistry`、`RdpTransactionModels` |
| P1 补偿调用卸载器后不验证却记 `Compensated` | 补偿后重新只读检测，只认 `WrapperState == NotInstalled`；未证实则记新增状态 `CompensationFailed` | `RunUninstallCompensationAsync`、`RdpTransactionStatus.CompensationFailed` |
| P0 恢复冲突/服务重启失败仍汇总为成功 | 冲突数、服务重启、步骤失败三者共同决定 `Success`；新增「恢复结果」步骤写明未恢复项与事务号 | `RestoreRegistryCore` |
| P0 事务日志写盘失败被吞掉 | 新增 `TrySaveCritical`（严格的恢复性写入）与内存标记 `LedgerIncomplete`；关键写入失败即停手、明确说明「已改动但不可自动恢复」；遗留事务若账本不完整则只结案不做自动恢复 | `RdpTransactionStore`、`ApplyRegistry`、服务重启前 |
| P1 解锁仍会自动启动 RDP_CnC.exe | 上游安装器行为无法关闭（官方 `install.bat` 最后一句就是它），改为如实报告残留进程并记成 `residue` 角色（只报告、不阻塞），不再由隐藏桌面掩盖 | `ReportCnCResidue` |
| P1 Support 静态 INI 与运行态监听混用 | 快照新增 `IniState`（纯静态），`-w` 决策与「换回备份后是否可用」只认 `IniState`；`SupportState` 仅用于展示 | `RdpUnlockModels`、`RdpReadOnlyDetector`、`RdpIniUpdatePolicy` |
| P1 INI 更新超时后仍继续重启服务 | `UpdateIniAsync` 返回 `Unknown`；调用方在结果不明时直接停住（事务保持未完成），不重启服务、不结案 | `AcquireIniAsync` / `UpdateIniAsync` |
| P1 卸载把未知状态当「已解除接管」 | 成功条件改为 `== NotInstalled`；`ThirdParty` / `Unknown` 分别给不同失败原因；`DecideStale` 的卸载目标同步收紧 | `RdpUninstallService`、`RdpTransactionStore.DecideStale` |
| P1 Release 缓存可能用一小时前的旧版本 | `ResolveLatestAsync` 增加 `forceRefresh`，真实下载路径强制重新解析；缓存降级时文案改为「离线降级 + 几小时前」 | `RdpReleaseResolver` |
| P2 服务重启失败时已停的依赖服务不恢复 | 任一环失败都按已停清单逆序拉回；依赖恢复结果单列 `DependentsOk`，与 `CoreOk` 分开呈现 | `RdpServiceControl.Restart`、`RdpServiceRestartResult` |
| P2 备份目录 ACL 收紧失败被静默忽略 | 结果记进账本 `BackupAclVerified`；判据改为**回读实际 ACL**（只放行 SYSTEM / Administrators 的写权限），未达标则在第一次系统修改前停止 | `RdpTransactionStore.TryHardenDirectory` / `DirectoryIsRestricted` |
| P2 监听器不校验状态值 | 新增纯函数 `RdpListenerVerdict.Classify`：名字命中且状态为 6 才算正在监听，同名非 Listen 记 `Unknown` 并在诊断里保留原状态 | `RdpUnlockModels`、`EnumerateSessions` |
| P2 WinStation 枚举失败时 Support 仍可能通过 | 新增 `RdpSupportState.RuntimeUnknown`：INI 静态覆盖但运行期读不出来时降为该项，等级为「未知」 | `RdpSupportVerdict.Combine` |
| P2 代理工程通配链接全部 Services | 显式排掉 RDP 修改/下载/事务/进程跟踪/诊断链（共 11 个文件），将来误引用会直接 CS0246 | `Yomori.Agent.csproj` |
| P2 C ABI 字符串字段未显式 UTF-8 | 4 个参数字段与 2 个回调参数加 `MarshalAs(UnmanagedType.LPUTF8Str)`；ABI 字段数断言改为跳过特性行（原先会少数 4 个字段） | `RdpNativeInterop.cs`、`_smoke/RdpNativeAbiCheck.cs` |
| P2 卸载器版本匹配缺乏证据 | 本地控制台工具不再无条件标「与记录匹配」：有安装事务记录才标，否则明确写出「无法证明同版本」 | `FindUninstallerAsync`、`FindRecordedUnlockRelease` |

### 未修复（与原因）

- **P2 原生 GFX 钩子的全局回调指针**（`ycn_rdp.c` 的 `g_orig_gfx_surface` 等）：需要按 GFX context 建实例存储，本机没有可用的原生构建链（cmake/MSBuild 原生工程未验证），按项目纪律不提交未经编译验证的原生改动。修复方案已明确：context → session 映射表受锁保护，断开时清除对应实例。
- **P2 tag 回退拼接未知资产**：保留现有降级路径。`_smoke` 明确断言「只凭 tag 也能选出安装器与卸载器」，改成「停止自动下载」需要产品先决定放弃这条离线出口；当前已在步骤与 `Limits` 中标注「拿不到官方大小与摘要」。
- **验收项**：专业版/家庭版隔离 VM 的安装/更新/卸载/还原矩阵、杀软拦截与进程卡死场景、30 分钟稳定性观察，均需实机，本轮未做。

### 验证

```text
dotnet build src/Yomori.App/Yomori.App.csproj -p:Platform=x64 -p:RuntimeIdentifier=win-x64   → 成功
dotnet build src/Yomori.Agent/Yomori.Agent.csproj -p:Platform=x64                            → 成功
dotnet run --project _smoke/Smoke.csproj --no-restore                                        → 全部通过（1601 项，跳过 4 项）
```

本机真机只读检测仍为 4/4（Wrapper 已安装、TermService 正在运行、RDP-Tcp 状态 6 判为监听、INI 已覆盖 10.0.26100.9549）。新增断言覆盖：监听器非 Listen 状态、运行期未知降级、INI 静态判据、工具进程记录往返、进程树存活判定、各条门禁的源码形状、代理工程排除项、ABI UTF-8 封送。

冒烟工程不覆盖解锁/卸载/还原的真实执行路径（会改系统），这些仍需在隔离 VM 上按上表逐项验收。
