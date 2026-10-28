# Task 01 — The S7 driver reads a batch with multi-variable requests packed to the PDU

Status: done

## Outcome
`SiemensS7Driver.ReadAsync(IReadOnlyList<ResolvedTag>)` sends as few `ReadMultipleVarsAsync` requests as the negotiated
PDU and the 20-item cap allow, with values, per-tag errors (incl. PUT/GET off), outage short-circuit and write
interleaving unchanged (plan D-01..D-03).

## Scope
- In:
  - Per-tag checks first (D-02b); invalid tags keep their own exception and are never sent.
  - `S7ReadPlanner`: greedy packing of the remaining valid tags into one request using the current `MaxPDUSize`, the
    S7netplus 0.20.0 formulas (request `19 + 12·n`, response `14 + 4·n + Σ even(len)`) and a 20-item cap — formulas cited
    with their source in the code.
  - Per request, inside the lock after `ConnectAsync` (D-01); lock released between requests.
  - Items `VarType.Byte`, Count = `max(SizeBytes,1)`, `byte` → `byte[1]`; null/wrong length → protocol error (D-02).
  - Errors and isolation per D-02c and D-02a: connection failure → unavailable for the request's and later tags, no
    further request; complete PLC error reply (refused item, 0x8104 header-only incl. the `IndexOutOfRangeException`
    case, recognised only when thrown inside `ReadMultipleVarsAsync`) → keep the connection, re-read that request's
    tags solo through `SequentialBatchReader` rules with an internal solo read that keeps the connection on a complete
    PLC error reply and discards otherwise; remembered bad tags read alone later through the same solo read (set touched
    only under the lock); a truncated frame → connection failure; cancellation propagates; an S7netplus limit
    exception → unexpected (planner bug); log the negotiated PDU and the job count per batch.
  - Single-tag `ReadAsync(IPlcAddress, …)` and `WriteAsync` unchanged (including their discard behaviour and tests).
  - `FakeS7Server` address-aware multi-item reads per D-03; existing modes and tests unchanged.
- Out: byte-block merging, Monitor/poller (task-02).

## Coverage
- CP-01

## Ownership
- Modify: `src/Edge/Drivers/SiemensS7Driver.cs`
- Create: `src/Edge/Drivers/S7ReadPlanner.cs`
- Modify: `tests/Edge.Tests/Fakes/FakeS7Server.cs`
- Create: `tests/Edge.Tests/S7BatchReadTests.cs`

## Steps
1. Extend the fake (D-03); tests first, every value asserted against KNOWN constants seeded per address:
   - the 21 tags of `src/Edge/appsettings.json` at PDU 240 → exactly 2 jobs (18 + 3), each within the limits; values for
     Bool (DB bit, I, Q, M), Byte, Word, Int, DWord, DInt, Real, with odd-length items placed before Word/Real items;
   - planner boundaries: 18 one-byte items at PDU 240 → 1 job, 19 → 2 jobs; a response exactly at the limit → 1 job,
     one byte over → split; PDU 960 with 30 tags → jobs of at most 20 items;
   - PDU changes after a forced reconnect → later requests planned for the new PDU;
   - a hand-written S7comm reply frame (odd item then Word/Real) parsed by the driver to the expected values;
   - a short final item → protocol error (unavailable, connection discarded, no further request);
   - refused item (return code 0x05/0x0A) → that tag today's access-denied text, neighbours Good, connection kept
     (`AcceptCount` unchanged); next batch: packed jobs for the good tags plus one solo job; after the fake accepts the
     address again → solo once, then packed again;
   - `HandshakeThenErrorHeader` (PUT/GET off) on a batch → every tag `PlcAccessDeniedException` with today's text, and
     after two cycles `AcceptCount == 1` (connection kept);
   - `HandshakeThenTruncatedFrame` on a batch → every tag the same `PlcUnavailableException`, connection discarded;
   - BlackHole at connect, and the fake's per-job script dropping after job 1 of 2 and during isolation → exactly one
     more connection attempt, no further job in the log, remaining tags carry the same exception instance;
   - a mixed batch with one incompatible type and one non-S7 address → those two keep today's per-tag errors, no job
     contains them;
   - a write issued while a 2-job batch runs completes between the jobs (deterministic via the job log / `JobReceived`
     and a response delay).
2. Implement the planner and the driver path.

## Acceptance
- AC-01, AC-02, AC-03: new tests pass; all pre-existing tests pass unchanged since the base commit; the solution builds.

## Dependencies
- none

