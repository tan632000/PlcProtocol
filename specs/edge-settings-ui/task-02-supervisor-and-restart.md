# Task 02 — An apply rebuilds the inner agent host under one outer lifetime, with rollback and crash recovery

Status: done

## Outcome
`Program.cs` builds one outer host. It owns `AddWindowsService` and the console/SCM lifetime, and runs `EdgeSupervisor`.
The supervisor builds the inner agent host and rebuilds it when a restart is requested (D-07). `/api/status` reports
`settingsApply` (D-08).

## Scope
- In:
  - The outer and inner host split. Inner hosts use a no-op `IHostLifetime` and set `ShutdownTimeout` explicitly.
  - `EdgeRestarter.Request(newBytes | delete)`: it only queues the request and returns an `applyId`. The supervisor acts after the response that carried it has completed.
  - Apply sequence:
    1. Write the `pending` marker with the previous bytes, or "absent".
    2. Write the file atomically (temp file, then rename).
    3. Stop and dispose the inner host.
    4. Build and start a new one.
    5. Check that the monitor is listening.
    6. Delete the marker on success.
  - Failure definition and rollback, as stated in D-07.
  - Cold-start recovery from the marker.
  - Exit code 1 when the rolled-back start also fails.
  - The outer stop ends the loop at any point.
  - `MonitorServer` exposes whether it is listening. During an apply it retries the bind for up to 5 s, and only then does a bind failure count as a start failure. On the first cold start, today's log-and-continue behaviour stays.
  - A shared-state list in the supervisor: instances created once and registered into every inner host (N-3). Task-02 adds `EdgeRestarter` to it; tasks 03-05 add their own.
  - `settingsApply` in the status payload.
- Out: the HTTP save route (task-04).

## Coverage
- CP-02

## Ownership
- Modify: `src/Edge/Program.cs` (keeps the literal `AddWindowsService(` call, see `deployments/edge/verify-edge.ps1:33-41`), `src/Edge/Monitoring/MonitorServer.cs` (listening flag + status record only)
- Create: `src/Edge/Settings/EdgeSupervisor.cs`, `src/Edge/Settings/EdgeRestarter.cs`, `tests/Edge.Tests/EdgeSupervisorTests.cs`
- Read: `src/Edge/Settings/EdgeSettingsFile.cs`, `src/Edge/Drivers/SiemensS7Driver.cs` (dispose), `tests/Edge.Tests/Fakes/FakeS7Server.cs`

## Steps
1. Write `EdgeSupervisorTests`. Run the supervisor in-process with a test outer host. Cases:
   - (a) A restart request with new bytes: the second inner host runs on them, and the state reaches `applied` for that `applyId`.
   - (b) After a restart against `FakeS7Server`: `OpenConnections == 1`, and the first inner host's services are disposed.
   - (c) The new inner host fails through a seam (an injected hosted service that throws in `StartAsync`, and separately the monitor port occupied for the **first** start attempt only, longer than the 5 s bind retry). The previous bytes are restored, the host runs on them, and the state is `rolledBack` with a fixed error.
   - (c2) The monitor port is busy for 2 s during an apply: the bind retry succeeds and the state is `applied`.
   - (c3) A singleton registered in the supervisor's shared-state list is the same instance in the first and the second inner host.
   - (d) A first-time file whose start fails: the file is deleted and the host runs on the base settings.
   - (e) The restored settings also fail: `RunAsync` returns 1.
   - (f) Cold start with a `pending` marker and a failing file: the marker contents are restored and the marker is deleted.
   - (g) Cold start with a marker and a good file: the marker is deleted and the file is kept.
   - (h) The outer stop during a restart ends cleanly within `ShutdownTimeout` + 2 s.
   - (i) The inner hosts' `IHostLifetime` is neither `WindowsServiceLifetime` nor `ConsoleLifetime`.

   Before the change, expect compile failure.
2. Implement it, and change `Program.cs` to the outer host plus the supervisor.

## Acceptance
- AC-02 (source level): (a)–(i) pass, and the full Edge suite passes. The live level (installed service survives a save) is in task-07.

## Dependencies
- task-01-settings-file.md

## Verification Plan
- Command: `dotnet test tests/Edge.Tests`
- Named probe: `EdgeSupervisorTests` (a)–(i), with the rest of the suite as regression. `verify-edge.ps1` default level must still show VERIFY-STATIC PASS (run `powershell -NoProfile -ExecutionPolicy Bypass -File deployments/edge/verify-edge.ps1`).
- Reachability: source level, dev machine and CI. The SCM behaviour is only observable live (task-07).
- Oracle: exit 0, `Skipped: 0`, `EdgeSupervisorTests` ≥ 11, and VERIFY-STATIC PASS.
- Counterexample:
  - `StopAsync` without dispose makes (b) see 2 connections.
  - `AddWindowsService` on the inner host makes (i) fail.
  - Deciding rollback by `.bak` existence makes (d) restore stale bytes when an old backup is present. The test seeds one.
- Artifacts: ephemeral temp directories.

## Failure Protocol
On a failed Step or Verification Plan run: stop; do not widen scope, change the Command, or weaken a test; record observed versus expected; repair only the cited cause; after three failed rounds, stop and ask the user.

## Receipt

