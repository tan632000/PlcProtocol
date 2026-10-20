# Task 05 — Operators issue commands that reach the Edge once, are audited, and end in a definite status

Status: done

## Outcome
`POST /api/v1/edges/{edgeId}/commands` stores and publishes exactly one `DeviceCommand` per request (or per user `Idempotency-Key`, forever), records whether publishing is certain, uncertain or impossible, accepts the Edge's result whenever it arrives, marks unanswered commands `timed_out`, and records who issued every command.

## Scope
- In: command endpoints (endpoint module), `CommandService` (validate, create, publish, idempotency incl. concurrent duplicates), `CommandResultIngestor` (subscription `iiot/+/command-results`, same DB-failure handling as task-02), `CommandTimeoutSweeper`, audit logging.
- Out: automatic re-publish/retry (never), SignalR push (task-06), command history purge.

## Coverage
- CP-05

## Ownership
- Create: `src/Cloud/Commands/{CommandService.cs,CommandResultIngestor.cs,CommandTimeoutSweeper.cs,CommandStatus.cs}`, `src/Cloud/Api/CommandEndpoints.cs`
- Modify: `src/Cloud/CloudServiceCollectionExtensions.cs`
- Create: `tests/Cloud.Tests/{CommandApiTests.cs,CommandFlowTests.cs}`
- Read: `src/Shared/Contracts/{DeviceCommand.cs,CommandResult.cs}`, `src/Shared/Common/{CommandOutcome.cs,CommandReasons.cs,ContractJson.cs}`, `src/Cloud/Messaging/CloudMqttConnection.cs` (API defined by task-02), `src/Cloud/Ingestion/TelemetryIngestor.cs` (DB-failure pattern), `src/Cloud/Data/*`, `tests/Cloud.Tests/Infrastructure/*`

## Contracts
**`POST /api/v1/edges/{edgeId}/commands`** (Operator policy — role Operator and a valid `sub`), body `{"tagId":"Line1.Speed","value":1200,"ttlSeconds":60}` (`ttlSeconds` optional → `DefaultTtlSeconds`), optional header `Idempotency-Key` (1..128 printable ASCII).
1. `edgeId` not allow-listed → 404 `edge_not_found`. Build the `DeviceCommand` (new `commandId`, `issuedAtUtc` = now, `expiresAtUtc` = now + TTL, `issuedBy` = `sub`) and call `Validate()` **before any insert**; `ttlSeconds` outside 5..`MaxTtlSeconds`, value not `true`/`false`/number, invalid `tagId` → 400 `invalid_request`, nothing stored (tag/type/range checks stay on the Edge).
2. Idempotency (forever per user): a row with the same (`issued_by`, `idempotency_key`) → same body (`request_hash` = SHA-256 of edgeId, tagId, canonical value, ttl) → 200 with that command and its current status, **no publish**; different body → 409 `idempotency_conflict`. Concurrent duplicates: the insert's unique violation (`23505`) is caught, the existing row is read and the same 200/409 rule applies — never 500, never a second publish.
3. Insert the row `pending` and commit.
4. Publish (QoS 1) to `iiot/{edgeId}/commands` with `CloudMqttConnection.PublishAsync` (task-02 API):
   - `NotConnected` (nothing sent) → status `not_sent` (guarded `WHERE status='pending'`), 503 `broker_unavailable` with `commandId`; never published later.
   - `Unconfirmed` (timeout, failed reason code, connection lost meanwhile) → status `publish_unknown` (guarded), 503 `publish_unconfirmed` with `commandId` and `Location`: the command may have reached the Edge — clients must check its status before issuing a new command.
   - `Confirmed` → status `published` (guarded `WHERE status='pending'`, so an earlier Edge result is never overwritten), 202 `{"commandId","status","expiresAtUtc"}` with `Location: /api/v1/commands/{id}` (the body shows the current status, which may already be an Edge outcome).
- Audit: one Information log per command with `commandId`, `edgeId`, `tagId`, `issuedBy`, outcome — never the token or the value.

**`GET /api/v1/commands/{commandId}`** (Viewer) → `{"commandId","edgeId","tagId","value","status","reason","issuedBy","issuedAtUtc","expiresAtUtc","completedAtUtc"}`; unknown → 404. **`GET /api/v1/edges/{edgeId}/commands?limit=50`** (Viewer, 1..500) newest first.

