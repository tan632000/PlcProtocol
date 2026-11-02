# Task 02 — Each device has its own driver and read loop; one PLC down does not slow the others

Status: done

## Outcome
The host builds one `DeviceRuntime` per enabled device (plan D-03) and `TagPoller` reads every device in its own loop at
its own interval, with its own back-off, state, last read duration and request count; telemetry for all devices goes to
the shared buffer and to a per-device live snapshot under the published TagIds; the host starts with one or several devices.

## Scope
- In:
  - `IPlcDriverFactory` (DI singleton) creates a driver per device from its driver kind (`S7` → `SiemensS7Driver`,
    `Slmp3E` → `MitsubishiSlmpDriver`; `Mc1E` is added in task-07); tests replace the factory instead of `IPlcDriver`.
  - `PlcDeviceRegistry`: one `DeviceRuntime` per enabled device (driver, address syntax, catalog slice); the TagId →
    device map including disabled devices; disposes drivers on stop.
  - `TagPoller`: one loop per device; today's per-tag error texts, outage short-circuit and one-interval skip stay per
    device; per device it exposes `readMs`, the last cycle's request count (from drivers that report it: S7 and SLMP
    report the requests of their last batch) and the state.
  - `LiveTagSnapshot`: latest cycle kept per device; reads return every device's tags.
  - Host wiring: `IPlcDriver` and `PlcTagCatalog` are no longer DI singletons for the poller. Consumers that still take
    one driver/catalog (`CommandExecutor`, `MonitorServer`, `PlcConnectionTester`, `SettingsService`) get the D-03
    adapter: one device → that device; several → union catalog read-only, a driver that refuses every write before I/O
    with `PlcUnavailableException`, a connection tester that refuses. Task-03 removes the adapter.
  - Existing tests that put a fake `IPlcDriver` into DI switch to a fake factory; only their registration lines change.
- Out: per-device commands and Monitor (task-03), FX3U (task-07).

## Coverage
- CP-02

## Ownership
- Create: `src/Edge/Drivers/IPlcDriverFactory.cs`, `src/Edge/Drivers/PlcDeviceRegistry.cs`, `src/Edge/Drivers/DeviceRuntime.cs`
- Modify: `src/Edge/Polling/TagPoller.cs`, `src/Edge/Monitoring/LiveTagSnapshot.cs`, `src/Edge/EdgeServiceCollectionExtensions.cs`,
  `src/Edge/Drivers/DriverServiceCollectionExtensions.cs`, `src/Edge/Drivers/SiemensS7Driver.cs` and
  `src/Edge/Drivers/Slmp/MitsubishiSlmpDriver.cs` (request count only), `src/Edge/Settings/PlcConnectionTester.cs` (refusal
  with several devices), `src/Edge/Drivers/PlcTagCatalog.cs` (a constructor from per-device resolved tags, so S7 and SLMP
  tags share one union catalog)
- Create: `tests/Edge.Tests/DeviceRuntimeTests.cs`
- Modify: `tests/Edge.Tests/PlcConnectionTesterTests.cs`, `tests/Edge.Tests/PlcTagCatalogTests.cs` (new cases only)
- Modify (fake registration only, unless named): `tests/Edge.Tests/Fakes/EdgeTestHost.cs` (multi-device helpers),
  `tests/Edge.Tests/TagPollerTests.cs`, `tests/Edge.Tests/BatchReadTests.cs`, `tests/Edge.Tests/CommandsSwitchTests.cs`,
  `tests/Edge.Tests/EdgeHostTests.cs`, `tests/Edge.Tests/EdgeSupervisorTests.cs`, `tests/Edge.Tests/PlcProtocolSwitchTests.cs`,
  `tests/Edge.Tests/SiemensS7DriverTests.cs`, `tests/Edge.Tests/MonitorTests.cs`, `tests/Edge.Tests/CommandExecutorTests.cs`,
  `tests/Edge.Tests/CommandFlowTests.cs`, `tests/Edge.Tests/SlmpDriverTests.cs`, `tests/Edge.Tests/LabSettingsTests.cs`,
  `tests/Deployment.Tests/EndToEndTelemetryTests.cs`
- Read: `src/Edge/Commands/CommandListener.cs`, `src/Edge/Settings/SettingsService.cs`, `src/Edge/Monitoring/MonitorServer.cs`

## Steps
1. Tests first: two devices with fake drivers at 200 ms and 1000 ms intervals read at their own rates; device A blocked
   for 3 s does not delay device B's cycles by more than one interval; two devices publishing alternately both stay in
   the snapshot with their own `readMs`; a disabled device has no driver and no reads; the host with two devices (one S7, one
   SLMP) starts, the adapter refuses a write with no frame and the tester refuses; the host starts from a schema-2 file
   while appsettings and environment (`Edge__Plc__Host`) still carry `Edge:Plc:*`; a v1 configuration still passes every existing
   `TagPollerTests` case; drivers are disposed on stop; the end-to-end telemetry test still passes.
