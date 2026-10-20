# Task 03 — The Edge deletes only Cloud-confirmed samples

Status: done

## Outcome
The Edge keeps every sample in SQLite until a `TelemetryAck` for its own edgeId and current stream covers it, never has more than `MaxUnacknowledged` sent-but-unconfirmed rows, and resends from the oldest queued row after a backed-off ack timeout or a reconnect (plan D-03).

## Scope
- In: publisher cursor, window, timeout backoff and reconnect reset; ack subscription via `MqttConnection.RegisterSubscription` (`src/Edge/Messaging/MqttConnection.cs:67`); a connection-generation counter on `MqttConnection` (today only `IsConnected`/`WaitForConnectionAsync`, `:61,87`); range delete and exact count in the store; two `Edge:Mqtt` options; updating every test that encodes delete-after-PUBACK.
- Out: persisting the cursor across restarts (a restart resends from the oldest row); purge rules (`src/Edge/Buffering/TelemetryStore.cs:195-227`); PLC Monitor changes (its `bufferPending` becomes exact through `CountAsync`).

## Coverage
- CP-03

## Ownership
- Modify: `src/Edge/Messaging/TelemetryPublisher.cs`
- Modify: `src/Edge/Messaging/MqttConnection.cs`
- Modify: `src/Edge/Messaging/MqttOptions.cs`
- Modify: `src/Edge/Buffering/TelemetryStore.cs` (also `OldestSequenceAsync` for the head check)
- Modify: `tests/Edge.Tests/TelemetryPublisherTests.cs`
- Modify: `tests/Edge.Tests/TelemetryStoreTests.cs`
- Read: `tests/Edge.Tests/Fakes/TestMqttBroker.cs` (shared with Cloud.Tests through a link — not modified), `src/Edge/Messaging/MqttTopics.cs`

