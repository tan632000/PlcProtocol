#Requires -Version 5.1
<#
.SYNOPSIS
  Removes the Windows Service "IndustrialIot.Edge" and its program files; keeps the data unless -RemoveData.

.DESCRIPTION
  Run in an elevated PowerShell. Which folders to remove is read from the registered service itself (its
  executable path and its Edge__Buffer__Path setting), never from arguments, and each must be one of the
  IndustrialIot\Edge* folders the installer creates. The tag list (appsettings.json) is copied to
  <data folder>\appsettings.backup.json first; the buffer database stays unless -RemoveData is given.
#>
[CmdletBinding()]
param([switch] $RemoveData)

$ErrorActionPreference = 'Stop'
$ServiceName = 'IndustrialIot.Edge'

function Stop-WithError([string] $Message) {
    Write-Host "uninstall-edge: $Message" -ForegroundColor Red
    exit 1
}

function Test-EdgeFolder([string] $Path, [string] $Root) {
    $pattern = '^' + [regex]::Escape($Root.TrimEnd('\')) + '\\IndustrialIot\\Edge[A-Za-z]*$'
    return $Path -match $pattern
}

# Deletes a folder tree without ever following a link or junction: a link is removed itself, never what it points
# to. (Remove-Item -Recurse in PowerShell 5.1 follows junctions; [IO.Directory]::Delete($dir, $true) in .NET
# Framework stops with "access denied" on them.)
function Remove-FolderNoFollow([string] $Path) {
    if ([IO.File]::GetAttributes($Path) -band [IO.FileAttributes]::ReparsePoint) { [IO.Directory]::Delete($Path); return }
    foreach ($entry in [IO.Directory]::GetFileSystemEntries($Path)) {
        $attributes = [IO.File]::GetAttributes($entry)
        if (($attributes -band [IO.FileAttributes]::Directory) -and -not ($attributes -band [IO.FileAttributes]::ReparsePoint)) {
            Remove-FolderNoFollow $entry
        }
        elseif ($attributes -band [IO.FileAttributes]::Directory) { [IO.Directory]::Delete($entry) }
        else {
            if ($attributes -band [IO.FileAttributes]::ReadOnly) { [IO.File]::SetAttributes($entry, [IO.FileAttributes]::Normal) }
            [IO.File]::Delete($entry)
        }
    }
    [IO.Directory]::Delete($Path)
}

$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Stop-WithError 'run this script from an elevated PowerShell (Run as administrator).'
}

$service = Get-CimInstance Win32_Service -Filter "Name='$ServiceName'"
if (-not $service) { Stop-WithError "the service $ServiceName is not installed; nothing to remove." }

# Folders come from the service registration, and must be the installer's folders.
$exe = $service.PathName.Trim('"')
$installDir = [IO.Path]::GetFullPath((Split-Path $exe -Parent)).TrimEnd('\')
$environment = (Get-Item "HKLM:\SYSTEM\CurrentControlSet\Services\$ServiceName").GetValue('Environment')
$bufferSetting = @($environment | Where-Object { $_ -like 'Edge__Buffer__Path=*' }) | Select-Object -First 1
$environment = $null
$dataDir = if ($bufferSetting) { [IO.Path]::GetFullPath((Split-Path ($bufferSetting -split '=', 2)[1] -Parent)).TrimEnd('\') } else { $null }

if (-not (Test-EdgeFolder $installDir $env:ProgramFiles) -or -not (Test-Path -LiteralPath $exe)) {
    Stop-WithError "unexpected program folder '$installDir'; remove the service and its folders by hand."
}
if ($dataDir -and -not (Test-EdgeFolder $dataDir $env:ProgramData)) {
    Stop-WithError "unexpected data folder '$dataDir'; remove the service and its folders by hand."
}

Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
& sc.exe delete $ServiceName | Out-Null
for ($i = 0; $i -lt 30 -and (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue); $i++) { Start-Sleep -Milliseconds 500 }
if (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) {
    Stop-WithError 'the service is still registered (close services.msc and retry).'
}
Write-Host "Removed the service $ServiceName."

$settings = Join-Path $installDir 'appsettings.json'
if ($dataDir -and (Test-Path -LiteralPath $settings) -and (Test-Path -LiteralPath $dataDir)) {
    # The service can write in the data folder: a backup name it turned into a link would redirect this copy.
    $backup = Join-Path $dataDir 'appsettings.backup.json'
    if ((Test-Path -LiteralPath $backup) -and ([IO.File]::GetAttributes($backup) -band [IO.FileAttributes]::ReparsePoint)) {
        if ([IO.File]::GetAttributes($backup) -band [IO.FileAttributes]::Directory) { [IO.Directory]::Delete($backup) } else { [IO.File]::Delete($backup) }
    }
    Copy-Item -LiteralPath $settings -Destination (Join-Path $dataDir 'appsettings.backup.json') -Force
    Write-Host "Kept the tag list as $dataDir\appsettings.backup.json"
}
Remove-FolderNoFollow $installDir
Write-Host "Removed $installDir"
if ($RemoveData -and $dataDir -and (Test-Path -LiteralPath $dataDir)) {
    Remove-FolderNoFollow $dataDir
    Write-Host "Removed $dataDir (buffer database and certificate)"
}

# Empty IndustrialIot parents and the event source go when nothing of the Edge is left.
foreach ($parent in (Split-Path $installDir -Parent), $(if ($dataDir) { Split-Path $dataDir -Parent })) {
    if ($parent -and (Test-Path -LiteralPath $parent) -and -not (Get-ChildItem -LiteralPath $parent -Force)) {
        Remove-Item -LiteralPath $parent -Force
    }
}
if ([Diagnostics.EventLog]::SourceExists($ServiceName)) { Remove-EventLog -Source $ServiceName }
