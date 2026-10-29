# Yomori WinUI 3 版（二游脚本统合管理器）

> 本目录是 Yomori 的 **C# / WinUI 3 版**，也是仓库唯一的代码主线。
> 原先仓库根目录的 Python + PySide6 版（`main.py` / `core/` / `ui/`）已于 2026-09-28 从仓库移除，
> 需要的话可以从 Git 历史（移除前的提交）里取回。

开发基线：**v2.0 WinUI 3**（原 Python 版基线 v31.17.1）

> **v2.0 相对 Python 版新增的能力**：
> 远程会话从「单通道 + 系统 mstsc 窗口」升级为 **多条「会话通道」并行执行** ——
> 任务可分配给不同目标账户 / 不同机器，通道之间并行、通道内部串行；
> 远程画面用原生 FreeRDP **内嵌**在 Yomori 窗口里（可同屏并列多路），鼠标键盘直接操作。
> 详见 [§七、会话通道](#七会话通道多通道并行执行) 与 [docs/rdp-multi-channel-plan.md](docs/rdp-multi-channel-plan.md)。

---

## 一、为什么要重写

重写动机与新旧对照表已归档到 [docs/migration-from-python.md](docs/migration-from-python.md#一为什么要重写)。

---

## 二、环境要求

| 项目 | 版本 |
|---|---|
| 操作系统 | Windows 10 1809（build 17763）及以上，推荐 Windows 11 |
| .NET SDK | 8.0（本机验证 8.0.413） |
| Windows App SDK | 1.8.260921001（NuGet 自动还原） |
| Microsoft.Windows.SDK.BuildTools | 10.0.26100.4654 |
| 目标框架 | `net8.0-windows10.0.19041.0` |
| 架构 | x64（同时声明 x86 / ARM64，需自行改平台编译） |

> 需要联网还原 NuGet 包。如果在受限网络下，设置代理后再还原：
> ```bash
> export http_proxy=http://127.0.0.1:7897 https_proxy=http://127.0.0.1:7897
> ```

---

外部脚本日志的配置、传输与验收说明见 [外部脚本实时日志](docs/live-script-logs.md)。

## 三、编译与运行

```bash
cd winui3
dotnet restore
dotnet build -p:Platform=x64            # Debug
dotnet build -c Release -p:Platform=x64 # Release
```

产物：

```text
winui3/src/Yomori.App/bin/x64/Debug/net8.0-windows10.0.19041.0/Yomori.exe
winui3/src/Yomori.App/bin/x64/Release/net8.0-windows10.0.19041.0/Yomori.exe
```

工程采用 **非打包（unpackaged）模式**（`WindowsPackageType=None` + `WindowsAppSDKSelfContained=true`），
因此 `Yomori.exe` **可以直接双击运行**，不需要 MSIX 安装、不需要侧载。

### ⚠️ 程序开着时改 XAML：必须换输出目录构建

**别一边开着 Yomori 一边 `dotnet build`。** 那会在复制 `Yomori.exe` 时失败（`MSB3027/MSB3021`，
文件被占用），而 **XAML 资源包 `Yomori.pri` 的更新排在复制之后** —— 于是 `.dll` 是新的、
`.pri` 里还是旧界面，启动后点页面直接 `InvalidCastException`（`Connect()` 把控件当别的类型转型）。

程序开着时想验证改动，用独立输出目录构建，再把产物同步回 `bin`（`.exe` 除外）：

```bash
dotnet build -c Release -p:Platform=x64 -p:OutDir=<临时目录>/
cp -rf <临时目录>/. bin/x64/Release/net8.0-windows10.0.19041.0/
```

最省事的做法仍是：**先完全退出程序，再正常 `dotnet build`**。

也可以用 Visual Studio 2022 打开 `winui3/Yomori.sln` 直接 F5。

### 原生层 `ycn_rdp.dll`（内嵌画面必需）

应用依赖 `src/Yomori.RdpNative/` 编出来的 `ycn_rdp.dll`（FreeRDP 桥接层）。
**它是构建产物、没有入库**，所以全新克隆之后要自己编一次：

```bash
# 1) 装 vcpkg 的 freerdp（一次，之后复用）——需要 VS 2022 + C++ 工作负载
git clone https://github.com/microsoft/vcpkg _tools/vcpkg
_tools/vcpkg/bootstrap-vcpkg.bat -disableMetrics
_tools/vcpkg/vcpkg.exe install "freerdp[client]:x64-windows"

# 2) 编桥接层（CMakeLists 默认从 <仓库>/../_tools/vcpkg 找依赖，也可用 -DVCPKG_INST 覆盖）
cmake -S winui3/src/Yomori.RdpNative -B winui3/src/Yomori.RdpNative/build \
      -G "Visual Studio 17 2022" -A x64 \
      "-DVCPKG_INST=<vcpkg>/installed/x64-windows"
cmake --build winui3/src/Yomori.RdpNative/build --config Release
```

产物是 `build/Release/ycn_rdp.dll` + 一批 FreeRDP 运行库（freerdp3 / winpr3 / openssl / zlib / cjson…），
`dotnet build` 会把它们自动拷进应用输出目录。

> 开发时对着的是 **FreeRDP 3.32.0**（对应 vcpkg 提交 `10541e31`）。CI 钉的就是这个提交；
> 换版本要重新真机验收内嵌画面。

**换 vcpkg / 换 FreeRDP 版本后，还要重跑一次许可证冻结**：

```bash
python winui3/tools/gen_third_party_notices.py --export-licenses
```

`winui3/third_party/licenses/` 里的许可证原文与 `sources.json` 里的版本号都取自这次导出的 vcpkg。
不重跑的话，发布包里声明的仍是旧版本 —— 正文本身不会变（Apache-2.0 / zlib / MIT 的条款是固定的），
但版本号会滞后。CI 原生层命中缓存时机器上没有 vcpkg，正是靠这份冻结副本出包，所以它不能缺。

### GitHub Actions：自动编译 / 测试版 / 正式版

`.github/workflows/` 下四个工作流：

| 工作流 | 触发 | 做什么 |
|---|---|---|
| `build.yml` | 被其余调用（自身不触发） | 编原生层（两级缓存）→ 编应用 → 打包 + 自检 → 上传产物 |
| `ci.yml` | push 到 main / PR / 手动 | 编译；**每一次编译都留一份独立归档**（预发布）；main 上再顺带刷新滚动 tag `test-build` |
| `release.yml` | **只能手动**（Actions → 正式版发布 → Run workflow） | 按填写的版本号打正式 tag `vX.Y.Z` 并发正式 Release |
| `cleanup-builds.yml` | **只能手动**（Actions → 清理归档构建） | 归档只增不减，用它按份数/天数瘦身（默认只演习） |

### 每编译一次就多一份归档，怎么找

CI 的每次构建都会建一个自己的预发布 tag，资产不会被下一次覆盖 —— 想回溯哪一版就去 Releases 找对应 tag：

| 触发 | 归档 tag | 说明 |
|---|---|---|
| PR | `pr-<PR号>-<构建号>` | 未合入的代码也有可下载的成品；fork 仓库的 PR 因无权写 Release 会跳过归档（产物在 Actions 页面） |
| push / 手动 | `build-<构建号>` | main 上每次合入都留一份 |

`test-build` 是**滚动标签**：总是指向 main 最新一次编译，方便只想要最新版的人；它本身会被覆盖，要回溯请用上面的 `build-*`。
因为要为每个版本单独留档，`concurrency` 设的是 `cancel-in-progress: false` —— 连续 push 会排队而不是互相取消（取消掉的那一版就没有归档了，想省 minutes 再改回 `true`）。

归档只增不减，自包含包一份几十 MB —— 攒多了用 `cleanup-builds.yml` 瘦身：默认**只演习**（把待删清单打进 job summary），确认没问题再勾「真的删除」重跑；保留规则是「最近 N 份 **或** 最近 N 天内」满足其一即留，且只认预发布、只处理 `build-` / `pr-` 前缀，`test-build` 与正式版 `vX.Y.Z` 都不会被碰。

### 版本号与「检查更新」

版本号**不写死在源码里**，来源是程序集信息版本：

| 场景 | 版本号 | 谁给的 |
|---|---|---|
| CI 测试版 | `<最新正式版修订号 +1>-dev.<构建号>`（如 `2.4.1-dev.412`） | `build.yml` 由已有 `vX.Y.Z` tag 推算 |
| CI 正式版 | 手填的 `X.Y.Z` | `release.yml` 的 `version` 输入 |
| 本机 `dotnet build` | `2.0.0-dev.local` | `Yomori.App.csproj` 里的 `Version` 兜底 |

测试版号**必须永远大于最新正式版**，否则「检查更新」会把测试版当成旧版（装测试版的人看不到更新，
装正式版的人切到测试版通道也拿不到更新）。所以 CI 不写死号，而是取已有正式版 tag 里最大的 `vX.Y.Z`，
把**修订号 +1** 再挂 `-dev.<构建号>`：最新正式版是 `v2.4.0` 时测试版就是 `2.4.1-dev.<构建号>`，
发完 `v2.4.1` 之后又自动变成 `2.4.2-dev.<构建号>`。一份正式版都没发过时从 `2.0.1-dev.<构建号>` 起。
（2026-10-28 之前这里写死 `2.0.0-dev.<构建号>`，正式版一过 2.0 就永远比正式版小。）

⚠️ CI 用 `-p:Version=` 传的是 **MSBuild 全局属性**，优先级高于项目文件里的赋值 ——
所以 csproj 里那行 `<Version Condition=…>` 不会覆盖发布版本的号，只管本机编译这一种情形。

「设置 → 关于」显示这个版本号，并提供**检查更新**：

| 更新渠道 | 查的是 | 对应发布 |
|---|---|---|
| 正式版（`stable`，默认） | `GET /releases/latest` | `release.yml` 手动发的 `vX.Y.Z` |
| 测试版（`beta`） | `GET /releases?per_page=50` 里版本号最大的一份 | CI 滚动预发布 `test-build`（资产名里解析版本号，因为 tag 里没有） |

渠道选择存进 `config.json` 的 `update_channel`。检查逻辑在 `Services/UpdateService.cs`，只**读**发布信息：
不下载、不安装、不改文件。一份正式版都没发过时 `/releases/latest` 返回 404，
程序把它当成「还没发过正式版」而不是错误。GitHub 匿名接口每小时 60 次上限，被限流会如实报出来。

发现新版本时，通知（`MainWindow` 右上角铃铛）按以下约定呈现：

| 环节 | 行为 | 实现位置 |
|---|---|---|
| 通知内容 | 版本对比 + **更新内容**（发布说明里的要点）+ 下一步提示。发布说明原文是发布页用的 Markdown（元信息表格、代码块、下载说明），由 `UpdateNotes.Summarize` 压成最多 6 条、240 字符的要点 | `Services/UpdateNotes.cs` |
| 点击通知 | 直接后台下载安装包；**该条通知随即移除**（动作已执行，留着只会被重复点击） | `MainViewModel.OpenNotification` |
| 下载中 | 通知正文显示百分比，行内进度条同步推进（只按整数百分比刷新，避免每 80KB 一次回调把消息泵塞满） | `AppNotification.Progress` |
| 下载完成 | 换成一条「安装包已下载」，点击后启动安装器并退出本程序 | `MainViewModel.StartInstallerAndExit` |
| 安装包清理 | 安装前把路径记入 `updates/pending-cleanup.json`；新版本首次启动时删除该安装包，并顺带清掉 `updates/` 里超过 7 天的旧包与 `*.part` 半成品 | `Services/UpdatePackageCleanup.cs` |
| 启动 | 通知列表**每次启动清空**（历史通知只对当时那一刻成立，例如「安装包已下载」在装完后点开只会报文件不存在） | `NotificationCenter.ResetOnStartup` |


### 每次构建出两份：安装版 / 绿色版

| 产物 | 用法 |
|---|---|
| `Yomori-<版本>-setup.exe` | **安装版**。默认装到 `%ProgramFiles%\Yomori\`，带开始菜单快捷方式与卸载入口；**双击即弹 UAC**（清单里就是 `requireAdministrator`）。安装结束时会自动部署会话代理（调 `Yomori.exe --deploy-agent`），装完即可连目标账户 |
| `Yomori-winui3-<版本>-x64.zip` | **绿色版**。解压到**固定目录**（别放临时目录，日志会写在它旁边），双击 `Yomori.exe` 即用 |

安装脚本是 `winui3/tools/installer.iss`（Inno Setup 6；CI 里**钉死 6.7.3**，本机复现要先装 Inno Setup 6）。

**安装包自己也要求管理员**（和主程序一致：双击立刻弹 UAC，不会「向导先出来、过一会儿再提权」）：
Inno 的引导器模板 `SetupLdr.e32/e64` 清单里写死 `asInvoker`，`PrivilegesRequired=admin`
只是让它启动之后再 runas 一次。所以 CI 会先把**编译器模板**的清单改成 `requireAdministrator`
（`winui3/tools/patch_setup_manifest.py`），再用这套模板编译；`installer.iss` 里那句
`DisablePrecompiledFileVerifications=setupldr` 就是为这一步准备的（Inno 6.7 起会校验模板哈希）。
CI 编译完还有一步断言：真去读产物清单，不是 `requireAdministrator` 就判红，
同时挡一下「安装包只剩 950KB 引导器」这种残缺产物。

> ❌ **别改「已经编好的」setup.exe**：它是「PE 引导器 + 追尾在文件尾的安装数据」，
> `mt.exe` 与 Win32 的 `UpdateResource` 写回时都会把追加数据整段丢掉 ——
> 实测 75MB 的安装包被截成 1,000,448 字节，双击只会报「安装数据损坏」。
> `patch_setup_manifest.py` 现在遇到带追加数据的文件会直接拒绝。
>
> ❌ 也别加 `PrivilegesRequiredOverridesAllowed`：它允许 `setup.exe /LOWEST` 强行按非管理员
> 装到用户目录，跟「装到 `{commonpf}`」这个前提冲突。

**「安装完成后启动」那条 `[Run]` 必须带 `runascurrentuser`**：Inno 对带 `postinstall` 的条目
**默认用 `runasoriginaluser`**（提权前那个普通令牌），拿它去 `CreateProcess` 一个
`requireAdministrator` 的程序会被拒 —— 用户点「完成」就弹
`CreateProcess failed; code 740 请求的操作需要提升`，程序反而没起来
（日志里是 `Run as: Original user` 紧跟 `code 740`）。`runascurrentuser` 继承 Setup 自己的
管理员令牌，必定能起。

装到 Program Files 后，程序要往安装目录写 `config.json` / `logs` / `runtime_stats` —— 所以主控端**声明式要求管理员**：
`app.manifest` 里是 `requestedExecutionLevel = requireAdministrator`，**双击即由系统弹 UAC**，一次到位，
不会出现"先起一个普通权限进程再拉起第二个"的双进程与窗口闪烁。
UAC 被拒时会弹窗说明原因并引导「以管理员身份运行」——不会静默按原权限往下跑
（那样只会在后续某处报一个跟权限八竿子打不着的错，用户极难反推回真正原因）。

会话代理（`--rdp-agent`）**必须保持不提权**：它是目标账户登录时由启动目录快捷方式**无人值守**拉起的，弹 UAC 没人点。
但代理副本是主控端**整个目录原样复制**的同一个 exe、同一份内嵌 manifest，而 `requestedExecutionLevel` 是
**编译期静态**的、没法按参数豁免 —— 所以 `DeployAgent` 在铺完副本后会用 `mt.exe` 把**代理那份 exe** 的
内嵌 manifest **就地改回 `asInvoker`**，主控端那份不动。少这一步，整个 RDP 代理功能会失效
（表现为「通道连上了但任务不执行」，而部署日志一切正常）。

> ❌ 别改用「计划任务 / schtasks + `/RL HIGHEST`」那条路来做免 UAC：那条路真机验证过，
> 连续 6 轮 `触发计划任务失败：错误: 找不到元素。`，代理根本起不来（见 `docs/` 与历史记录）。
> 公共启动目录快捷方式才是被验证过的选型。

> 数据文件不在安装脚本的 `[Files]` 里 —— 覆盖安装会保留配置；卸载后 `config.json` / `logs` 仍留在安装目录，要清掉自己删。
- 正式版会校验版本号格式，并**拒绝复用已存在的 tag**；可勾「先存为草稿」再公开；
- 首次 CI 要现编 FreeRDP（约 20-40 分钟），之后命中缓存只需一两分钟；
  **只改 C# 时原生层整段直接跳过**（按 `ycn_rdp.c/h` + `CMakeLists.txt` 的哈希缓存）。

**在本机复现 CI 的编译与打包**（产物落在 `dist/`，命令与工作流里逐字一致）：

```bash
# 1) 编译（前提：原生层产物已在 src/Yomori.RdpNative/build/Release/）
dotnet build winui3/src/Yomori.App/Yomori.App.csproj -c Release -p:Platform=x64 -p:OutDir=ci-build/

# 2) 自包含发布（省掉这一步就是框架依赖包 —— 体积小，但目标机要装 .NET 8 运行时）
dotnet publish winui3/src/Yomori.App/Yomori.App.csproj -c Release -p:Platform=x64 \
  -r win-x64 --self-contained true -o ci-publish

# 2.5) 发布会话代理（独立工程，不含界面栈）。产物会被铺进安装目录的 agent\，
#      部署到目标账户时只搬这约 73MB，而不是整套主控端的约 228MB。
#      自包含开关要与主控端一致。
dotnet publish winui3/src/Yomori.Agent/Yomori.Agent.csproj -c Release -p:Platform=x64 \
  -r win-x64 --self-contained true -o ci-agent

# 3) 打包 + 自检（缺 .pri / XBF / FreeRDP 运行库 / 代理载荷 / 第三方许可声明会直接报错退出）
#    这一步还会生成 THIRD-PARTY-NOTICES.txt：原生组件的许可证原文取自 vcpkg
#    （默认 <仓库>/../_tools/vcpkg，CI 里用 --vcpkg-inst 指到 .vcpkg/installed/x64-windows）
python winui3/tools/package_release.py --build-dir ci-build --publish-dir ci-publish \
  --agent-dir ci-agent --assets assets --out dist --name Yomori-winui3-2.1.0-x64 --version 2.1.0

# 4) 编安装程序 —— 先把编译器目录复制一份，改掉模板清单再编（Program Files 里的别动）
cp -r "/c/Program Files (x86)/Inno Setup 6" inno-patched
python winui3/tools/patch_setup_manifest.py inno-patched/SetupLdr.e32 inno-patched/SetupLdr.e64
inno-patched/ISCC.exe winui3/tools/installer.iss \
  /DMyAppVersion=2.1.0 /DSrcDir="$PWD/dist/app" /DOutDir="$PWD/dist" /DIconFile="$PWD/assets/icon.ico"

# 5) 验收：产物清单必须是 requireAdministrator
python winui3/tools/patch_setup_manifest.py dist/Yomori-2.1.0-setup.exe --expect-admin
```

> **为什么不能直接发 `dotnet publish` 的输出**：WinUI 3 的 `Yomori.pri` 与各页面的 `*.xbf`
> 只存在于 **build** 输出（`Views/`、`Themes/`、`embed/`），FreeRDP 那批运行库也是 ——
> publish 目录里没有它们（实测），缺了程序起不来 / 页面打不开。
> `package_release.py` 负责把两边合并、补上 `assets/` 素材与使用说明，
> 并在关键文件缺失或误打包 `config.json` 时**直接失败**（宁可 CI 红，也不发一个跑不起来的包）。

> ⚠️ **合并顺序：build 先铺、publish 后铺，且 build 的 `Yomori.exe` /
> `Yomori.runtimeconfig.json` / `Yomori.deps.json` 一律不复制。**
> 第 1 步的 `dotnet build` 没带 `-r win-x64`，产出的是**框架依赖**版 ——
> 它的 runtimeconfig 写 `"framework": Microsoft.NETCore.App 8.0.0`，
> 装到没装 .NET 的机器上双击就弹 *"You must install or update .NET to run this application"*。
> 早先的顺序是反的（publish 先、build 后），框架依赖那份把自包含的盖掉了，
> 而 `System.Private.CoreLib.dll` 还在（是 publish 铺的），旧自检只看它有没有 → **查不出来，CI 全绿**。
> 现在打包脚本会直接读 `runtimeconfig.json`：自包含包必须是 `includedFrameworks`，
> 出现 `framework` 就报错退出；`build.yml` 在编安装程序前也会再核一遍。

---

## 四、目录结构

```text
winui3/
├─ Yomori.sln                   # 只包含 Yomori.App
├─ docs/                            # 设计文档（内嵌 RDP / 多会话通道 / 菜单重构计划书）
├─ third_party/
│  ├─ components.json               # 第三方组件清单（唯一数据源：界面与打包脚本共用）
│  └─ licenses/*.txt                # 各组件许可证原文副本（CI 拿不到 vcpkg 时的回退）
├─ tools/package_release.py         # 发布打包：合并 build+publish 输出、补素材、关键文件自检
├─ tools/gen_third_party_notices.py # 生成 THIRD-PARTY-NOTICES.txt（第三方组件清单 + 许可证原文）
├─ tools/installer.iss              # 安装程序脚本（Inno Setup 6）
├─ tools/patch_setup_manifest.py    # 把 Inno 模板 SetupLdr 的清单改成 requireAdministrator
├─ src/Yomori.RdpNative/        # 原生 FreeRDP 封装（ycn_rdp.c / .h + CMakeLists.txt）
│  └─ ycn_rdp.c                     #   多会话上限 YCN_MAX_SESSIONS = 8
├─ src/Yomori.Agent/            # 会话代理（独立进程，不含界面栈）
│  └─ Yomori.Agent.csproj           #   链接 App 的 Models/Services，产物铺进安装目录 agent\
└─ src/Yomori.App/
   ├─ Yomori.App.csproj
   ├─ app.manifest                 # asInvoker + PerMonitorV2 + UTF-8
   ├─ App.xaml / App.xaml.cs       # 应用入口、全局资源、异常兜底
   ├─ Themes/Styles.xaml           # Yomori 雾蓝主题（YomoriAccent 等 Brush / 卡片样式）
   ├─ Models/
   │  ├─ Enums.cs                  # WaitModes / TimeoutActions / ConcurrentPolicies
   │  ├─ RdpModels.cs              # RdpConfig / RdpResolutions / RdpCommand / RdpStatus / RdpTaskEvent / RdpHeartbeat
   │  ├─ RdpChannel.cs             # 会话通道模型（主机 / 账户 / 桥目录 / 收尾 / 凭据标记）
   │  ├─ RdpChannelPaths.cs        # 桥目录规则（按账户名分目录，纯函数）
   │  ├─ RdpChannelPlanner.cs      # 分组 + 启动计划 + 并发上限（纯函数）
   │  ├─ RunSummary.cs             # 整轮汇总（纯函数）
   │  ├─ ChannelOverviewItem.cs    # 主页通道概览快照行
   │  ├─ RdpClientStateMachine.cs  # 内嵌客户端连接状态机
   │  ├─ RdpInputMapper.cs         # 鼠标 / 键盘 → RDP 输入事件
   │  ├─ RdpErrorText.cs           # RDP 错误码 → 中文说明
   │  ├─ TaskConfig.cs             # 单条任务配置（含 channel_id，键名与 config.json 完全一致）
   │  ├─ TaskScopePlanner.cs       # 【按通道编排任务】分组读取 / 通道内移动 / 改派 / 序号重编（纯函数）
   │  ├─ ChannelScope.cs           # 任务页左侧「执行通道」列表的一项（本地 / 通道 / 未知通道）
   │  ├─ AppConfig.cs              # 全局配置 + 旧单通道配置迁移
   │  ├─ WindowConfig.cs           # 主窗口位置/尺寸（记忆上次窗口大小）
   │  └─ MonitorSpec.cs            # 监控目标描述
   ├─ Services/
   │  ├─ AppPaths.cs               # 配置/日志/统计/素材路径统一入口
   │  ├─ WindowPlacement.cs        # 窗口几何收敛（钳到当前显示器工作区，纯函数可单测）
   │  ├─ RdpBridge.cs              # 指令桥（按通道实例化，ProgramData 下 command / status / stop.json）
   │  ├─ RdpChannelSession.cs      # 【每通道一份】运行时状态：指令归属 / 进度 / 心跳 / 收尾幂等
   │  ├─ RdpSessionService.cs      # 本机环回 RDP：预检、凭据、连接、会话查询、断开/注销、代理部署
   │  ├─ RdpAgentRunner.cs         # 目标会话里的常驻执行代理（--rdp-agent 模式）
   │  ├─ RdpEmbeddedClient.cs      # 内嵌会话客户端（每实例一条会话）
   │  ├─ RdpD3DRenderer.cs         # D3D11 交换链渲染
   │  ├─ RdpNativeInterop.cs       # ycn_rdp.dll 的 P/Invoke 封装
   │  ├─ RdpCredentialStore.cs     # 凭据管理器（键 = 主机|账户）
   │  ├─ LowLevelKeyboardHook.cs   # 全屏时 WH_KEYBOARD_LL 键盘接管
   │  ├─ PendingNotificationStore.cs # 锁屏期间落暂存，回前台补发（仅主控端）
   │  ├─ NotificationService.cs    # 主控端系统通知（经典 Toast API）；代理侧不发
   │  ├─ ConfigManager.cs          # config.json 读写 + 旧键迁移 + 损坏备份
   │  ├─ FileLogger.cs             # 按启动批次分文件日志 + SESSION 标记
   │  ├─ RuntimeStatsManager.cs    # 耗时统计（json / csv / summary）
   │  ├─ NativeMethods.cs          # Win32 P/Invoke 声明
   │  ├─ ProcessHelper.cs          # tasklist / taskkill / 窗口枚举 / 关键词匹配
   │  ├─ MonitoredProcess.cs       # 进程包装 + UAC 提权启动（runas）
   │  ├─ CommandLine.cs            # Windows 风格命令行拆分（CommandLineToArgvW）
   │  ├─ ScriptRunner.cs           # 任务执行引擎（核心）
   │  ├─ SystemServices.cs         # 开机自启动（HKCU Run）、自动关机
   │  └─ ScreenshotService.cs      # 超时现场全屏截图
   ├─ Helpers/
   │  ├─ ObservableObject.cs       # INotifyPropertyChanged 基类
   │  ├─ RelayCommand.cs           # ICommand（含 AsyncRelayCommand）
   │  ├─ KeywordHelper.cs          # 关键词归一化（兼容 ; , ， 分隔）
   │  ├─ FormatHelper.cs           # 时间格式化
   │  ├─ WindowHelper.cs           # 取主窗口 HWND（WinUI 3 未直接暴露）
   │  └─ DialogHelper.cs           # 文件选择器 / 消息对话框
   ├─ ViewModels/
   │  └─ MainViewModel.cs          # 单一主 ViewModel：任务 / 通道 / 并行调度 / 面板聚合
   └─ Views/
      ├─ MainWindow.xaml(.cs)      # NavigationView + 自定义标题栏 + 「会话通道」父项与子菜单
      ├─ RdpMultiViewPage.xaml(.cs) # 多画面页：全部通道 2×2 网格（父项「会话通道」进这里）
      ├─ ChannelsPage.xaml(.cs)    # 通道管理：总开关 / 通道列表与增删复制 / 编辑卡 / 每通道预检
      ├─ RdpChannelPage.xaml(.cs)  # 单条通道的画面页（本通道画面 + 进度 / 心跳 / 事件）
      ├─ RdpSurfaceCell.xaml(.cs)  # 多画面里的一格（画面 + 通道名/状态 + 全屏·弹出·断开）
      ├─ RdpView.xaml(.cs)         # 内嵌画面控件（D3D 交换链 + 输入 + 帧率统计）
      ├─ RdpFullScreenCoordinator.cs / RdpFullScreenWindow.cs   # 全屏编排（两页共用）
      ├─ RdpChannelWindow.cs       # 通道「弹出独立窗口」（复用同一 client，不带钩子）
      ├─ HomePage.xaml(.cs)        # 主页（顶部一张可右键更换的图 + 三张入口卡）
      ├─ TasksPage.xaml(.cs)       # 任务管理：三栏（执行通道 / 通道内任务 / 任务详情），按通道编排
      ├─ StatsPage.xaml(.cs)       # 耗时统计
      ├─ LogsPage.xaml(.cs)        # 运行日志
      ├─ SettingsPage.xaml(.cs)    # 设置
      └─ ShutdownCountdownDialog.xaml(.cs)  # 关机倒计时
```

代码量约 **19000 行**（76 个 `.cs` / `.xaml`，不含 obj 下的自动生成文件），全部在 `Yomori.App`。
> 早期那个承载 COM ActiveX 的 WinForms 类库 `Yomori.RdpHost` 已随「改用 FreeRDP 内嵌」一并删除，不再出现在目录里。

---

## 五、移植对照表

Python → C# 的逐项对照（核心逻辑 / 界面 / 主题）已归档到 [docs/migration-from-python.md](docs/migration-from-python.md#二移植对照表)。

---

## 六、已完整移植的执行语义

监控优先级、清理顺序、超时动作等逐条对齐清单已归档到 [docs/migration-from-python.md](docs/migration-from-python.md#三已完整移植的执行语义)。

---

## 七、会话通道（多通道并行执行）

### 能做什么

一条**会话通道** = 一台目标主机 + 一个目标账户 + 它自己的凭据与指令桥目录。

把任务分配给不同通道，点「开始执行」后：

- **通道之间并行**：每条通道各自下发指令、各自连自己的目标账户，互不等待；
- **通道内部串行**：同一条通道里的任务仍按「任务执行」页的顺序一条条跑；
- **没分配通道的任务**（下拉里选「本地执行（当前会话）」）跑在 Yomori 自己所在的会话里，
  和通道任务同时进行。

远程画面直接**内嵌**在 Yomori 窗口里（FreeRDP + D3D 交换链），鼠标键盘可以直接操作；
也可以改成用系统自带的 `mstsc` 开独立窗口。

菜单是**两级**的（计划书 [docs/channel-menu-multiview-plan.md](docs/channel-menu-multiview-plan.md)）：

```text
左侧菜单
├─ 会话通道 ▾                 ← 点它本身 = 多画面（全部通道 2×2 网格）
│   ├─ 多画面（全部通道）      ← 同一个页面，一个确定能点的入口
│   ├─ 带 ⛓ 的通道项（每条一个）← 该通道单独的画面 / 进度 / 心跳 / 事件
│   └─ 通道管理…              ← 配置通道、凭据、部署代理、预检
…
```

- 子菜单里列的是**配置里启用中的通道**（不跑任务也能点进去手动连画面看），
  本轮在跑的通道一定会在；改通道配置后菜单即时跟着变。
- 多画面网格每页 4 格（2×2），超过翻页；每格可「弹出窗口」；
  **多画面不给全屏**（会盖掉别的路），要放大某一路请进它自己的通道页。

**「连接」与「开始执行」是分开的两件事**：

1. 想先看画面：在多画面页点「连接全部通道」，或在通道页点**「连接本通道」** →
   立刻建立内嵌连接，先手动把游戏 / 脚本准备好；
2. 点**「开始执行」** → 按任务分配下发到各通道（还没连的会先自动连接）。

断开画面（画面格子上的「断开」）只释放内嵌连接，**目标会话和里面的代理都保留**；
要销毁会话得用「注销目标账户」或把该通道的收尾方式设成「注销」。

### 目标主机可以填什么

| 用法 | 目标主机填什么 | 典型场景 |
|---|---|---|
| 切本机另一个账户（默认） | `127.0.0.1` | 游戏或脚本装在另一个账户下、需要另一套用户环境、想把挂机环境和日常环境隔开 |
| 连另一台机器 | 局域网 IP / 主机名 / 公网地址，可带端口（如 `192.168.1.20:3390`） | 主机和挂机机分开、多台机器统一由一台下发任务 |

**一台机器上放多条通道**：每条通道请配**不同账户** —— 同一个账户开两条通道没有意义（它们会抢同一个会话）。
另外，本机能不能同时跑多条通道，取决于**系统是否允许多个交互式会话并存**：系统允许多会话时可以真正并行；
普通 Windows 客户端版同一时刻只允许一个交互式会话，第二条本机通道会把前一个桌面顶掉 ——
这种情况下建议把通道分到不同机器。通道管理页对「同一主机有多条通道」只做**提示**，不禁止。

### 工作原理

Windows 会话之间不能直接通信，主控端也无法跨会话启动进程，所以用「共享文件 + 登录自启代理」来搭桥。
**每条通道一套桥目录**，互不干扰：

```text
主控端（Yomori，账户 A）
  ├─ 1. 任务分组：channel_id 空 → 本地执行；= ch-xxx → 那条通道
  ├─ 2. 对每条通道各写一份 command.json，各自轮询自己的 status.json
  ├─ 3. 停止执行 → 给每条通道各写一份 stop.json；
  │     之后每 5 秒确认一次代理回执，没回执就补发（最多 5 次）
  └─ 4. 画面：每条通道各持一个内嵌客户端（一个客户端同时只能挂一块画面）

桥目录（每通道一份，按账户名隔离）
  ├─ 本机默认：%ProgramData%\Yomori\rdp\<账户名>\
  └─ 远程目标：你填的共享路径（两台机器都能读写）

目标会话（账户 B / 远程机器）
  ├─ 1. 账户登录后，公共启动目录里的快捷方式自动拉起 Yomori.exe --rdp-agent
  ├─ 2. 代理**常驻**：循环「等指令 → 执行 → 回等待」，等待期每秒写一次 idle 心跳
  ├─ 3. 读到 command.json 立刻删掉（防重复执行），用同一套 ScriptRunner 跑任务队列
  ├─ 4. 每完成一个任务：写回 status.json 的事件（**不发通知**：目标会话里的 Toast
  │      主控端看不到，通知统一由主控端发）
  └─ 5. 收到 stop.json → 相位切成 stopping 作为回执、删掉请求文件、停止本轮；
        全部结束后按该通道的收尾方式处理会话，并清掉可能残留的 stop.json
```

**停止请求由一条独立的 500ms 循环监听**（`StartStopWatcher`），与任务执行并行。
不要把它挂回 `ScriptRunner` 的每秒计时回调上 —— 那个回调只在任务主循环里触发，
执行流程停在启动确认 / 接力确认 / 并发组等待 / 进程清理时就没人读了，
表现是"点了停止，代理那侧毫无动静"（真机复现过）。
同理，`ScriptRunner.RequestStop` 里**不能**做整轮关键词清理（要枚举全部进程 + 逐个 `taskkill`，
实测十几秒），那会把工作线程堵死、进度心跳全停；它只置标志位 + 结束已登记的直接脚本进程，
三层清理统一由 `RunCore` 收口处的 `CleanupForStop()` 做。

⚠️ **收口处必须兜住"启动确认阶段被停止打断"这条路**：`ConfirmLaunchSuccess` /
`TryConfirmRelayLaunch` 一看到停止请求就返回 false，而那条分支只放弃等待、不做清理 ——
若把清理只挂在任务主循环里，目标进程（含提权 / 单实例接力起来的新实例）就会活下来，
用户看到的是"点了停止，游戏还在跑"。所以那条分支在停止时**不解除登记**，
把进程留给 `CleanupForStop()`；清理范围取**全部任务配置** —— 界面上只有一个停止按钮，
没有"更彻底的那个"可以再点一次，所以一次就要清到位（含「启动后不等待」那类已脱离本轮的进程）。

**完成信号走事件通道，不看相位**：代理写下的 `done` 相位毫秒内就会被下一拍空闲心跳覆盖，
主控端 1Hz 轮询根本撞不上（历史上有过"任务跑完了但界面没反应"）。
所以完成统一认 **status.json 里的 `Finished` 事件** —— 空闲心跳不会碰事件列表。

**为什么远程主机要多填一个「指令桥目录」**：`%ProgramData%` 是本机路径，远程机器上的代理根本读不到。
所以目标是别的机器时，必须指定一个两边都能访问的位置（网上邻居共享、NAS、同步盘都行），
主控端写指令、代理读指令都走那里。部署代理时这个路径会自动写进快捷方式参数。

目标是本机另一个账户时这项**留空即可** —— 留空时按账户名自动分目录（`rdp\<账户名>\`），
一条通道一个目录，多条通道不会串数据。

### 前置条件

| 条件 | 说明 |
|---|---|
| 目标主机 | 默认 `127.0.0.1`（本机）。可填 IP / 主机名，支持 `IP:端口`；填 `rdp://xxx` 也会自动去掉前缀 |
| Windows 版本 | 目标机需要能接受 RDP 连接。专业版 / 企业版 / 工作站版原生支持；家庭版可用设置页「解锁 RDP」自动把 3389 起来（一次没有成功可以多试几次），程序按「端口是否在监听」判断，不按系统版本拦截。连远程主机时本机只当客户端，对版本没有要求 |
| 远程桌面 | 目标机器上需开启（设置 → 系统 → 远程桌面）。本机多账户同样要先开启；应用内不再提供一键开启。判定依据是注册表开关 `fDenyTSConnections`，不成立时再看 3389 是否在监听 |
| 目标账户 | 必须已创建，且**必须设置过登录密码**（空密码账户无法通过 RDP 登录）。一条通道一个账户，**别让两条通道共用一个账户** |
| 系统会话数 | 本机想同时跑多条通道，需要系统允许多个交互式会话并存。普通 Windows 客户端版同一时刻只允许一个 —— 设置页「解锁 RDP」可**自动解除**这一限制（一次没有成功可以多试几次）；多机或 Server 多会话则天然支持 |
| 通道数上限 | 一次最多 **8 条**通道并行（原生层上限）。超出的通道会被截断，开始执行时会弹一次说明，日志里也有警告 |
| 凭据 | 用通道编辑卡里的「保存凭据」存入 Windows 凭据管理器，**按「主机 + 账户」各存一条**，密码不写进 `config.json` |
| 账户权限 | 保存本机通道的凭据后，程序会把该账户**设为本机管理员**（加入 Administrators）。已是管理员则只提示「无需修改」。组成员关系只追加不删除 —— Users、Remote Desktop Users 等原有关系全部保留。新权限需要该账户**注销并重新登录**后才由新登录令牌体现，程序不会强制注销或断开已有会话。确认框里取消只取消授权，已经保存的凭据仍然保留。远程目标账户不在这里处理，需在目标计算机上自行设置 |
| 会话代理 | 点「部署会话代理」放到所有用户公共启动目录（需管理员，一次性）。远程目标要在**对方机器**上部署 |
| 指令桥目录 | **仅远程主机需要**。填双方都能读写的 UNC 或盘符路径，例如 `\\192.168.1.20\YomoriBridge`；本机多账户留空即可 |

### 解锁系统多会话限制

设置页「远程桌面解锁（RDP）」卡片，主按钮是**「解锁 RDP」**：自动下载多用户支持组件、
接管 TermService、写好配置并重启服务，不需要再手动照第三方教程操作。旁边的
「还原注册表」只回滚本功能改过的键值，「卸载」调用官方卸载器；三者不能同时运行。

> **一次没有成功可以多试几次**：不同环境下组件下载、服务重启的耗时不一，个别步骤
> 偶发失败重试即可。实现检查记录见 [docs/rdp-unlock-review-2026-10-26.md](docs/rdp-unlock-review-2026-10-26.md)。

**手动兜底**：多次重试仍失败时，卡片里的「打开排查教程」会打开第三方 RDP Wrapper
手动安装教程，按它在系统里手动装完，再回来点「重新检测」确认状态。这条路径不在程序的
自动化流程里（没有事务记录与失败补偿），风险自行评估。

### 会话代理是怎么部署的

点「部署会话代理」时做两件事：

```text
1. 复制程序到公共目录
   源：当前运行的 Yomori.exe 所在目录（可能是 C:\Users\<你的账户>\... 下的开发产物）
   目标：C:\ProgramData\Yomori\agent\
   跳过：config.json / logs/ / runtime_stats/   ← 目标账户要自己生成这些

2. 建快捷方式
   C:\ProgramData\Microsoft\Windows\Start Menu\Programs\Startup\Yomori RDP 会话代理.lnk
   TargetPath = C:\ProgramData\Yomori\agent\Yomori.exe
   Arguments  = --rdp-agent [--bridge:<桥目录>]
```

**为什么非要多复制这么一份**（早期版本踩过坑）：

第一版直接用主控端 exe 的路径写快捷方式。但主控端 exe 通常在 `C:\Users\<你的账户>\` 下，
而 `C:\Users\` 下的目录**不给其他普通用户读权限**。目标账户登录后启动快捷方式直接被拒绝，
而且 Windows 对启动项失败是**静默的** —— 表现为"远程桌面连上了，但任务就是不执行"，日志里什么都看不到。

复制到 `%ProgramData%` 就绕开了这个问题：这个目录默认对 `BUILTIN\Users` 开放读权限。

复制只在程序有更新时发生（比对 `Yomori.exe` 的写入时间），平时点「部署」是秒过的。
238 个文件约 142MB，本地 SSD 实测 600ms 左右。

> 如果你把 Yomori 装在 `%ProgramData%\Yomori\agent\` 下再点部署，程序会识别出"源就是目标"直接跳过复制。

「移除代理」会同时删掉快捷方式和那份程序副本。

**副本目录被占用时怎么办（三档策略）**

代理是常驻进程，它会一直持有副本目录里的 DLL —— 所以“覆盖部署”有时会被系统拦住。
遇到这种情况程序会按下面三档退让，**不会**把原因归到“你需要管理员权限”（那是误报）：

| 档 | 条件 | 做法 |
|---|---|---|
| ① | 副本目录未被占用 | 原地覆盖，路径不变（最常见） |
| ② | 被占用，且旧代理**空闲** | 先把空闲的旧代理送走，再原地覆盖 |
| ③ | 旧代理**正在跑任务**，或跨会话杀不掉 | 拒绝部署（跑任务时） / 改用备用目录 `agent_b` 并把快捷方式改指过去 |

> 旧代理**正在执行任务时不会硬来**（会先读目标账户那路桥的 `status.json`，相位是 `running` 就拒绝），
> 免得打断跑了一半的活。部署成功后旧代理已被结束，**要注销目标账户再重新连一次**，启动项才会拉起新代理。

> 曾试过“把旧副本目录整个改名挪开再重建”—— 实测走不通：只要目录内有文件被持有句柄，
> `Directory.Move` 一律返回 Access denied（`FileShare` 四种模式全试过）。别回头再试。

### 画面：内嵌 / 独立窗口 / 多画面同屏

**内嵌（默认）**：画面直接画在「会话通道」的多画面页与各通道子页里（FreeRDP + D3D11 交换链），不是 COM ActiveX 那一套。
鼠标键盘点上去就能操作远端；全屏时 `Win` / `Alt+Tab` 等系统组合键会转投远端，`F11` 退出全屏。
**画面上的双击不做任何本地切换**——它原样转投远端（双击图标、选中文字都照常）。

> 早期确实走过 COM ActiveX + `SetParent` 的路（那个 WinForms 类库 `Yomori.RdpHost`），
> 在 WinUI 3 上要跟 XAML 抢 Z 序、还要处理 STA / 坐标换算 / 分辨率动态重协商，稳定性代价太大，已彻底删除。
> 现在用原生 FreeRDP 自绘画面，不涉及子窗口 Z 序问题。

**独立窗口（`mstsc`）**：通道管理页把「连接方式」改成「独立窗口」即可，
连接时打开系统自带的远程桌面客户端 —— 最稳、永远可用，但画面不在 Yomori 里。

| 项 | 内嵌 | 独立窗口（mstsc） |
|---|---|---|
| 画面位置 | 多画面页 / 各通道子页里（可多路并列） | 独立的远程桌面窗口 |
| 鼠标键盘 | 直接操作 | 直接操作 |
| 全屏 | 有（独立全屏窗口 + 键盘接管，仅在单通道页） | mstsc 自带 |
| 分辨率 | 跟随画面格子大小自动适配 | 按「远程桌面窗口尺寸」档位生成临时 `.rdp` |
| 多画面同屏 | 支持 | 不支持（每个窗口一套） |

**多画面同屏**：左侧菜单点「会话通道」父项（或其子项「多画面（全部通道）」）进多画面页；
进某条通道自己的页面就是那一路的单画面。

- **多画面页**：2×2 四格并列显示全部通道，每格标通道名与状态；格子里没画面的通道
  直接给「连接到这条通道」，所以不跑任务时也能先看画面（也有「连接全部通道」一键连）；
  通道多于 4 条时出现「上一页 / 下一页」（翻页，不是滚动）；
- **通道页**：只摆本通道一格，占满整个画面区，保留「全屏」（`F11`）；
- 每格有「弹出窗口」→ 把这路画面弹成一个普通可缩放窗口，你可以自己并排摆放；
  关掉窗口后画面**自动回到**页面里那一格；
- 多画面页**不提供全屏**（会盖掉其它几路），要放大某一路请进它自己的通道页；
- 为什么每页 4 格而不是 8：8 路画面同时渲染的 CPU / GPU 开销不可接受；
  分页天然保证「同时渲染不超过 4 路」，用不到的画面格一律解挂（不占渲染）。

**渲染纪律**（排障时有用）：一个内嵌客户端同一时刻只能挂一块画面。
所以画面在「页面格子」和「弹出窗口」之间转移时，一定**先解挂再挂载**。
若某路画面黑屏或几路互相闪烁，先关掉那路的弹出窗口再重开一次。

### 自定义分辨率

分辨率**按通道**配置（通道编辑卡里的「远程桌面窗口尺寸」），写进 `config.json` 该通道的节点里：

| 档位 | 内嵌模式 | 独立窗口模式 |
|---|---|---|
| 自动（默认） | 跟随画面格子大小自动适配 | 不生成 `.rdp`，`mstsc /v:<主机>` 用系统默认尺寸 |
| 1280×720 / 1366×768 / 1600×900 / 1920×1080 / 2560×1440 | 按该尺寸向服务端申请桌面 | 按该尺寸生成临时 `.rdp` 连接 |

独立窗口模式下，选了具体档位会先在 `%LocalAppData%\Yomori\session-<通道id>.rdp` 生成一份配置，再 `mstsc <该文件>`：

```ini
full address:s:192.168.1.20
server port:i:3390          # 填了端口才有这行
screen mode id:i:1          # 必须写：不写会走全屏，宽高被忽略
desktopwidth:i:1920
desktopheight:i:1080
smart sizing:i:1            # 窗口缩小时整体等比缩放，不出现滚动条
dynamic resolution:i:0      # 必须写：:1 会跟随窗口而忽略上面两个尺寸
use multimon:i:0            # 必须写：否则可能继承上次设置而横跨多屏
prompt for credentials:i:0  # 凭据走凭据管理器，不弹输入框
```

后三个项（`screen mode id` / `dynamic resolution` / `use multimon`）是踩过坑才钉死的：
`.rdp` 里没写的项会拿「上次会话设置」补齐，少写一个就可能是全屏窗口或横跨多屏。

注意点：

- **改档位后要重新连接才生效**（内嵌连接也一样，要重连一次）；
- 独立窗口模式下 `.rdp` **按通道分文件**（`session-<通道id>.rdp`），多条通道并行时不会互相覆盖；
- 尺寸超出范围会被收敛到 `640×480` ~ `4096×4096`；
- `mstsc /w: /h:` 在给了 `.rdp` 文件时会被忽略，所以尺寸必须写进文件里。

`smart sizing:i:1` 是让体验好一点的关键：窗口拉小的时候画面整体缩小、比例不变，
而不是出现滚动条让你拖着看。

### 怎么用

**第一次配置（只需做一次）**

1. 打开左侧「**会话通道**」→ 子项「**通道管理…**」（通道功能恒为开启，页面上没有总开关）；
2. 点「**新建通道**」，在下面的编辑卡里填：通道名称（留空会按账户名自动起）、目标主机、目标账户；
3. 点「**保存凭据**」填一次该账户的密码（存进 Windows 凭据管理器，不写进 `config.json`）；
4. 点「**部署会话代理**」（一次性，需要管理员；远程目标要在**对方机器**上做这一步）；
5. 点「**重新预检**」，确认那一行显示环境就绪、代理已部署。
   目标账户至少要**登录过一次**，代理才会被启动项拉起来并显示「在线」。

> 一套通道要配两个不同账户时：再点一次「新建通道」就行。
> 文本字段填完**切走焦点就会自动保存**（也有「保存通道设置」按钮兜底）。

**把任务排进通道（按通道编排）**

打开「**任务执行**」页，它是三栏：

- **左：执行通道** —— 第一项固定是「本地执行（当前会话）」，后面是配置里的各条通道，
  末尾若有「未知通道」说明有任务指向了已被删除的通道（可以移走，见下）；
- **中：这条通道的任务** —— 「添加 / 删除 / 上移 / 下移」都只作用于当前通道，
  序号就是这条通道里的执行顺序；
- **右：任务详情** —— 顶部一行「所属：<通道名>」，旁边的「移到通道…」可把任务改派到别的通道
  （或移回本地执行），它会被放到目标通道末尾。

没分配通道的任务归「**本地执行**」，跑在 Yomori 自己所在的会话里 —— 这也是新建任务的默认值，
免得忘了选通道就悄悄跑远程。通道之间并行，通道内部按这里的顺序一条条跑。

**跑一轮**

1. 点顶栏「**开始执行**」。日志会先写一行「本轮启动 N 条会话通道并行执行：…」；
2. 左侧菜单「会话通道」**下面就是各条通道**（配置里启用中的 + 本轮在跑的），
   点一条进去 = 该通道的画面 + 进度 + 心跳 + 事件；点父项本身 = **多画面**（全部通道网格）；
3. 主页的「会话通道」概览卡一眼看完各通道状态（点一行跳过去）；
4. 一条通道内部的任务按顺序跑；顶栏「停止执行」在远程 / 并行执行时**也是可用的**，
   会向**所有**通道下发 `stop.json`；
5. 过程中：每跑完一个任务弹一条「任务已完成（i/n）」、每条通道整条跑完弹一条「通道「x」任务结束」
   （都由主控端发，`rdp.notify_on_task_done` / `rdp.notify_on_channel_done` 可关）；
6. 全部结束后：日志出现 `[汇总] 共 N 块（本地 X / 通道 Y）：成功 …、异常 …。`，
   并弹**一条**汇总通知。

> 想盯内嵌画面的渲染细节（D3D 有没有起来、走的是哪条渲染路径、帧到达节奏）就去
> 「设置 → 开发者」打开**开发者模式** —— 关着时这些带 `[embed]` 标签的诊断行不上「运行日志」页
> （挂着画面时每 5 秒一条，几路并行会把任务进度顶出屏幕）。日志文件里始终完整保留。

**连另一台机器**

1. 通道编辑卡的目标主机填对方的 IP 或主机名；
2. 指令桥目录填双方都能读写的共享路径（本机多账户留空即可）；
3. 在**对方机器**上装 Yomori，用同样的桥目录部署会话代理（这一步只能在对方机器上做）；
4. 点「重新预检」确认没有阻塞项，然后正常选通道、跑任务。

### UU 远程修复（实验性）

某些环境下网易 UU 远程会把远控连到**被控通道账户的会话**而不是本程序所在桌面。
根因在本机 `wtsapi32.dll` 兼容层缺 `WTSGetActiveConsoleSessionId`，UU 按名查不到
console 会话身份。设置页「UU 远程修复」开关打开后，连接或开始执行后 18 秒自动应用修正：
UU 会连到本程序所在桌面。

- 应用时 UU 投屏进程会重启一次（远控短暂中断）；投屏进程已在 console 会话时不会被打断；
- 关闭开关不撤销已生效的修正；
- 不修改 UU 的任何文件。

### 需要注意的限制

- **本机多通道并行取决于系统会话数。** 系统允许多个交互式会话并存时可真并行；普通 Windows 客户端版同一时刻只允许一个，第二条本机通道会把前一个桌面顶掉 —— 这种环境下多通道建议分散到不同机器。
- **一次最多 8 条通道并行**（原生层上限）。第 9 条及以后的通道**不会被启动**，开始执行时会弹一次说明；被截断的通道在日志里也会逐条写出原因。
- **同一账户不能开两条通道。** 它们抢的是同一个会话，没有意义；配成同一主机时管理页只提示，不阻止。
- **一条任务不会静默改跑地方。** `channel_id` 指向的通道被删掉 / 停用 / 没配账户时，该任务**本轮不执行**并给出原因（不会偷偷改跑本地 —— 那样可能把游戏重复拉起来）。删通道时会先问你把这些任务交给谁（默认回本地执行），所以正常操作下不该再出现「未知通道」。
- **并发组只在同一个通道内生效。** 各通道各跑各的一批任务，组名相同也不会并在一起；跨通道同名组在开始执行时会在日志里点一句。
- **通知只从主控端发。** 系统通知只在发出它的那个用户会话里显示，目标会话里弹的你在主控端桌面上根本看不到 ——
  所以代理侧**不再发任何通知**（`RdpCommand` 里两个 `notify_*` 键固定下发 `false`，只为让仍部署在目标账户里的
  旧版代理闭嘴）。主控端轮到事件后自己弹：① 每个任务完成（`rdp.notify_on_task_done`）、
  ② 每条通道整条跑完 / 失联（`rdp.notify_on_channel_done`）、③ 整轮汇总（`rdp.notify_on_all_done`）。
  三个开关都在「通道管理」页的「执行与通知」卡片里；锁屏期间的通知会落暂存，回前台补发。
  实现走的是**经典 Toast API**（`Windows.UI.Notifications`），不是 Windows App SDK 的
  `AppNotificationManager` —— 后者在本程序这种**提权**进程里会**静默失效**（不抛异常，但通知
  一个都不投递），详见 `Services/NotificationService.cs` 的类注释。
- **代理里的自动关机会直接执行。** 代理没有交互界面，如果开启了自动关机，会按配置排定倒计时关机（切过去可以 `shutdown /a` 取消）。
- **目标账户要能写日志。** 日志与统计仍写到程序目录，若目标账户对该目录没有写权限，任务照常执行但日志会缺失。
- **远程目标无法在本机枚举会话。** WTS API 只能查本机会话，所以连远程主机时不会等"会话就绪"，直接以对方回传的 `status.json` 为准。同样地，「断开 / 注销」只对本机目标生效，远程目标请用任务结束后的收尾方式。
- **远程目标的预检只能查一半。** 对方账户是否存在、远程桌面是否开启、代理有没有部署，本机都查不到，需要你在对方机器上确认。
- ⚠️ **从旧版本升级必须重新部署一次代理** —— 桥目录规则改成了按账户名分目录，详见下面的「升级注意」。

### 排障

| 现象 | 排查 |
|---|---|
| 提示"等待目标会话超时" | 凭据是否正确；账户是否有密码；远程桌面是否真的开启；手动开一次 `mstsc` 看能否登录 |
| 一直停在"等待代理接管" | 检查代理是否已部署、是否显示「在线」；打开**该通道的**桥目录看 `status.json` 有没有被更新 |
| 无法写入桥目录 | 本机默认 `<ProgramData>\Yomori\rdp\<账户名>` 需要写权限，用管理员运行一次即可；远程目录要确认对方也开了写权限 |
| 能连上远程主机但任务不动 | 90% 是桥目录没配或对方读不到：确认两边指向同一个共享路径，且对方代理的启动参数里带了 `--bridge:` |
| **连上了但任务完全不执行** | 先看 `%ProgramData%\Yomori\rdp\<账户名>\status.json`：如果 `AgentUser` 是空的、时间停在"下发那一刻"，说明目标会话里的代理没起来 —— **重新点一次「部署会话代理」**（会把程序复制到公共目录），再注销目标账户重新连 |
| 升级后"代理明明在跑却显示不在线" | 桥目录规则变了（`rdp\` → `rdp\<账户名>\`）。**重新部署一次代理**，并注销目标账户再重连 |
| 提示需要配置「指令桥目录」 | 目标是远程主机时的正常拦截；只想切本机账户的话，把远程主机改回 `127.0.0.1`（本机多账户留空即可） |
| 填了 IPv6 地址识别不对 | 裸 IPv6（如 `::1`）会被整体当作地址；带端口要写 `[::1]:3390` |
| 远程桌面窗口没弹出来 | 日志里会写 `mstsc` 的启动结果。多半是路径被策略拦了，手动跑一次 `mstsc` 验证系统可用 |
| 日志说"登录失败：账户名或密码不对" | 密码没交到 mstsc 手上。重新填一次密码点「保存凭据」（写进 Windows 凭据管理器，重启程序也不用重填） |
| 保存凭据后提示"读不回来" | 凭据管理器被组策略限制了。可以手动在「远程桌面」窗口里输入一次密码，或联系管理员放开凭据管理器 |
| 分辨率不是我想要的 | 改档位后**重新连接一次**（已连接的会话不能动态改尺寸）；选「自动」则跟随画面格子大小 / 系统默认值 |
| 窗口拉小后出现滚动条 | `.rdp` 里已写 `smart sizing:i:1`。若出现说明文件没生效，删掉 `%LocalAppData%\Yomori\session-<通道id>.rdp` 后重连一次 |
| 提示"环境未就绪" | 按「重新预检」看具体缺哪一项（远程桌面未开 / 服务未启动 / 代理未部署） |
| **某路画面黑屏 / 几路互相闪烁** | 一个客户端同时只能挂一块画面。先关掉那路的「弹出窗口」再重开；多画面翻页时会重排格位，属正常 |
| **只跑了部分通道** | 看日志里的「本轮启动 N 条」与被跳过通道的原因：常见是超 8 条上限、通道被停用、没配账户、或 `channel_id` 指向了已删除的通道 |
| **以前能看到的 `[embed]` D3D / 帧率日志不见了** | 这是**预期行为**：那些内嵌画面诊断行默认不在「运行日志」页显示（挂着画面时每 5 秒一条，会淹掉任务进度）。要看得去「设置 → 开发者」打开**开发者模式**，开关一拨这些行会立刻回到页面上。⚠️ 它们**从来没停止落盘** —— `logs/` 目录里的日志文件始终是完整的，只是不在页面上显示 |
| **"停止执行"看起来没反应** | 按顺序看三处：① 运行日志里有没有「目标会话的代理已收到停止请求」—— 有说明停止链路是通的，只是目标进程收尾慢；② 该通道 `status.json` 的 `Phase` 有没有变成 `stopping`，心跳时间还走不走 —— 不走说明代理已经不在了；③ 日志里有没有「已下发 N 次停止请求…始终没有回执」，有就是**目标会话里还是旧版本代理**，重新「部署会话代理」后注销目标账户重连 |
| 桥目录里躺着一份没人消费的 `stop.json` | 正常情况下代理收尾后会清掉，主控端下发新指令时也会清。若仍残留，说明代理是旧版本 —— 归属校验能挡住误停，但按上面那条重新部署代理 |
| 日志说"任务队列已停止"，但**游戏本体还在跑** | 先看清理末尾那条**复查**：`✓ 已复查确认，任务相关的进程都已结束` = 进程真的没了，你看到的只是内嵌画面停在最后一帧（点一下「断开」再「连接本通道」就会刷新成真实桌面）。若是 `⚠ 复查发现 N 项仍在运行`，说明强杀被拒 —— 看它上面那几行 `结束进程名「X」：未能结束：…` 的原因（多半是反作弊 / 驱动级保护），按提示改以管理员运行代理，或手动结束 |
| 日志里只有"正在结束"、没有成功/失败字样 | 那是旧版本代理（新版每条都会写 `已结束` / `未能结束：原因`）。重新「部署会话代理」 |
| 想知道游戏真实镜像名 | 启动后日志会有一行 `已记下监控命中的真实进程名：YuanShen（PID n）`。把这类名字补进任务的「进程关键词」最稳 —— 不依赖窗口标题，也能被复查覆盖 |

### 升级注意（从旧版本升级必读）

新版桥目录规则变了：

```text
旧：<ProgramData>\Yomori\rdp\
新：<ProgramData>\Yomori\rdp\<账户名>\
```

**已部署过会话代理的机器，升级后必须重新点一次「部署会话代理」**，
否则新版主控端在新目录里读写、旧代理还在旧目录里转 —— 表现就是"代理明明在跑，却一直显示不在线"。
重新部署后还要**注销目标账户再重连**，新代理才会被启动项拉起。

其他升级注意：

- 旧配置里没有 `channels` 时会自动迁移出一个默认通道（继承原来的目标账户 / 桥目录 / 分辨率 / 收尾方式），
  **任务会自动指向它**，不需要手工重配；
- 一个通道内的任务仍然串行，只有**通道之间**才是并行 —— 如果原来靠 `concurrent_group` 做并发，行为不变；
- 内嵌画面改用原生 FreeRDP 实现，`src/Yomori.RdpHost/`（旧 COM ActiveX 方案）已删除；
  旧配置里的 `rdp.embed_remote_desktop` 键仅为兼容而保留，读进来恒为 `false`；
- 左侧菜单的通道项从「平级分组（执行中的通道）」改成**「会话通道」父项下的子菜单**（没有配置项要迁移）：
  父项 = 多画面，子项 = 各通道单独画面 + 「通道管理…」；
- 顺带一提 `config.json` 完全不用动 —— 本次只改界面层级与导航。

---

## 八、数据目录

| 用途 | 路径 | 说明 |
|---|---|---|
| 配置文件 | `config.json` | 位于程序根目录（向上回溯到有 `config.json` / `assets/` 的那一层） |
| 日志目录 | `logs/` | 同上，按日期分文件 |
| 统计目录 | `runtime_stats/` | 脚本耗时历史，按账户分家 |
| 更新渠道 | `config.json` 的 `update_channel` | `stable` = 正式版（默认）；`beta` = 测试版。见「设置 → 关于」 |
| 开发者模式 | `config.json` 的 `developer_mode` | `false`（默认）= 「运行日志」页隐藏带 `[embed]` 标签的内嵌画面诊断行；`true` = 显示。见「设置 → 开发者」 |

Python + PySide6 版原先共用这几份数据；它已于 2026-09-28 从仓库移除，仅保留在 Git 历史里。

---

## 九、已知限制

已归档到 [docs/known-limits-and-roadmap.md](docs/known-limits-and-roadmap.md#已知限制)。

---

## 十、后续建议

已归档到 [docs/known-limits-and-roadmap.md](docs/known-limits-and-roadmap.md#后续建议)。
