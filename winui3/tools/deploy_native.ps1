# 部署新的原生层（H.264 硬解版）到安装目录
#
# 必须以管理员身份运行：目标在 C:\Program Files\Yomori，普通权限写不进去。
#   PowerShell(管理员) > 右击本文件 →「使用 PowerShell 运行」，或：
#   Start-Process pwsh -Verb RunAs -ArgumentList '-File','<本文件路径>'
#
# 前置：先完全退出 Yomori。DLL 被占用时复制会失败（且程序运行中替换 DLL
# 也不会生效，必须重启）。
#
# 变动的文件（其余不动）：
#   覆盖  ycn_rdp.dll          —— 新增 FREERDP_HWCTX_TYPE=d3d11va 与 Gfx codec 协商
#   覆盖  freerdp3.dll         —— 重新构建，链入 FFmpeg（avcodec-63.dll）
#   覆盖  freerdp-client3.dll  —— 同批重建
#   覆盖  winpr3.dll           —— 同批重建
#   新增  avcodec-63.dll / avutil-61.dll / swscale-10.dll / swresample-7.dll
#
# 回滚：脚本会把被覆盖的文件备份到 <安装目录>\_backup-<时间戳>\，
#       把该目录内容拷回去即可。

param(
    [string]$InstallDir = "C:\Program Files\Yomori",
    [string]$SourceDir = "",
    [switch]$Force
)

$ErrorActionPreference = "Stop"

# ── 权限 ──────────────────────────────────────────────────────────────────
$id = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object Security.Principal.WindowsPrincipal($id)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Host "需要管理员权限。请以管理员身份重新运行本脚本。" -ForegroundColor Red
    exit 1
}

if (-not $SourceDir) {
    $SourceDir = Join-Path $PSScriptRoot "../src/Yomori.RdpNative/build/Release"
}
$SourceDir = (Resolve-Path $SourceDir).Path

if (-not (Test-Path $InstallDir)) {
    Write-Host "安装目录不存在：$InstallDir" -ForegroundColor Red
    exit 1
}

# ── 程序是否在跑 ───────────────────────────────────────────────────────────
$running = Get-Process -Name "Yomori" -ErrorAction SilentlyContinue
if ($running) {
    Write-Host "Yomori 正在运行（PID $($running.Id -join ', ')），DLL 被占用。" -ForegroundColor Yellow
    if (-not $Force) {
        Write-Host "请先完全退出 Yomori 再运行本脚本，或加 -Force 由脚本结束它。" -ForegroundColor Yellow
        exit 1
    }
    Write-Host "正在结束 Yomori…" -ForegroundColor Yellow
    $running | Stop-Process -Force
    Start-Sleep -Seconds 3
}

# ── 待部署清单 ─────────────────────────────────────────────────────────────
# 前三项是原有依赖（同批重建，必须一起换，否则 FreeRDP 内部版本不匹配）。
$overwrite = @(
    "ycn_rdp.dll",
    "freerdp3.dll",
    "freerdp-client3.dll",
    "winpr3.dll"
)
# FFmpeg：新增，之前安装目录里没有。
$add = @(
    "avcodec-63.dll",
    "avutil-61.dll",
    "swscale-10.dll",
    "swresample-7.dll"
)

$all = $overwrite + $add
foreach ($f in $all) {
    if (-not (Test-Path (Join-Path $SourceDir $f))) {
        Write-Host "源文件缺失：$(Join-Path $SourceDir $f)" -ForegroundColor Red
        Write-Host "先跑 tools/build_deps.ps1 再 cmake --build，否则没有产物。" -ForegroundColor Red
        exit 1
    }
}

# ── 备份 ──────────────────────────────────────────────────────────────────
$stamp = Get-Date -Format "yyyyMMdd-HHmmss"
$backupDir = Join-Path $InstallDir "_backup-$stamp"
New-Item -ItemType Directory -Path $backupDir -Force | Out-Null

foreach ($f in $all) {
    $target = Join-Path $InstallDir $f
    if (Test-Path $target) {
        Copy-Item $target (Join-Path $backupDir $f) -Force
        Write-Host "已备份 $f"
    }
}

# ── 复制 ──────────────────────────────────────────────────────────────────
foreach ($f in $all) {
    Copy-Item (Join-Path $SourceDir $f) (Join-Path $InstallDir $f) -Force
    $size = [math]::Round((Get-Item (Join-Path $InstallDir $f)).Length / 1MB, 1)
    Write-Host ("已部署 {0,-22} {1,6} MB" -f $f, $size)
}

# ── 校验 ──────────────────────────────────────────────────────────────────
$missing = @()
foreach ($f in $all) {
    if (-not (Test-Path (Join-Path $InstallDir $f))) { $missing += $f }
}
if ($missing) {
    Write-Host ("校验失败，缺少：" + ($missing -join ", ")) -ForegroundColor Red
    Write-Host "备份在 $backupDir" -ForegroundColor Yellow
    exit 1
}

Write-Host ""
Write-Host "部署完成。回滚备份：$backupDir" -ForegroundColor Green
Write-Host "下一步：启动 Yomori，让通道建立内嵌连接，然后看日志里是否出现" -ForegroundColor Green
Write-Host "  ·「Using d3d11va [...] for accelerated H264 decoding」" -ForegroundColor Green
Write-Host "  · gfx_state 不再是 2（GDI 全帧）" -ForegroundColor Green
