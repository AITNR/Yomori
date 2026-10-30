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

; 中文语言文件：**随仓库入库**（winui3/tools/lang/ChineseSimplified.isl），不在构建时下载。
;
; 教训（2026-10-30）：以前从 issrc 仓库的 Unofficial 目录现场拉这个文件，拉不到就静默
; 退回英文向导、构建照样通过。而 2026-06-23 上游把这个翻译提升为**正式翻译**、挪到了
; Files\Languages\ 下 —— 旧地址从此 404，于是之后每一个安装包都是英文的，没人发现。
; 现在文件在仓库里，缺了就是编译错误（而不是"悄悄变英文"）。
;
; 相对路径按**脚本所在目录**解析（与 Source 一致），所以本地编译不需要额外参数；
; CI 仍传绝对路径。
#ifndef LangFile
  #define LangFile "lang\ChineseSimplified.isl"
#endif

#define MyAppName "Yomori"
#define MyAppExe "Yomori.exe"
#define MyAgentExe "Yomori.Agent.exe"
#define MyAppPublisher "AITNR"
; 仓库地址（产品已从 Anigame-script-manager 改名为 Yomori，旧名会 301 跳转 —— 这里写新名）
#define MyAppURL "https://github.com/AITNR/Yomori"

; 代理端版本号，由 CI 从 Yomori.Agent.csproj 的 <AgentVersion> 读出来传进来
; （/DAgentVersion=2.0.0）。留空 = 判据不可用，代理载荷按「每次都写」处理 ——
; 手头没有版本号时宁可多写一次，不要留下一个来路不明的旧代理。
#define AgentVersion ""

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

; 恒有中文（单语言 → 不弹语言选择框）。文件缺失时 ISCC 会直接报错，
; 这正是要的：宁可构建红，也不要再发出一个英文向导的安装包。
[Languages]
Name: "chinesesimplified"; MessagesFile: "{#LangFile}"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "附加图标："

[Files]
; 主程序与其余文件：照旧全部覆盖（ignoreversion 是有意的 —— 界面/服务里的时间戳不可靠）。
; 代理载荷与 UU 修复载荷各自单列，按判据决定写不写（见下两条）。
Source: "{#SrcDir}\*"; DestDir: "{app}"; Excludes: "\agent\*,\uu_session_fix.dll"; Flags: recursesubdirs createallsubdirs ignoreversion

; 代理载荷（{app}\agent\，约 73 MB、几百个文件）
;
; 为什么要单列：代理端有自己的版本号（Yomori.Agent.csproj 的 <AgentVersion>），
; 升级安装十有八九只是主程序动了、代理一个字节没变，把 73MB 重写一遍纯属浪费；
; 而且写完后 --deploy-agent 还会再拿版本比一次才决定要不要重铺 ProgramData 里的副本，
; 于是同一次安装要为同一批没变的文件搬两遍。
;
; 判据只看版本号（ShouldInstallAgent）：
;   · 已安装 exe 的版本 == 本次随包版本 → 整块跳过；
;   · 读不到 / 不同 / 没装过 → 照常写入。
; 忘给 <AgentVersion> +1 的后果是「代理不更新」，不是安装失败 —— 见 csproj 里的说明。
Source: "{#SrcDir}\agent\*"; DestDir: "{app}\agent"; Flags: recursesubdirs createallsubdirs ignoreversion; Check: ShouldInstallAgent

