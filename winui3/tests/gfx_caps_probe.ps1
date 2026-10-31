<#
.SYNOPSIS
Build and run the offline FreeRDP RDPGFX CAPS probe.

.DESCRIPTION
Compiles winui3/tests/gfx_caps_probe.c against a FreeRDP SDK and runs it against
the matching freerdp-client3.dll.  Build products and captured PDU files go to
winui3/tests/build/gfx-caps (ignored).

No RDP login, network access, server change, native bridge change, or upstream
FreeRDP patch is involved.

.PARAMETER SdkRoot
SDK root containing include/, lib/, bin/ (the x64-windows vcpkg tree), or a
FreeRDP working root containing vcpkg-installed/x64-windows.  If omitted, the
script checks FREERDP_SDK_ROOT / GFX_FREERDP_SDK, then the local native-deps
installed trees, then the known WorkBuddy ycn-hwdecode SDK.

.PARAMETER NoRun
Build only; do not execute the probe.
#>
[CmdletBinding()]
param(
    [string]$SdkRoot = "",
    [switch]$NoRun
)

$ErrorActionPreference = "Stop"

$TestDir = $PSScriptRoot
$RepoRoot = (Resolve-Path (Join-Path $TestDir "..\..")).Path
$Source = Join-Path $TestDir "gfx_caps_probe.c"
$BuildDir = Join-Path $TestDir "build\gfx-caps"
$Exe = Join-Path $BuildDir "gfx_caps_probe.exe"
$Log = Join-Path $BuildDir "gfx-caps-probe.log"

function Resolve-SdkRoot([string]$Requested) {
    $candidates = New-Object System.Collections.Generic.List[string]
    if ($Requested) { $candidates.Add($Requested) }
    if ($env:FREERDP_SDK_ROOT) { $candidates.Add($env:FREERDP_SDK_ROOT) }
    if ($env:GFX_FREERDP_SDK) { $candidates.Add($env:GFX_FREERDP_SDK) }
    if ($env:USERPROFILE) {
        $candidates.Add((Join-Path $env:USERPROFILE "WorkBuddy\_tools\ycn-hwdecode-20261031\vcpkg-installed\x64-windows"))
    }
    $candidates.Add((Join-Path $RepoRoot "winui3\native-deps\installed\x64-windows"))
    $candidates.Add((Join-Path $RepoRoot "winui3\native-deps\vcpkg_installed\x64-windows"))

    foreach ($candidate in $candidates) {
        if (-not $candidate) { continue }
        $root = $candidate
        if ((Test-Path -LiteralPath (Join-Path $root "vcpkg-installed\x64-windows\bin\freerdp-client3.dll"))) {
            $root = Join-Path $root "vcpkg-installed\x64-windows"
        }
        if (Test-Path -LiteralPath (Join-Path $root "bin\freerdp-client3.dll")) {
            return (Resolve-Path -LiteralPath $root).Path
        }
    }
    throw "FreeRDP SDK not found. Pass -SdkRoot <x64-windows SDK root> or set FREERDP_SDK_ROOT."
}

$ResolvedSdkRoot = Resolve-SdkRoot $SdkRoot
$IncludeDir = Join-Path $ResolvedSdkRoot "include"
$WinPrIncludeDir = Join-Path $IncludeDir "winpr3"
$LibDir = Join-Path $ResolvedSdkRoot "lib"
$BinDir = Join-Path $ResolvedSdkRoot "bin"
$ClientDll = Join-Path $BinDir "freerdp-client3.dll"

function Assert-File([string]$Path, [string]$Label) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "$Label not found: $Path"
    }
}

Assert-File $Source "probe source"
Assert-File $ClientDll "freerdp-client3.dll"
Assert-File (Join-Path $IncludeDir "freerdp\freerdp.h") "FreeRDP header"
Assert-File (Join-Path $WinPrIncludeDir "winpr\stream.h") "WinPR header"
Assert-File (Join-Path $LibDir "freerdp3.lib") "freerdp3 import library"
Assert-File (Join-Path $LibDir "winpr3.lib") "winpr3 import library"

New-Item -ItemType Directory -Force -Path $BuildDir | Out-Null

