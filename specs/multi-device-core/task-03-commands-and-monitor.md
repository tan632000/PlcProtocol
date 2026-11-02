# Task 03 — Commands reach the owning device, and the Monitor shows every device

Status: done

## Outcome
`CommandExecutor` resolves a command's TagId to its device and writes through that device's driver, only when the global
switch, the device's `Enabled` and `WritesEnabled` and the tag's `Writable` all allow it (plan D-07); the Monitor shows a
row per device; the connection tester tests one named device; the task-02 adapter is removed and the host resolves every
consumer from the registry.

## Scope
- In:
  - Routing by the registry map; unknown TagId keeps today's rejection; device disabled → `device_disabled`; device
    writes disabled → `device_writes_disabled` (both added to `CommandReasons` and its `All` list), each with no driver call.
  - Global switch: unchanged; when `Edge:Commands:Enabled` is false the listener still does not subscribe. Tests assert:
    no subscription, the executor is not called, no frame reaches the fake.
  - Monitor `/api/status`: `devices[]` {`name`, `model`, `host`, `port`, `state`, `lastGoodUtc`, `lastError`, `readMs`,
    `requests`} and each tag carries its device; `monitor.html` renders the device table; the old top-level
    protocol/host fields remain for one-device configurations.
  - `PlcConnectionTester` takes a device name (the v1 page passes the only device).
  - `SettingsService` and the host wiring take the registry instead of a single driver/catalog; schema-1 behaviour is unchanged.
- Out: SLMP/MC 1E write frames (task-08/09; until then those drivers keep refusing before I/O).

## Coverage
- CP-02, CP-03

## Ownership
- Modify: `src/Edge/Commands/CommandExecutor.cs`, `src/Shared/Common/CommandReasons.cs`, `src/Edge/Monitoring/MonitorServer.cs`,
  `src/Edge/Monitoring/monitor.html`, `src/Edge/Settings/PlcConnectionTester.cs`, `src/Edge/Settings/SettingsService.cs`,
  `src/Edge/EdgeServiceCollectionExtensions.cs`, `src/Edge/Monitoring/LiveTagSnapshot.cs` (each device's last good cycle time
  only; added by the user on 2026-10-30 because the snapshot keeps only the latest cycle)
- Modify: `tests/Edge.Tests/CommandExecutorTests.cs`, `tests/Edge.Tests/CommandsSwitchTests.cs`, `tests/Edge.Tests/MonitorTests.cs`,
  `tests/Edge.Tests/PlcConnectionTesterTests.cs`, `tests/Edge.Tests/SettingsApiTests.cs`, `tests/Shared.Tests/CommandReasonsTests.cs`
  (the exact reason set gains the two codes)
- Modify (tests that resolve `IPlcDriver`/`PlcTagCatalog` from the host; only that resolution changes, to go through
  `PlcDeviceRegistry`, and the receipt lists each changed line): `tests/Edge.Tests/EdgeHostTests.cs`,
  `tests/Edge.Tests/EdgeSettingsFileTests.cs`, `tests/Edge.Tests/EdgeSupervisorTests.cs`, `tests/Edge.Tests/PlcProtocolSwitchTests.cs`,
  `tests/Edge.Tests/SlmpDriverTests.cs`, `tests/Edge.Tests/TagPollerTests.cs`, `tests/Edge.Tests/LabSettingsTests.cs`
- Read: `src/Edge/Drivers/PlcDeviceRegistry.cs`, `src/Edge/Commands/CommandListener.cs`, `src/Cloud/Commands/CommandResultIngestor.cs` (reason passthrough)

## Steps
1. Tests first: two S7 fake devices; a command on device B's tag writes only to B; B with `WritesEnabled=false` →
   `device_writes_disabled`, B with `Enabled=false` → `device_disabled`, zero driver calls; global switch off → no
   subscription, executor not called, zero frames; tag not writable keeps today's reason; Monitor JSON lists both devices
   with `readMs` and `requests`; the page renders two rows; the tester tests the named device only; the settings API still
   works on a v1 file.