; UU 远程修复载荷（uu_session_fix.dll）——**刻意不放进普通 [Files] 条目**。
;
; 这个 DLL 在「UU 远程修复」开启时会被注入 `GameViewerService`（SYSTEM 常驻服务）。
; 只要它出现在安装列表里，Inno 的占用检查（关闭应用/重启管理器）就会把该服务列进
; 「需要关闭的应用」，用户被迫停服务 —— 而停 UU 服务 = 断远控，正好是修复功能要治的那件事；
; 更荒唐的是，多数升级里这个 DLL 根本没有变化。
;
; 所以改成 dontcopy 提取到临时目录，由 [Code] 的 InstallUuFixPayload 比对内容后决定是否落盘：
;   · 文件不在安装列表 ⇒ 不参与占用检查 ⇒ 不会出现那条提示；
;   · 内容一致 ⇒ 一个字节都不写，服务不受影响；
;   · 内容不同 ⇒ 直接覆盖；被占用则排到下次启动时替换（不停服务、不断远控）。
; 代价：手工落盘的文件不进卸载日志，所以 [UninstallDelete] 里补了登记。
Source: "{#SrcDir}\uu_session_fix.dll"; DestDir: "{tmp}"; Flags: dontcopy

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
;
; ⚠️ 这一段是「快速路径」，**不是**唯一保障：Inno 处理 [UninstallDelete] 时遇到被占用的
;    文件只会静默跳过（日志里记一条失败，界面上什么都不提示），而卸载时代理多半还常驻着。
;    真正的兜底在 [Code] 的 CurUninstallStepChanged(usPostUninstall)：先结束代理进程、
;    再 DelTree 一遍，还删不掉就明确弹框告诉用户路径。
Type: files; Name: "{commonstartup}\Yomori RDP 会话代理.lnk"
; 改名前（v2.1.1 及更早）留下的自启项：指向 %ProgramData%\YukinoChan\agent，
; 那份副本早就没人创建了，留着只会让每个账户登录时白试一次。
Type: files; Name: "{commonstartup}\雪乃酱 RDP 会话代理.lnk"
Type: files; Name: "{commonstartup}\YukinoChan RDP 会话代理.lnk"
Type: filesandordirs; Name: "{commonappdata}\Yomori\agent"
Type: filesandordirs; Name: "{commonappdata}\Yomori\agent_b"

; UU 修复载荷是 [Code] 手工落盘的（见 [Files] 里那条 dontcopy），不在 Inno 的卸载日志里，
; 不补这两条的话卸载会把它们留在 Program Files 里。
; .new 是「被占用、已排到下次启动替换」时的临时件，正常情况下不存在。
Type: files; Name: "{app}\uu_session_fix.dll"
Type: files; Name: "{app}\uu_session_fix.dll.new"

[Code]
// ── 代理载荷：按版本号决定要不要重写 ─────────────────────────────────────
// 判据与主程序的 RdpSessionService.ReadAgentExeVersion 同一口径：读 exe 的**文件版本
// 资源**（安装器读不到托管程序集属性），按四段数字比较、缺位补 0，
// 免得 "2.0.0" 与 "2.0.0.0" 被当成两个不同的版本而白写一遍 73MB。
//
// 结果缓存进 AgentSkipDecision：通配条目会展开成几百个文件，每个都重新读一次版本
// 既慢又可能因为中途文件被替换而前后不一致。
var
  AgentSkipDecision: Integer;   // -1 = 还没算过；0 = 需要写入；1 = 跳过

function VersionPart(const Value: String; Index: Integer): Integer;
var
  I, Part, Start: Integer;
begin
  Result := 0;
  Part := 1;
  Start := 1;
  for I := 1 to Length(Value) + 1 do
  begin
    if (I > Length(Value)) or (Value[I] = '.') then
    begin
      if Part = Index then
      begin
        if I > Start then
          Result := StrToIntDef(Copy(Value, Start, I - Start), 0);
        Exit;
      end;
      Inc(Part);
      Start := I + 1;
    end;
  end;
end;

function SameVersion(const A, B: String): Boolean;
begin
  Result := (VersionPart(A, 1) = VersionPart(B, 1))
        and (VersionPart(A, 2) = VersionPart(B, 2))
        and (VersionPart(A, 3) = VersionPart(B, 3))
        and (VersionPart(A, 4) = VersionPart(B, 4));
end;

function ShouldInstallAgent(): Boolean;
var
  InstalledExe, Installed: String;