**Statuses**: non-final = `pending`, `published`, `publish_unknown`, `timed_out`; final = `not_sent` and the Edge outcomes `applied`, `rejected`, `expired`, `duplicate`, `failed`, `unknown`. An Edge outcome is accepted from any non-final status and is final; later results with a different outcome are ignored and logged.

**`CommandResultIngestor`** (`iiot/+/command-results`): topic edgeId allow-listed, valid `CommandResult`, payload `edgeId` == topic edgeId, `commandId` exists for that edge — otherwise drop + warn + acknowledge. One `UPDATE ... WHERE command_id = @id AND status IN ('pending','published','publish_unknown','timed_out')`; acknowledge after commit; database failure → retry in place, reconnect after 30 s (task-02 rule).

**`CommandTimeoutSweeper`**: every 10 s on the `TimeProvider`, rows in `pending`, `published` or `publish_unknown` with `expires_at_utc + TimeoutGraceSeconds < now` → `timed_out` (guarded by the same status list).

## Steps
1. Service + endpoint module + idempotency.
2. Result ingestor + sweeper; registration.
3. API tests (publish observed at the test broker; `RejectPublish` for a failed PUBACK; an early result by attaching a test handler to the public `broker.Server.InterceptingPublishAsync` event that, for the command message, injects the `CommandResult` and waits until the Cloud has stored it before letting the publish (and its PUBACK) proceed — the linked `TestMqttBroker.cs` itself is not modified; concurrent idempotent requests released together by a barrier so they truly overlap) and end-to-end tests with a fake Edge client that subscribes to commands and answers results.

## Acceptance
- AC-06: Operator POST → 202, exactly one message on `iiot/{edgeId}/commands` with `issuedBy` = token `sub` and the requested TTL; Viewer → 403; invalid `ttlSeconds` → 400 and no row; same key twice (sequential and 2 concurrent requests) → same `commandId`, one published message; same key, different body → 409; broker down → 503 `broker_unavailable`, status `not_sent`, nothing published after the broker returns.
- AC-07: fake Edge answers `applied` → `applied`; a result arriving while the row is still `pending` (PUBACK delayed by the test interceptor) → `applied` and the later `published` update does not overwrite it; failed PUBACK (`RejectPublish`) → 503 `publish_unconfirmed`, status `publish_unknown`, and a subsequent Edge result → recorded; a second, different result → ignored; no answer → `timed_out` after expiry + grace (fake clock), also for rows left in `pending` (simulated crash before the status update) and in `publish_unknown`; a late `applied` after `timed_out` → `applied`; result with mismatching topic edgeId or unknown `commandId` → dropped.

## Dependencies
- task-04-read-api.md

## Verification Plan
- Command: `dotnet test IndustrialIot.sln`
- Named probe: `CommandApiTests.Post_PublishesOnce_Audited`, `CommandApiTests.Post_RequiresOperator`, `CommandApiTests.InvalidInput_NothingStored`, `CommandApiTests.IdempotencyKey_SequentialAndConcurrent`, `CommandApiTests.BrokerDown_NotSent_NeverPublished`, `CommandApiTests.UnconfirmedPublish_PublishUnknown`, `CommandFlowTests.Result_UpdatesStatus_FirstWins`, `CommandFlowTests.EarlyResult_NotOverwritten`, `CommandFlowTests.NoResult_TimesOut_LateResultWins`, `CommandFlowTests.PendingOrUnknown_TimesOut`, `CommandFlowTests.SpoofedResult_Dropped`
- Reachability: source — .NET 10 SDK + Docker engine + loopback TLS broker.
- Oracle: exit 0, 0 failed and `Skipped: 0` across the solution; the eleven probes listed by `--list-tests` and passing.
- Counterexample: re-publishing on idempotent replay, a 500 on concurrent duplicates, re-publishing `not_sent`, reporting a failed PUBACK as `not_sent`, dropping a result that arrives before `published`, or letting a second result overwrite the first must fail the named probes.
- Artifacts: ephemeral.

## Failure Protocol
On a failed Step or Verification Plan run: stop; do not widen scope, change the Command, or weaken a test; record observed versus expected; repair only the cited cause; after three failed rounds, stop and ask the user.

## Receipt