2. Implement; remove the adapter from task-02.

## Acceptance
- AC-04: Monitor JSON and page show both devices with the listed fields.
- AC-05: routing and the switch rules hold, with zero driver calls on every refusal.

## Dependencies
- task-02-device-runtime.md

## Verification Plan
- Command: `dotnet build IndustrialIot.sln -c Release && dotnet test tests/Edge.Tests && dotnet test tests/Shared.Tests && dotnet test tests/Cloud.Tests && dotnet test tests/Deployment.Tests`
- Named probe: `CommandExecutorTests` (routing, switches), `CommandsSwitchTests` (no subscription), `MonitorTests` (devices),
  `PlcConnectionTesterTests`, `SettingsApiTests`, `CommandReasonsTests`; Cloud tests prove the new reasons pass the contract.
- Reachability: source — real executor, listener and Monitor in the host with fake drivers.
- Oracle: build exit 0; `Failed: 0`, `Skipped: 0` in all four suites.
- Counterexample: writing through the first device ignores ownership; checking only the global switch lets a disabled
  device write; a listener that subscribes while off; a Monitor that lists only one device fails the two-row assertion.
- Artifacts: ephemeral.

## Failure Protocol
On a failed Step or Verification Plan run: stop; do not widen scope, change the Command, or weaken a test; record observed versus expected; repair only the cited cause; after three failed rounds, stop and ask the user.

## Receipt

