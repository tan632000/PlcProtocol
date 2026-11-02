# Task 01 — The Edge reads a v2 multi-device configuration and still starts from v1 with the same TagIds

Status: done

## Outcome
`Edge:Channels` (plan D-01) binds and validates; a v1 configuration (appsettings, environment or a schema-1 settings
file) is turned into one Channel / one Device in memory (plan D-02) and publishes exactly the old TagIds; the settings
file accepts schema 1 or 2 and is never written at start; the v1 settings page never rewrites a schema-2 file.

## Scope
- In:
  - Options model for D-01 (`EdgeChannelsOptions` → channels → devices → tags) and its validator; every error names the
    full key (e.g. `Edge:Channels:1:Devices:0:Model`); duplicate device names and TagIds compared ignoring case;
    duplicate `Host`:`Port` rejected.
  - A resolver that yields the effective device list: v2 as bound, or v1 migrated per D-02 (all `Edge:Plc` keys copied,
    `CpuType` kept for the S7 driver, `TagId` → `Name`).
  - Published TagId: `LegacyTagIds` → `Name`; otherwise `<Device>.<Name>`; uniqueness and ≤ 64 checked on the published id.
  - `EdgeSettingsFile`: `SchemaVersion` 1 (owns `Edge:Plc`/`Edge:Poll`/`Edge:Tags`) or 2 (owns `Edge:Channels` and
    removes the v1 keys from the lower layers); the other version's keys inside a file → error naming the key; message
    "SchemaVersion must be 1 or 2." Without a schema-2 file, v1 keys and `Edge:Channels` together → error naming both.
  - `SettingsService`: on a schema-2 file every Save is refused with "This Edge uses the multi-device configuration;
    edit the file or use the new settings page." and the file's bytes stay unchanged; schema-1 behaviour is unchanged.
- Out: model defaults (task-04; this task only checks `Model` is one of the three names and matches the driver),
  the runtime that uses the list (task-02).

## Coverage
- CP-01

## Ownership
- Create: `src/Edge/Configuration/EdgeChannelsOptions.cs`, `src/Edge/Configuration/EdgeDeviceResolver.cs`
- Modify: `src/Edge/Settings/EdgeSettingsFile.cs`, `src/Edge/Settings/SettingsService.cs` (the schema-2 refusal only)
- Create: `tests/Edge.Tests/EdgeChannelsOptionsTests.cs`, `tests/Edge.Tests/EdgeDeviceResolverTests.cs`,
  `tests/Edge.Tests/Fixtures/lab-v1.settings.json` (a committed copy of the lab v1 file, 22 tags), and the fixture's
  copy-to-output entry in `tests/Edge.Tests/Edge.Tests.csproj`
- Modify: `tests/Edge.Tests/EdgeSettingsFileTests.cs` (the schema message changes to "1 or 2"), `tests/Edge.Tests/SettingsApiTests.cs` (schema-2 refusal)
- Read: `src/Edge/Drivers/PlcProtocolOptions.cs`, `src/Edge/Drivers/SiemensS7Options.cs`, `src/Edge/Drivers/Slmp/SlmpOptions.cs`,
  `src/Edge/Drivers/PlcTagCatalog.cs`, `src/Edge/appsettings.json`, `deployments/edge/install-edge.ps1`

## Steps
1. Tests first: v2 with two channels binds; each AC-02 error case names its key (including `line1.temp` vs `Line1.Temp`
   and two devices on one `Host`:`Port`); v1 appsettings → `Plc1`, legacy ids, Host/Port/Rack/Slot/TimeoutMs, interval
   and writes flag carried; v1 with `CpuType=S71500` keeps that CPU type for the driver and logs the warning; the
   committed fixture → the same 22 TagIds; a schema-2 file plus `Edge:Plc:*` from appsettings and environment
   (`Edge__Plc__Host`) composes with the v1 keys removed and resolves the v2 devices (compose/resolver level; the host
   start is proven in task-02); v1 and v2 keys without a file fail; schema-2 file with v1 keys and
   schema-1 file with `Edge:Channels` both rejected; the file's bytes are unchanged after start; a Save on a schema-2 file
   is refused and the bytes are unchanged.
2. Implement the options, validator and resolver; extend `EdgeSettingsFile`; add the refusal to `SettingsService`.

## Acceptance
- AC-01: the fixture and a v1 appsettings resolve to the old TagIds; file bytes unchanged.
- AC-02: every listed invalid case fails naming the key; the installed-Edge case resolves.

## Dependencies
- none

## Verification Plan
- Command: `dotnet build IndustrialIot.sln -c Release && dotnet test tests/Edge.Tests && dotnet test tests/Deployment.Tests`
- Named probe: `EdgeChannelsOptionsTests`, `EdgeDeviceResolverTests`, `EdgeSettingsFileTests`, `SettingsApiTests`.
- Reachability: source — options binding and the settings file as the host composes them; Deployment tests start the host.
- Oracle: build exit 0; `Failed: 0`, `Skipped: 0` in both suites; the fixture case lists the 22 ids.
- Counterexample: a migration that prefixes `Plc1.` changes the lab ids; a silent merge of v1 and v2 keys starts instead
  of failing; a schema-2 file that leaves `Edge:Plc` from appsettings fails the installed-Edge case; a start or a v1-page
  Save that rewrites a schema-2 file changes its bytes.
