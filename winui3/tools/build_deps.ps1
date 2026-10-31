# 构建原生层依赖：freerdp[client,ffmpeg]（带 H.264 硬解）
#
# 为什么需要这个脚本而不是直接跑 vcpkg install：
#
#   vcpkg 在 Windows 上会**重建**子进程环境 —— 它通过调 reg.exe 读
#   HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\Environment 来还原系统 PATH。
#   当 reg.exe 被安全产品/策略拦截（本机就是这种情况）时，重建出来的 PATH 不含
#   Windows SDK 的 bin 目录，于是 CMake 找不到 rc.exe / mt.exe：
#
#     --rc=rc --mt=CMAKE_MT-NOTFOUND
#     RC Pass 1: command "rc /fo .../manifest.res ..." failed
#     CMake will not be able to correctly generate this project
#
#   连最简单的 cl 编译器测试都过不去，编译任何包都会失败。
#
#   解法有两层：
#     1) VCPKG_KEEP_ENV_VARS 让 vcpkg 保留我们设的 PATH（这是关键，实测有效）；
#     2) WindowsSDKVersion / WindowsSdkDir 给 vcpkg 的
#        scripts/cmake/vcpkg_get_windows_sdk.cmake 一个现成答案，免得它去查注册表。
#
#   两者缺一，构建会在 "Detecting C compiler ABI info" 处失败。
#
# 用法：
#   pwsh -File tools/build_deps.ps1
#   pwsh -File tools/build_deps.ps1 -VcpkgRoot D:\vcpkg
#
# 产物：<vcpkg>/installed/x64-windows/{lib,bin} 下的 freerdp3 / winpr3 /
#       avcodec-* / avutil-* / swscale-* / swresample-*。
# 原生层 CMake 通过 -DVCPKG_INST 指向 installed/x64-windows。

param(
    [string]$VcpkgRoot = "",
    [string]$Triplet = "x64-windows"
)

$ErrorActionPreference = "Stop"

if (-not $VcpkgRoot) {
    # 与 Yomori.RdpNative/CMakeLists.txt 的默认值保持一致：
    # <仓库>/../_tools/vcpkg。$PSScriptRoot = .../整合/repo/winui3/tools，
    # 往上四级到 .../WorkBuddy，再拼 _tools/vcpkg。
    $up4 = Split-Path (Split-Path (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent) -Parent) -Parent
    $VcpkgRoot = Join-Path $up4 "_tools/vcpkg"
}

$vcpkgExe = Join-Path $VcpkgRoot "vcpkg.exe"
if (-not (Test-Path $vcpkgExe)) {
    throw "找不到 $vcpkgExe —— 用 -VcpkgRoot 指定 vcpkg 根目录"
}

$overlayPorts = Join-Path $PSScriptRoot "../third_party/vcpkg-ports"
$overlayPorts = (Resolve-Path $overlayPorts).Path
if (-not (Test-Path (Join-Path $overlayPorts "freerdp/portfile.cmake"))) {
    throw "找不到 overlay port：$overlayPorts\freerdp —— 它承载 WITH_FFMPEG_HWACCEL=ON，缺了就只能软解"
}

# overlay triplet 修的是另一个独立的坑：vcpkg 把 CMAKE_FIND_ROOT_PATH 设成
# installed/<triplet>，CMake 据此按 cross-compiling 处理，find_program 会把 PATH
# 里每一项都拼到该前缀之后，rc.exe / mt.exe 永远找不到。见 triplet 文件头注释。
$overlayTriplets = Join-Path $PSScriptRoot "../third_party/vcpkg-triplets"
$overlayTriplets = (Resolve-Path $overlayTriplets).Path
if (-not (Test-Path (Join-Path $overlayTriplets "$Triplet.cmake"))) {
    throw "找不到 overlay triplet：$overlayTriplets\$Triplet.cmake —— 缺了会在 RC Pass 1 失败"
}

# ── Windows SDK ────────────────────────────────────────────────────────────
# 取已安装的最高版本（要求 x64\rc.exe 存在，避免挑到只有头文件的残缺 SDK）。
$kitsRoot = "C:\Program Files (x86)\Windows Kits\10"
if (-not (Test-Path $kitsRoot)) {
    throw "找不到 Windows SDK：$kitsRoot"
}

$sdkVer = Get-ChildItem (Join-Path $kitsRoot "bin") -Directory -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -match '^10\.' -and (Test-Path (Join-Path $_.FullName "x64\rc.exe")) } |
    Sort-Object { [version]($_.Name -replace '[^0-9.].*$','') } -Descending |
    Select-Object -First 1 -ExpandProperty Name

if (-not $sdkVer) {
    throw "在 $kitsRoot\bin 下没找到带 x64\rc.exe 的 SDK 版本"
}

$sdkBin = Join-Path $kitsRoot "bin\$sdkVer\x64"

# ── vcpkg 环境 ─────────────────────────────────────────────────────────────
# PATH 必须在 KEEP_ENV_VARS 里，否则被 vcpkg 重建时丢掉。
# INCLUDE / LIB 由 overlay triplet 负责补 SDK 路径，这里一并保留以免被清空。
$env:VCPKG_KEEP_ENV_VARS = "PATH;WindowsSDKVersion;WindowsSdkDir;INCLUDE;LIB;LIBPATH"
$env:WindowsSDKVersion = "$sdkVer\"
$env:WindowsSdkDir = "$kitsRoot\"
$env:PATH = "$sdkBin;$env:PATH"

Write-Host "vcpkg     : $vcpkgExe"
Write-Host "overlay   : $overlayPorts"
Write-Host "SDK       : $sdkVer"
Write-Host "rc.exe    : $(Join-Path $sdkBin 'rc.exe')"
Write-Host ""

# 先验证环境里真的能看见 rc —— 早失败比在 CMake configure 里失败省十分钟。
$probe = & $vcpkgExe env "where rc.exe" --triplet $Triplet 2>&1
if ($probe -notmatch 'rc\.exe') {
    throw "vcpkg 环境里仍找不到 rc.exe，构建必然失败。检查 VCPKG_KEEP_ENV_VARS 是否生效。输出：$probe"
}

& $vcpkgExe install "freerdp[client,ffmpeg]:$Triplet" `
    --overlay-ports=$overlayPorts `
    --overlay-triplets=$overlayTriplets `
    --recurse
exit $LASTEXITCODE
