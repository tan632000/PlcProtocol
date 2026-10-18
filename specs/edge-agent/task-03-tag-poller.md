# Task 03 — The poller turns each cycle into one stored sample per catalog tag

Status: done

## Outcome
`TagPoller` (hosted `BackgroundService`) reads every catalog tag each `Edge:Poll:IntervalMs`, converts the values to JSON, and appends the cycle to `TelemetryStore` in one batch; read failures and non-finite values become `value=null`, `quality=bad`; after a PLC outage it backs off so remote commands still get the driver.

## Scope
- In: `PollOptions` + validator, poller loop and back-off, value → `JsonElement` conversion, failure handling, catalog enumeration, registration, `FakePlcDriver` (reads **and** writes, for task-05 too), `EdgeTestHost` defaults.
- Out: publishing (task-04), per-tag intervals and deadband (excluded at GATE-SCOPE).

## Coverage
- CP-03, CP-01 (poll config validation)

## Ownership
- Create: `src/Edge/Polling/{PollOptions.cs,TagPoller.cs}`
- Modify: `src/Edge/Drivers/PlcTagCatalog.cs` (add `IReadOnlyList<ResolvedTag> Tags` in configuration order), `src/Edge/EdgeServiceCollectionExtensions.cs`, `src/Edge/appsettings.json` (add `Edge:Poll`), `tests/Edge.Tests/Fakes/EdgeTestHost.cs`
- Create: `tests/Edge.Tests/TagPollerTests.cs`, `tests/Edge.Tests/Fakes/FakePlcDriver.cs`
- Read: `src/Edge/Drivers/{IPlcDriver.cs,PlcExceptions.cs,SiemensS7Driver.cs}`, `src/Edge/Buffering/TelemetryStore.cs`, `tests/Edge.Tests/Fakes/FakeS7Server.cs`

## Contracts
- Options `Edge:Poll`: `IntervalMs` = 1000 (100..3_600_000). Invalid → `OptionsValidationException` at start.
- **Schedule** (no `PeriodicTimer`): cycle *k* is due at `start + k × interval` on the injected `TimeProvider`; after a cycle the loop delays until the next due time strictly in the future, skipping any due times already passed (an overrun of 2.5 intervals → next cycle at 3.0, never a catch-up burst). Empty catalog → idle, nothing appended. For tests the poller exposes an internal "waiting for next due time" signal so a test can advance the fake clock only after the delay is armed.
- Per cycle, tags in `PlcTagCatalog.Tags` order, one `ReadAsync` at a time; `timestampUtc` = `TimeProvider.GetUtcNow()` after each read; the cycle is appended with one `AppendBatchAsync`.
- Value mapping: `bool` → `true`/`false`; `byte`/`ushort`/`short`/`uint`/`int` → JSON integer; finite `float` → JSON number (round-trip `"R"`); NaN/±Inf → `null` + `bad`.
- Failures: `PlcUnavailableException` (incl. `PlcBusyException`) → that tag and every **remaining** tag of the cycle `null`/`bad` without calling the driver, and the next cycle is skipped (the driver lock stays free for at least one interval); `PlcAccessDeniedException` or `PlcValueException` → only that tag `null`/`bad`; caller cancellation → stop; any other exception → that tag `null`/`bad`, logged once per cycle.
- Store append failure → logged as error, cycle continues; the hosted service never crashes the host.
- Logs never contain values at Information level or above.

**`FakePlcDriver`** (tests only): per-address scripted read results or exceptions; per-address scripted write exceptions; records every read and every write (address, type, value); optional gate that blocks a call until released.

## Steps
1. Add `Tags` to `PlcTagCatalog`; `PollOptions` + validator; register `TagPoller` as hosted service (after the `EdgeDatabase` starter); add poll defaults to `EdgeTestHost`.
2. Implement the schedule, mapping and back-off.
3. `FakePlcDriver`; tests with `FakeTimeProvider` and a real temp `TelemetryStore`; one probe with the real `SiemensS7Driver` against `FakeS7Server`.