2. Implement the factory, registry, per-device loops and snapshot; rewire the host; switch the listed tests to the factory.

## Acceptance
- AC-03: the isolation test passes; existing poller, host and end-to-end tests stay green with only registration changes.

## Dependencies
- task-01-schema-v2-migration.md

## Verification Plan
- Command: `dotnet build IndustrialIot.sln -c Release && dotnet test tests/Edge.Tests && dotnet test tests/Deployment.Tests`
- Named probe: `DeviceRuntimeTests` (rates, isolation, snapshot, disabled, two-device host, disposal), `TagPollerTests`, `EndToEndTelemetryTests`.
- Reachability: source — the real host with fake drivers through the factory; Deployment tests start the Edge host against a broker.
- Oracle: build exit 0; `Failed: 0`, `Skipped: 0` in both suites; isolation test reports B's max gap ≤ 1.5 × interval;
  the receipt lists any assertion changed beyond registration lines (expected: none).
- Counterexample: one shared loop makes B wait for A's 3 s timeout; one shared driver instance serialises both devices;
  a single `_latest` snapshot loses device A's tags when B publishes; a host that resolves one `IPlcDriver` fails to start
  with two devices.
- Artifacts: ephemeral.

## Failure Protocol
On a failed Step or Verification Plan run: stop; do not widen scope, change the Command, or weaken a test; record observed versus expected; repair only the cited cause; after three failed rounds, stop and ask the user.

## Receipt

Verification: PASS
Command: dotnet build IndustrialIot.sln -c Release && dotnet test tests/Edge.Tests && dotnet test tests/Deployment.Tests
Exit: 0
Base: d8d498a1bdb6249b1b7d46c12d0092edd9cc3f65
Head: 6d0af6f5311e9cae5f6d6f0140600beec5681daae61c0d497430a978040eace9
Changed existing assertions: none. Existing tests changed only their fake-driver registration line (`services.UseDriver(driver)`, or a factory in `EndToEndTelemetryTests`); new cases were added to `PlcTagCatalogTests` and `PlcConnectionTesterTests`.
Isolation probe: `DeviceRuntimeTests.ADeviceThatHangs_DoesNotDelayTheOther` asserts B's longest gap is at most 300 ms (1.5 x 200 ms) while A hangs.
Review: fresh-context code review, PASS after one repair round (the connection tester answers a multi-device Edge's only PLC through its running driver; a stopped device loop is logged; a failed batch has no request count; a failed registry build releases its drivers).
Notes for later tasks: the single-driver adapter stays until task-03; single-PLC binder type errors now surface while the host is composed (inferred, not run); a driver whose dispose throws during a failed registry build hides the first error.
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

Time Elapsed 00:00:01.45
  Determining projects to restore...
  All projects are up-to-date for restore.
  IndustrialIot.Shared -> C:\Users\case 046\Documents\Project\PlcProtocol\src\Shared\bin\Debug\net10.0\IndustrialIot.Shared.dll
  IndustrialIot.Edge -> C:\Users\case 046\Documents\Project\PlcProtocol\src\Edge\bin\Debug\net10.0\IndustrialIot.Edge.dll
  Edge.Tests -> C:\Users\case 046\Documents\Project\PlcProtocol\tests\Edge.Tests\bin\Debug\net10.0\Edge.Tests.dll
Test run for C:\Users\case 046\Documents\Project\PlcProtocol\tests\Edge.Tests\bin\Debug\net10.0\Edge.Tests.dll (.NETCoreApp,Version=v10.0)
A total of 1 test files matched the specified pattern.

Passed!  - Failed:     0, Passed:   603, Skipped:     0, Total:   603, Duration: 29 s - Edge.Tests.dll (net10.0)
  Determining projects to restore...
  All projects are up-to-date for restore.
  IndustrialIot.Shared -> C:\Users\case 046\Documents\Project\PlcProtocol\src\Shared\bin\Debug\net10.0\IndustrialIot.Shared.dll
  IndustrialIot.Edge -> C:\Users\case 046\Documents\Project\PlcProtocol\src\Edge\bin\Debug\net10.0\IndustrialIot.Edge.dll
  IndustrialIot.Cloud -> C:\Users\case 046\Documents\Project\PlcProtocol\src\Cloud\bin\Debug\net10.0\IndustrialIot.Cloud.dll
  Deployment.Tests -> C:\Users\case 046\Documents\Project\PlcProtocol\tests\Deployment.Tests\bin\Debug\net10.0\Deployment.Tests.dll
Test run for C:\Users\case 046\Documents\Project\PlcProtocol\tests\Deployment.Tests\bin\Debug\net10.0\Deployment.Tests.dll (.NETCoreApp,Version=v10.0)
A total of 1 test files matched the specified pattern.

Passed!  - Failed:     0, Passed:    37, Skipped:     0, Total:    37, Duration: 3 m 9 s - Deployment.Tests.dll (net10.0)
```
