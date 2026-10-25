; ═══════════════════════════════════════════════════════════════════════════════
; Yomori · 安装程序（Inno Setup 6）
;
; 被 CI 调用（build.yml）：ISCC 传 MyAppVersion / SrcDir / OutDir / IconFile。
;   ISCC.exe winui3\tools\installer.iss /DMyAppVersion=2.1.0 /DSrcDir=<abs>\dist\app /DOutDir=<abs>\dist
; ⚠️ SrcDir / OutDir 请传**绝对路径** —— 相对路径会被当成「相对本脚本所在目录」解析。
;
; 默认装到公共目录 Program Files\Yomori。主程序 app.manifest 是 requireAdministrator
; （双击即由系统提权），所以往安装目录写 config.json / logs 没问题。
;
; 数据（config.json / logs / runtime_stats）不在 [Files] 里，因此：
;   · 升级安装 = 覆盖程序文件、保留配置；
;   · 卸载后配置和日志也会留在安装目录，需要清掉自己删。
;
; 装完文件后还会调 `Yomori.exe --deploy-agent` 自动部署会话代理（见 [Code] 的
; DeploySessionAgent）：把随包的 agent\ 载荷铺到 %ProgramData%\Yomori\agent，
; 并登记「所有用户」的登录自启项 —— 装完即可连目标账户，不必先手动点一次部署。
; 该步骤失败不阻断安装（用户仍可在「通道管理」里手动部署）。
;
; ── 为什么还要「改编译器模板」这一步（见 build.yml 的「让安装包强制要求管理员」）──
;   Inno 的引导器（SetupLdr.e32/e64）清单里写死 asInvoker，PrivilegesRequired=admin
;   只是让它在**自己启动之后**再 runas 提权一次 —— 于是双击后先出向导、过一会儿才弹
;   UAC，而主程序是 requireAdministrator（双击立刻弹）。两者体验不一致，且在
;   「双击后才发现没权限」的场景下已经晚了。
;   所以 CI 会先把 SetupLdr 的清单改成 requireAdministrator 再编译，下面那句
;   DisablePrecompiledFileVerifications 就是为这一步准备的（6.7 起会校验模板哈希）。
;   ⚠️ 不要试图改**编好的** setup.exe：它是「PE + 尾部追加的安装数据」，
;      mt.exe / UpdateResource 都会把追加数据整段丢掉，产物只剩 1MB 的残骸。
; ═══════════════════════════════════════════════════════════════════════════════

#ifndef MyAppVersion
  #define MyAppVersion "0.0.0"
#endif

#ifndef SrcDir
  #define SrcDir "dist\app"
#endif

#ifndef OutDir
  #define OutDir "dist"
#endif

; 留空 = 不设安装程序图标（默认图标）
#ifndef IconFile
  #define IconFile ""
#endif

; 留空 = 不加载语言文件（向导用默认英文）。
; Inno Setup 6.7 的 choco 包不带 Languages 目录，中文 isl 得另外下载 ——
; CI 里下载到了才传这个参数，下载不到就退回英文，别让这一步把构建搞红。
#ifndef LangFile
  #define LangFile ""
#endif

#define MyAppName "Yomori"
#define MyAppExe "Yomori.exe"
#define MyAppPublisher "AITNR"
; 仓库地址（产品已从 Anigame-script-manager 改名为 Yomori，旧名会 301 跳转 —— 这里写新名）
#define MyAppURL "https://github.com/AITNR/Yomori"

; 改名前（v2.1.1 及更早）的安装目录名。仅用于升级后收掉旧目录的空壳，
; 不参与任何路径安装逻辑 —— 见 [Setup] 里 UsePreviousAppDir 的说明。
#define LegacyDirName "YukinoChan"

[Setup]
AppId={{D7A2C45E-9F3B-4A61-8C2D-1E5B7F0A93C6}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}/issues
; ⚠️ 别写 UpdatesInfoURL / AppUpdatesInfoURL —— 这指令在新版 Inno 才有，
;    runner 上装的版本（6.2.x）不认，编译直接 abort。

; 公共目录（Program Files）—— 装这里必须管理员，下面显式要求
DefaultDirName={pf}\Yomori
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
DisableDirPage=auto