Verification: PASS
Command: dotnet test IndustrialIot.sln
Exit: 0
Base: 8f23ff1bb1d7b0e5c78cd79ee7ff9fed2bbdcb72
Head: d730a852bb88d7d264db387157ae29a6936d29560533e4d389c77eebb0d14d96
```text
$ dotnet test IndustrialIot.sln
Exit: 0
Passed!  - Failed:     0, Passed:    47, Skipped:     0, Total:    47, Duration: 519 ms - Shared.Tests.dll (net10.0)
Passed!  - Failed:     0, Passed:   279, Skipped:     0, Total:   279, Duration: 15 s - Edge.Tests.dll (net10.0)
Passed!  - Failed:     0, Passed:    80, Skipped:     0, Total:    80, Duration: 49 s - Cloud.Tests.dll (net10.0)
```
- Named probes (`dotnet test tests/Cloud.Tests --list-tests`): `CommandApiTests.Post_PublishesOnce_Audited`, `Post_RequiresOperator`, `InvalidInput_NothingStored` (12 rows), `IdempotencyKey_SequentialAndConcurrent`, `BrokerDown_NotSent_NeverPublished`, `UnconfirmedPublish_PublishUnknown`; `CommandFlowTests.Result_UpdatesStatus_FirstWins`, `EarlyResult_NotOverwritten`, `NoResult_TimesOut_LateResultWins`, `PendingOrUnknown_TimesOut`, `SpoofedResult_Dropped`; plus `CommandApiTests.FloatToken_PublishedAsWritten` and `DatabaseFailureAfterPublish_StillAnswers`; `Skipped: 0` in every assembly. Output above is the summary lines of the final run.
- Pre-change run: exit 0 with 0 command tests listed → oracle not met, as expected.
- How the races are forced: concurrent duplicates — a `DbCommandInterceptor` holds `INSERT INTO commands` until both requests reach it (both lookups have passed); early result — a handler on the broker's `InterceptingPublishAsync` injects the result and holds the PUBACK until the row is `applied`; timeouts — `FakeTimeProvider` with one sweep per advance (`WaitsArmed`). The "fake Edge" for end-to-end answers is a real MQTTnet client (`CommandTestKit.FakeEdge`); spoofed/late results are injected through the broker.
- Counterexamples (mutations in a scratch copy), each caught: replay re-publishes, unique-violation catch disabled → `IdempotencyKey_SequentialAndConcurrent`; `NotConnected` left `pending` → `BrokerDown_NotSent_NeverPublished`; failed PUBACK recorded as `not_sent` → `UnconfirmedPublish_PublishUnknown`; publish update unguarded, `pending` not accepting results → `EarlyResult_NotOverwritten`; result update without status guard → `Result_UpdatesStatus_FirstWins`; `timed_out` treated as final, grace ignored → `NoResult_TimesOut_LateResultWins`; sweeper on `published` only → `PendingOrUnknown_TimesOut`; result accepted from any edge, payload edgeId trusted → `SpoofedResult_Dropped`; `Validate()` skipped, TTL upper bound removed → `InvalidInput_NothingStored`; value normalized again → `FloatToken_PublishedAsWritten`; post-publish catch removed → `DatabaseFailureAfterPublish_StillAnswers`.
- Repair rounds: R1 review FAIL — High: the value was normalized (`1500.0` → `1500`) in the published command, defeating the Edge's integer-token rejection accepted as R-06 in edge-plc-driver → value published and stored as written, idempotency hash on the raw token (`1200` vs `1200.0` under one key → 409); Medium: a database error after a successful publish returned 500 without audit or commandId → caught, audited, answered per publish outcome; Lows: insert not cancellable, allow-list checked before header/body. Final re-review: code-auditor PASS (0 Critical/High/Medium).
- Known limitations: if recording the publish outcome fails, the response status comes from memory while the row may stay `pending`; the post-publish catch includes `InvalidOperationException`; jsonb normalizes stored numbers (`1e2` is shown as `100` by GET while `1e2` was published); a value token jsonb cannot hold (e.g. `1e-20000`) fails the insert → 500, nothing stored or published; replay of a `not_sent` command answers 200 with status `not_sent` (key stays bound); `TimeoutGraceSeconds` may be 0 (a slow publish can then answer 202 with `timed_out`); only the failed-PUBACK path of `publish_unknown` is probed (PUBACK timeout and mid-publish disconnect are not); `FloatToken_PublishedAsWritten` does not assert the 202 status.
