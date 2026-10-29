#Requires -Version 5.1
<#
.SYNOPSIS
  Verifies the Edge packaging and Windows Service scripts (task-03).

.DESCRIPTION
  Default (no elevation, nothing installed): VERIFY-BUILD, VERIFY-STATIC, VERIFY-PACKAGE, VERIFY-FAILFAST,
  VERIFY-PARSE, VERIFY-SECRETS.
  -Live (elevated PowerShell, on a test machine without an installed Edge): installs the package as the real
  service into separate EdgeVerify folders with a throw-away Edge id and unreachable broker/PLC, checks
  LIVE-SERVICE, LIVE-ACL, LIVE-REGACL, LIVE-RUNNING, LIVE-UNINSTALL, and always removes everything again.
  Prints PASS/FAIL per check; exit 0 only when all pass.
#>
[CmdletBinding()]
param([switch] $Live)

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$ServiceName = 'IndustrialIot.Edge'
$script:failures = 0
$work = Join-Path ([IO.Path]::GetTempPath()) ('iiot-verify-edge-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $work | Out-Null

function Report([string] $Id, [bool] $Ok, [string] $What) {
    if ($Ok) { Write-Host "PASS $Id $What" } else { Write-Host "FAIL $Id $What"; $script:failures++ }
}

try {
    # --- VERIFY-BUILD ---
    & dotnet build (Join-Path $repo 'IndustrialIot.sln') -nologo -v quiet | Out-Null
    Report 'VERIFY-BUILD' ($LASTEXITCODE -eq 0) 'the solution builds'

    # --- VERIFY-STATIC: AddWindowsService only acts under the SCM, so a test cannot observe it; check the source. ---
    # Comment lines are dropped first: a commented-out call must not count.
    $program = (Get-Content (Join-Path $repo 'src\Edge\Program.cs') | Where-Object { $_ -notmatch '^\s*//' }) -join "`n"
    $installTokens = $null; $parseErrors = $null
    $installAst = [Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot 'install-edge.ps1'), [ref] $installTokens, [ref] $parseErrors)
    # The text of the command's first element: GetCommandName() is null for a quoted path such as "$env:ProgramFiles\dotnet\dotnet.exe".
    $callsDotnet = $installAst.FindAll({ param($n) $n -is [Management.Automation.Language.CommandAst] -and
            $n.CommandElements[0].Extent.Text.Trim('"', "'") -match '(^|[\\/])dotnet(\.exe)?$' }, $true).Count -gt 0
    Report 'VERIFY-STATIC' (($program -match 'AddWindowsService\(') -and -not $callsDotnet) 'Program.cs calls AddWindowsService; install-edge.ps1 never runs dotnet'

    # --- VERIFY-PACKAGE ---
    $zip = Join-Path $work 'iiot-edge-win-x64.zip'
    & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'package-edge.ps1') -Output $zip | Out-Null
    $expanded = Join-Path $work 'package'
    $packaged = (Test-Path $zip) -and $LASTEXITCODE -eq 0
    if ($packaged) {
        Expand-Archive -LiteralPath $zip -DestinationPath $expanded
        foreach ($name in 'IndustrialIot.Edge.exe', 'appsettings.json', 'install-edge.ps1', 'uninstall-edge.ps1', 'hostfxr.dll') {
            if (-not (Test-Path (Join-Path $expanded $name))) { $packaged = $false; Write-Host "  missing in package: $name" }
        }
        # Files the dev machine wrote under src\Edge\data (settings, password hash, buffer) must not ship.
        if (Test-Path (Join-Path $expanded 'data')) { $packaged = $false; Write-Host '  the package contains a data folder' }
    }
    Report 'VERIFY-PACKAGE' $packaged 'self-contained package with exe, runtime, appsettings.json and the scripts, no data folder'

    # --- VERIFY-FAILFAST: the packaged exe in console mode stops on invalid configuration, naming the key. ---
    $failFast = $false
    if ($packaged) {
        $saved = $env:Edge__EdgeId
        try {
            $env:Edge__EdgeId = 'not valid'
            $out = Join-Path $work 'failfast.txt'
            $process = Start-Process -FilePath (Join-Path $expanded 'IndustrialIot.Edge.exe') -WorkingDirectory $expanded -PassThru -NoNewWindow `
                -RedirectStandardOutput $out -RedirectStandardError "$out.err"
            if (-not $process.WaitForExit(60000)) { $process.Kill(); Write-Host '  the exe kept running with an invalid configuration' }
            else {
                $text = (Get-Content -Raw $out) + (Get-Content -Raw "$out.err")
                $failFast = $process.ExitCode -ne 0 -and $text -match 'Edge:EdgeId'
                if (-not $failFast) { Write-Host "  exit code $($process.ExitCode); key not named" }
            }
        }
        finally { $env:Edge__EdgeId = $saved }
    }
    Report 'VERIFY-FAILFAST' $failFast 'packaged exe exits non-zero naming Edge:EdgeId for an invalid id'

    # --- VERIFY-PARSE ---
    $parseOk = $true
    foreach ($script in 'package-edge.ps1', 'install-edge.ps1', 'uninstall-edge.ps1', 'verify-edge.ps1') {
        $tokens = $null; $errors = $null
        [Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot $script), [ref] $tokens, [ref] $errors) | Out-Null
        if ($errors.Count -gt 0) { $parseOk = $false; Write-Host "  $script : $($errors.Count) parse error(s)" }
    }
    Report 'VERIFY-PARSE' $parseOk 'all four scripts parse with PowerShell 5.1'

    # --- VERIFY-SECRETS: the password is never a parameter and never reaches an output. ---
    $leaks = @()
    $secretName = 'password|bstr|secure'
    $isSecret = { param($n) $n -is [Management.Automation.Language.VariableExpressionAst] -and $n.VariablePath.UserPath -match $secretName }
    foreach ($script in 'install-edge.ps1', 'uninstall-edge.ps1', 'package-edge.ps1') {
        $tokens = $null; $errors = $null
        $ast = [Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot $script), [ref] $tokens, [ref] $errors)
        $outputCommand = '(^|[\\/])(Write-\w+|Out-\w+|Tee-Object|Add-Content|Set-Content|echo|Export-\w+)$'
        foreach ($pipeline in $ast.FindAll({ param($n) $n -is [Management.Automation.Language.PipelineAst] }, $true)) {
            $elements = $pipeline.PipelineElements
            $hasSecret = $pipeline.FindAll($isSecret, $true).Count -gt 0
            $toOutput = @($elements | Where-Object { $_ -is [Management.Automation.Language.CommandAst] -and $_.GetCommandName() -match $outputCommand }).Count -gt 0
            # A statement that is only a secret variable (or a string built from one) writes it to the output stream.
            # Method calls such as $key.SetValue(...) return nothing and are not output.
            $bareExpression = $elements.Count -eq 1 -and $elements[0] -is [Management.Automation.Language.CommandExpressionAst] -and
                ($pipeline.Parent -is [Management.Automation.Language.NamedBlockAst] -or $pipeline.Parent -is [Management.Automation.Language.StatementBlockAst]) -and
                ($elements[0].Expression -is [Management.Automation.Language.VariableExpressionAst] -or
                 $elements[0].Expression -is [Management.Automation.Language.ExpandableStringExpressionAst] -or
                 $elements[0].Expression -is [Management.Automation.Language.BinaryExpressionAst])
            if ($hasSecret -and ($toOutput -or $bareExpression)) { $leaks += "$script line $($pipeline.Extent.StartLineNumber): output of a secret" }
        }
        foreach ($call in $ast.FindAll({ param($n) $n -is [Management.Automation.Language.InvokeMemberExpressionAst] -and
                    $n.Expression.Extent.Text -match 'Console' }, $true)) {
            if ($call.FindAll($isSecret, $true).Count -gt 0) { $leaks += "$script line $($call.Extent.StartLineNumber): Console write of a secret" }
        }
        if ($ast.FindAll({ param($n) $n -is [Management.Automation.Language.ParameterAst] -and $n.Name.VariablePath.UserPath -match 'password' }, $true).Count -gt 0) {
            $leaks += "${script}: a password parameter (it must be asked for interactively)"
        }
    }
    $leaks | ForEach-Object { Write-Host "  $_" }
    Report 'VERIFY-SECRETS' ($leaks.Count -eq 0) 'no password parameter; no output of a password'

    if ($Live) {
        $principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
        $installDir = Join-Path $env:ProgramFiles 'IndustrialIot\EdgeVerify'
        $dataDir = Join-Path $env:ProgramData 'IndustrialIot\EdgeVerify'
        if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
            Report 'LIVE-SERVICE' $false 'needs an elevated PowerShell'
        }
        elseif ((Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) -or (Test-Path $installDir) -or (Test-Path $dataDir)) {
            Report 'LIVE-SERVICE' $false "refusing: $ServiceName or the EdgeVerify folders already exist (never touches a real installation)"
        }
        elseif (-not $packaged) {
            Report 'LIVE-SERVICE' $false 'no package to install'
        }
        else {
            try {
                # A throw-away CA certificate and a random password that exists only as a SecureString.
                $rsa = [Security.Cryptography.RSA]::Create(2048)
                $request = New-Object Security.Cryptography.X509Certificates.CertificateRequest('CN=verify-edge throw-away CA', $rsa,
                    [Security.Cryptography.HashAlgorithmName]::SHA256, [Security.Cryptography.RSASignaturePadding]::Pkcs1)
                $certificate = $request.CreateSelfSigned([DateTimeOffset]::UtcNow.AddDays(-1), [DateTimeOffset]::UtcNow.AddDays(1))
                $caFile = Join-Path $work 'ca.pem'
                Set-Content -Path $caFile -Encoding ascii -Value ("-----BEGIN CERTIFICATE-----`r`n" +
                    [Convert]::ToBase64String($certificate.RawData, 'InsertLineBreaks') + "`r`n-----END CERTIFICATE-----")
                Write-Host 'When asked for the MQTT password, type anything: the broker of this test install is unreachable.'
                & (Join-Path $PSScriptRoot 'install-edge.ps1') -PackagePath $zip -EdgeId 'verify-edge-live' -BrokerHost '127.0.0.1' -BrokerPort 1 `
                    -CaCertificate $caFile -PlcHost '127.0.0.1' -InstallDir $installDir -DataDir $dataDir
                $installed = [bool](Get-Service -Name $ServiceName -ErrorAction SilentlyContinue)
                $keyPath = "HKLM:\SYSTEM\CurrentControlSet\Services\$ServiceName"

                # LIVE-SERVICE: failure actions read from the registry (sc.exe's text is localized).
                $service = Get-CimInstance Win32_Service -Filter "Name='$ServiceName'"
                $restarts = 0
                $actions = if ($installed) { (Get-Item $keyPath).GetValue('FailureActions') } else { $null }
                if ($actions) {
                    $count = [BitConverter]::ToInt32($actions, 12)
                    for ($i = 0; $i -lt $count; $i++) {
                        if ([BitConverter]::ToInt32($actions, 20 + 8 * $i) -eq 1 -and [BitConverter]::ToInt32($actions, 24 + 8 * $i) -eq 5000) { $restarts++ }
                    }
                }
                Report 'LIVE-SERVICE' ($installed -and $service.StartMode -eq 'Auto' -and $service.StartName -eq "NT SERVICE\$ServiceName" -and $restarts -eq 3) `
                    'automatic, virtual account, restart x3 after 5 s on failure'

                $serviceSid = if ($installed) { (New-Object Security.Principal.NTAccount("NT SERVICE\$ServiceName")).Translate([Security.Principal.SecurityIdentifier]).Value } else { 'none' }
                $sid = { param($rule) $rule.IdentityReference.Translate([Security.Principal.SecurityIdentifier]).Value }
                # Exactly these principals with exactly these rights; inheritance off at the top; owner Administrators.
                function Test-Acl($Acl, [hashtable] $Expected, [string] $RightsProperty, [bool] $Top) {
                    $rules = @($Acl.Access)
                    $bySid = @{}
                    foreach ($rule in $rules) { $bySid[(& $sid $rule)] = $rule }
                    if (Compare-Object @($bySid.Keys | Sort-Object) @($Expected.Keys | Sort-Object)) { return $false }
                    foreach ($entry in $Expected.GetEnumerator()) {
                        $rights = [int]$bySid[$entry.Key].$RightsProperty -band -bnot 0x100000    # ignore SYNCHRONIZE
                        if ($rights -ne ([int]$entry.Value -band -bnot 0x100000)) { return $false }
                    }
                    $owner = $Acl.GetOwner([Security.Principal.SecurityIdentifier]).Value
                    return ($owner -eq 'S-1-5-32-544') -and (-not $Top -or $Acl.AreAccessRulesProtected)
                }
                $fs = [Security.AccessControl.FileSystemRights]
                $programRights = @{ 'S-1-5-18' = $fs::FullControl; 'S-1-5-32-544' = $fs::FullControl; $serviceSid = $fs::ReadAndExecute }
                $dataRights = @{ 'S-1-5-18' = $fs::FullControl; 'S-1-5-32-544' = $fs::FullControl; $serviceSid = $fs::Modify }

                # LIVE-ACL: folders and the files inside them.
                $aclOk = $installed -and (Test-Acl (Get-Acl $installDir) $programRights 'FileSystemRights' $true) -and
                    (Test-Acl (Get-Acl $dataDir) $dataRights 'FileSystemRights' $true)
                foreach ($file in (Join-Path $installDir 'IndustrialIot.Edge.exe'), (Join-Path $installDir 'appsettings.json')) {
                    $aclOk = $aclOk -and (Test-Acl (Get-Acl $file) $programRights 'FileSystemRights' $false)
                }
                $aclOk = $aclOk -and (Test-Acl (Get-Acl (Join-Path $dataDir 'ca.pem')) $dataRights 'FileSystemRights' $false)
                $inheritable = @((Get-Acl $installDir).Access + (Get-Acl $dataDir).Access |
                        Where-Object { $_.InheritanceFlags -ne 'ContainerInherit, ObjectInherit' }).Count -eq 0
                foreach ($file in (Join-Path $installDir 'IndustrialIot.Edge.exe'), (Join-Path $installDir 'appsettings.json'), (Join-Path $dataDir 'ca.pem')) {
                    $fileAcl = Get-Acl $file
                    if ($fileAcl.AreAccessRulesProtected -or @($fileAcl.Access | Where-Object { -not $_.IsInherited }).Count -gt 0) { $inheritable = $false }
                }
                Report 'LIVE-ACL' ($aclOk -and $inheritable) `
                    'program (service: read/execute) and data (service: modify) folders and files: exactly SYSTEM, Administrators, the service; owner Administrators'

                # LIVE-REGACL
                $rg = [Security.AccessControl.RegistryRights]
                $keyRights = @{ 'S-1-5-18' = $rg::FullControl; 'S-1-5-32-544' = $rg::FullControl; $serviceSid = $rg::ReadKey }
                $key = if ($installed) { Get-Item $keyPath } else { $null }
                $absolute = $false
                if ($key) {
                    $absolute = @($key.GetValue('Environment') | Where-Object { $_ -match '^Edge__(Mqtt__CaCertificatePath|Buffer__Path)=' } |
                            Where-Object { [IO.Path]::IsPathRooted(($_ -split '=', 2)[1]) }).Count -eq 2
                }
                $regAcl = if ($key) { Get-Acl $keyPath } else { $null }
                $regOk = $key -and $regAcl.AreAccessRulesProtected -and (Compare-Object @($regAcl.Access | ForEach-Object { & $sid $_ } | Sort-Object -Unique) @($keyRights.Keys | Sort-Object)) -eq $null
                if ($regOk) {
                    foreach ($rule in $regAcl.Access) { if ([int]$rule.RegistryRights -ne [int]$keyRights[(& $sid $rule)]) { $regOk = $false } }
                }
                Report 'LIVE-REGACL' ($regOk -and $key.GetValueKind('Environment') -eq 'MultiString' -and $absolute) `
                    'service key: SYSTEM/Administrators full, the service read only, nobody else; Environment is REG_MULTI_SZ with absolute paths'

                # LIVE-RUNNING: started under the virtual account and still running with broker and PLC unreachable.
                $running = $false
                for ($i = 0; $i -lt 30 -and -not $running; $i++) { Start-Sleep -Seconds 1; $running = (Get-Service $ServiceName).Status -eq 'Running' }
                Start-Sleep -Seconds 10
                $process = Get-CimInstance Win32_Process -Filter "Name='IndustrialIot.Edge.exe'" | Where-Object { $_.ExecutablePath -like "$installDir*" }
                $owner = if ($process) { Invoke-CimMethod -InputObject $process -MethodName GetOwner } else { $null }
                Report 'LIVE-RUNNING' ($running -and (Get-Service $ServiceName).Status -eq 'Running' -and $owner.Domain -eq 'NT SERVICE' -and $owner.User -eq $ServiceName) `
                    'running as NT SERVICE\IndustrialIot.Edge for 10 s with broker and PLC unreachable'

                # LIVE-UNINSTALL
                & (Join-Path $PSScriptRoot 'uninstall-edge.ps1')
                Report 'LIVE-UNINSTALL' ((-not (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue)) -and -not (Test-Path $installDir) -and
                    (Test-Path (Join-Path $dataDir 'appsettings.backup.json'))) 'service and program removed; tag list kept in the data folder'
            }
            finally {
                if (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) {
                    & (Join-Path $PSScriptRoot 'uninstall-edge.ps1') -RemoveData | Out-Null
                }
                # rmdir /s removes a junction itself and never follows it.
                foreach ($leftover in $dataDir, $installDir) { if (Test-Path $leftover) { & cmd.exe /c rmdir /s /q "$leftover" } }
                foreach ($parent in (Split-Path $installDir -Parent), (Split-Path $dataDir -Parent)) {
                    if ((Test-Path $parent) -and -not (Get-ChildItem -LiteralPath $parent -Force)) { Remove-Item -LiteralPath $parent -Force }
                }
            }
        }
    }
}
finally {
    Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
}

if ($script:failures -eq 0) { Write-Host 'Edge verification passed.'; exit 0 }
Write-Host "Edge verification failed: $script:failures check(s)."
exit 1
