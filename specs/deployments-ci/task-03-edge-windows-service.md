# Task 03 — The Edge is packaged on the dev machine and runs as a Windows Service

Status: blocked
Blocker: live level not run — `powershell -NoProfile -ExecutionPolicy Bypass -File deployments/edge/verify-edge.ps1 -Live` needs an elevated PowerShell; this session is not elevated. The user runs it and shares the output (default level: 6/6 PASS, exit 0; code review PASS after three repair rounds, 2026-10-20).

## Outcome
The Edge still runs from a console (`dotnet run`); `package-edge.ps1` produces a self-contained zip on the dev machine; `install-edge.ps1` installs it on a Windows machine without SDK, source or Internet as an automatically starting Windows Service that restarts on failure, with program, data and registry environment protected as D-07 states; `uninstall-edge.ps1` removes it.

## Scope
- In: Windows Service hosting, package/install/uninstall/verify scripts, the Edge section of `docs/deployment.md`.
- Out: Linux/container Edge; any change to polling, buffering, MQTT or command behaviour.

## Coverage
- CP-03

## Ownership
- Modify: `src/Edge/Program.cs` (`builder.Services.AddWindowsService(o => o.ServiceName = "IndustrialIot.Edge")`), `src/Edge/IndustrialIot.Edge.csproj` (`Microsoft.Extensions.Hosting.WindowsServices` 10.0.12), `docs/deployment.md` (Edge section)
- Create: `deployments/edge/{package-edge.ps1,install-edge.ps1,uninstall-edge.ps1,verify-edge.ps1}`
- Modify (extends the user's 2026-10-19 decision "raise the test database timeouts", recorded in task-02): `tests/Cloud.Tests/Infrastructure/PostgresFixture.cs` — `CommandTimeout=120`; the full suite this task's Oracle requires failed with "Database migration failed: Timeout during reading attempt" (Npgsql's 30 s command timeout) under load.
- Read: `src/Edge/EdgeServiceCollectionExtensions.cs`, `src/Edge/Buffering/EdgeDatabase.cs:33`, `src/Edge/Messaging/MqttOptions.cs:62`, `src/Edge/appsettings.json`

## Contracts
- `package-edge.ps1 [-Output <zip>]` (dev machine): `dotnet publish src/Edge -c Release -r win-x64 --self-contained` into a temp folder, zips it with `appsettings.json` and the install/uninstall scripts; prints the zip path.
- `install-edge.ps1 -PackagePath <zip> -EdgeId <id> -BrokerHost <host> -CaCertificate <path> -PlcHost <ip> [-InstallDir "$env:ProgramFiles\IndustrialIot\Edge"] [-DataDir "$env:ProgramData\IndustrialIot\Edge"]`: requires an elevated session (exit 1 otherwise); `InstallDir` must be under `%ProgramFiles%` (exit 1 otherwise); refuses when the service exists; reads the MQTT password with `Read-Host -AsSecureString` (never a parameter, output or log line); order: extract package → copy CA into `DataDir` → `New-Service` (manual start, stopped) → `sc.exe config IndustrialIot.Edge obj= "NT SERVICE\IndustrialIot.Edge"` → ACLs (`InstallDir`: Administrators/SYSTEM full, service read/execute, inheritance removed; `DataDir`: SYSTEM, Administrators, service full, inheritance removed) → service registry key ACL: inheritance removed, SYSTEM/Administrators full, service read, no `Users`/`ALL APPLICATION PACKAGES` → `Environment` REG_MULTI_SZ **value** on the service key with `Edge__EdgeId`, `Edge__Mqtt__Host`, `Edge__Mqtt__Username`=EdgeId, `Edge__Mqtt__Password`, `Edge__Mqtt__CaCertificatePath` (absolute), `Edge__Buffer__Path` (absolute), `Edge__Plc__Host` → failure actions restart/5000 ×3, reset 1 day → start type automatic → start; on any failure removes what it created and exits 1.
- `uninstall-edge.ps1 [-RemoveData]`: stops and deletes the service, copies `appsettings.json` (the tag list) into `DataDir` as `appsettings.backup.json`, removes `InstallDir`; deletes `DataDir` only with `-RemoveData`.
- `verify-edge.ps1 [-Live]`: default (no elevation, nothing installed) — `VERIFY-BUILD` the solution builds; `VERIFY-STATIC` `Program.cs` calls `AddWindowsService` (static check, since the call only takes effect under the SCM) and `install-edge.ps1` never invokes `dotnet`; `VERIFY-PACKAGE` `package-edge.ps1` produces a zip containing the self-contained exe; `VERIFY-FAILFAST` the packaged exe in console mode with an invalid configuration exits non-zero naming the key; `VERIFY-PARSE` all scripts parse with 0 errors; `VERIFY-SECRETS` no script writes the password variable to any output stream. `-Live` (elevated, on a Windows dev/test machine): installs the package with a throw-away Edge id and a dummy broker/PLC host, then checks `LIVE-SERVICE` (exists, automatic, account `NT SERVICE\IndustrialIot.Edge`, recovery actions), `LIVE-ACL` (install/data dir ACLs as above), `LIVE-REGACL` (the service key's ACL holds exactly SYSTEM, Administrators and the service SID — any other principal fails; `Environment` is a REG_MULTI_SZ value with absolute paths), `LIVE-RUNNING` (the process starts under the service account and keeps restarting on failure while the broker is unreachable), `LIVE-UNINSTALL` (service gone, backup kept); always uninstalls with `-RemoveData` at the end.

## Steps
1. Hosting change + package reference.
2. Scripts; runbook Edge section (packaging on the dev machine, installation, editing the tag list read-only first, firewall/IP notes).

## Acceptance
- AC-06: `verify-edge.ps1` prints six `PASS VERIFY-*` lines; `verify-edge.ps1 -Live` (elevated) prints five `PASS LIVE-*` lines.

## Dependencies
- task-02-cloud-compose-keycloak.md

## Verification Plan
- Command: `powershell -NoProfile -ExecutionPolicy Bypass -File deployments/edge/verify-edge.ps1`; live level: the same with `-Live` from an elevated PowerShell (run by the controller if the session is elevated, otherwise by the user, who shares the output — the task stays `blocked` on that cause until then).
- Named probe: `VERIFY-BUILD`, `VERIFY-STATIC`, `VERIFY-PACKAGE`, `VERIFY-FAILFAST`, `VERIFY-PARSE`, `VERIFY-SECRETS`, `LIVE-SERVICE`, `LIVE-ACL`, `LIVE-REGACL`, `LIVE-RUNNING`, `LIVE-UNINSTALL` (script output lines)
- Reachability: source — Windows, PowerShell 5.1, .NET 10 SDK; live — the same plus administrator rights.
- Oracle: exit 0 and every listed `PASS` line; `dotnet test IndustrialIot.sln` still exits 0 with `Skipped: 0`.
- Counterexample: removing `AddWindowsService` (→ `VERIFY-STATIC`), echoing the password (→ `VERIFY-SECRETS`), granting the data ACL before the service exists (→ install fails, `LIVE-SERVICE`), leaving `Users` read access on the service key (→ `LIVE-REGACL`), a relative CA path in `Environment` (→ `LIVE-REGACL`) must fail.
- Artifacts: ephemeral (temp publish folder and zip deleted; live install removed).

## Failure Protocol
On a failed Step or Verification Plan run: stop; do not widen scope, change the Command, or weaken a test; record observed versus expected; repair only the cited cause; after three failed rounds, stop and ask the user.

## Receipt
<!-- Fill only after execution; see canonical form in templates. -->
