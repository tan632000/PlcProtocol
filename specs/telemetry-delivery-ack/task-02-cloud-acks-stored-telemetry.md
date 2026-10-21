# Task 02 — The Cloud acks exactly the stored telemetry

Status: done

## Outcome
After the Cloud has inserted, found as duplicate, or rejected by a clock-independent contract rule a telemetry message whose payload edgeId matches its allow-listed topic Edge, it publishes one `TelemetryAck` (from = to) for that sequence to `iiot/{topic edgeId}/telemetry-acks`; every other drop and every store failure produces no ack (plan D-02, D-05).

## Scope
- In: refactor `Validate` so the handler can tell *why* a message was dropped and whether its streamId/sequence parsed (today every reason collapses to `null`, `src/Cloud/Ingestion/TelemetryIngestor.cs:47-50,70-113`); publish the ack after the commit via the existing `connection` (`CloudMqttConnection.PublishAsync`, `src/Cloud/Messaging/CloudMqttConnection.cs:110`); a `NotConnected` or failed publish is logged with edge/stream/sequence only and ignored.
- Out: insert/dedupe SQL; ack batching; command results.

## Coverage
- CP-02

## Ownership
- Modify: `src/Cloud/Ingestion/TelemetryIngestor.cs`
- Create: `tests/Cloud.Tests/TelemetryAckTests.cs`
- Read: `tests/Cloud.Tests/Infrastructure/CloudTestHost.cs`, `tests/Cloud.Tests/Infrastructure/DatabaseFaultInterceptor.cs`, `tests/Cloud.Tests/TelemetryIngestionTests.cs`, `src/Shared/Contracts/TelemetryAck.cs`

## Steps
1. `Validate` returns a result that distinguishes: accepted sample; clock-independent contract rejection with parsed `streamId` + `sequence` (ack); and no-ack drops — unparseable, non-allow-listed topic Edge, payload/topic edgeId mismatch (`:96-99`), future timestamp (`:101-104`). Existing warning logs stay.
2. `HandleAsync`: after `stored` is true (inserted or duplicate) or for an ack-able rejection, build `TelemetryAck { EdgeId = topic edgeId, StreamId, FromSequence = ToSequence = sequence }` and publish to `iiot/{topic edgeId}/telemetry-acks`; never before the commit; never when the store failed (the message stays unacknowledged as today).
3. `TelemetryAckTests` (real Cloud via `CloudTestHost` + its `TestMqttBroker`; subscribe as the Edge to `iiot/<edge>/telemetry-acks`, and in the negative cases also to `iiot/+/telemetry-acks`): (a) sequences 1..250 → one ack per sequence, each within 1 s; (b) a duplicate resend is acked again; (c) a contract-invalid but parseable sample with matching edgeId (e.g. invalid tagId) is acked; (d) future timestamp → no ack; (e) non-allow-listed edge → no ack; (f) payload edgeId ≠ topic → no ack on any topic; (g) while the database is faulted (`DatabaseFaultInterceptor`) → no ack until it recovers and the sample is stored. Negative cases wait 2 s.

## Acceptance
- AC-03: cases (a), (b), (c) pass.
- AC-04: cases (d), (e), (f), (g) pass, and the existing `TelemetryIngestionTests` still pass unchanged.

## Dependencies
- task-01-telemetry-ack-contract-acl.md

## Verification Plan
- Command: `dotnet test tests/Cloud.Tests --filter "FullyQualifiedName~TelemetryAckTests|FullyQualifiedName~TelemetryIngestionTests"`
- Named probe: `TelemetryAckTests` cases a–g; `TelemetryIngestionTests` as regression.
- Reachability: source — real Cloud host `CloudTestHost : WebApplicationFactory<Program>` (`tests/Cloud.Tests/Infrastructure/CloudTestHost.cs:25`) on Testcontainers PostgreSQL; requires Docker.
- Oracle: `Failed: 0`, `Skipped: 0`, `TelemetryAckTests` contributes ≥ 7 passed tests.
- Counterexample: addressing the ack by payload edgeId (case f), acking the future-timestamp drop (case d), or publishing before commit (case g) must fail.
- Artifacts: ephemeral.

## Failure Protocol
On a failed Step or Verification Plan run: stop; do not widen scope, change the Command, or weaken a test; record observed versus expected; repair only the cited cause; after three failed rounds, stop and ask the user.

## Receipt

Verification: PASS
Command: dotnet test tests/Cloud.Tests --filter "FullyQualifiedName~TelemetryAckTests|FullyQualifiedName~TelemetryIngestionTests"
Exit: 0
Base: 8f7e6c753cf52c4a9e65d36b996f95edd7ab5959
Head: 81fce74ae00b2584759319feff1fd0cb1d29d8a4d20ff22e2e97b305e57f1869
```text
$ dotnet test tests/Cloud.Tests --filter "FullyQualifiedName~TelemetryAckTests|FullyQualifiedName~TelemetryIngestionTests"
Test run for C:\Users\case 046\Documents\Project\PlcProtocol\tests\Cloud.Tests\bin\Debug\net10.0\Cloud.Tests.dll (.NETCoreApp,Version=v10.0)
Passed!  - Failed:     0, Passed:    26, Skipped:     0, Total:    26, Duration: 23 s - Cloud.Tests.dll (net10.0)
```
- TelemetryAckTests contributes 14 listed tests (oracle: >= 7).
- Review: code-auditor FAIL -> 2 repair rounds (poison message from invalid UTF-8 and lone-surrogate escapes in the rejection parser; duplicate-property ambiguity; per-ack latency) -> PASS (0 critical/high/medium).
- Limitations: the pre-change failing run was skipped for this task (controller omission); the mutation run of the old catch on a disposable copy was denied by the permission system, so the poison-test oracle rests on review reasoning; the DB retry-budget/ForceReconnect branch has no ack-specific test (the throw precedes the ack).
- Beyond plan text, recorded in D-02: payloads with another schemaVersion or duplicated properties are never acked.
- Feature integration found a regression from this task's tests: Cloud.Tests AuthTests failed with 53300 too many clients (each CloudTestHost leaves an idle Npgsql pool on the shared container). Fixed in TelemetryAckTests (NpgsqlConnection.ClearAllPools after each host), re-reviewed PASS; full Cloud.Tests 112/112. Follow-up outside ownership: move the pool cleanup into CloudTestHost.
- Final-Head rebind: fresh run at the feature's final Head.
