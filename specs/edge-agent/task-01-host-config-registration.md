# Task 01 — Edge host starts only with valid configuration and resolves driver + catalog

Status: done

## Outcome
`src/Edge/Program.cs` builds the Generic Host through one `AddEdgeAgent(IConfiguration)` extension that binds and validates `Edge:EdgeId`, registers the wave-1 S7 driver and the tag catalog (with the configured tags), and fails during host start on invalid configuration — before any PLC I/O. A shared test host helper lets every later task start the full agent against fakes.

## Scope
- In: `EdgeOptions` (`EdgeId`) + validator, `AddEdgeAgent`, catalog registration with startup validation, `TimeProvider.System`, sample `appsettings.json`, `Program.cs` call, `EdgeTestHost` helper.
- Out: store, poller, MQTT, executor registrations (tasks 02–05 add theirs to the same extension and their defaults to `EdgeTestHost`).

## Coverage
- CP-01

## Ownership
- Create: `src/Edge/EdgeOptions.cs`, `src/Edge/EdgeServiceCollectionExtensions.cs`, `src/Edge/appsettings.json`
- Modify: `src/Edge/Program.cs`, `src/Edge/IndustrialIot.Edge.csproj` (copy `appsettings.json` to output only if the Worker SDK does not already)
- Create: `tests/Edge.Tests/EdgeHostTests.cs`, `tests/Edge.Tests/Fakes/EdgeTestHost.cs`
- Read: `src/Edge/Drivers/{DriverServiceCollectionExtensions.cs,PlcTagCatalog.cs,SiemensS7Options.cs}`

## Contracts
**Configuration** (section `Edge`; environment variables override with `Edge__...`):
```json
{
  "Edge": {
    "EdgeId": "line1-edge01",
    "Plc":  { "Host": "192.168.0.10", "Port": 102, "Rack": 0, "Slot": 1, "TimeoutMs": 3000 },
    "Tags": [ { "TagId": "Line1.Speed", "Address": "DB1.DBW2", "DataType": "Int", "Writable": false } ]
  }
}
```
- `EdgeId`: required, same rule as contract `edgeId` (`^[A-Za-z0-9_-]{1,64}\z`, `src/Shared/Common/ContractJson.cs:185`).
- `Tags` may be empty (Edge then reads nothing and denies every write).
- **Catalog binding:** `PlcTagCatalogOptions` is bound from `configuration.GetSection("Edge")` so the array `Edge:Tags` lands in `PlcTagCatalogOptions.Tags` (`src/Edge/Drivers/PlcTagCatalog.cs:10-12`). Binding `GetSection("Edge:Tags")` would look for `Edge:Tags:Tags:*` and silently produce an empty catalog.
- `AddEdgeAgent` registers: `IOptions<EdgeOptions>` (bind + validator + `ValidateOnStart`), `AddSiemensS7Driver`, `PlcTagCatalogOptions`, `PlcTagCatalog` singleton, `TimeProvider.System` (`TryAdd`, so tests can replace it).
- Startup validation: an invalid catalog surfaces from `IHost.StartAsync` as `OptionsValidationException` — an `IValidateOptions<PlcTagCatalogOptions>` constructs the catalog and returns the `InvalidOperationException` message as the failure.
- Host start performs no PLC I/O (driver connects lazily).

**`EdgeTestHost`** (tests only): builds a host via `Host.CreateEmptyApplicationBuilder` + `AddEdgeAgent` with valid in-memory defaults for every `Edge:*` section that exists so far, a unique temp content root per instance (deleted on dispose), `Edge:Plc:Host` = `127.0.0.1` with a port where nothing listens (the real driver is registered; host start performs no PLC I/O), an optional hook to replace `IPlcDriver` (used from task-03 on, when `FakePlcDriver` exists), `TimeProvider` replaceable, and per-test overrides of any key. Later tasks extend its defaults so every existing host test keeps starting.

## Steps
1. Add `EdgeOptions` + validator and `AddEdgeAgent`; register driver, catalog, `TimeProvider`.
2. Replace `Program.cs` body with `builder.Services.AddEdgeAgent(builder.Configuration)`.
3. Add `appsettings.json` with the sample above (no secrets).
4. Add `EdgeTestHost`, then `EdgeHostTests` using it.