begin
  if AgentSkipDecision < 0 then
  begin
    // 默认：写入。判据不可用（没传 /DAgentVersion、或读不到版本）时宁可多写一次。
    AgentSkipDecision := 0;
    // ⚠️ 这里用**运行时**的字符串比较而不是 #if：ISPP 的 #if 对字符串比较不可靠
    //    （写成 #if AgentVersion != "" 时整段会被静默丢掉，变量变成"从未使用"，
    //     编译照样成功 —— 判据就这么没了，只有 ISCC 的 Hint 里能看出端倪）。
    if '{#AgentVersion}' <> '' then
    begin
      InstalledExe := ExpandConstant('{app}\agent\{#MyAgentExe}');
      if FileExists(InstalledExe) then
      begin
        if GetVersionNumbersString(InstalledExe, Installed)
           and SameVersion(Installed, '{#AgentVersion}') then
        begin
          AgentSkipDecision := 1;
          Log('代理载荷版本未变（{#AgentVersion}），跳过写入：' + ExpandConstant('{app}\agent'));
        end
        else
          Log('代理载荷需要更新：已安装 ' + Installed + ' → 本次 {#AgentVersion}');
      end
      else
        Log('代理载荷未安装过，按首次安装写入：' + InstalledExe);
    end
    else
      Log('未传 /DAgentVersion，代理载荷按「每次写入」处理');
  end;

  Result := AgentSkipDecision = 0;
end;

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

// ── UU 远程修复载荷：内容没变就不碰 ─────────────────────────────────────
// 判据是内容（SHA-256）—— 这个 DLL 没有版本号可用，也不该为它另发明一个。
// 为什么用 dontcopy 而不是普通 [Files] 条目、为什么被占用时不停服务：
// 见 [Files] 里那条注释。
function IsSameFile(const Left, Right: String): Boolean;
var
  A, B: String;
begin
  Result := False;
  if (not FileExists(Left)) or (not FileExists(Right)) then
    Exit;

  try
    A := GetSHA256OfFile(Left);
    B := GetSHA256OfFile(Right);
  except
    // 读不出来（被独占、权限不足）就当「不同」：宁可多写一次，不能少写一次
    Exit;
  end;

  Result := (A <> '') and (A = B);
end;

procedure InstallUuFixPayload();
var
  Temp, Target, Pending: String;
begin
  ExtractTemporaryFile('uu_session_fix.dll');
  Temp := ExpandConstant('{tmp}\uu_session_fix.dll');
  if not FileExists(Temp) then
  begin
    Log('取不出 uu_session_fix.dll（安装包不完整？），跳过这一步');
    Exit;
  end;

  Target := ExpandConstant('{app}\uu_session_fix.dll');

  if IsSameFile(Target, Temp) then
  begin
    Log('uu_session_fix.dll 内容未变，保留现有文件（不停 UU 服务、不断远控）');
    Exit;
  end;

  if FileCopy(Temp, Target, False) then
  begin
    Log('uu_session_fix.dll 已更新：' + Target);
    Exit;
  end;

  // 覆盖失败 = 文件正被映像锁定（「UU 远程修复」注入着 GameViewerService）。
  // ⚠️ 这里**不要**去停服务：停 UU 服务 = 断掉用户可能正在用的远控，而这正是修复要治的事。
  // 改为排到下次启动时替换。在那之前旧 DLL 继续用，行为与新版本一致（内容差异只在被替换那一刻生效）。
  Pending := Target + '.new';
  if not FileCopy(Temp, Pending, True) then
  begin
    Log('uu_session_fix.dll 无法更新，临时件也写不进去：' + Pending);
    Exit;
  end;

  try
    RestartReplace(Pending, Target);
    Log('uu_session_fix.dll 被占用（多半是 UU 服务正在运行），已排到下次启动时替换：' + Pending);
  except
    // 同一个目标已经有挂起的替换操作时会冲突；那就留着 .new 让人看得见
    Log('uu_session_fix.dll 被占用，登记「重启后替换」失败，保留临时件：' + Pending);
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
    InstallUuFixPayload();
    DeploySessionAgent();
  end;
end;

