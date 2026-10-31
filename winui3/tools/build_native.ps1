[CmdletBinding()]
param(
  [string]$RepoRoot = '',
  [string]$VcpkgRoot = '',
  [string]$InstallRoot = '',
  [string]$WorkRoot = '',
  [string]$BuildDir = '',
  [string]$OutDir = '',
  [string]$Triplet = 'x64-windows',
  [string]$Configuration = 'Release',
  [string]$PythonExe = '',
  [switch]$SkipInstall,
  [switch]$InstallOnly,
  [switch]$Clean,
  [switch]$NoCleanLoad,
  [switch]$DryRun,
  [switch]$PrintCacheKey
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0

$PinnedVcpkgCommit = '10541e317a660f4165ba4ac2851ab54a8d4577b1'

function Write-Step([string]$Message) {
  Write-Host ''
  Write-Host "==> $Message" -ForegroundColor Cyan
}

function Get-FullPath([string]$Path) {
  return [System.IO.Path]::GetFullPath($Path)
}

function Remove-TreeSafely([string]$Path, [string]$AllowedRoot) {
  $full = (Get-FullPath $Path).TrimEnd('\')
  $root = (Get-FullPath $AllowedRoot).TrimEnd('\')
  if (-not $full.StartsWith($root + '\', [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "拒绝删除不在允许目录内的路径：$full（允许根：$root）"
  }
  if (Test-Path -LiteralPath $full) {
    Remove-Item -LiteralPath $full -Recurse -Force
  }
}

function Get-Sha256([string]$Path) {
  return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Get-NativeCacheKey {
  $files = @(
    (Join-Path $RepoRoot 'winui3\src\Yomori.RdpNative\CMakeLists.txt'),
    (Join-Path $RepoRoot 'winui3\src\Yomori.RdpNative\verify_runtime_dlls.cmake'),
    (Join-Path $RepoRoot 'winui3\src\Yomori.RdpNative\ycn_rdp.c'),
    (Join-Path $RepoRoot 'winui3\src\Yomori.RdpNative\ycn_rdp.h'),
    (Join-Path $RepoRoot 'winui3\tools\build_native.ps1'),
    (Join-Path $RepoRoot 'winui3\tools\verify_native_runtime.py'),
    (Join-Path $RepoRoot 'winui3\tools\package_release.py')
  )

  $nativeDeps = Join-Path $RepoRoot 'winui3\native-deps'
  if (Test-Path -LiteralPath $nativeDeps) {
    $files += @(Get-ChildItem -LiteralPath $nativeDeps -Recurse -File | ForEach-Object { $_.FullName })
  }

  $seen = @{}
  foreach ($f in $files) {
    if (Test-Path -LiteralPath $f) { $seen[(Get-FullPath $f)] = $true }
  }

  $lines = New-Object System.Collections.Generic.List[string]
  $lines.Add('schema=ycn-native-cache-v1')
  $lines.Add('vcpkg_commit=' + $PinnedVcpkgCommit)
  $lines.Add('triplet=' + $Triplet)
  $lines.Add('configuration=' + $Configuration)

  $patchDir = Join-Path $nativeDeps 'patches'
  $patchFiles = @()
  if (Test-Path -LiteralPath $patchDir) {
    $patchFiles = @(Get-ChildItem -LiteralPath $patchDir -Filter '*.patch' -File | Sort-Object Name)
  }
  if ($patchFiles.Count -eq 0) {
    $lines.Add('patches=<none>')
  }

  foreach ($f in ($seen.Keys | Sort-Object)) {
    $rel = $f
    if ($f.StartsWith($RepoRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
      $rel = $f.Substring($RepoRoot.Length).TrimStart('\', '/')
    }
    $rel = $rel.Replace('\', '/')
    $lines.Add($rel + "`0" + (Get-Sha256 $f))
  }

  $text = [string]::Join("`n", $lines)
  $sha = [System.Security.Cryptography.SHA256]::Create()
  try {
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($text)
    $hash = ($sha.ComputeHash($bytes) | ForEach-Object { $_.ToString('x2') }) -join ''
  }
  finally {
    $sha.Dispose()
  }
  return 'v1-' + $hash.Substring(0, 32)
}

if (-not $RepoRoot) {
  $RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
}
$RepoRoot = Get-FullPath $RepoRoot

if (-not $VcpkgRoot) {
  if ($env:VCPKG_ROOT) {
    $VcpkgRoot = $env:VCPKG_ROOT
  }
  elseif (Test-Path -LiteralPath (Join-Path $RepoRoot '.vcpkg\vcpkg.exe')) {
    $VcpkgRoot = Join-Path $RepoRoot '.vcpkg'
  }
  elseif (Test-Path -LiteralPath (Join-Path $env:USERPROFILE 'WorkBuddy\_tools\vcpkg\vcpkg.exe')) {
    # 本机已有的固定提交 vcpkg；依赖仍装进 ASCII WorkRoot，不写它的 installed。
    $VcpkgRoot = Join-Path $env:USERPROFILE 'WorkBuddy\_tools\vcpkg'
  }
  else {
    $VcpkgRoot = Join-Path $RepoRoot '.vcpkg'
  }
}
$VcpkgRoot = Get-FullPath $VcpkgRoot

$managedVcpkg = $VcpkgRoot.StartsWith($RepoRoot.TrimEnd('\') + '\', [System.StringComparison]::OrdinalIgnoreCase)

if (-not $WorkRoot) {
  if ($env:YCN_NATIVE_WORK_ROOT) {
    $WorkRoot = $env:YCN_NATIVE_WORK_ROOT
  }
  elseif ($RepoRoot -match '[^\x00-\x7F]') {
    # vcpkg still rejects some non-ASCII path layouts. Keep its roots ASCII.
    $WorkRoot = Join-Path $env:USERPROFILE 'WorkBuddy\_tools\ycn-hwdecode-20261031'
  }
  else {
    $WorkRoot = Join-Path $RepoRoot '.native-work'
  }
}
$WorkRoot = Get-FullPath $WorkRoot

if (-not $InstallRoot) {
  if ($managedVcpkg) {
    $InstallRoot = Join-Path $VcpkgRoot 'installed'
  }
  else {
    $InstallRoot = Join-Path $WorkRoot 'vcpkg-installed'
  }
}
$InstallRoot = Get-FullPath $InstallRoot

if (-not $BuildDir) { $BuildDir = Join-Path $WorkRoot 'build' }
if (-not $OutDir) { $OutDir = Join-Path $RepoRoot 'winui3\src\Yomori.RdpNative\build\Release' }
$BuildDir = Get-FullPath $BuildDir
$OutDir = Get-FullPath $OutDir

$installGuardRoots = @($RepoRoot, $WorkRoot)
$installInsideAllowed = $false
foreach ($root in $installGuardRoots) {
  $r = (Get-FullPath $root).TrimEnd('\')
  if ($InstallRoot.StartsWith($r + '\', [System.StringComparison]::OrdinalIgnoreCase)) {
    $installInsideAllowed = $true
  }
}
if (-not $installInsideAllowed) {
  throw "InstallRoot 必须位于本仓库或 ASCII WorkRoot 内，绝不能覆盖外部 vcpkg 的 installed：$InstallRoot"
}

if ($PrintCacheKey) {
  Write-Output (Get-NativeCacheKey)
  return
}

$ManifestSource = Join-Path $RepoRoot 'winui3\native-deps'
$ManifestRoot = Join-Path $WorkRoot 'manifest'
$BuildtreesRoot = Join-Path $WorkRoot 'vcpkg-buildtrees'
$PackagesRoot = Join-Path $WorkRoot 'vcpkg-packages'
$DownloadsRoot = Join-Path $WorkRoot 'vcpkg-downloads'
$VcpkgInst = Join-Path $InstallRoot $Triplet
$VerifyTool = Join-Path $RepoRoot 'winui3\tools\verify_native_runtime.py'
$LockFile = Join-Path $ManifestSource 'native-deps.lock.json'
$PatchDir = Join-Path $ManifestSource 'patches'

New-Item -ItemType Directory -Force $WorkRoot | Out-Null
New-Item -ItemType Directory -Force $ManifestRoot | Out-Null

Write-Step '缓存身份'
Write-Host ("vcpkg commit : {0}" -f $PinnedVcpkgCommit)
Write-Host ("triplet      : {0}" -f $Triplet)
Write-Host ("manifest     : {0}" -f $ManifestSource)
Write-Host ("work root    : {0}" -f $WorkRoot)
Write-Host ("install root : {0}" -f $InstallRoot)
Write-Host ("cache key    : {0}" -f (Get-NativeCacheKey))

function Find-Git {
  $git = Get-Command git.exe -ErrorAction SilentlyContinue
  if ($git) { return $git.Source }
  $candidates = @(
    (Join-Path $env:ProgramFiles 'Git\cmd\git.exe'),
    (Join-Path ${env:ProgramFiles(x86)} 'Git\cmd\git.exe')
  )
  foreach ($c in $candidates) { if (Test-Path -LiteralPath $c) { return $c } }
  throw '找不到 git.exe'
}

function Ensure-VcpkgCheckout {
  $git = Find-Git
  $isRepo = Test-Path -LiteralPath (Join-Path $VcpkgRoot '.git')

  if (-not $isRepo) {
    if (-not $managedVcpkg) {
      throw "外部 vcpkg 根不存在或不是 git 仓库：$VcpkgRoot"
    }
    Write-Step "创建本仓库托管的 vcpkg：$VcpkgRoot"
    New-Item -ItemType Directory -Force $VcpkgRoot | Out-Null
    & $git init $VcpkgRoot
    if ($LASTEXITCODE -ne 0) { throw 'git init vcpkg 失败' }
    & $git -C $VcpkgRoot remote add origin https://github.com/microsoft/vcpkg.git
    if ($LASTEXITCODE -ne 0) {
      & $git -C $VcpkgRoot remote set-url origin https://github.com/microsoft/vcpkg.git
    }
    & $git -C $VcpkgRoot fetch --depth 1 origin $PinnedVcpkgCommit
    if ($LASTEXITCODE -ne 0) {
      Write-Host '按提交号浅取失败，退回 blobless clone' -ForegroundColor Yellow
      Remove-TreeSafely $VcpkgRoot (Split-Path $VcpkgRoot -Parent)
      & $git clone --filter=blob:none --no-checkout https://github.com/microsoft/vcpkg.git $VcpkgRoot
      if ($LASTEXITCODE -ne 0) { throw '克隆 vcpkg 失败' }
      & $git -C $VcpkgRoot fetch --depth 1 origin $PinnedVcpkgCommit
      if ($LASTEXITCODE -ne 0) { throw "取不到固定提交 $PinnedVcpkgCommit" }
    }
    & $git -C $VcpkgRoot checkout --detach FETCH_HEAD
    if ($LASTEXITCODE -ne 0) { throw 'checkout 固定提交失败' }
  }

  $head = (& $git -C $VcpkgRoot rev-parse HEAD).Trim()
  if ($head -ne $PinnedVcpkgCommit) {
    if (-not $managedVcpkg) {
      throw "外部 vcpkg HEAD=$head，与固定提交 $PinnedVcpkgCommit 不一致。请改用匹配的只读 vcpkg 根，或让它保持原样并使用本仓库托管的 .vcpkg。"
    }
    Write-Step "把本仓库托管的 vcpkg 切到固定提交"
    & $git -C $VcpkgRoot fetch --depth 1 origin $PinnedVcpkgCommit
    if ($LASTEXITCODE -ne 0) { throw "取不到固定提交 $PinnedVcpkgCommit" }
    & $git -C $VcpkgRoot checkout --detach FETCH_HEAD
    if ($LASTEXITCODE -ne 0) { throw 'checkout 固定提交失败' }
    $head = (& $git -C $VcpkgRoot rev-parse HEAD).Trim()
    if ($head -ne $PinnedVcpkgCommit) { throw "vcpkg HEAD 仍是 $head" }
  }

  Write-Host ("vcpkg HEAD   : {0} (固定提交一致)" -f $head) -ForegroundColor Green
  if (-not (Test-Path -LiteralPath (Join-Path $VcpkgRoot 'ports\freerdp\vcpkg.json'))) {
    throw "vcpkg 里找不到 ports\freerdp：$VcpkgRoot"
  }
}

function Ensure-VcpkgExe {
  $exe = Join-Path $VcpkgRoot 'vcpkg.exe'
  if (-not (Test-Path -LiteralPath $exe)) {
    if (-not $managedVcpkg) {
      throw "外部 vcpkg 没有 vcpkg.exe：$exe（不会修改外部安装）"
    }
    Write-Step 'bootstrap vcpkg'
    # Native command output enters PowerShell's success stream. Route it to the
    # host, otherwise Ensure-VcpkgExe returns bootstrap text plus the path and
    # the caller tries to execute the first output line as a program.
    & (Join-Path $VcpkgRoot 'bootstrap-vcpkg.bat') -disableMetrics | Out-Host
    if ($LASTEXITCODE -ne 0) { throw 'bootstrap-vcpkg 失败' }
  }
  if (-not (Test-Path -LiteralPath $exe)) { throw "bootstrap 后仍找不到 $exe" }
  return $exe
}

function Write-ManifestSnapshot {
  Write-Step "复制 manifest 到 ASCII 工作区：$ManifestRoot"
  Remove-TreeSafely $ManifestRoot $WorkRoot
  Copy-Item -LiteralPath $ManifestSource -Destination $ManifestRoot -Recurse -Force
  if (-not (Test-Path -LiteralPath (Join-Path $ManifestRoot 'vcpkg.json'))) {
    throw "manifest 快照缺少 vcpkg.json：$ManifestRoot"
  }
  if (-not (Test-Path -LiteralPath (Join-Path $ManifestRoot 'ports\freerdp\portfile.cmake'))) {
    throw "manifest 快照缺少 overlay port：$ManifestRoot\ports\freerdp"
  }
}

function Install-Deps {
  Ensure-VcpkgCheckout
  $vcpkg = Ensure-VcpkgExe
  Write-ManifestSnapshot

  New-Item -ItemType Directory -Force $InstallRoot | Out-Null
  New-Item -ItemType Directory -Force $BuildtreesRoot | Out-Null
  New-Item -ItemType Directory -Force $PackagesRoot | Out-Null
  New-Item -ItemType Directory -Force $DownloadsRoot | Out-Null

  $env:VCPKG_DEFAULT_TRIPLET = $Triplet
  $vcpkgArgs = @(
    'install',
    '--triplet', $Triplet,
    ('--x-install-root=' + $InstallRoot),
    ('--x-buildtrees-root=' + $BuildtreesRoot),
    ('--x-packages-root=' + $PackagesRoot),
    ('--downloads-root=' + $DownloadsRoot),
    ('--overlay-ports=' + (Join-Path $ManifestRoot 'ports')),
    '--clean-after-build'
  )
  if ($DryRun) { $vcpkgArgs += '--dry-run' }

  Write-Step "安装固定依赖：freerdp[client,ffmpeg]:$Triplet"
  Push-Location $ManifestRoot
  try {
    & $vcpkg @vcpkgArgs
    if ($LASTEXITCODE -ne 0) { throw "vcpkg install 失败（exit $LASTEXITCODE）" }
  }
  finally {
    Pop-Location
  }

  if ($DryRun) {
    Write-Host 'dry-run 完成：manifest / overlay / patch 计划已解析，未安装文件。' -ForegroundColor Green
    return
  }

  foreach ($required in @('lib\freerdp3.lib', 'bin\freerdp3.dll', 'bin\freerdp-client3.dll', 'bin\winpr3.dll', 'include\freerdp3')) {
    if (-not (Test-Path -LiteralPath (Join-Path $VcpkgInst $required))) {
      throw "安装后缺少 $required：$VcpkgInst"
    }
  }
  foreach ($component in @('avcodec', 'avutil', 'swscale', 'swresample')) {
    $matches = @(Get-ChildItem -LiteralPath (Join-Path $VcpkgInst 'bin') -Filter ($component + '-*.dll') -File -ErrorAction SilentlyContinue)
    if ($matches.Count -eq 0) { throw "安装后缺少 FFmpeg 组件 $component-*.dll：$VcpkgInst\bin" }
  }

  Write-Host "依赖安装完成：$VcpkgInst" -ForegroundColor Green
}

function Import-VsDevEnv {
  $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
  $vsPath = $null
  if (Test-Path -LiteralPath $vswhere) {
    $vsPath = (& $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath) | Select-Object -First 1
  }
  if (-not $vsPath) {
    foreach ($candidate in @(
      (Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\2022\BuildTools'),
      (Join-Path ${env:ProgramFiles} 'Microsoft Visual Studio\2022\BuildTools'),
      (Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\2022\Community'),
      (Join-Path ${env:ProgramFiles} 'Microsoft Visual Studio\2022\Community')
    )) {
      if (Test-Path -LiteralPath (Join-Path $candidate 'Common7\Tools\VsDevCmd.bat')) { $vsPath = $candidate; break }
    }
  }
  if (-not $vsPath) { throw '找不到 Visual Studio 2022 C++ 工具链（VsDevCmd.bat）' }

  $devCmd = Join-Path $vsPath 'Common7\Tools\VsDevCmd.bat'
  if (-not (Test-Path -LiteralPath $devCmd)) { throw "找不到 VsDevCmd.bat：$devCmd" }
  Write-Step "导入 VS 环境：$devCmd"
  $lines = & cmd.exe /s /c "`"$devCmd`" -arch=amd64 -host_arch=amd64 >nul && set"
  if ($LASTEXITCODE -ne 0) { throw "VsDevCmd 失败（exit $LASTEXITCODE）" }
  foreach ($line in $lines) {
    if ($line -match '^([^=]+)=(.*)$') {
      $name = $Matches[1]
      $value = $Matches[2]
      Set-Item -Path ('Env:' + $name) -Value $value
    }
  }
}

function Find-CMake {
  $cmd = Get-Command cmake.exe -ErrorAction SilentlyContinue
  if ($cmd) { return $cmd.Source }

  $candidates = New-Object System.Collections.Generic.List[string]
  foreach ($vsRoot in @(
    (Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\2022\BuildTools'),
    (Join-Path ${env:ProgramFiles} 'Microsoft Visual Studio\2022\BuildTools'),
    (Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\2022\Community'),
    (Join-Path ${env:ProgramFiles} 'Microsoft Visual Studio\2022\Community')
  )) {
    $candidates.Add((Join-Path $vsRoot 'Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe'))
  }
  $vcpkgCmakes = @(Get-ChildItem -Path (Join-Path $VcpkgRoot 'downloads\tools') -Filter 'cmake.exe' -Recurse -File -ErrorAction SilentlyContinue | Sort-Object FullName -Descending)
  foreach ($c in $vcpkgCmakes) { $candidates.Add($c.FullName) }

  foreach ($c in $candidates) {
    if (Test-Path -LiteralPath $c) { return $c }
  }
  throw '找不到 cmake.exe（PATH、VS2022 BuildTools、vcpkg downloads 都没有）'
}

function Find-Python {
  if ($PythonExe) {
    if (-not (Test-Path -LiteralPath $PythonExe)) { throw "指定的 Python 不存在：$PythonExe" }
    return (Get-FullPath $PythonExe)
  }
  $cmd = Get-Command python.exe -ErrorAction SilentlyContinue
  if ($cmd) { return $cmd.Source }
  foreach ($c in @(
    (Join-Path $env:ProgramFiles 'python\python.exe'),
    (Join-Path $env:LOCALAPPDATA 'Programs\Python\Python312\python.exe'),
    (Join-Path $env:LOCALAPPDATA 'Programs\Python\Python311\python.exe')
  )) {
    if (Test-Path -LiteralPath $c) { return $c }
  }
  throw '找不到 python.exe'
}

function Build-Native {
  if (-not (Test-Path -LiteralPath (Join-Path $VcpkgInst 'lib\freerdp3.lib'))) {
    throw "找不到 $VcpkgInst\lib\freerdp3.lib；先运行不带 -SkipInstall 的 build_native.ps1"
  }

  Import-VsDevEnv
  $cmake = Find-CMake
  $python = Find-Python

  if ($Clean) { Remove-TreeSafely $BuildDir $WorkRoot }
  New-Item -ItemType Directory -Force $BuildDir | Out-Null

  Write-Step 'CMake 配置 Yomori.RdpNative'
  $generator = 'Visual Studio 17 2022'
  $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
  if (Test-Path -LiteralPath $vswhere) {
    $vsVersion = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationVersion
    if ($vsVersion) {
      $major = ([string]$vsVersion).Split('.')[0]
      $match = (& $cmake --help | Select-String -Pattern ("Visual Studio " + $major + " [0-9]{4}") | Select-Object -First 1)
      if (-not $match) { throw "CMake 不支持已安装的 Visual Studio $vsVersion" }
      $generator = [regex]::Match([string]$match, 'Visual Studio [0-9]+ [0-9]{4}').Value
    }
  }
  $configure = @(
    '-S', (Join-Path $RepoRoot 'winui3\src\Yomori.RdpNative'),
    '-B', $BuildDir,
    '-G', $generator,
    '-A', 'x64',
    ('-DVCPKG_INST=' + $VcpkgInst),
    ('-DYCN_VCPKG_COMMIT=' + $PinnedVcpkgCommit),
    ('-DYCN_VCPKG_TRIPLET=' + $Triplet),
    '-DYCN_FREERDP_FEATURES=client;ffmpeg',
    '-DYCN_EXPECTED_FREERDP_VERSION=3.32.0',
    '-DYCN_EXPECTED_FFMPEG_VERSION=9.0.2',
    ('-DYCN_VERIFY_TOOL=' + $VerifyTool),
    ('-DYCN_PYTHON_EXECUTABLE=' + $python)
  )
  & $cmake @configure
  if ($LASTEXITCODE -ne 0) { throw "CMake 配置失败（exit $LASTEXITCODE）" }

  Write-Step "编译 ycn_rdp.dll（$Configuration / x64）"
  & $cmake --build $BuildDir --config $Configuration --parallel
  if ($LASTEXITCODE -ne 0) { throw "CMake 编译失败（exit $LASTEXITCODE）" }

  Write-Step "复制原生层产物到 $OutDir"
  New-Item -ItemType Directory -Force $OutDir | Out-Null
  $builtDir = Join-Path $BuildDir $Configuration
  Get-ChildItem -LiteralPath $builtDir -Filter '*.dll' -File | ForEach-Object {
    Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $OutDir $_.Name) -Force
  }
  $capSrc = Join-Path $builtDir 'ycn_rdp.capabilities.json'
  if (-not (Test-Path -LiteralPath $capSrc)) { throw "CMake 没有生成 $capSrc" }
  Copy-Item -LiteralPath $capSrc -Destination (Join-Path $OutDir 'ycn_rdp.capabilities.json') -Force

  Write-Step '把 native-deps patch 哈希写入能力信息'
  $patches = @()
  if (Test-Path -LiteralPath $PatchDir) {
    $patches = @(Get-ChildItem -LiteralPath $PatchDir -Filter '*.patch' -File | Sort-Object Name | ForEach-Object {
      [pscustomobject]@{ name = $_.Name; sha256 = (Get-Sha256 $_.FullName) }
    })
  }
  $capPath = Join-Path $OutDir 'ycn_rdp.capabilities.json'
  $cap = Get-Content -LiteralPath $capPath -Raw | ConvertFrom-Json
  $cap | Add-Member -NotePropertyName patches -NotePropertyValue $patches -Force
  $cap | Add-Member -NotePropertyName generated_by -NotePropertyValue 'winui3/tools/build_native.ps1' -Force
  $cap | Add-Member -NotePropertyName cache_key -NotePropertyValue (Get-NativeCacheKey) -Force
  $capJson = $cap | ConvertTo-Json -Depth 16
  [System.IO.File]::WriteAllText($capPath, $capJson + "`n", (New-Object System.Text.UTF8Encoding($false)))

  Write-Step '验证 PE 传递依赖 / FFmpeg 组件 / 硬件解码编译证据 / 干净加载'
  $verifyArgs = @(
    $VerifyTool,
    '--dir', $OutDir,
    '--require-ffmpeg',
    '--expect-hwaccel',
    '--forbid-system-gpu',
    '--allow-vc-runtime-external',
    '--capabilities', $capPath,
    '--lock', $LockFile,
    '--patches-dir', $PatchDir
  )
  if (-not $NoCleanLoad) { $verifyArgs += @('--clean-load', '--expect-ycn-h264-api') }
  & (Find-Python) @verifyArgs
  if ($LASTEXITCODE -ne 0) { throw "原生层运行依赖验证失败（exit $LASTEXITCODE）" }

  Write-Host ''
  Write-Host '原生层构建完成：' -ForegroundColor Green
  Write-Host ("  DLL 目录 : {0}" -f $OutDir)
  Write-Host ("  能力信息 : {0}" -f $capPath)
  Write-Host ("  vcpkg    : {0}" -f $VcpkgInst)
}

if ($InstallOnly) {
  Install-Deps
  Write-Host ''
  Write-Host ("依赖已安装：{0}" -f $VcpkgInst) -ForegroundColor Green
  return
}

if (-not $SkipInstall) {
  Install-Deps
}

if (-not (Test-Path -LiteralPath $VerifyTool)) {
  throw "缺少验证工具：$VerifyTool"
}

Build-Native
