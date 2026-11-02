<#
.SYNOPSIS
  Samples the PLC Monitor (/api/status) of a running Edge and prints, per device, the median readMs and requests
  over at least -Cycles distinct poll cycles.

.DESCRIPTION
  A cycle is counted once per new lastGoodUtc of a device (a cycle that reached the PLC). Exit 0 when every device
  named in -Devices has at least -Cycles cycles and every median readMs is at most -MaxMedianMs; exit 1 otherwise.
  Without -Devices, every device that is not disabled is required. Read-only: it only calls GET /api/status.

.EXAMPLE
  powershell -NoProfile -File src/Edge/lab/measure-read-ms.ps1 -Url http://localhost:5080 -Cycles 30 -Devices S7Lab,Fx5uLab
#>
param(
    [string]$Url = 'http://localhost:5080',
    [int]$Cycles = 30,
    [string[]]$Devices = @(),
    [double]$MaxMedianMs = 500,
    [int]$TimeoutSeconds = 300
)

$ErrorActionPreference = 'Stop'

# -Devices a,b arrives as one string under -File; split it so both spellings work.
$Devices = @($Devices | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ })

function Get-Median([double[]]$values) {
    $sorted = $values | Sort-Object
    $n = $sorted.Count
    if ($n -eq 0) { return $null }
    if ($n % 2 -eq 1) { return [double]$sorted[($n - 1) / 2] }
    return ([double]$sorted[$n / 2 - 1] + [double]$sorted[$n / 2]) / 2
}

$samples = @{}   # device name -> @{ Seen = set of lastGoodUtc; ReadMs = list; Requests = list; State; Model }
$deadline = (Get-Date).AddSeconds($TimeoutSeconds)
$statusUrl = $Url.TrimEnd('/') + '/api/status'

while ((Get-Date) -lt $deadline) {
    try {
        $status = Invoke-RestMethod -Uri $statusUrl -TimeoutSec 5
    } catch {
        Write-Host "GET $statusUrl failed: $($_.Exception.Message)"
        Start-Sleep -Milliseconds 1000
        continue
    }

    foreach ($d in @($status.devices)) {
        if (-not $samples.ContainsKey($d.name)) {
            $samples[$d.name] = @{ Seen = @{}; ReadMs = New-Object System.Collections.Generic.List[double]; Requests = New-Object System.Collections.Generic.List[double]; State = ''; Model = $d.model }
        }
        $s = $samples[$d.name]
        $s.State = $d.state
        $cycle = "$($d.lastGoodUtc)"
        if ($cycle -and $null -ne $d.readMs -and -not $s.Seen.ContainsKey($cycle)) {
            $s.Seen[$cycle] = $true
            $s.ReadMs.Add([double]$d.readMs)
            if ($null -ne $d.requests) { $s.Requests.Add([double]$d.requests) }
        }
    }

    $required = if ($Devices.Count -gt 0) { $Devices } else { @($samples.Keys | Where-Object { $samples[$_].State -ne 'disabled' }) }
    $done = $required.Count -gt 0
    foreach ($name in $required) {
        if (-not $samples.ContainsKey($name) -or $samples[$name].ReadMs.Count -lt $Cycles) { $done = $false }
    }
    if ($done) { break }
    Start-Sleep -Milliseconds 250
}

$required = if ($Devices.Count -gt 0) { $Devices } else { @($samples.Keys | Where-Object { $samples[$_].State -ne 'disabled' }) }
$failed = $required.Count -eq 0
if ($failed) { Write-Host 'No device to measure.' }

Write-Host ('{0,-16} {1,-8} {2,-12} {3,7} {4,15} {5,15}' -f 'Device', 'Model', 'State', 'Cycles', 'Median readMs', 'Median requests')
foreach ($name in @($samples.Keys | Sort-Object)) {
    $s = $samples[$name]
    $medianMs = Get-Median $s.ReadMs.ToArray()
    $medianRequests = Get-Median $s.Requests.ToArray()
    Write-Host ('{0,-16} {1,-8} {2,-12} {3,7} {4,15} {5,15}' -f $name, $s.Model, $s.State, $s.ReadMs.Count, $medianMs, $medianRequests)
}

foreach ($name in $required) {
    if (-not $samples.ContainsKey($name)) {
        Write-Host "FAIL: device '$name' is not in $statusUrl."
        $failed = $true
        continue
    }
    $s = $samples[$name]
    if ($s.ReadMs.Count -lt $Cycles) {
        Write-Host "FAIL: device '$name' has $($s.ReadMs.Count) cycles, fewer than $Cycles within $TimeoutSeconds s."
        $failed = $true
        continue
    }
    $medianMs = Get-Median $s.ReadMs.ToArray()
    if ($medianMs -gt $MaxMedianMs) {
        Write-Host "FAIL: device '$name' median readMs $medianMs is over $MaxMedianMs."
        $failed = $true
    }
}

if ($failed) { exit 1 }
Write-Host "PASS: every required device has at least $Cycles cycles with a median readMs at most $MaxMedianMs."
exit 0
