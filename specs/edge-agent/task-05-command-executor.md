# Task 05 — Remote commands are validated, executed at most once, and answered with one consistent result

Status: done

## Outcome
The Edge handles `iiot/{edgeId}/commands`: each `DeviceCommand` goes through size/contract → `edgeId` → known-result → clock rollback → freshness → catalog → log capacity checks, is written to the PLC only when all pass and a durable `started` record exists, and gets one consistent `CommandResult` on `iiot/{edgeId}/command-results` — re-published identically after reconnects or redeliveries.

## Scope
- In: `CommandLog` (table in the task-02 database via `EdgeDatabase`), `CommandExecutor` (decision + write + result mapping, own serialization, in-flight set, in-memory results), `CommandListener` (subscription registration, unsent-result replay, log purge), new reason code `edge_storage_error`, registration, `EdgeTestHost` defaults.
- Out: read-back after unknown writes and any automatic retry (user decision), Cloud side (wave 3), broker ACLs (wave 4).

## Coverage
- CP-05

## Ownership
- Create: `src/Edge/Commands/{CommandLog.cs,CommandExecutor.cs,CommandListener.cs}`
- Modify: `src/Shared/Common/CommandReasons.cs` (add `EdgeStorageError = "edge_storage_error"` and include it in `All`), `tests/Shared.Tests/CommandReasonsTests.cs` (expected set grows to twelve)
- Modify: `src/Edge/EdgeServiceCollectionExtensions.cs`, `tests/Edge.Tests/Fakes/EdgeTestHost.cs`
- Create: `tests/Edge.Tests/CommandExecutorTests.cs`, `tests/Edge.Tests/CommandFlowTests.cs`
- Read: `src/Shared/Contracts/{DeviceCommand.cs,CommandResult.cs}`, `src/Shared/Common/CommandOutcome.cs`, `src/Edge/Drivers/{PlcTagCatalog.cs,PlcExceptions.cs}`, `src/Edge/Messaging/MqttConnection.cs`, `src/Edge/Buffering/EdgeDatabase.cs`, `tests/Edge.Tests/Fakes/{FakePlcDriver.cs,TestMqttBroker.cs}`

## Contracts
**`command_log` table** (created through `EdgeDatabase.EnsureTableAsync`): `command_id TEXT PRIMARY KEY`, `state TEXT NOT NULL` (`started` | `completed`), `result TEXT NULL` (exact `ContractJson.Serialize(CommandResult)`), `result_sent INTEGER NOT NULL DEFAULT 0`, `expires_utc_ms INTEGER NOT NULL`, `created_utc_ms INTEGER NOT NULL`.
- Logged: decision rows 6, 8–10 and 13 — never `edge_mismatch`, clock-rollback or log-full rejections (row 12 is the failed insert itself). `expires_utc_ms` is the command's own `expiresAtUtc`, never clamped (user decision 2026-10-18, review M1: clamping let a far-future command rejected as `clock_skew` be forgotten and later executed under the same id; `DeviceCommand.Validate` already bounds `expiresAtUtc ≤ issuedAtUtc + 24 h`). If storing fails on row 6 the result is kept in memory like row 13 (published, identical payload on redelivery — safe because the `started` row makes every later answer `unknown`). If storing fails on rows 8–10 nothing is published and the message is not acknowledged, so it is decided again when the broker redelivers it on the next session after storage works (the Cloud needs its own timeout for unanswered commands — wave 3) (user decision 2026-10-18, review: an unstored published rejection would be forgotten by a restart). Known limitation (user decision): `issuedAtUtc` has no upper bound, so a far-future command is remembered until its own expiry and such rows can fill the capacity; only the Cloud identity may publish commands (plan D-04, broker ACL in wave 4).
- Purge (hourly, skipped while `EdgeDatabase.IsClockRolledBack`): delete only rows with `result_sent = 1` whose `expires_utc_ms + 2 min` is in the past. **A row whose command could still be fresh is never deleted.**
- Capacity: at most 100,000 rows. When full, purge runs first; if still full, the new command is answered `failed`/`edge_storage_error` without a PLC write (row 11).

