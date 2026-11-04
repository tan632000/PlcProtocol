#Requires -Version 5.1
<#
.SYNOPSIS
  Packages the Edge for a Windows machine without .NET SDK, source code or Internet (run on the dev machine).

.DESCRIPTION
  Publishes src/Edge self-contained for win-x64 and zips it together with appsettings.json, the
  install/uninstall scripts and the desktop monitor (src/EdgeDesktop, in EdgeDesktop\). Copy the zip to the
  Edge machine and run install-edge.ps1 from it.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File deployments\edge\package-edge.ps1
#>
[CmdletBinding()]
param(
    # Where to write the zip (default: the temp folder; the path is printed).
    [string] $Output = (Join-Path ([IO.Path]::GetTempPath()) 'iiot-edge-win-x64.zip')
)

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$staging = Join-Path ([IO.Path]::GetTempPath()) ('iiot-edge-package-' + [guid]::NewGuid().ToString('N'))

try {
    Write-Host "Publishing the Edge (self-contained, win-x64)..."
    & dotnet publish (Join-Path $repo 'src\Edge\IndustrialIot.Edge.csproj') -c Release -r win-x64 --self-contained true `
        -o $staging -nologo -v quiet
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE." }

    # The desktop monitor, self-contained too; install-edge.ps1 moves EdgeDesktop\ to its own folder every user may run.
    Write-Host "Publishing the desktop monitor (self-contained, win-x64)..."
    & dotnet publish (Join-Path $repo 'src\EdgeDesktop\IndustrialIot.EdgeDesktop.csproj') -c Release -r win-x64 --self-contained true `
        -o (Join-Path $staging 'EdgeDesktop') -nologo -v quiet
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish of the desktop monitor failed with exit code $LASTEXITCODE." }

    # The scripts travel with the program, so the Edge machine needs nothing but this zip.
    Copy-Item (Join-Path $PSScriptRoot 'install-edge.ps1'), (Join-Path $PSScriptRoot 'uninstall-edge.ps1') $staging

    if (Test-Path $Output) { Remove-Item $Output -Force }
    Compress-Archive -Path (Join-Path $staging '*') -DestinationPath $Output
    Write-Host "Package: $Output"
}
finally {
    if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }
}