// ── 卸载：收掉会话代理的程序副本 ─────────────────────────────────────────
// 代理是「随目标账户登录自启、常驻」的，卸载时它多半正在运行。运行中的 exe/dll 被映像
// 锁住，而 Inno 处理 [UninstallDelete] 时遇到占用只会静默跳过 —— 结果就是程序目录删干净了，
// {commonappdata}\Yomori\agent 里还留着 70MB+ 的副本，卸载完看不出任何异常。
//
// 所以收尾时自己再来一遍：先结束进程，再删目录；还删不掉就明确告诉用户去哪删。
// 用 usPostUninstall 而不是 usUninstall 是因为它**肯定**排在 [UninstallDelete] 之后
// （后者是卸载的最后一步），不会出现"先杀进程、后又被别的阶段重新拉起"的窗口。
procedure KillAgentImage(const ImageName: String);
var
  ResultCode: Integer;
begin
  if not Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM "' + ImageName + '" /T',
              '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
  begin
    Log('结束 ' + ImageName + ' 时没能启动 taskkill：' + SysErrorMessage(ResultCode));
    Exit;
  end;

  // 0 = 至少结束了一个；128 = 本来就没有这个进程（正常，不是错误）
  if ResultCode = 0 then
    Log('已结束会话代理进程：' + ImageName);
end;

// 旧形态代理（v2.1.x 及更早的部署）：把整个程序目录复制到 ProgramData，
// 跑的是主控端那份 Yomori.exe。
// ⚠️ 不能直接 taskkill /IM Yomori.exe —— 那会把用户正开着的主控端界面一起杀掉。
//    这里按可执行文件路径精确匹配，只结束副本目录里的那些。
procedure KillLegacyAgentCopy();
var
  ResultCode: Integer;
begin
  if not FileExists(ExpandConstant('{commonappdata}\Yomori\agent\Yomori.exe')) then
    Exit;

  Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
       '-NoProfile -NonInteractive -Command "Get-Process -Name Yomori -ErrorAction SilentlyContinue'
       + ' | Where-Object { $_.Path -like ''*\Yomori\agent\*'' } | Stop-Process -Force"',
       '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

procedure RemoveAgentCopies();
var
  Dir: String;
begin
  Dir := ExpandConstant('{commonappdata}\Yomori\agent');
  if DirExists(Dir) then
  begin
    if DelTree(Dir, True, True, True) then
      Log('已删除代理副本：' + Dir)
    else
      Log('代理副本删不干净（文件仍被占用）：' + Dir);
  end;

  Dir := ExpandConstant('{commonappdata}\Yomori\agent_b');
  if DirExists(Dir) then
  begin
    if DelTree(Dir, True, True, True) then
      Log('已删除备用代理副本：' + Dir)
    else
      Log('备用代理副本删不干净：' + Dir);
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  AgentDir: String;
  BackupDir: String;
begin
  if CurUninstallStep <> usPostUninstall then
    Exit;

  KillAgentImage('Yomori.Agent.exe');
  KillAgentImage('YukinoChan.exe');          // 改名前（v2.1.1 及更早）
  KillAgentImage('YukinoChan.Agent.exe');    // 曾经用过的独立代理名
  KillLegacyAgentCopy();

  // taskkill /F 返回之后句柄释放还有几十毫秒的延迟，删太快会撞上"正在被占用"
  Sleep(500);
  RemoveAgentCopies();

  // 空目录才收（RemoveDir 语义）：rdp\ 里有任务状态与耗时统计，属于用户数据，
  // 卸载程序不该替用户清 —— 它在就留着整个 Yomori 目录。
  RemoveDir(ExpandConstant('{commonappdata}\Yomori'));

  AgentDir := ExpandConstant('{commonappdata}\Yomori\agent');
  BackupDir := ExpandConstant('{commonappdata}\Yomori\agent_b');

  // 静默卸载（升级安装时 Inno 会静默跑一遍旧版卸载器）绝不能弹框 —— 无人值守，弹了就卡死。
  if (DirExists(AgentDir) or DirExists(BackupDir)) and (UninstallSilent = False) then
    MsgBox('会话代理的程序副本没能删掉：' + #13#10#13#10
           + AgentDir + #13#10#13#10
           + '常见原因是那个目录里的程序还在运行 —— 只要还有账户登录着，代理就会常驻，'
           + '被占用的文件 Windows 不允许删除。' + #13#10
           + '注销所有账户（或重启）后手动删掉该目录即可；其余内容已正常卸载。',
           mbInformation, MB_OK);
end;