Verification: PASS
Command: dotnet build IndustrialIot.sln -c Release && dotnet test tests/Edge.Tests && dotnet test tests/Shared.Tests && dotnet test tests/Cloud.Tests && dotnet test tests/Deployment.Tests
Exit: 0
Base: d8d498a1bdb6249b1b7d46c12d0092edd9cc3f65
Head: fd32bcdceac5ed4a8fa9f8298c92d1d47352215dee80aae1dffe518539e8da32
Changed existing assertions: the resolution of the driver and catalog in `EdgeHostTests`, `EdgeSettingsFileTests`, `EdgeSupervisorTests`, `PlcProtocolSwitchTests`, `SlmpDriverTests`, `TagPollerTests`, `LabSettingsTests` and `MonitorTests` now goes through `PlcDeviceRegistry` (`Devices[0].Driver` / `Devices[0].Catalog`; `EdgeSupervisorTests` expects the disposed provider to refuse `PlcDeviceRegistry`); `CommandReasonsTests` gains `device_writes_disabled` and `device_disabled`.
Edits beyond the listed Ownership, required by Step 2 "remove the adapter from task-02": `src/Edge/Drivers/PlcDeviceRegistry.cs` (adapter removed; the registry is built in its constructor, because without the adapter the build moved to the poller's background thread and drivers could be created after disposal); `tests/Edge.Tests/DeviceRuntimeTests.cs` (the adapter test now asserts no single-driver service). `src/Edge/Monitoring/LiveTagSnapshot.cs` was added to Ownership by the user.
Interpretation: without a `device` name the connection tester keeps the single-PLC page behaviour (it tests the proposed settings, through the running driver when they are the running PLC); with a name it tests that PLC.
Review: fresh-context code review, PASS; three test-only follow-ups (single-PLC host has no driver service, order of the device switches, a stale comment) re-reviewed PASS.
Limitations: the monitor page script is checked by content, not executed in a browser; with several PLCs all waiting or disabled the summary badge reads "Đọc tốt"; `lastError` is the first tag error of the last cycle and `lastGoodUtc` counts a reachable PLC even when every tag failed; a schema-2 Edge with one disabled device shows `plc.state` "waiting"; a named disabled device is tested with a temporary connection; `GET /api/settings` on a multi-device Edge shows empty single-PLC defaults (saving is refused).
```text
$ dotnet build IndustrialIot.sln -c Release && dotnet test tests/Edge.Tests && dotnet test tests/Shared.Tests && dotnet test tests/Cloud.Tests && dotnet test tests/Deployment.Tests
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

Time Elapsed 00:00:01.66
  Determining projects to restore...
  All projects are up-to-date for restore.
  IndustrialIot.Shared -> C:\Users\case 046\Documents\Project\PlcProtocol\src\Shared\bin\Debug\net10.0\IndustrialIot.Shared.dll
  IndustrialIot.Edge -> C:\Users\case 046\Documents\Project\PlcProtocol\src\Edge\bin\Debug\net10.0\IndustrialIot.Edge.dll
  Edge.Tests -> C:\Users\case 046\Documents\Project\PlcProtocol\tests\Edge.Tests\bin\Debug\net10.0\Edge.Tests.dll
Test run for C:\Users\case 046\Documents\Project\PlcProtocol\tests\Edge.Tests\bin\Debug\net10.0\Edge.Tests.dll (.NETCoreApp,Version=v10.0)
A total of 1 test files matched the specified pattern.

Passed!  - Failed:     0, Passed:   612, Skipped:     0, Total:   612, Duration: 28 s - Edge.Tests.dll (net10.0)
  Determining projects to restore...
  All projects are up-to-date for restore.
  IndustrialIot.Shared -> C:\Users\case 046\Documents\Project\PlcProtocol\src\Shared\bin\Debug\net10.0\IndustrialIot.Shared.dll
  Shared.Tests -> C:\Users\case 046\Documents\Project\PlcProtocol\tests\Shared.Tests\bin\Debug\net10.0\Shared.Tests.dll
Test run for C:\Users\case 046\Documents\Project\PlcProtocol\tests\Shared.Tests\bin\Debug\net10.0\Shared.Tests.dll (.NETCoreApp,Version=v10.0)
A total of 1 test files matched the specified pattern.

Passed!  - Failed:     0, Passed:    55, Skipped:     0, Total:    55, Duration: 111 ms - Shared.Tests.dll (net10.0)
  Determining projects to restore...
  All projects are up-to-date for restore.
  IndustrialIot.Shared -> C:\Users\case 046\Documents\Project\PlcProtocol\src\Shared\bin\Debug\net10.0\IndustrialIot.Shared.dll
  IndustrialIot.Cloud -> C:\Users\case 046\Documents\Project\PlcProtocol\src\Cloud\bin\Debug\net10.0\IndustrialIot.Cloud.dll
  Cloud.Tests -> C:\Users\case 046\Documents\Project\PlcProtocol\tests\Cloud.Tests\bin\Debug\net10.0\Cloud.Tests.dll
Test run for C:\Users\case 046\Documents\Project\PlcProtocol\tests\Cloud.Tests\bin\Debug\net10.0\Cloud.Tests.dll (.NETCoreApp,Version=v10.0)
A total of 1 test files matched the specified pattern.

Passed!  - Failed:     0, Passed:   143, Skipped:     0, Total:   143, Duration: 1 m 36 s - Cloud.Tests.dll (net10.0)
  Determining projects to restore...
  All projects are up-to-date for restore.
  IndustrialIot.Shared -> C:\Users\case 046\Documents\Project\PlcProtocol\src\Shared\bin\Debug\net10.0\IndustrialIot.Shared.dll
  IndustrialIot.Edge -> C:\Users\case 046\Documents\Project\PlcProtocol\src\Edge\bin\Debug\net10.0\IndustrialIot.Edge.dll
  IndustrialIot.Cloud -> C:\Users\case 046\Documents\Project\PlcProtocol\src\Cloud\bin\Debug\net10.0\IndustrialIot.Cloud.dll
  Deployment.Tests -> C:\Users\case 046\Documents\Project\PlcProtocol\tests\Deployment.Tests\bin\Debug\net10.0\Deployment.Tests.dll
Test run for C:\Users\case 046\Documents\Project\PlcProtocol\tests\Deployment.Tests\bin\Debug\net10.0\Deployment.Tests.dll (.NETCoreApp,Version=v10.0)
A total of 1 test files matched the specified pattern.

Passed!  - Failed:     0, Passed:    37, Skipped:     0, Total:    37, Duration: 3 m 18 s - Deployment.Tests.dll (net10.0)
```