## Steps
1. `MqttOptions`: `MaxUnacknowledged` (default 500, valid 1..5000), `AckTimeoutSeconds` (default 30, valid 5..600); validator messages name the key only.
2. `MqttConnection`: `ConnectionGeneration` (incremented on every successful connect).
3. `TelemetryStore`: `ReadBatchAfterAsync(long afterSequence, int max)`; `CountUpToAsync(long sequence)` (rows with `sequence ≤ n`); `AcknowledgeRangeAsync(Guid streamId, long from, long to)` returning the number of deleted rows (0 when `streamId` ≠ current stream); remove `AcknowledgeAsync` (`:168-176`); `CountAsync` becomes `SELECT COUNT(*)` so acked gaps cannot over-count (affects `TrimAfterAppendAsync` `:119-126` and the Monitor).
4. `TelemetryPublisher` (update the class comment `:6-9`): register the ack handler before host start; ignore acks with another edgeId/streamId; on a valid ack call `AcknowledgeRangeAsync`; catch and log store errors inside the handler so the MQTT message is still acknowledged. Loop: publish rows after the cursor while `CountUpToAsync(cursor) < MaxUnacknowledged`; PUBACK advances only the cursor. Reset the cursor to "before the oldest queued row" (before any I/O, so a failing read cannot skip the reset) when the generation changed or no progress (the smallest queued sequence advanced past the head recorded when the timer started) for the current timeout; timeout starts at `AckTimeoutSeconds`, doubles per reset up to 600 s, returns to the start on progress; log each reset with counts only.
5. Tests (`TelemetryPublisherTests`; ack helper = publish a `TelemetryAck` to the Edge's ack topic through the existing `TestMqttBroker` in the test file): adjust `Drains_InSequenceOrder_DeletesAfterPuback` and `BrokerOutage_BuffersThenDrainsInOrder` (`:21-68`) to the new contract (rows stay after PUBACK, removed after acks; reconnect resends the unacked head) and add: window stop at `MaxUnacknowledged = 5`; timeout resend from oldest with doubling (FakeTimeProvider: resets at 5 s, +10 s, +20 s; back to 5 s after progress); duplicate ack after resend; purge of unacked rows mid-window does not stall; ack for a foreign edgeId/streamId deletes nothing; gap ack deletes only its range; store failure in the ack handler is logged and a later ack still deletes; acks for later samples keep arriving while the oldest is never acked → the oldest is resent after the timeout; invalid option values fail at start. `TelemetryStoreTests`: replace the `AcknowledgeAsync` tests (`:165-172`) with `AcknowledgeRangeAsync` / `CountUpToAsync` / exact `CountAsync` cases.

## Acceptance
- AC-05: after PUBACK without ack the row count is unchanged; after an ack for [a,b] exactly those rows are gone; foreign-edge/stream acks delete nothing; a failing delete is logged and the next ack still works.
- AC-06: with `MaxUnacknowledged = 5` and no acks exactly 5 publishes reach the broker; resets happen at 5 s, 15 s, 35 s after the first send, each restarting at the oldest row; after progress the next reset is 5 s later; acks for later rows that leave the oldest row unconfirmed do not count as progress; after a broker restart publishing restarts at the oldest queued row.

## Dependencies
- task-01-telemetry-ack-contract-acl.md

## Verification Plan
- Command: `dotnet test tests/Edge.Tests --filter "FullyQualifiedName~TelemetryPublisherTests|FullyQualifiedName~TelemetryStoreTests|FullyQualifiedName~EdgeHostTests|FullyQualifiedName~MonitorTests"`
- Named probe: `TelemetryPublisherTests` (Step 5 cases), `TelemetryStoreTests` (range/count cases), `TelemetryPublisherTests.InvalidMqttConfig_FailsAtStart` (option validation, `tests/Edge.Tests/TelemetryPublisherTests.cs:189-191`), `EdgeHostTests` (regression), `MonitorTests` (regression of the buffer count).
- Reachability: source — real Edge host (`tests/Edge.Tests/Fakes/EdgeTestHost.cs`) against the in-process TLS `TestMqttBroker`.
- Oracle: `Failed: 0`, `Skipped: 0`, > 0 tests in each named class.
- Counterexample: deleting in the PUBACK path, a counter-based window, or a fixed (non-doubling) resend interval must fail the AC-05 / AC-06 cases.
- Artifacts: ephemeral.

## Failure Protocol
On a failed Step or Verification Plan run: stop; do not widen scope, change the Command, or weaken a test; record observed versus expected; repair only the cited cause; after three failed rounds, stop and ask the user.

## Receipt

Verification: PASS
Command: dotnet test tests/Edge.Tests --filter "FullyQualifiedName~TelemetryPublisherTests|FullyQualifiedName~TelemetryStoreTests|FullyQualifiedName~EdgeHostTests|FullyQualifiedName~MonitorTests"
Exit: 0
Base: 8f7e6c753cf52c4a9e65d36b996f95edd7ab5959
Head: 81fce74ae00b2584759319feff1fd0cb1d29d8a4d20ff22e2e97b305e57f1869
```text
$ dotnet test tests/Edge.Tests --filter "FullyQualifiedName~TelemetryPublisherTests|FullyQualifiedName~TelemetryStoreTests|FullyQualifiedName~EdgeHostTests|FullyQualifiedName~MonitorTests"
Test run for C:\Users\case 046\Documents\Project\PlcProtocol\tests\Edge.Tests\bin\Debug\net10.0\Edge.Tests.dll (.NETCoreApp,Version=v10.0)
Passed!  - Failed:     0, Passed:    56, Skipped:     0, Total:    56, Duration: 28 s - Edge.Tests.dll (net10.0)
```
- Pre-change run: exit 0 on the 43 existing tests; the AC-05/AC-06 named probes did not exist yet (oracle unmet).
- Full Edge.Tests at the same Head: 299 passed, 0 failed (regression); timing tests (AckTimeout_, UnconfirmedHead_, Window_Stops, PurgedUnconfirmed) repeated 3x green.
- Review: code-auditor FAIL (H-1: progress = any removal let one unconfirmed sample wait forever behind acked later ones until the MaxRows purge) -> user amended D-03 to head-advance -> fixed with M-1 (cursor reset before I/O) -> PASS; post-PASS Low-1 log/signal split re-reviewed PASS.
- Repair rounds: 1 (test expectations + store-failure injection: Microsoft.Data.Sqlite retries busy until CommandTimeout 30 s), 2 (H-1/M-1).
- Limitations: COUNT(*) on 1 000 000 rows measured 12 ms warm on the dev SSD only (target eMMC unmeasured); the reconnect-store-failure path of M-1 is fixed by construction without a dedicated test; ReadBatchAsync remains for tests only.
- Feature integration found UnconfirmedHead_ResentWhileLaterSamplesAreAcked flaky under full-suite load (a missed append wake-up waits IdleWait on the fake clock). Test waits for sends now advance the fake clock; 5/5 green under parallel Cloud.Tests load. The product keeps the bounded race (real clock: at most 1 s delay; pre-existing design).
- Final-Head rebind: fresh run at the feature's final Head.