**In-memory state** (process lifetime): in-flight command ids; results whose PLC write finished but whose `completed` row could not be stored (keyed by command id, removed once stored).

**Decision order** (first match wins; `completedAtUtc` = `TimeProvider.GetUtcNow()`; result `edgeId` = own id):
| # | Check | Outcome | Reason | PLC write? | Logged? |
|---|---|---|---|---|---|
| 1 | payload > 16 KiB or not a valid `DeviceCommand` | — (no result; warning without payload; message acknowledged) | — | no | no |
| 2 | `command.EdgeId` ≠ own `EdgeId` (ordinal) | `rejected` | `edge_mismatch` | no | no |
| 3 | log row with stored result | **that stored result** (re-published; `result_sent` reset to 0 until PUBACK) | | no | existing row |
| 4 | command id in the in-memory results | **that result** (re-published); retry storing `completed` | | no | existing row |
| 5 | command id in flight | wait for that execution, then as #3/#4 | | no | existing row |
| 6 | log row `started`, not in flight, no in-memory result (process restarted during a write) | `unknown` | `write_outcome_unknown` | no | row → `completed` |
| 7 | `EdgeDatabase.IsClockRolledBack` | `rejected` | `clock_skew` | no | no |
| 8 | `CheckFreshness` = `Expired` | `expired` | null | no | yes |
| 9 | `CheckFreshness` = `ClockSkew` | `rejected` | `clock_skew` | no | yes |
| 10 | `EvaluateWrite` denied | `rejected` | the catalog reason | no | yes |
| 11 | log full after purge | `failed` | `edge_storage_error` | **no** | no |
| 12 | insert row `started` fails (SQLite error) | `failed` | `edge_storage_error` | **no** | no |
| 13 | `started` committed → `IPlcDriver.WriteAsync(…, CancellationToken.None)` | see mapping | | yes | row → `completed` |

**Write mapping** (most specific type first: `PlcBusyException` is checked before its base `PlcUnavailableException`): success → `applied`/null; `PlcBusyException` → `failed`/`plc_busy`; `PlcUnavailableException` → `failed`/`plc_unavailable`; `PlcAccessDeniedException` → `failed`/`plc_access_denied`; `PlcWriteOutcomeUnknownException` **or any other exception** → `unknown`/`write_outcome_unknown`. No retry in any case.

**Processing**
- `CommandListener` registers its handler via `MqttConnection.RegisterSubscription` during start, before the connection starts (task-04 contract). The executor serializes all commands with its own `SemaphoreSlim(1,1)` regardless of how many MQTT handler invocations are active.
- After row 13: store `completed` (`result_sent = 0`), then publish; after a successful PUBACK `result_sent = 1`. If storing `completed` fails, the result goes to the in-memory results, is still published, an error is logged, storing is retried with the unsent-result replay, and **the message is not acknowledged** until the row is stored.
- Unsent results (log rows and in-memory results) are re-published oldest first with identical payloads after every reconnect and at least every 30 s while connected.
- Acknowledgement: after the outcome is durably stored (rows 3, 6, 8–10, 13), or after the result publish was attempted for results that are deliberately not stored (rows 1, 2, 7, 11, 12); an exception escaping the executor → no acknowledgement.
- **Not remembered (plan D-06):** results of rows 2, 7, 11, 12 are not stored; the Cloud must use a **new** `commandId` to retry such a command (requirement for wave 3).
- Logs: command id, tag id, outcome, reason — never `targetValue` at Information level or above, never `issuedBy`.

