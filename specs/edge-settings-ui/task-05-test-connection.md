# Task 05 — The operator tests a PLC connection without saving

Status: done

## Outcome
`POST /api/settings/test-connection {password, plc}` returns `{result: ok|unreachable, elapsedMs}`.
It does not save anything, does not disturb the running driver, and does not open a second connection to the running
PLC (D-10).

## Scope
- In:
  - The D-11 guard, then validation of `plc` (400 with key paths), then the password check. Wrong passwords count toward the same lockout as saves.
  - Only one test may run at a time (409), and at most 10 tests per minute (429).
  - If the proposed protocol, Host and Port match the running driver, the answer comes from the running driver's state.
  - Otherwise the endpoint builds a separate driver, calls `IsReachableAsync` with the proposed `TimeoutMs`, and disposes the driver.
  - Every test is logged as host:port plus the result.
- Out: reading tags or browsing the PLC.

## Coverage
- CP-04

## Ownership
- Modify: `src/Edge/Monitoring/MonitorServer.cs` (route), `src/Edge/Settings/EdgeSupervisor.cs` (shared-state list entry for the rate limiter), `src/Edge/EdgeServiceCollectionExtensions.cs` (registration)
- Create: `src/Edge/Settings/PlcConnectionTester.cs`, `tests/Edge.Tests/PlcConnectionTesterTests.cs`
- Read: `src/Edge/Drivers/IPlcDriver.cs`, `src/Edge/Drivers/SiemensS7Driver.cs`, `src/Edge/Drivers/Slmp/*.cs`, `tests/Edge.Tests/Fakes/FakeS7Server.cs`, `tests/Edge.Tests/Fakes/FakeSlmpServer.cs`

## Steps
1. Write `PlcConnectionTesterTests`:
   - (a) S7 against `FakeS7Server` → `ok`.
   - (b) SLMP against `FakeSlmpServer` → `ok`.
   - (c) A closed port → `unreachable` within TimeoutMs + 1 s.
   - (d) A second concurrent test → 409. An 11th test in one minute → 429 (fake `TimeProvider`).
   - (e) A wrong password → 401, and the fake sees 0 connections. 5 wrong tests then lock saves too (429).
   - (f) A host polling fake A, then a test against A's host:port → `ok`, and A's `OpenConnections` stays 1.
   - (g) Cross-site `Origin` → 403, and no connection is made.

   Before the change, expect 404 or a compile failure.
2. Implement the tester and the route.

## Acceptance
- AC-04 (test part): (e) and (g) pass.
- AC-06: (a)–(d) and (f) pass.
- The full Edge suite passes.

## Dependencies
- task-04-settings-api.md

## Verification Plan
- Command: `dotnet test tests/Edge.Tests`
- Named probe: `PlcConnectionTesterTests` (a)–(g).
- Reachability: source level with fakes. The live level needs a lab PLC, which the user does not have now. That is a recorded limitation and is not used as proof.
- Oracle: exit 0, `Skipped: 0`, `PlcConnectionTesterTests` ≥ 7.
- Counterexample: opening a new connection for the running PLC makes (f) see 2; skipping the password check makes (e) see a connection.
- Artifacts: ephemeral.

## Failure Protocol
On a failed Step or Verification Plan run: stop; do not widen scope, change the Command, or weaken a test; record observed versus expected; repair only the cited cause; after three failed rounds, stop and ask the user.

## Receipt

Verification: PASS
Command: dotnet test tests/Edge.Tests
Exit: 0
Base: 927442f6c144893b2810cf30b14ac572eee996e8
Head: 88f6fc75528450d0ed1279ba1432356abc9d5df2b3771c70e5b03eb08825c9a9
```text
$ dotnet test tests/Edge.Tests
Passed!  - Failed:     0, Passed:   543, Skipped:     0, Total:   543, Duration: 29 s - Edge.Tests.dll (net10.0)
$ dotnet test tests/Edge.Tests --no-build --filter "FullyQualifiedName~PlcConnectionTesterTests"
Passed!  - Failed:     0, Passed:     8, Skipped:     0, Total:     8, Duration: 16 s - Edge.Tests.dll (net10.0)
```
- Pre-change run: the tests were written first, and `PlcConnectionTesterTests` failed 8/8 because the route did not exist yet.
- Probe → acceptance mapping:

  | Probe | Test |
  |---|---|
  | (a) | `S7_AgainstAFakePlc_IsOk`: the test connection is closed afterwards |
  | (b) | `Slmp_AgainstAFakePlc_IsOk` |
  | (c) | `ClosedPort_IsUnreachable_WithinTheTimeoutPlusOneSecond`: the response carries only `result` and `elapsedMs` |
  | (d) | `OneTestAtATime_409_AndAtMostTenAMinute_429`: a BlackHole fake holds the slot; `FakeTimeProvider` drives the minute |
  | (e) | `WrongPassword_Is401_WithoutConnecting_AndCountsTowardTheSaveLockout`: 0 connections; after 5 wrong tries both the test and the save answer 429 |
  | (f) | `ThePlcInUse_IsAnsweredByTheRunningDriver_WithoutASecondConnection`: accept count unchanged, 1 open connection |
  | (g) | `CrossSiteOrigin_Is403_AndNothingConnects` |
  | Added | `InvalidPlc_Is400_WithKeyPaths` |

- Linux: the full Edge suite passed 543/543 with `Skipped: 0` in the pinned SDK container.
- Counterexamples, each run on a fresh scratch copy (`scratchpad/mut-esui05`, script `scratchpad/mut-t05.sh`); each one fails the test listed:
  - A new driver opened even for the PLC in use → (f) fails.
  - The password not checked → (e) fails.
  - The limiter bypassed → (d) fails. The first attempt at this mutation did not compile (warnings are errors), so it was rewritten and rerun.
- Deviation: `src/Edge/Settings/SettingsService.cs` (task-04) was changed:
  - `ReadGuardedAsync`, `Reply`, `PathOf` and `FixedMessage` became internal.
  - The password switch was extracted into `PasswordAcceptedAsync`, which saves and tests now share.

  This refactor adds no behaviour. The 37 `SettingsApiTests` still pass in the run above.
- Review (code-auditor, fresh context): PASS, with 0 Critical, High or Medium findings. Lows left as limitations:
  - A test cancelled by the client is not logged.
  - The host is logged unfiltered (control characters, length).
  - The running-PLC path uses the running driver's timeout. It can report `unreachable` while a long read holds the lock, and `ok` on a half-open connection.
  - The host is compared as a string (`localhost` ≠ `127.0.0.1`, no trim).
  - The test-connection route allows connect-only scanning of any host:port. It requires the password, is limited to 10 a minute, runs one test at a time, and logs every test.
  - An unrelated S7 key with a bad type sent for Slmp gives a generic 400.
  - Running-driver disposal mid-test gives a 500.
  - The SLMP test does not assert that the connection is closed, and nothing tests a client abort.
  - `IServiceProvider` is used as a service locator.
- Live level: needs a lab PLC, which is not available. It is not part of this proof.
