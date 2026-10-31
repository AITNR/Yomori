<#
.SYNOPSIS
  Capture normalized per-process CPU and optional WPR CPU stacks for an active embedded session.
.DESCRIPTION
  Does not start/stop Yomori, reconnect a channel, change quality, or change server policy.
  Run against the controller while the same moving scene remains visible.
.EXAMPLE
  ./winui3/tools/profile_rdp_cpu.ps1 -DurationSeconds 30
.EXAMPLE
  # From an already elevated PowerShell; never prompts for elevation itself:
  ./winui3/tools/profile_rdp_cpu.ps1 -TargetProcessId 1234 -RecordStacks
#>
[CmdletBinding()]
param(
    [int]$TargetProcessId = 0,
    [ValidateRange(5, 120)][int]$DurationSeconds = 30,
    [ValidateRange(1, 5)][int]$IntervalSeconds = 1,
    [string]$OutputDirectory = '',
    [switch]$RecordStacks
)
$ErrorActionPreference = 'Stop'
if (-not $TargetProcessId) {
    $candidate = Get-Process Yomori -ErrorAction SilentlyContinue |
        Sort-Object WorkingSet64 -Descending | Select-Object -First 1
    if (-not $candidate) { throw 'Yomori is not running. Start a visible embedded session, then run this script.' }
    $TargetProcessId = $candidate.Id
}
$target = Get-Process -Id $TargetProcessId -ErrorAction Stop
if ($target.ProcessName -ne 'Yomori') { throw "PID $TargetProcessId is not Yomori." }
if (-not $OutputDirectory) {
    $OutputDirectory = Join-Path $PSScriptRoot ('../tests/build/cpu-profile/' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
}
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$logicalCpus = [int](Get-CimInstance Win32_ComputerSystem).NumberOfLogicalProcessors
if ($logicalCpus -le 0) { $logicalCpus = [Environment]::ProcessorCount }
$csv = Join-Path $OutputDirectory 'cpu.csv'
$etl = Join-Path $OutputDirectory 'cpu-stacks.etl'
$metadata = [ordered]@{
    target_process_id = $TargetProcessId
    started = [DateTimeOffset]::Now.ToString('o')
    logical_processors = $logicalCpus
    cpu_percent_definition = '100 * delta(process CPU 100ns) / delta(wall 100ns) / logical processors'
    requested_seconds = $DurationSeconds
    interval_seconds = $IntervalSeconds
    stacks_requested = [bool]$RecordStacks
    stacks_saved = $false
    process_path = $null
    modules = @()
}
try { $metadata.process_path = $target.MainModule.FileName } catch { }
try {
    $metadata.modules = @($target.Modules | Where-Object {
        $_.ModuleName -in @('ycn_rdp.dll', 'freerdp3.dll', 'freerdp-client3.dll', 'avcodec-63.dll')
    } | ForEach-Object { [ordered]@{ name = $_.ModuleName; path = $_.FileName } })
} catch { $metadata.modules_unavailable = 'Process module access denied; rerun at the same elevation as the controller.' }
$wprInstance = "YomoriCpu-" + [Guid]::NewGuid().ToString("N")
$ownRecording = $false
$rows = New-Object System.Collections.Generic.List[object]
function Get-RawCpu {
    $items = @(Get-CimInstance Win32_PerfRawData_PerfProc_Process -Filter "IDProcess=$TargetProcessId")
    if ($items.Count -ne 1) { throw "PID $TargetProcessId disappeared or performance counters are unavailable." }
    return $items[0]
}
try {
    if ($RecordStacks) {
        $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
        $principal = New-Object Security.Principal.WindowsPrincipal($identity)
        if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
            throw '-RecordStacks requires an already elevated shell. CPU-only capture works without elevation.'
        }
        # Starting WPR fails safely if an incompatible recording already exists;
        # this script never cancels another recording.
        & wpr.exe -start CPU -filemode -instancename $wprInstance
        if ($LASTEXITCODE -ne 0) { throw 'WPR CPU start failed; an existing recording may be active.' }
        $ownRecording = $true
    }
    $previous = Get-RawCpu
    $clock = [Diagnostics.Stopwatch]::StartNew()
    while ($clock.Elapsed.TotalSeconds -lt $DurationSeconds) {
        Start-Sleep -Seconds $IntervalSeconds
        $current = Get-RawCpu
        $wall = [double]$current.Timestamp_Sys100NS - [double]$previous.Timestamp_Sys100NS
        $cpu = [double]$current.PercentProcessorTime - [double]$previous.PercentProcessorTime
        if ($wall -gt 0 -and $cpu -ge 0) {
            $rows.Add([pscustomobject]@{
                time = [DateTimeOffset]::Now.ToString('o')
                elapsed_seconds = [math]::Round($clock.Elapsed.TotalSeconds, 3)
                process_id = $TargetProcessId
                cpu_percent = [math]::Round(100 * $cpu / $wall / $logicalCpus, 3)
                cpu_core_equivalents = [math]::Round($cpu / $wall, 3)
                private_bytes = $current.PrivateBytes
                working_set = $current.WorkingSet
                thread_count = $current.ThreadCount
            })
        }
        $previous = $current
    }
} finally {
    if ($ownRecording) {
        & wpr.exe -stop $etl -instancename $wprInstance
        $metadata.stacks_saved = ($LASTEXITCODE -eq 0 -and (Test-Path -LiteralPath $etl))
    }
    if ($rows.Count) {
        $rows | Export-Csv -LiteralPath $csv -NoTypeInformation -Encoding UTF8
        $metadata.samples = $rows.Count
        $metadata.average_cpu_percent = [math]::Round(($rows | Measure-Object cpu_percent -Average).Average, 3)
        $metadata.maximum_cpu_percent = ($rows | Measure-Object cpu_percent -Maximum).Maximum
    }
    $metadata.finished = [DateTimeOffset]::Now.ToString('o')
    $metadata | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'metadata.json') -Encoding UTF8
}
Write-Output "CPU capture: $csv"
Write-Output "Average normalized CPU: $($metadata.average_cpu_percent)%"
if ($metadata.stacks_saved) { Write-Output "CPU stack trace: $etl" }