- Artifacts: ephemeral.

## Failure Protocol
On a failed Step or Verification Plan run: stop; do not widen scope, change the Command, or weaken a test; record observed versus expected; repair only the cited cause; after three failed rounds, stop and ask the user.

## Receipt

Verification: PASS
Command: dotnet build IndustrialIot.sln -c Release && dotnet test tests/Edge.Tests && dotnet test tests/Deployment.Tests
Exit: 0
Base: d8d498a1bdb6249b1b7d46c12d0092edd9cc3f65
Head: 40870ccc62b19234f63d7afd5b577bbe09e3711e6a75efbf231829c671123088
Changed existing assertions: `EdgeSettingsFileTests` version rows (the message is now "SchemaVersion must be 1 or 2."; a schema-2 file with `Edge:Tags` now names that key). No other existing assertion changed.
Review: fresh-context code review, PASS after two repair rounds (schema-2 file with no channel value is rejected; environment `Edge:Channels` cannot merge into the file; the v1+v2 message names the keys present).
Notes for task-02: the v1 settings page `Validate` still runs on a schema-2 file (writes nothing); `Edge:Channels: []` in appsettings without v1 keys resolves to zero devices.
```text
$ dotnet build IndustrialIot.sln -c Release && dotnet test tests/Edge.Tests && dotnet test tests/Deployment.Tests
  Determining projects to restore...
  All projects are up-to-date for restore.
  IndustrialIot.Shared -> C:\Users\case 046\Documents\Project\PlcProtocol\src\Shared\bin\Release\net10.0\IndustrialIot.Shared.dll
  Shared.Tests -> C:\Users\case 046\Documents\Project\PlcProtocol\tests\Shared.Tests\bin\Release\net10.0\Shared.Tests.dll
  IndustrialIot.Edge -> C:\Users\case 046\Documents\Project\PlcProtocol\src\Edge\bin\Release\net10.0\IndustrialIot.Edge.dll
  IndustrialIot.Cloud -> C:\Users\case 046\Documents\Project\PlcProtocol\src\Cloud\bin\Release\net10.0\IndustrialIot.Cloud.dll
  Edge.Tests -> C:\Users\case 046\Documents\Project\PlcProtocol\tests\Edge.Tests\bin\Release\net10.0\Edge.Tests.dll
  Deployment.Tests -> C:\Users\case 046\Documents\Project\PlcProtocol\tests\Deployment.Tests\bin\Release\net10.0\Deployment.Tests.dll
  Cloud.Tests -> C:\Users\case 046\Documents\Project\PlcProtocol\tests\Cloud.Tests\bin\Release\net10.0\Cloud.Tests.dll

Build succeeded.
    0 Warning(s)
    0 Error(s)

Time Elapsed 00:00:01.37
  Determining projects to restore...
  All projects are up-to-date for restore.
  IndustrialIot.Shared -> C:\Users\case 046\Documents\Project\PlcProtocol\src\Shared\bin\Debug\net10.0\IndustrialIot.Shared.dll
  IndustrialIot.Edge -> C:\Users\case 046\Documents\Project\PlcProtocol\src\Edge\bin\Debug\net10.0\IndustrialIot.Edge.dll
  Edge.Tests -> C:\Users\case 046\Documents\Project\PlcProtocol\tests\Edge.Tests\bin\Debug\net10.0\Edge.Tests.dll
Test run for C:\Users\case 046\Documents\Project\PlcProtocol\tests\Edge.Tests\bin\Debug\net10.0\Edge.Tests.dll (.NETCoreApp,Version=v10.0)
A total of 1 test files matched the specified pattern.

Passed!  - Failed:     0, Passed:   587, Skipped:     0, Total:   587, Duration: 28 s - Edge.Tests.dll (net10.0)
  Determining projects to restore...
  All projects are up-to-date for restore.
  IndustrialIot.Shared -> C:\Users\case 046\Documents\Project\PlcProtocol\src\Shared\bin\Debug\net10.0\IndustrialIot.Shared.dll
  IndustrialIot.Edge -> C:\Users\case 046\Documents\Project\PlcProtocol\src\Edge\bin\Debug\net10.0\IndustrialIot.Edge.dll
  IndustrialIot.Cloud -> C:\Users\case 046\Documents\Project\PlcProtocol\src\Cloud\bin\Debug\net10.0\IndustrialIot.Cloud.dll
  Deployment.Tests -> C:\Users\case 046\Documents\Project\PlcProtocol\tests\Deployment.Tests\bin\Debug\net10.0\Deployment.Tests.dll
Test run for C:\Users\case 046\Documents\Project\PlcProtocol\tests\Deployment.Tests\bin\Debug\net10.0\Deployment.Tests.dll (.NETCoreApp,Version=v10.0)
A total of 1 test files matched the specified pattern.

Passed!  - Failed:     0, Passed:    37, Skipped:     0, Total:    37, Duration: 3 m 8 s - Deployment.Tests.dll (net10.0)
```
