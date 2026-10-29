# Task 02 — The Monitor shows the read duration; the gain is measured on the lab S7-1200

Status: blocked
Blocker: Step 1 is committed (a7d4c01); the live baseline/after runs need the lab S7-1200, and no PLC is available to the user right now (2026-10-28). Baseline worktree ../PlcProtocol-baseline is built and kept for that run.

## Outcome
The Monitor status carries the last cycle's batch-read duration (`readMs`); on the lab S7-1200 with the 21 configured
tags the median after the change is clearly lower than a measured baseline and the values match TIA Portal (plan D-04,
AC-04).

## Scope
- In:
  - `PollCycle` (`src/Edge/Monitoring/LiveTagSnapshot.cs:10`) gains the read duration, measured in `TagPoller` around
    the batch read with the injected `TimeProvider` (`GetTimestamp`/`GetElapsedTime`); `MonitorStatus`
    (`src/Edge/Monitoring/MonitorServer.cs:180-188`) exposes it as `readMs` (null before the first cycle);
    `monitor.html` shows it next to the last-cycle time (`:67`, `:154`).
  - A test with `FakeTimeProvider` where the fake driver advances the clock by 250 ms during the batch → `readMs == 250`.
  - The PDU and job-count log lines come from task-01; the `readMs` changes (LiveTagSnapshot, TagPoller, MonitorServer,
    monitor.html, MonitorTests) are committed as ONE commit on their own (user approval at that time) so the baseline can
    cherry-pick them.
  - Live measurement (mandatory measured baseline): a git worktree at `f4803e5` with only the `readMs` commit
    cherry-picked, and this branch, both run with the "PLC test (no broker)" profile, same build configuration, debugger
    attached or not in both; discard the first 3 cycles, record ≥ 5 `readMs` samples per run and report the medians and
    the job counts from the job-count log line (2 vs 21); the user compares at least one value per data type with the TIA watch table, including a tag
    that follows an odd-length item (e.g. IW64 after IB0/Q0.0).
- Out: changing the poll interval; dashboards.

## Coverage
- CP-02

## Ownership
- Modify: `src/Edge/Monitoring/LiveTagSnapshot.cs`, `src/Edge/Polling/TagPoller.cs`, `src/Edge/Monitoring/MonitorServer.cs`,
  `src/Edge/Monitoring/monitor.html`
- Modify: `tests/Edge.Tests/MonitorTests.cs`

## Steps
1. `readMs` in the cycle and the status; exact-value test; page; commit these alone.
2. Live: baseline worktree + cherry-pick, then this branch; record samples, medians and TIA readings.

## Acceptance
- AC-04: the exact-value test passes; the recorded median after the change is clearly below the measured baseline
  median (numbers reported); the user confirms the compared values match TIA Portal.

## Dependencies
- task-01-s7-multi-var-reads.md

## Verification Plan
- Command: `dotnet test tests/Edge.Tests --filter "FullyQualifiedName~MonitorTests" && curl -s http://localhost:5080/api/status`
- Live: the curl runs (≥ 5 samples each) against the user's Edge started from Visual Studio on the lab S7-1200, baseline and after.
- Named probe: the `readMs` exact-value test; the recorded `/api/status` samples (baseline and after); the PDU and
  job-count log lines (from task-01); the user's TIA readings.
- Reachability: live — lab S7-1200 (192.168.3.20).
- Oracle: test passes; after-median clearly below baseline-median; all tags Good; values equal TIA readings.
- Counterexample: a driver still reading one tag per request shows a median near the baseline; a wrong item mapping
  shows a value that differs from TIA (the tag after an odd item).
- Artifacts: the recorded JSON samples (no secrets) in the receipt.

## Failure Protocol
On a failed Step or Verification Plan run: stop; do not widen scope, change the Command, or weaken a test; record observed versus expected; repair only the cited cause; after three failed rounds, stop and ask the user. Without the lab PLC, the user's runs, or a measured baseline the task stays `blocked` naming which (the documented 370 ms is not a substitute).

## Receipt