; ⚠️ 必须显式关掉：默认值 yes 会让 Inno 把**旧安装的 InstallLocation** 当作默认目录，
;    直接盖掉上面的 DefaultDirName。改名前旧版装在 {pf}\YukinoChan，于是新版本
;    （AppId 不变，走升级）不声不响又装回 YukinoChan —— 目录名与产品名就此脱节，
;    卸载项、开始菜单、任务栏固定项全指向旧路径。
;    关掉之后：升级仍会先静默卸载旧版（同 AppId 的标准升级流程），再把文件铺到 {pf}\Yomori。
;    代价是用户当年手动改过的安装目录不再被沿用 —— 对这个产品可接受（路径规范、极少自定义）。
UsePreviousAppDir=no

; 装到 Program Files 就要管理员：不降权装到用户目录（那样数据与程序就分家了）
PrivilegesRequired=admin
; ⚠️ 不要加 PrivilegesRequiredOverridesAllowed —— 它允许 `setup.exe /LOWEST` 强行按
;    非管理员方式装到用户目录，跟「装到 Program Files」这个前提冲突，也绕过了本文件要求的提权。

; 6.7 起编译器会校验 SetupLdr.e32/e64 的哈希（.issig）；CI 为了让安装包自带
; requireAdministrator 会先改它们的清单，所以这里必须放行。
; 老版本（<6.7）没有这项校验，也不需要这条指令（写了反而报未知指令）。
#if Ver >= EncodeVer(6,7,0)
DisablePrecompiledFileVerifications=setupldr
#endif

ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
WizardStyle=modern
Compression=lzma2/max
SolidCompression=yes
LZMAUseSeparateProcess=yes
OutputBaseFilename=Yomori-{#MyAppVersion}-setup
OutputDir={#OutDir}
UninstallDisplayName={#MyAppName} {#MyAppVersion}
UninstallDisplayIcon={app}\{#MyAppExe}
SetupLogging=yes

; ⚠️ 别写 VersionInfoVersion：CI 传的版本可能是 2.0.0-dev.42 这种带后缀的，
;    Inno 只接受纯点分数字，写了会编译失败。

#if IconFile != ""
SetupIconFile={#IconFile}
#endif

#if LangFile != ""
[Languages]
Name: "chinesesimplified"; MessagesFile: "{#LangFile}"
#endif

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "附加图标："

[Files]
Source: "{#SrcDir}\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion

[Icons]
; 图标统一指向 exe 自己（ApplicationIcon 编译期嵌入 icon.ico），不再依赖外部 .ico 副本 ——
; 升级安装时若换了图标，这里也不会漏改。
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExe}"; IconFilename: "{app}\{#MyAppExe}"
Name: "{group}\卸载 {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExe}"; IconFilename: "{app}\{#MyAppExe}"; Tasks: desktopicon

; ⚠️ 必须带 runascurrentuser —— 少了它会报「CreateProcess failed; code 740 请求的操作需要提升」。
;    Inno 对带 postinstall 的 [Run] 条目**默认用 runasoriginaluser**（提权之前那个普通令牌，
;    即 explorer 的 medium 完整性级别）。而主程序 app.manifest 是 requireAdministrator，
;    拿普通令牌 CreateProcess 一定被拒 → 用户点「完成」就弹错误框，程序反而没起来。
;    （日志里能直接看到 `Run as: Original user` 紧跟 `code 740`。）
;    runascurrentuser = 继承 Setup 自己的令牌；Setup 本身就是管理员，所以必定能启动。
[Run]
Filename: "{app}\{#MyAppExe}"; Description: "安装完成后启动 {#MyAppName}"; Flags: nowait postinstall runascurrentuser skipifsilent

[UninstallDelete]
; 代理副本与自启项是「安装时自动部署」的产物（不归 [Files] 管），卸载要自己收尾：
; 否则程序文件删干净了，ProgramData 里还留着 73MB 副本，外加一个永远指向不存在程序的
; 启动项 —— 那东西会让每个账户登录时都白试一次。
;
; ⚠️ 只删 agent 子目录，**不要**删整个 {commonappdata}\Yomori：那里还有指令桥 rdp\，
;    里面是任务状态、耗时统计与日志，属于用户数据，卸载程序不该替用户清。
Type: files; Name: "{commonstartup}\Yomori RDP 会话代理.lnk"
Type: filesandordirs; Name: "{commonappdata}\Yomori\agent"
Type: filesandordirs; Name: "{commonappdata}\Yomori\agent_b"

[Code]
// 兜底：万一谁用「没打补丁的编译器模板」编出这个安装包（外层清单还是 asInvoker），
// 至少别让它静默地以普通用户身份往 Program Files 里写 —— 那样会装一半失败，报错还看不懂。
// ⚠️ [Code] 是 Pascal Script：注释只能写 // 或 { }，不能用分号（分号是语句分隔符）。
function InitializeSetup(): Boolean;
begin
  Result := True;
  if not IsAdminInstallMode then
  begin
    MsgBox('本安装程序需要管理员权限，才能把程序装到 Program Files。' + #13#10 + #13#10 +
           '请右键点击安装包，选择「以管理员身份运行」。', mbError, MB_OK);
    Result := False;
  end;
end;

// ── 安装时自动部署会话代理 ───────────────────────────────────────────────
// 文件铺完后调主程序自己的部署入口（--deploy-agent）：把随包的 agent\ 载荷铺到
// {commonappdata}\Yomori\agent，并在「所有用户」的启动目录写下代理自启项。
// 目标是「装完就能直接连目标账户」，不必先想起来去点界面上的「部署会话代理」按钮。
//
// 为什么不在这里用 [Files] / [Icons] 直接铺：
//   · 代理运行期要往副本目录写 logs / runtime_stats，得给 BUILTIN\Users 放开权限；
//     主程序那份部署逻辑已经连同「旧代理正占着文件 → 改铺备用目录 agent_b」一起做完了，
//     在这里重写一遍 = 两套规则迟早漂移。
//   · 幂等：载荷内容一致就跳过整套搬运，升级安装不会白搬 73MB。
//   · 已有自启项时**不重写** —— 它的参数里带着用户配的 --bridge:，安装器不该替用户决定。
//
// 失败绝不中断安装（用户还能在程序里手动部署一次），只写安装日志。
// 退出码：0 已部署 / 已是最新；1 失败；2 代理正在执行任务（跳过）；3 缺管理员权限。
procedure DeploySessionAgent();
var
  ResultCode: Integer;
begin
  if not Exec(ExpandConstant('{app}\{#MyAppExe}'), '--deploy-agent',
              ExpandConstant('{app}'), SW_HIDE, ewWaitUntilTerminated, ResultCode) then
  begin
    // Exec 自己失败时 ResultCode 是 GetLastError
    Log('会话代理自动部署没能启动（Exec 失败）：' + SysErrorMessage(ResultCode));
    Exit;
  end;

  Log('会话代理自动部署结束，退出码 ' + IntToStr(ResultCode));
  if ResultCode <> 0 then
    Log('  未部署成功。详情见 ' + ExpandConstant('{app}') + '\logs\agent_deploy.log；'
        + '安装完成后可在「通道管理」里点「部署会话代理」重试。');
end;

// 从旧版（装在 {pf}\YukinoChan）升级上来时，同 AppId 会让 Inno 先静默跑一遍旧卸载器，
// 但卸载器只删它自己登记过的文件，目录本身会留下一个空壳。
// 这里在文件全部铺完之后收掉它 —— RemoveDir 只删空目录，里面还有用户放的东西就不动。
procedure CurStepChanged(CurStep: TSetupStep);
var
  LegacyDir: String;
begin
  if CurStep = ssPostInstall then
  begin
    LegacyDir := ExpandConstant('{pf}\{#LegacyDirName}');
    if DirExists(LegacyDir) then
    begin
      if RemoveDir(LegacyDir) then
        Log('已删除旧版遗留的空安装目录：' + LegacyDir)
      else
        Log('旧安装目录非空，予以保留：' + LegacyDir);
    end;

    // 放在 ssPostInstall：此时文件已全部落地，而 [Run] 里那条「安装完成后启动」还没跑 ——
    // 主程序起来时代理已经就位，首次连接的预检就能直接看到"已部署"。
    DeploySessionAgent();
  end;
end;