## Acceptance
- AC-04: one cycle over 3 tags (Bool, Int, Real) appends 3 rows with correct JSON values and `good`; Real NaN → `null`/`bad`; `PlcUnavailableException` on tag 1 → tags 1..3 `bad`, driver called once, and no read during the next interval; `PlcAccessDeniedException` on tag 1 → tag 1 `bad`, tags 2..3 read normally.
- Advancing the clock interval by interval (after each "waiting" signal) for 3 intervals → 3 cycles; a cycle gated for 2.5 intervals → the next cycle runs at 3.0 and only once.
- With the real `SiemensS7Driver` against a `FakeS7Server` in `BlackHole` mode and the poller running, a write issued right after a failed cycle reaches the driver lock within one interval (does not end in `PlcBusyException`).
- AC-01 (part): `IntervalMs = 50` fails at host start.

## Dependencies
- task-02-sqlite-telemetry-store.md

## Verification Plan
- Command: `dotnet test IndustrialIot.sln`
- Named probe: `TagPollerTests.Cycle_AppendsOneSamplePerTag`, `TagPollerTests.NonFinite_IsNullBad`, `TagPollerTests.Unavailable_ShortCircuitsAndBacksOff`, `TagPollerTests.AccessDenied_OnlyThatTagBad`, `TagPollerTests.Schedule_NoBurstAfterOverrun`, `TagPollerTests.RealDriver_OutageDoesNotStarveWrites`, `TagPollerTests.InvalidPollConfig_FailsAtStart`; all earlier probes still pass.
- Reachability: source — .NET 10 SDK; poller resolved through `EdgeTestHost` (`AddEdgeAgent`) in at least one probe.
- Oracle: exit 0, 0 failed across the solution; seven named probes pass.
- Counterexample: reading every tag after `PlcUnavailableException`, polling again immediately after it, or catching up missed ticks must fail the named probes.
- Artifacts: ephemeral temp directories.

## Failure Protocol
On a failed Step or Verification Plan run: stop; do not widen scope, change the Command, or weaken a test; record observed versus expected; repair only the cited cause; after three failed rounds, stop and ask the user.

## Receipt

Verification: PASS
Command: dotnet test IndustrialIot.sln
Exit: 0
Base: d50cbfdf5a122f42e9cba23409b986e85969ba2e
Head: 7c5fb36b365a75221f8644f46391da7a5cf74ac7d327c307a65d13ebda9d2286
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

Passed!  - Failed:     0, Passed:    47, Skipped:     0, Total:    47, Duration: 393 ms - Shared.Tests.dll (net10.0)

Passed!  - Failed:     0, Passed:   248, Skipped:     0, Total:   248, Duration: 14 s - Edge.Tests.dll (net10.0)
```
- Pre-change run: exit 0 with 0 `TagPollerTests` listed → oracle (seven named probes) not met, as expected.
- Named probes present and passing: `Cycle_AppendsOneSamplePerTag`, `NonFinite_IsNullBad`, `Unavailable_ShortCircuitsAndBacksOff`, `AccessDenied_OnlyThatTagBad`, `Schedule_NoBurstAfterOverrun`, `RealDriver_OutageDoesNotStarveWrites`, `InvalidPollConfig_FailsAtStart`; all earlier probes still pass. `TagPollerTests` repeated 3 times before and 2 times after the repair round, 7/7 each.
- Counterexamples (mutations in a scratch copy), each caught: reading every tag after `PlcUnavailableException` and polling again without back-off → `Unavailable_ShortCircuitsAndBacksOff` + `RealDriver_OutageDoesNotStarveWrites`; catching up missed ticks (delay clamped at zero) → `Schedule_NoBurstAfterOverrun`.
- `EdgeTestHost` runs only the database starter unless `runAgentServices: true` (otherwise the poller would write into the store tests); task-04/05 keep at least one smoke test with all services running.
- Repair rounds: R1 review PASS_WITH_WARNINGS (waits signalled before the fake timer existed; ~0.2 s margin in the real-driver probe) → timer created before signalling, assert at +1.0 s (+ explicit value mapping, catalog `Tags` as array). Final re-review: code-auditor PASS (0 Critical/High/Medium).
- Known limitations: one warning per tag per cycle while the PLC refuses access and one error per cycle while the store fails (no rate limiting); the schedule uses wall-clock time, so an NTP step only shifts the phase; `RealDriver_OutageDoesNotStarveWrites` depends on real timing (≈0.7 s margin each side); the test helper polls `CountAsync` synchronously.
