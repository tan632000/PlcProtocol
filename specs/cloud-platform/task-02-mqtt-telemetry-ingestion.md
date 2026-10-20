# Task 02 — Telemetry from allow-listed Edges is stored once, latest values are kept, and old rows are purged

Status: done

## Outcome
`CloudMqttConnection` holds one persistent TLS MQTT 5 session; `TelemetryIngestor` stores every valid telemetry message exactly once per (`edgeId`,`streamId`,`sequence`), keeps the newest value per tag, drops spoofed, unknown-edge or far-future messages, survives database failures without losing messages, and `RetentionJob` deletes telemetry received more than 90 days ago.

## Scope
- In: `CloudMqttOptions` + validator, `CloudMqttConnection` (edge-agent D-03/D-07 rules: TLS mandatory, strict certificate validation incl. `CustomRootTrust` + serverAuth EKU, back-off, persistent session, manual ack, handlers registered before connect, topic-filter dispatch with `+`), DB-failure retry/reconnect, telemetry ingestion, retention job, readiness MQTT check.
- Out: command results (task-05), SignalR push (task-06).

## Coverage
- CP-02, CP-01 (MQTT config validation)

## Ownership
- Create: `src/Cloud/Messaging/{CloudMqttOptions.cs,CloudMqttConnection.cs,MqttTopicFilter.cs}`, `src/Cloud/Ingestion/{TelemetryIngestor.cs,RetentionJob.cs}`
- Modify: `src/Cloud/CloudServiceCollectionExtensions.cs`, `src/Cloud/appsettings.json` (`Cloud:Mqtt` without password), `src/Cloud/IndustrialIot.Cloud.csproj` (`MQTTnet` 5.2.0.1603), `src/Cloud/Health/ReadinessMonitor.cs` (MQTT check)
- Modify: `tests/Cloud.Tests/Cloud.Tests.csproj` (`MQTTnet.Server` 5.2.0.1603; link `..\Edge.Tests\Fakes\TestMqttBroker.cs` as a compiled file), `tests/Cloud.Tests/Infrastructure/CloudTestHost.cs` (MQTT defaults)
- Create: `tests/Cloud.Tests/TelemetryIngestionTests.cs`
- Read: `src/Edge/Messaging/MqttConnection.cs` (reference implementation), `src/Shared/Contracts/TelemetryPayload.cs`

## Contracts
- Options `Cloud:Mqtt`: `Host`, `Port` = 8883, `Username`, `Password` (env `Cloud__Mqtt__Password`), `CaCertificatePath`, `ClientId` = `iiot-cloud` (`[A-Za-z0-9_-]{1,64}`), `KeepAliveSeconds`, `ReconnectMinDelayMs`, `ReconnectMaxDelayMs` — same ranges and secrecy rules as `Edge:Mqtt`; invalid → `OptionsValidationException` naming the key, never the value.
- **`CloudMqttConnection` API** (used by this task and task-05): `RegisterSubscription(string topicFilter, Func<MqttApplicationMessage, CancellationToken, Task> handler)` — only before the host has started; the session's subscriptions are exactly the registered filters (QoS 1, `DoNotSendOnSubscribe`), renewed after every connect; `Task<PublishOutcome> PublishAsync(string topic, string payload, CancellationToken ct)` with `PublishOutcome` = `NotConnected` (nothing was sent), `Unconfirmed` (sent or possibly sent, no success PUBACK within the timeout) or `Confirmed`; `Task ForceReconnectAsync()` (drops the session connection so the broker redelivers unacknowledged messages on reconnect); `IsConnected`.
- This task registers `iiot/+/telemetry`; task-05 registers `iiot/+/command-results`. `CleanStart=false`, session expiry 24 h; `MaximumPacketSize` 16 KiB; one message processed at a time.
- **Ingestion** per message:
  1. Topic `iiot/{edgeId}/telemetry` with `edgeId` allow-listed (ordinal); payload ≤ 16 KiB and a valid `TelemetryPayload`; payload `edgeId` == topic `edgeId`; `timestampUtc` ≤ received + 2 min. Violation → drop, warning with topic and reason only (no payload), **acknowledge**.
  2. One transaction: `INSERT ... ON CONFLICT (edge_id, stream_id, sequence) DO NOTHING`; if inserted, upsert `latest_values` only when `timestamp_utc` is later than the stored one; `received_utc` = `TimeProvider` now.
  3. Commit → acknowledge.