$launcherCandidates = @(
    "C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\Common7\Tools\Launch-VsDevShell.ps1",
    "C:\Program Files\Microsoft Visual Studio\2022\BuildTools\Common7\Tools\Launch-VsDevShell.ps1",
    "C:\Program Files (x86)\Microsoft Visual Studio\2022\Community\Common7\Tools\Launch-VsDevShell.ps1",
    "C:\Program Files\Microsoft Visual Studio\2022\Community\Common7\Tools\Launch-VsDevShell.ps1"
)
$launcher = $launcherCandidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if (-not $launcher) {
    $found = Get-ChildItem "C:\Program Files (x86)\Microsoft Visual Studio\2022" -Recurse -Filter Launch-VsDevShell.ps1 -ErrorAction SilentlyContinue |
        Select-Object -First 1 -ExpandProperty FullName
    if ($found) { $launcher = $found }
}
if (-not $launcher) {
    throw "Launch-VsDevShell.ps1 not found; VS2022 Build Tools are required."
}

Write-Host "SDK root: $ResolvedSdkRoot"
Write-Host "Importing VS2022 developer environment: $launcher"
& $launcher -Arch amd64 -HostArch amd64 -SkipAutomaticLocation | Out-Null

$cl = Get-Command cl.exe -ErrorAction SilentlyContinue
if (-not $cl) {
    throw "cl.exe is not available after importing the VS developer environment."
}

Write-Host "Building $Exe"
$obj = Join-Path $BuildDir "gfx_caps_probe.obj"
$pdb = Join-Path $BuildDir "gfx_caps_probe.pdb"
$clArgs = @(
    "/nologo",
    "/std:c17",
    "/W3",
    "/O2",
    "/MD",
    "/D_CRT_SECURE_NO_WARNINGS",
    "/I$IncludeDir",
    "/I$WinPrIncludeDir",
    $Source,
    "/Fo$obj",
    "/Fd$pdb",
    "/Fe$Exe",
    "/link",
    "/LIBPATH:$LibDir",
    "freerdp-client3.lib",
    "freerdp3.lib",
    "winpr3.lib",
    "crypt32.lib",
    "bcrypt.lib",
    "ws2_32.lib",
    "user32.lib",
    "advapi32.lib",
    "shell32.lib",
    "ole32.lib",
    "shlwapi.lib"
)

& cl.exe @clArgs
if ($LASTEXITCODE -ne 0) {
    throw "cl.exe failed with exit code $LASTEXITCODE"
}
Assert-File $Exe "probe executable"

$sourceHash = (Get-FileHash -LiteralPath $Source -Algorithm SHA256).Hash
$exeHash = (Get-FileHash -LiteralPath $Exe -Algorithm SHA256).Hash
$dllHash = (Get-FileHash -LiteralPath $ClientDll -Algorithm SHA256).Hash
Write-Host "source sha256: $sourceHash"
Write-Host "exe    sha256: $exeHash"
Write-Host "dll    sha256: $dllHash"

if ($NoRun) {
    Write-Host "NoRun specified; build complete."
    exit 0
}

Write-Host "Running probe against: $ClientDll"
Write-Host "Working directory: $BuildDir"
$oldPath = $env:PATH
$oldLocation = Get-Location
$oldErrorAction = $ErrorActionPreference
try {
    $ErrorActionPreference = "Continue"
    $env:PATH = "$BinDir;$env:PATH"
    Set-Location -LiteralPath $BuildDir
    & $Exe $ClientDll 2>&1 | Tee-Object -FilePath $Log
    $runExitCode = $LASTEXITCODE
}
finally {
    $ErrorActionPreference = $oldErrorAction
    $env:PATH = $oldPath
    Set-Location -LiteralPath $oldLocation
}

Write-Host ""
Write-Host "Exit code: $runExitCode"
Write-Host "Log: $Log"
Write-Host "Captured PDU artifacts:"
Get-ChildItem -LiteralPath $BuildDir -Filter "caps-*" | Sort-Object Name | ForEach-Object {
    Write-Host ("  {0}  {1} bytes" -f $_.FullName, $_.Length)
}

if ($runExitCode -ne 0) {
    throw "gfx_caps_probe.exe failed with exit code $runExitCode"
}

