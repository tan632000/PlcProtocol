# Task 02 — One batch read per cycle, with today's outage short-circuit and back-off

Status: done

## Outcome
The poller reads all tags with one `ReadAsync(IReadOnlyList<ResolvedTag>)` call per cycle; an unreachable PLC still
costs about one timeout per cycle, the remaining tags still say "Not read: …", and the next interval is still skipped
(plan D-03, D-07).

## Scope
- In:
  - `IPlcDriver.ReadAsync(IReadOnlyList<ResolvedTag> tags, CancellationToken ct)` → `IReadOnlyList<PlcReadResult>`
    (value or `Exception`, same order); the single-tag read leaves `IPlcDriver` only — `SiemensS7Driver` keeps its
    single-tag `ReadAsync`, so `SiemensS7DriverTests` are unchanged.
  - A production helper `SequentialBatchReader` (per-tag read delegate) implementing the rules of plan D-03: stop all
    I/O after the first `PlcUnavailableException` and return it for every remaining tag; let `OperationCanceledException`
    propagate; per-tag `PlcAccessDenied`/`PlcValue` errors and any other exception stay per tag (today's behaviour). The S7 driver uses it with its lock taken per
    tag; `FakePlcDriver` uses the same helper, so poller tests exercise production logic.
  - `TagPoller` (`src/Edge/Polling/TagPoller.cs:45-121`): one batch call; samples, quality, "Not read: {reason}" text,
    per-cycle log de-duplication and the `nextDue` back-off unchanged; a per-tag non-PLC exception gives that tag
    "Unexpected error; see the Edge log" and the other tags are still read (logged once per cycle, as today);
    cancellation stops the cycle without storing it.
  - Monitor reachability (`MonitorServer`, cycle → plcState) unchanged.
- Out: protocol selection, SLMP.

## Coverage
- CP-01

## Ownership
- Modify: `src/Edge/Drivers/IPlcDriver.cs`, `src/Edge/Drivers/SiemensS7Driver.cs`, `src/Edge/Polling/TagPoller.cs`
- Create: `src/Edge/Drivers/SequentialBatchReader.cs` (with `PlcReadResult`)
- Modify (signatures only, no assertion change): `tests/Edge.Tests/Fakes/FakePlcDriver.cs`, `tests/Deployment.Tests/EndToEndTelemetryTests.cs` (`SimulatedPlc`)
- Create: `tests/Edge.Tests/BatchReadTests.cs`

## Steps
1. Tests first:
   - real S7 driver against `FakeS7Server` in its black-hole mode with three tags and `TimeoutMs=300`: the batch returns
     within about two timeouts, the server sees exactly one connection attempt, and tags 2-3 carry the same
     `PlcUnavailableException`;
   - order and per-tag isolation (an access-denied middle tag);
   - cancellation propagates and the poller stores nothing for that cycle;
   - one tag whose read throws `InvalidOperationException` → only that tag "Unexpected error; see the Edge log", the
     other tags Good, one log entry (today's behaviour, previously untested).
2. Contract, helper, S7 driver, poller, fakes.

## Acceptance
- AC-01 (reads and outage behaviour): new tests pass; all pre-existing tests pass unchanged (including
  `TagPollerTests.Unavailable_ShortCircuitsAndBacksOff` and `RealDriver_OutageDoesNotStarveWrites`); the solution builds.

## Dependencies
- task-01-protocol-neutral-addresses.md

## Verification Plan
- Command: `dotnet build IndustrialIot.sln -c Release && dotnet test tests/Edge.Tests && dotnet test tests/Deployment.Tests --filter "FullyQualifiedName~EndToEndTelemetryTests" && ! git diff -U0 HEAD -- tests/Edge.Tests tests/Deployment.Tests | grep -E '^-[^-].*(Assert\.|InlineData|MemberData|\{ ")'`
- Named probe: `BatchReadTests` (real-driver outage, order/isolation, cancellation, unexpected exception) and the
  existing `TagPollerTests`, `MonitorTests`, `CommandFlowTests`, `EndToEndTelemetryTests`.
- Reachability: source — real S7 driver over TCP to the fake S7 server; real host for the poller.
- Oracle: build exit 0; `Failed: 0`, `Skipped: 0`; the grep prints nothing.
- Counterexample: a batch that keeps reading after an outage shows three connection attempts and fails the real-driver
  test; a fake with its own short-circuit cannot hide it because the fake uses the production helper.
- Artifacts: ephemeral.

## Failure Protocol
On a failed Step or Verification Plan run: stop; do not widen scope, change the Command, or weaken a test; record observed versus expected; repair only the cited cause; after three failed rounds, stop and ask the user.

## Receipt

Verification: PASS
Command: dotnet build IndustrialIot.sln -c Release && dotnet test tests/Edge.Tests && dotnet test tests/Deployment.Tests --filter "FullyQualifiedName~EndToEndTelemetryTests" && ! git diff -U0 HEAD -- tests/Edge.Tests tests/Deployment.Tests | grep -E '^-[^-].*(Assert\.|InlineData|MemberData|\{ ")'
Exit: 0
Base: f985f23f58461cb5364cf96e0815c54fa444d6ec
Head: 5c6276e951174ea856115c9a45f17a7540989377c613954aa53d414a4c1c4344
```text
$ dotnet build IndustrialIot.sln -c Release && dotnet test tests/Edge.Tests && dotnet test tests/Deployment.Tests --filter "FullyQualifiedName~EndToEndTelemetryTests" && ! git diff -U0 HEAD -- tests/Edge.Tests tests/Deployment.Tests | grep -E '^-[^-].*(Assert\.|InlineData|MemberData|\{ ")'
Test run for C:\Users\case 046\Documents\Project\PlcProtocol\tests\Edge.Tests\bin\Debug\net10.0\Edge.Tests.dll (.NETCoreApp,Version=v10.0)
Passed!  - Failed:     0, Passed:   325, Skipped:     0, Total:   325, Duration: 28 s - Edge.Tests.dll (net10.0)
Test run for C:\Users\case 046\Documents\Project\PlcProtocol\tests\Deployment.Tests\bin\Debug\net10.0\Deployment.Tests.dll (.NETCoreApp,Version=v10.0)
Passed!  - Failed:     0, Passed:     2, Skipped:     0, Total:     2, Duration: 3 m 18 s - Deployment.Tests.dll (net10.0)
```
- Pre-change run passed (Edge 316, E2E 2): the unmet probe was the absent BatchReadTests. After: Edge 325 (316 + 9 new cases), E2E 2, build 0 warnings, removed-assertion/test-data grep empty.
- Named probes: real SiemensS7Driver vs FakeS7Server BlackHole, 3 tags, TimeoutMs 300 -> AcceptCount 1, < 850 ms, tags 2-3 carry the same exception instance; helper order/isolation and cancellation; poller stores nothing on a cancelled cycle; two tags throwing InvalidOperationException -> only those Bad, one log; broken batch (throw / short result / foreign OCE) -> all tags Unexpected error, one log, polling continues; value and error both null -> Value is not a finite number, no log.
- FakePlcDriver and SimulatedPlc use the production SequentialBatchReader, so poller tests (Unavailable_ShortCircuitsAndBacksOff, RealDriver_OutageDoesNotStarveWrites) exercise production short-circuit logic unchanged.
- Review: code-auditor PASS_WITH_WARNINGS (poller could stop the host on a contract-breaking driver) -> repair round 1 (batch guard) -> PASS. Limitations: sample timestamps are taken after the whole batch (up to one timeout late during an outage); a PlcUnavailableException thrown by a batch call itself (outside the helper) is reported as unexpected without back-off; per-tag lock interleaving is proven by reading, not by a test.