## Acceptance
- AC-01 (part): valid config → `StartAsync` succeeds, `IPlcDriver`, `PlcTagCatalog`, `TimeProvider` resolve, and `catalog.TryGet("Line1.Speed", out _)` is true; invalid `EdgeId` (empty, `a b`, 65 chars), missing `Edge:Plc:Host`, invalid catalog (writable `I0.0`) → `StartAsync` throws `OptionsValidationException` naming the key.

## Dependencies
- none

## Verification Plan
- Command: `dotnet test IndustrialIot.sln`
- Named probe: `EdgeHostTests.ValidConfig_StartsAndResolvesServices`, `EdgeHostTests.InvalidConfig_FailsAtStart`
- Reachability: source — .NET 10 SDK; `Program.cs` compiles against `AddEdgeAgent`, the only registration path.
- Oracle: exit 0, 0 failed across Shared.Tests and Edge.Tests; both probes listed by `--list-tests` and passing; the invalid-catalog row asserts the exception comes from `StartAsync`.
- Counterexample: binding the catalog from `Edge:Tags`, or registering it lazily without startup validation, must fail the named probes.
- Artifacts: ephemeral temp content roots.

## Failure Protocol
On a failed Step or Verification Plan run: stop; do not widen scope, change the Command, or weaken a test; record observed versus expected; repair only the cited cause; after three failed rounds, stop and ask the user.

## Receipt

Verification: PASS
Command: dotnet test IndustrialIot.sln
Exit: 0
Base: d50cbfdf5a122f42e9cba23409b986e85969ba2e
Head: ce1a18cd5cbf89d873a57c5140b58562bceb2a78686306bc886fa6c5aa75166a
```text
$ dotnet test IndustrialIot.sln
  Determining projects to restore...
  All projects are up-to-date for restore.
  IndustrialIot.Shared -> C:\Users\HOME\source\repos\PLCProtocol\src\Shared\bin\Debug\net10.0\IndustrialIot.Shared.dll
  IndustrialIot.Edge -> C:\Users\HOME\source\repos\PLCProtocol\src\Edge\bin\Debug\net10.0\IndustrialIot.Edge.dll
  Shared.Tests -> C:\Users\HOME\source\repos\PLCProtocol\tests\Shared.Tests\bin\Debug\net10.0\Shared.Tests.dll
Test run for C:\Users\HOME\source\repos\PLCProtocol\tests\Shared.Tests\bin\Debug\net10.0\Shared.Tests.dll (.NETCoreApp,Version=v10.0)
A total of 1 test files matched the specified pattern.
  Edge.Tests -> C:\Users\HOME\source\repos\PLCProtocol\tests\Edge.Tests\bin\Debug\net10.0\Edge.Tests.dll
Test run for C:\Users\HOME\source\repos\PLCProtocol\tests\Edge.Tests\bin\Debug\net10.0\Edge.Tests.dll (.NETCoreApp,Version=v10.0)
A total of 1 test files matched the specified pattern.

Passed!  - Failed:     0, Passed:    47, Skipped:     0, Total:    47, Duration: 438 ms - Shared.Tests.dll (net10.0)

Passed!  - Failed:     0, Passed:   228, Skipped:     0, Total:   228, Duration: 14 s - Edge.Tests.dll (net10.0)
```
- Pre-change run: exit 0 with 0 `EdgeHostTests` listed → oracle (two named probes) not met, as expected.
- Named probes: `ValidConfig_StartsAndResolvesServices`, `InvalidConfig_FailsAtStart` (6 rows) present and passing.
- Counterexamples (mutations in a scratch copy): binding the catalog from `Edge:Tags` → `ValidConfig_StartsAndResolvesServices` + the Tags row fail; removing the catalog startup validator → the Tags row fails.
- No csproj change: the Worker SDK copies `appsettings.json` to output (verified in `bin`).
- Repair rounds: 0. Review: code-auditor PASS (0 Critical/High/Medium).
- Known limitations: binder conversion errors (e.g. `DataType: "Integer"`) surface from `StartAsync` as `InvalidOperationException`, and several invalid sections as `AggregateException` — still fail-fast and name the key. `EdgeTestHost` Lows (case-sensitive overrides, temp cleanup incl. SQLite pools, closed-port reuse) are handled in task-02/task-03, which own that file next.