- **Database failure** (any `DbException`/EF exception): retry the same message with back-off (0.5 s doubling to 5 s); after 30 s of failures, give up on it without acknowledging and call `ForceReconnectAsync` so the broker redelivers it (back-off of the connection applies); other messages wait (sequential). Never acknowledged unless committed. The back-off delays and the 30 s budget run on the injected `TimeProvider` (tests use a fake clock). The same helper is reused by task-05's result ingestor.
- `RetentionJob`: hourly on the `TimeProvider`; deletes `telemetry` with `received_utc` older than now − `TelemetryDays` in batches of 10,000 per transaction (uses the `received_utc` index); `latest_values` untouched.
- Readiness: `/health/ready` also requires a connected MQTT session (cached).

## Steps
1. Options, filter matcher, `CloudMqttConnection`, registration (connect after `ApplicationStarted`).
2. `TelemetryIngestor` incl. retry/reconnect; `RetentionJob`.
3. Tests through the linked `TestMqttBroker`, publishing as an Edge client, with `DatabaseFaultInterceptor` for outages.

## Acceptance
- AC-02: 3 samples published twice → 3 rows; an older-timestamp sample leaves `latest_values` unchanged; payload `edgeId` ≠ topic, unknown edge, malformed JSON, > 16 KiB, timestamp 10 min ahead → 0 rows, acknowledged (not redelivered after a reconnect); interceptor on for 2 s then off → the message is stored without restarting the broker; interceptor on for > 30 s (fake back-off clock) → the Cloud reconnects and the redelivered message is stored once the interceptor is off.
- AC-03: rows received more than 90 days ago (fake clock) are deleted by one retention run; newer rows and `latest_values` stay.
- AC-01 (part): missing `Cloud:Mqtt:Host`, `Port` 0, `ClientId` `a b` → start fails naming the key.
- `/health/ready` is 503 while the broker is down and 200 after reconnect plus one refresh period (fake clock).

## Dependencies
- task-01-cloud-host-database.md

## Verification Plan
- Command: `dotnet test IndustrialIot.sln`
- Named probe: `TelemetryIngestionTests.Duplicates_StoredOnce`, `TelemetryIngestionTests.LatestValue_OnlyNewer`, `TelemetryIngestionTests.Spoofed_Invalid_FutureDropped`, `TelemetryIngestionTests.DatabaseFailure_RetriedInPlace`, `TelemetryIngestionTests.DatabaseFailure_ReconnectRedelivers`, `TelemetryIngestionTests.Retention_DeletesOldRows`, `TelemetryIngestionTests.Readiness_TracksMqtt`, `TelemetryIngestionTests.InvalidMqttConfig_FailsAtStart`
- Reachability: source — .NET 10 SDK + Docker engine + loopback TLS broker.
- Oracle: exit 0, 0 failed and `Skipped: 0` across the solution; the eight probes listed by `--list-tests` and passing.
- Counterexample: a plain `INSERT`, trusting the payload edgeId over the topic, acknowledging before commit, or waiting for "the next session" without forcing a reconnect must fail the named probes.
- Artifacts: ephemeral.

## Failure Protocol
On a failed Step or Verification Plan run: stop; do not widen scope, change the Command, or weaken a test; record observed versus expected; repair only the cited cause; after three failed rounds, stop and ask the user.

## Receipt