Verification: PASS
Command: dotnet test tests/Edge.Tests
Exit: 0
Base: 927442f6c144893b2810cf30b14ac572eee996e8
Head: 54712e8312d9336f8d680bad350028987c6da8086cae069ad0574f5e41b60722
```text
$ dotnet test tests/Edge.Tests
Passed!  - Failed:     0, Passed:   472, Skipped:     0, Total:   472, Duration: 28 s - Edge.Tests.dll (net10.0)
$ dotnet test tests/Edge.Tests --no-build --filter "FullyQualifiedName~EdgeSupervisorTests"
Passed!  - Failed:     0, Passed:    15, Skipped:     0, Total:    15, Duration: 5 s - Edge.Tests.dll (net10.0)
$ powershell -NoProfile -ExecutionPolicy Bypass -File deployments/edge/verify-edge.ps1
PASS VERIFY-BUILD the solution builds
PASS VERIFY-STATIC Program.cs calls AddWindowsService; install-edge.ps1 never runs dotnet
PASS VERIFY-PACKAGE self-contained package with exe, runtime, appsettings.json and the scripts
PASS VERIFY-FAILFAST packaged exe exits non-zero naming Edge:EdgeId for an invalid id
PASS VERIFY-PARSE all four scripts parse with PowerShell 5.1
PASS VERIFY-SECRETS no password parameter; no output of a password
Edge verification passed.
```
- Pre-change run: compile failure CS0246, because `EdgeSupervisor`, `EdgeRestarter` and `ApplyStatus` (the types this task creates) did not exist yet.
- Probe → acceptance mapping:
  - (a) `Apply_RebuildsTheInnerHost_OnTheNewSettings`
  - (b) `Apply_DisposesTheOldInnerHost_SoOnlyOnePlcConnectionRemains`: the old host's provider throws `ObjectDisposedException`, and the fake has 1 open connection.
  - (c) throwing seam: `FailingStart_RestoresThePreviousFile_AndReportsRolledBack`. Monitor port seam (retry 2 s, port held 3 s): `MonitorPortTakenLongerThanTheBindRetry_RollsBack`.
  - (c2) `MonitorPortBusyShorterThanTheBindRetry_Applies`
  - (c3) and (i) `InnerHosts_DoNotOwnTheProcessLifetime_AndShareTheSupervisorState`
  - (d) `FirstTimeFileThatFails_IsDeleted_AndTheBaseSettingsRun`: a stale `.bak` is seeded and must not be restored.
  - (e) `RollbackThatAlsoFails_StopsTheOuterHostWithExitCode1`
  - (f) `ColdStartWithMarker_AndAFailingFile_RestoresTheMarkerContents`
  - (g) `ColdStartWithMarker_AndAGoodFile_KeepsTheFile_AndDropsTheMarker`
  - (h) `OuterStop_DuringARestart_EndsCleanly`: waits until the hanging start is entered; checks it was cancelled and the marker kept.
  - Added: a cold-start failure without a marker fails the outer start; a second request is refused while one is pending; a settings path that cannot be written keeps the running host (`NotWrittenMessage`); `/api/status` JSON `settingsApply`.
- Linux check (the CI `test` job runs on ubuntu): ran in a container from the repo's pinned SDK image (`mcr.microsoft.com/dotnet/sdk@sha256:0d99eb…`, kernel 6.18 WSL2) on a copy of `src/Edge`, `src/Shared`, `tests/Edge.Tests`, `Directory.Build.props`, `global.json` and `IndustrialIot.sln`. Result: full Edge suite 472/472, `Skipped: 0`. The first container run left out the `.sln` and failed only the 6 tests that locate the repo root through it.
- Counterexamples, run on scratch copies (`scratchpad/mut-esui02`):
  - Removing the dispose after stop is caught by (b). The connection count alone did not catch it, because the old S7 connection closes anyway; the `ObjectDisposedException` assertion was added for this reason.
  - Inner hosts keeping `ConsoleLifetime` is caught by (i).
  - Rolling back from a stale `.bak` is caught by (d).
  - Disabling the write-failure catch is caught by the cannot-be-written test.
- Stability: the supervisor tests passed three runs in a row after each repair.
- Behaviour notes for GATE-DONE and task-07:
  - A failed cold start without a marker still crashes the process as before. VERIFY-FAILFAST still passes.
  - A fatal apply stops the outer host with exit code 1, and under the SCM also sets `ServiceBase.ExitCode = 1`.
  - Whether the SCM then restarts the service depends on `sc failureflag`, which `install-edge.ps1` does not set. This was inferred from the runtime source and has not been observed live.
- Review (code-auditor, fresh context):
  - Round 1: FAIL.
    - High: exceptions outside the start in `ApplyAsync` killed the loop, left the status stuck at `applying`, and could leave the process alive with no agent.
    - Medium: the exit code did not reach the SCM; test (h) did not ensure the stop happened mid-apply; `settingsApply` had no test.
  - Repair round 1 fixed all of these, plus the Low items: dispose on any monitor start exception, the cold-start OCE filter, a distinct fatal message, and the port-test margin.
  - Round 2: FAIL. High: the new read-only-file test would pass on Windows only (on Linux, `rename(2)` replaces a read-only file).
  - Repair round 2: the seam became a directory at the settings path, and the `ServiceBase` cast moved into a `[SupportedOSPlatform("windows")]` helper.
  - Round 3: PASS, 0 Critical/High/Medium.
  - Lows left as is: no separate "listening" flag (the start throws instead); `Stopwatch` instead of `TimeProvider` in the monitor retry; a low-probability race when the outer shutdown times out; `EdgeSupervisor.cs` is about 385 lines.