## Verification Plan
- Command: `dotnet build IndustrialIot.sln -c Release && dotnet test tests/Edge.Tests && dotnet test tests/Deployment.Tests --filter "FullyQualifiedName~EndToEndTelemetryTests" && ! git diff -U0 f4803e5 -- tests/Edge.Tests tests/Deployment.Tests | grep -E '^-[^-].*(Assert\.|InlineData|MemberData|\{ "|Fact\]|Theory\])' && ! git diff -U0 f4803e5 -- tests/Edge.Tests tests/Deployment.Tests | grep -E '^\+.*Skip\s*='`
- Named probe: `S7BatchReadTests` (job counts asserted exactly, boundaries, PDU change, hand-written frame, short item,
  refused item and recovery, PUT/GET off, outage, pre-I/O errors, write interleaving); existing `SiemensS7DriverTests`,
  `BatchReadTests`, `TagPollerTests`, `CommandFlowTests`.
- Reachability: source — the real S7 driver over TCP to the in-process fake S7 server.
- Oracle: build exit 0; `Failed: 0`, `Skipped: 0`; both greps print nothing.
- Counterexample: one request per tag shows 21 jobs, not 2; a wrong item mapping or fill byte gives wrong constants; a
  refused item that blanks the request or discards the connection fails the isolation case; treating the 0x8104 reply
  as an outage fails the PUT/GET case; an off-by-one planner fails the boundary cases.
- Artifacts: ephemeral.

## Failure Protocol
On a failed Step or Verification Plan run: stop; do not widen scope, change the Command, or weaken a test; record observed versus expected; repair only the cited cause; after three failed rounds, stop and ask the user.

## Receipt

Verification: PASS
Command: dotnet build IndustrialIot.sln -c Release && dotnet test tests/Edge.Tests && dotnet test tests/Deployment.Tests --filter "FullyQualifiedName~EndToEndTelemetryTests" && ! git diff -U0 f4803e5 -- tests/Edge.Tests tests/Deployment.Tests | grep -E '^-[^-].*(Assert\.|InlineData|MemberData|\{ "|Fact\]|Theory\])' && ! git diff -U0 f4803e5 -- tests/Edge.Tests tests/Deployment.Tests | grep -E '^\+.*Skip\s*='
Exit: 0
Base: f4803e5a241e9f788d081c6ddd7574f5fa869c39
Head: 75ff9337e54a3f2447829a4412d767d6bdbfd468384c478233978b882bb5aaf7
```text
$ dotnet build IndustrialIot.sln -c Release && dotnet test tests/Edge.Tests && dotnet test tests/Deployment.Tests --filter "FullyQualifiedName~EndToEndTelemetryTests" && ! git diff -U0 f4803e5 -- tests/Edge.Tests tests/Deployment.Tests | grep -E '^-[^-].*(Assert\.|InlineData|MemberData|\{ "|Fact\]|Theory\])' && ! git diff -U0 f4803e5 -- tests/Edge.Tests tests/Deployment.Tests | grep -E '^\+.*Skip\s*='
Build succeeded.
    0 Warning(s)
Test run for C:\Users\case 046\Documents\Project\PlcProtocol\tests\Edge.Tests\bin\Debug\net10.0\Edge.Tests.dll (.NETCoreApp,Version=v10.0)
Passed!  - Failed:     0, Passed:   432, Skipped:     0, Total:   432, Duration: 28 s - Edge.Tests.dll (net10.0)
Test run for C:\Users\case 046\Documents\Project\PlcProtocol\tests\Deployment.Tests\bin\Debug\net10.0\Deployment.Tests.dll (.NETCoreApp,Version=v10.0)
Passed!  - Failed:     0, Passed:     2, Skipped:     0, Total:     2, Duration: 3 m 11 s - Deployment.Tests.dll (net10.0)
```
- Both git-diff greps printed nothing (no removed Assert/InlineData/MemberData/Fact/Theory lines, no added Skip) — command exit 0.
- Pre-change run failed as expected: CS0103 S7ReadPlanner missing, CS1729 SiemensS7Driver has no 2-arg constructor.
- Mutation checks (reverted): classifier for the 0x8104 reply broken -> PutGetOff fails; connection always discarded -> PutGetOff + 4 RefusedItem cases fail; prune removed -> RememberedTag_Pruned fails.
- Review: code-auditor PASS (re-review of the Low-1/Low-3 repair PASS); remaining Low: ObjectDisposedException for a whole batch after dispose, limit-exception path untested, results[19] not asserted Same.