## Steps
1. Add `edge_storage_error` to `CommandReasons` and its test.
2. `CommandLog` (schema, insert-started, complete, find, unsent, mark-sent, purge, capacity).
3. `CommandExecutor` per the table, unit-tested with `FakePlcDriver` + `FakeTimeProvider` + temp DB (SQLite failures injected by holding an exclusive lock from a second connection; capacity tested with a small internal limit).
4. `CommandListener` (hosted service); register in `AddEdgeAgent`; add defaults to `EdgeTestHost`.
5. End-to-end tests through `TestMqttBroker`.

## Acceptance
- AC-07: one test row per decision line 1, 2, 7, 8, 9, 10, 11, 12 and every write-mapping line (incl. `PlcBusyException` → `plc_busy`) asserts outcome, reason and whether `WriteAsync` was called.
- AC-08: the same command delivered twice → one write, identical results; a row left `started` (restart simulated, not in flight, no in-memory result) + redelivery → `unknown`/`write_outcome_unknown`, no write; a redelivery while the first execution is gated → waits, one write, identical results; a stored `applied` result + clock rollback + redelivery → the stored `applied` result, no write; `completed` storing fails after the write → redelivery in the same process gets the identical `applied` result and the first message is not acknowledged until storing succeeds.
- Capacity: with the limit reached and only unexpired rows, a new command → `failed`/`edge_storage_error`, no write, no row deleted; an `issuedAtUtc`/`expiresAtUtc` far in the future is stored with its own (unclamped) `expires_utc_ms`.
- AC-09 (part): a command published while the Edge is offline is executed and answered after the Edge starts.
- AC-10: broker stopped after the write but before the result publish → after restart a result with the identical payload arrives (≥ 1 delivery, all identical).
- End-to-end: a command published by a test client yields one `applied` result and one `FakePlcDriver` write with the normalized CLR value.
- `CommandReasonsTests.All_ExactSetUniqueSnakeCase` expects twelve codes including `edge_storage_error`.

## Dependencies
- task-04-mqtt-telemetry-publisher.md

## Verification Plan
- Command: `dotnet test IndustrialIot.sln`
- Named probe: `CommandExecutorTests.DecisionOrder_ReasonCodes`, `CommandExecutorTests.WriteExceptions_MapToOutcomes`, `CommandExecutorTests.Redelivery_RepublishesStoredResult_NoSecondWrite`, `CommandExecutorTests.StartedWithoutResult_IsUnknown_NoWrite`, `CommandExecutorTests.ConcurrentRedelivery_WaitsForInFlight`, `CommandExecutorTests.ClockRollback_RepublishesStoredResult_RejectsNew`, `CommandExecutorTests.CompletedStoreFailure_SameResultOnRedelivery`, `CommandExecutorTests.StorageFailure_NoWrite_EdgeStorageError`, `CommandExecutorTests.LogFull_RejectsWithoutEvictingFreshRows`, `CommandFlowTests.Command_EndToEnd_Applied`, `CommandFlowTests.OfflineQueuedCommand_AnsweredAfterStart`, `CommandFlowTests.ResultPublishFailure_RepublishedAfterReconnect`, `CommandReasonsTests.All_ExactSetUniqueSnakeCase`; all earlier probes still pass.
- Reachability: source — .NET 10 SDK + loopback TLS broker; listener resolved through `EdgeTestHost`.
- Oracle: exit 0, 0 failed across the solution; thirteen named probes pass.
- Counterexample: emitting a fresh `duplicate` instead of the stored result, checking the clock before the stored result, writing the `started` row after the PLC write, re-executing a `started` command, writing to the PLC when `started` could not be stored, evicting unexpired rows at capacity, mapping `PlcBusyException` to `plc_unavailable`, or mapping an unexpected exception to `failed` must fail the named probes.
- Artifacts: ephemeral.

## Failure Protocol
On a failed Step or Verification Plan run: stop; do not widen scope, change the Command, or weaken a test; record observed versus expected; repair only the cited cause; after three failed rounds, stop and ask the user.

## Receipt

Verification: PASS
Command: dotnet test IndustrialIot.sln
Exit: 0
Base: d50cbfdf5a122f42e9cba23409b986e85969ba2e
Head: cac6486ab1f0a0737791256f2db1a44fe37c9f421334abd069b0ccd83f787068
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