Verification: PASS
Command: dotnet test IndustrialIot.sln
Exit: 0
Base: 8f23ff1bb1d7b0e5c78cd79ee7ff9fed2bbdcb72
Head: 104c85b1e3660907a65b4ae3095ae9d31981d623e619f1ac006de10ae29a111e
```text
$ dotnet test IndustrialIot.sln
Exit: 0
Passed!  - Failed:     0, Passed:    47, Skipped:     0, Total:    47, Duration: 474 ms - Shared.Tests.dll (net10.0)
Passed!  - Failed:     0, Passed:   279, Skipped:     0, Total:   279, Duration: 14 s - Edge.Tests.dll (net10.0)
Passed!  - Failed:     0, Passed:    23, Skipped:     0, Total:    23, Duration: 16 s - Cloud.Tests.dll (net10.0)
```
- Named probes (`dotnet test tests/Cloud.Tests --list-tests`): `TelemetryIngestionTests.Duplicates_StoredOnce`, `LatestValue_OnlyNewer`, `Spoofed_Invalid_FutureDropped`, `DatabaseFailure_RetriedInPlace`, `DatabaseFailure_ReconnectRedelivers`, `Retention_DeletesOldRows`, `Readiness_TracksMqtt`, `InvalidMqttConfig_FailsAtStart` (4 rows: Host "", Port 0, ClientId `a b`, Password ""), plus the added `ExtremeNumber_StoredNormalized`; `Skipped: 0` in every assembly. Output above is the summary lines of the final run (build lines omitted).
- Pre-change run: exit 0 with 0 `TelemetryIngestionTests` listed → oracle not met, as expected.
- Counterexamples (mutations in a scratch copy), each caught by the named probe: plain `INSERT` → `Duplicates_StoredOnce`; payload edgeId trusted over the topic → `Spoofed_Invalid_FutureDropped`; future check disabled → same; dropped messages left unacknowledged → same; latest-value `WHERE` removed → `LatestValue_OnlyNewer`; acknowledge before the handler → `DatabaseFailure_ReconnectRedelivers`; no `ForceReconnectAsync` (waiting for the next session) → same; readiness ignoring MQTT → `Readiness_TracksMqtt`; retention cutoff broken → `Retention_DeletesOldRows`; MQTT validator unregistered → `InvalidMqttConfig_FailsAtStart`; raw value text stored → `ExtremeNumber_StoredNormalized`.
- Reachability: TLS test broker (`TestMqttBroker`, linked from Edge.Tests) per host + Testcontainers `postgres:17-alpine`; Docker 27.5.1.
- Repair rounds: R1 review FAIL — High: a contract-valid number such as `1e-20000` cannot be stored in jsonb, so the message retried, forced a reconnect and was redelivered forever, blocking all sequential ingestion (confirmed at runtime by the new probe timing out) → value re-written from the accepted double/bool, retry limited to `DbException`/`DbUpdateException`/`TimeoutException`; Medium: a stale handler could keep retrying on a dead connection and force-reconnect the new session → per-connection cancellation scope; Medium: tautological wait in `Readiness_TracksMqtt` → steps whole refresh periods; Lows: forced reconnect now always applies back-off, cancelled handlers logged at Information, retention margin 2 days. The pre-existing task-01 probe `Readiness_UsesCachedState` failed once during implementation (first refresh raced the first MQTT connect) → monitor waits for the first connection attempt. Final re-review: code-auditor PASS (0 Critical/High/Medium).
- Known limitations: deterministic database errors are not classified (a future input PostgreSQL rejects would loop like the fixed case; changing this touches D-06); packets near or above 16 KiB are discarded by the broker without a Cloud log, so the ingestor's size check is a second line only; values are stored as doubles (integers beyond 2^53 lose precision, `1.0` stored as `1`); the first retention run happens 1 h after start; `Retention_DeletesOldRows` relies on the job reading the clock after `Advance` (true for FakeTimeProvider 10.x); a cancellation surfacing as `NpgsqlException` mid-command is logged at Error; the retry-budget assertion measures from host start, not from the first failure.
