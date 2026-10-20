#Requires -Version 5.1
<#
.SYNOPSIS
  Installs the Edge from a package (package-edge.ps1) as the Windows Service "IndustrialIot.Edge".

.DESCRIPTION
  Run in an elevated PowerShell on the Edge machine. Needs no .NET SDK, no source code and no Internet.
  - Program files go to InstallDir (%ProgramFiles%\IndustrialIot\Edge*): Administrators/SYSTEM write, the service reads.
  - Data (buffer database, CA certificate) goes to DataDir (%ProgramData%\IndustrialIot\Edge*): only SYSTEM,
    Administrators and the service, all owned by Administrators.
  - The service runs as its own virtual account "NT SERVICE\IndustrialIot.Edge", starts automatically and
    restarts on failure.
  - Settings and the MQTT password are stored as the service's environment (registry value "Environment"),
    on a registry key only SYSTEM, Administrators and the service can read.
  The MQTT password is asked for interactively, never printed and never passed to a cmdlet (so PowerShell
  module/script-block logging cannot record it). On any failure everything created is removed.

.EXAMPLE
  .\install-edge.ps1 -PackagePath C:\Temp\iiot-edge-win-x64.zip -EdgeId line1-edge01 -BrokerHost 192.168.1.50 `
                     -CaCertificate C:\Temp\ca.pem -PlcHost 192.168.0.10
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)] [string] $PackagePath,
    [Parameter(Mandatory = $true)] [ValidatePattern('^[A-Za-z0-9_-]{1,64}$')] [string] $EdgeId,
    [Parameter(Mandatory = $true)] [string] $BrokerHost,
    [Parameter(Mandatory = $true)] [string] $CaCertificate,
    [Parameter(Mandatory = $true)] [string] $PlcHost,
    [ValidateRange(1, 65535)] [int] $BrokerPort = 8883,
    [string] $InstallDir = (Join-Path $env:ProgramFiles 'IndustrialIot\Edge'),
    [string] $DataDir = (Join-Path $env:ProgramData 'IndustrialIot\Edge')
)

$ErrorActionPreference = 'Stop'
$ServiceName = 'IndustrialIot.Edge'
$ServiceAccount = "NT SERVICE\$ServiceName"
$System = New-Object Security.Principal.SecurityIdentifier('S-1-5-18')
$Administrators = New-Object Security.Principal.SecurityIdentifier('S-1-5-32-544')

function Stop-WithError([string] $Message) {
    Write-Host "install-edge: $Message" -ForegroundColor Red
    exit 1
}

function Invoke-Sc {
    & sc.exe @args | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "sc.exe $($args[0]) failed with exit code $LASTEXITCODE." }
}

# Only <Root>\IndustrialIot\Edge or <Root>\IndustrialIot\Edge<letters>: never a system folder or anything else.
function Test-EdgeFolder([string] $Path, [string] $Root) {
    $pattern = '^' + [regex]::Escape($Root.TrimEnd('\')) + '\\IndustrialIot\\Edge[A-Za-z]*$'
    return $Path -match $pattern
}

function Test-ReparsePoint([string] $Path) {
    return (Test-Path -LiteralPath $Path) -and
        ((Get-Item -LiteralPath $Path -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)
}

# A folder that already exists must belong to Administrators or SYSTEM: an ordinary user who pre-created it
# would stay its owner and could always grant themselves access again (owners keep WRITE_DAC).
function Test-TrustedOwner([string] $Path) {
    if (-not (Test-Path -LiteralPath $Path)) { return $true }
    $owner = (Get-Acl -LiteralPath $Path).GetOwner([Security.Principal.SecurityIdentifier])
    return $owner -eq $System -or $owner -eq $Administrators
}

function New-PrivateSecurity([object[]] $Grants) {
    $acl = New-Object Security.AccessControl.DirectorySecurity
    $acl.SetAccessRuleProtection($true, $false)
    $acl.SetOwner($Administrators)
    $inherit = [Security.AccessControl.InheritanceFlags]'ContainerInherit, ObjectInherit'
    $none = [Security.AccessControl.PropagationFlags]::None
    foreach ($grant in $Grants) {
        $acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule($grant[0], $grant[1], $inherit, $none, 'Allow')))
    }
    return $acl
}

# Everything below a folder, without following links (a link inside is refused, never traversed).
function Get-ContentsNoLinks([string] $Path) {
    $pending = New-Object Collections.Generic.Stack[string]
    $pending.Push($Path)
    while ($pending.Count -gt 0) {
        foreach ($entry in [IO.Directory]::EnumerateFileSystemEntries($pending.Pop())) {
            $attributes = [IO.File]::GetAttributes($entry)
            if ($attributes -band [IO.FileAttributes]::ReparsePoint) { throw "$entry is a link or junction; refusing to continue." }
            $entry
            if ($attributes -band [IO.FileAttributes]::Directory) { $pending.Push($entry) }
        }
    }
}

# ACL: inheritance from the parent removed; exactly the given rules; owner Administrators. Everything already
# inside is reset to inherit only (explicit entries removed, protection off) and owned by Administrators, so a file
# someone slipped in beforehand keeps no permission of its own.
function Set-PrivateFolder([string] $Path, [object[]] $Grants) {
    (Get-Item -LiteralPath $Path -Force).SetAccessControl((New-PrivateSecurity $Grants))
    foreach ($entry in @(Get-ContentsNoLinks $Path)) {
        $item = Get-Item -LiteralPath $entry -Force
        $acl = $item.GetAccessControl()
        foreach ($rule in @($acl.GetAccessRules($true, $false, [Security.Principal.SecurityIdentifier]))) { [void]$acl.RemoveAccessRuleSpecific($rule) }
        $acl.SetAccessRuleProtection($false, $false)
        $acl.SetOwner($Administrators)
        $item.SetAccessControl($acl)
    }
}

# Creates a folder with its final ACL in one step; then insists the folder is really ours (someone could have
# created it in between, in which case CreateDirectory does nothing and the owner tells). -FolderOnly (the shared
# IndustrialIot parents) leaves what is inside untouched: resetting it would unprotect an existing Edge data folder
# and let it inherit the parent's Users read access until it is locked again.
function New-PrivateFolder([string] $Path, [object[]] $Grants, [switch] $FolderOnly) {
    [void][IO.Directory]::CreateDirectory($Path, (New-PrivateSecurity $Grants))
    if ((Test-ReparsePoint $Path) -or -not (Test-TrustedOwner $Path)) { throw "$Path was created or replaced by someone else; refusing to continue." }
    if ($FolderOnly) { (Get-Item -LiteralPath $Path -Force).SetAccessControl((New-PrivateSecurity $Grants)) }
    else { Set-PrivateFolder -Path $Path -Grants $Grants }
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

# Service registry key: the Environment value holds the MQTT password, so no other principal may read it.
function Set-PrivateServiceKey {
    $acl = New-Object Security.AccessControl.RegistrySecurity
    $acl.SetAccessRuleProtection($true, $false)
    $inherit = [Security.AccessControl.InheritanceFlags]::ContainerInherit
    $none = [Security.AccessControl.PropagationFlags]::None
    $service = New-Object Security.Principal.NTAccount($ServiceAccount)
    $acl.AddAccessRule((New-Object Security.AccessControl.RegistryAccessRule($System, 'FullControl', $inherit, $none, 'Allow')))
    $acl.AddAccessRule((New-Object Security.AccessControl.RegistryAccessRule($Administrators, 'FullControl', $inherit, $none, 'Allow')))
    $acl.AddAccessRule((New-Object Security.AccessControl.RegistryAccessRule($service, 'ReadKey', $inherit, $none, 'Allow')))
    $key = [Microsoft.Win32.Registry]::LocalMachine.OpenSubKey("SYSTEM\CurrentControlSet\Services\$ServiceName",
        [Microsoft.Win32.RegistryKeyPermissionCheck]::ReadWriteSubTree, [Security.AccessControl.RegistryRights]::ChangePermissions)
    try { $key.SetAccessControl($acl) } finally { $key.Close() }
}

$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Stop-WithError 'run this script from an elevated PowerShell (Run as administrator).'
}

# The password is asked for before the folder security checks: those checks and the work that depends on them must run without a
# pause in between (someone could prepare a folder while the installer is busy copying the password).
if (-not (Test-Path -LiteralPath $PackagePath -PathType Leaf)) { Stop-WithError "package not found: $PackagePath" }
if (-not (Test-Path -LiteralPath $CaCertificate -PathType Leaf)) { Stop-WithError "CA certificate not found: $CaCertificate" }

$password = Read-Host -AsSecureString "MQTT password for $EdgeId (deployments/.secrets/passwords/$EdgeId.txt on the stack machine)"
if ($password.Length -eq 0) { Stop-WithError 'the MQTT password is empty.' }

# --- Preconditions: checked before anything is changed. ---

$InstallDir = [IO.Path]::GetFullPath($InstallDir).TrimEnd('\')
$DataDir = [IO.Path]::GetFullPath($DataDir).TrimEnd('\')
if (-not (Test-EdgeFolder $InstallDir $env:ProgramFiles)) {
    Stop-WithError "InstallDir must be $env:ProgramFiles\IndustrialIot\Edge (elsewhere ordinary users could replace the program or its tag list)."
}
if (-not (Test-EdgeFolder $DataDir $env:ProgramData)) {
    Stop-WithError "DataDir must be $env:ProgramData\IndustrialIot\Edge."
}
$programParent = Split-Path $InstallDir -Parent
$dataParent = Split-Path $DataDir -Parent
foreach ($folder in $programParent, $InstallDir, $dataParent, $DataDir) {
    if (Test-ReparsePoint $folder) { Stop-WithError "$folder is a link or junction; refusing to install through it." }
    if (-not (Test-TrustedOwner $folder)) {
        Stop-WithError "$folder already exists and is not owned by Administrators or SYSTEM; remove it (or take ownership) first."
    }
}
if (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) {
    Stop-WithError "the service $ServiceName already exists; run uninstall-edge.ps1 first."
}
if (Test-Path -LiteralPath $InstallDir) { Stop-WithError "$InstallDir already exists; run uninstall-edge.ps1 first." }

$created = @{ ProgramParent = -not (Test-Path $programParent); DataParent = -not (Test-Path $dataParent); Data = -not (Test-Path $DataDir) }
$serviceCreated = $false
$bstr = [IntPtr]::Zero
try {
    Write-Host "Installing the Edge '$EdgeId' into $InstallDir"

    # The IndustrialIot parents first (Users may only read), so no ordinary user can create or swap anything below.
    $users = New-Object Security.Principal.SecurityIdentifier('S-1-5-32-545')
    $adminOnly = @(@($System, 'FullControl'), @($Administrators, 'FullControl'), @($users, 'ReadAndExecute'))
    New-PrivateFolder -Path $programParent -Grants $adminOnly -FolderOnly
    New-PrivateFolder -Path $dataParent -Grants $adminOnly -FolderOnly
    # Until the service exists, only SYSTEM and Administrators; the service's own entry is added once it can be resolved.
    New-PrivateFolder -Path $InstallDir -Grants @(@($System, 'FullControl'), @($Administrators, 'FullControl'))
    New-PrivateFolder -Path $DataDir -Grants @(@($System, 'FullControl'), @($Administrators, 'FullControl'))

    Expand-Archive -LiteralPath $PackagePath -DestinationPath $InstallDir
    $exe = Join-Path $InstallDir 'IndustrialIot.Edge.exe'
    if (-not (Test-Path -LiteralPath $exe)) { throw "the package does not contain IndustrialIot.Edge.exe." }

    # A fresh file (never an overwrite) so it carries only the folder's inherited permissions.
    $caPath = Join-Path $DataDir 'ca.pem'
    if (Test-Path -LiteralPath $caPath) { [IO.File]::Delete($caPath) }
    Copy-Item -LiteralPath $CaCertificate -Destination $caPath
    $bufferPath = Join-Path $DataDir 'edge.db'

    # The service first (stopped): its virtual account only exists once the service does.
    New-Service -Name $ServiceName -BinaryPathName "`"$exe`"" -DisplayName 'IIoT Edge agent' `
        -Description 'Reads and writes the S7-1200 PLC and exchanges data with the Cloud over MQTT/TLS.' -StartupType Manual | Out-Null
    $serviceCreated = $true
    Invoke-Sc config $ServiceName obj= $ServiceAccount

    $service = New-Object Security.Principal.NTAccount($ServiceAccount)
    Set-PrivateFolder -Path $InstallDir -Grants @(@($System, 'FullControl'), @($Administrators, 'FullControl'), @($service, 'ReadAndExecute'))
    Set-PrivateFolder -Path $DataDir -Grants @(@($System, 'FullControl'), @($Administrators, 'FullControl'), @($service, 'Modify'))
    Set-PrivateServiceKey

    # Written through the .NET registry API, not a cmdlet: cmdlet parameters can be recorded by PowerShell logging.
    # Absolute paths: a service's working directory is System32.
    $bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($password)
    $key = [Microsoft.Win32.Registry]::LocalMachine.OpenSubKey("SYSTEM\CurrentControlSet\Services\$ServiceName", $true)
    try {
        $key.SetValue('Environment', [string[]]@(
                "Edge__EdgeId=$EdgeId",
                "Edge__Mqtt__Host=$BrokerHost",
                "Edge__Mqtt__Port=$BrokerPort",
                "Edge__Mqtt__Username=$EdgeId",
                ('Edge__Mqtt__Password=' + [Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr)),
                "Edge__Mqtt__CaCertificatePath=$caPath",
                "Edge__Buffer__Path=$bufferPath",
                "Edge__Plc__Host=$PlcHost"), [Microsoft.Win32.RegistryValueKind]::MultiString)
    }
    finally { $key.Close() }

    # The service logs to the Application event log under its own name; only an administrator can create
    # that event source, the service account cannot.
    if (-not [Diagnostics.EventLog]::SourceExists($ServiceName)) {
        New-EventLog -LogName Application -Source $ServiceName
    }

    Invoke-Sc failure $ServiceName reset= 86400 actions= restart/5000/restart/5000/restart/5000
    Set-Service -Name $ServiceName -StartupType Automatic
    Start-Service -Name $ServiceName

    Write-Host "Installed and started $ServiceName (account $ServiceAccount)."
    Write-Host "Tags: edit $InstallDir\appsettings.json (Edge:Tags; start read-only), then Restart-Service $ServiceName."
}
catch {
    Write-Host "install-edge: $($_.Exception.Message)" -ForegroundColor Red
    Write-Host 'Undoing the partial installation...'
    if ($serviceCreated) {
        Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
        & sc.exe delete $ServiceName | Out-Null
    }
    if (Test-Path -LiteralPath $InstallDir) { try { Remove-FolderNoFollow $InstallDir } catch { } }
    if ($created.Data -and (Test-Path -LiteralPath $DataDir)) { try { Remove-FolderNoFollow $DataDir } catch { } }
    foreach ($parent in @(@($created.ProgramParent, $programParent), @($created.DataParent, $dataParent))) {
        if ($parent[0] -and (Test-Path -LiteralPath $parent[1]) -and -not (Get-ChildItem -LiteralPath $parent[1] -Force)) {
            Remove-Item -LiteralPath $parent[1] -Force -ErrorAction SilentlyContinue
        }
    }
    exit 1
}
finally {
    if ($bstr -ne [IntPtr]::Zero) { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr) }
}