Passed!  - Failed:     0, Passed:    47, Skipped:     0, Total:    47, Duration: 190 ms - Shared.Tests.dll (net10.0)

Passed!  - Failed:     0, Passed:   279, Skipped:     0, Total:   279, Duration: 14 s - Edge.Tests.dll (net10.0)
```
- Pre-change run: exit 0 with 0 `CommandExecutorTests`/`CommandFlowTests` listed → oracle (named probes) not met, as expected.
- Named probes present and passing: `DecisionOrder_ReasonCodes`, `WriteExceptions_MapToOutcomes`, `Redelivery_RepublishesStoredResult_NoSecondWrite`, `StartedWithoutResult_IsUnknown_NoWrite`, `ConcurrentRedelivery_WaitsForInFlight`, `ClockRollback_RepublishesStoredResult_RejectsNew`, `CompletedStoreFailure_SameResultOnRedelivery`, `StorageFailure_NoWrite_EdgeStorageError`, `LogFull_RejectsWithoutEvictingFreshRows`, `CommandFlowTests.Command_EndToEnd_Applied`, `CommandFlowTests.OfflineQueuedCommand_AnsweredAfterStart`, `CommandFlowTests.ResultPublishFailure_RepublishedAfterReconnect`, `CommandReasonsTests.All_ExactSetUniqueSnakeCase` (twelve codes); plus `UnstoredRejection_NotPublished_DecidedAgain`, `UnstoredUnknown_PublishedAndRemembered`, `Purge_OnlyExpiredSentRows_WatermarkFirst`. Command tests repeated 3 times after the last change, 15/15 each.
- Counterexamples (mutations in a scratch copy), each caught: fresh `duplicate` instead of the stored result; clock-rollback check before the known result; `started` row written after the PLC write (`StorageFailure_NoWrite_EdgeStorageError`); re-executing a `started` command; writing when `started` could not be stored; evicting fresh rows at capacity; `PlcBusyException` → `plc_unavailable`; unexpected exception → `failed`; clamped expiry; purge without the 2-min tolerance; purge during a rollback; watermark persisted after the delete; publishing an unstored rejection; forgetting an unstored `unknown`.
- Storage failures are simulated with SQLite triggers (`RAISE(ABORT)`), immediate and deterministic.
- Ownership deviation: one line in `tests/Edge.Tests/TelemetryPublisherTests.cs` (task-04's file) removes `CommandListener` from those hosts, because they subscribe to the commands topic themselves and `MqttConnection` allows one handler per topic; test-only, no behaviour change.
- Repair rounds: R1 the end-to-end test published before the Edge had subscribed; R2 review FAIL (rows 6/8–10 published but not remembered when storing failed; clamped expiry vs never deleting a possibly-fresh row — user: remember until the command's own expiry; purge rules untested) → fixed + probes; R3 re-review PASS_WITH_WARNINGS → user decisions: an unstored rejection is not published (decided again on redelivery), unbounded `issuedAtUtc` accepted as a limitation relying on the wave-4 ACL; spec Acceptance/Contracts aligned. Final re-review: code-auditor PASS (0 Critical/High/Medium).
- Known limitations: results not stored (`edge_mismatch`, rollback `clock_skew`, `edge_storage_error`) are not remembered — the Cloud must retry with a new `commandId`; a restart between a PLC write and storing `completed` turns the redelivery into `unknown`; an unacknowledged command is redelivered only on the next MQTT session (the Cloud needs its own timeout — wave 3); far-future `issuedAtUtc` rows live until their own expiry and can fill the 100,000-row capacity (wave-4 ACL); orphaned `started` rows whose session expired are never answered or purged; rows 8–10 are not capacity-checked; in-memory results grow while storage keeps failing; the executor logs "Rejected" before knowing whether the rejection could be stored; no live PLC or Mosquitto proof.
