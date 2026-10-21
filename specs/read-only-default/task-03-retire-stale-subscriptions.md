# Task 03 — Remove stale command subscriptions when commands are off

Status: done

## Outcome
An Edge or Cloud that runs with commands off retires its command filter: whatever is still delivered on it (queued in
its persistent session or in flight before the change) is acknowledged and discarded without executing or storing it,
and the filter is unsubscribed after every connect; nothing on it comes back after a reconnect and telemetry/acks keep
flowing (plan D-05).

## Scope
- In: a "retired filter" registration on both MQTT connections (registered before host start like subscriptions;
  a message whose topic matches a retired filter is acknowledged and dropped with one Information log per message
  "Discarded a message on retired topic '{Topic}'" (topic only, no payload); UNSUBSCRIBE sent after each successful connect and subscribe, result logged at Debug, never
  fatal); `CommandListener` (Edge) and `CommandResultIngestor` (Cloud) register their filter as retired when off.
- Out: any other topic; changing the session expiry.

## Coverage
- CP-03

## Ownership
- Modify: `src/Edge/Messaging/MqttConnection.cs`
- Modify: `src/Cloud/Messaging/CloudMqttConnection.cs`
- Modify: `src/Edge/Commands/CommandListener.cs`
- Modify: `src/Cloud/Commands/CommandResultIngestor.cs`
- Modify: `tests/Edge.Tests/CommandsSwitchTests.cs`
- Modify: `tests/Cloud.Tests/CommandsDisabledTests.cs`
- Read: `tests/Cloud.Tests/Infrastructure/CloudTestHost.cs`, `tests/Edge.Tests/Fakes/TestMqttBroker.cs`

## Steps
1. `MqttConnection.RetireSubscription(string topic)` (same registration window and duplicate rules as
   `RegisterSubscription`); in the message handler, a retired topic is acknowledged and dropped before the
   "no handler" branch (`src/Edge/Messaging/MqttConnection.cs:288-293`); after the subscribe step in `ConnectAsync`,
   UNSUBSCRIBE every retired topic.
2. `CloudMqttConnection.RetireSubscription(string topicFilter)` with the same behaviour (filter match with
   `MqttTopicFilter.Matches`, before `src/Cloud/Messaging/CloudMqttConnection.cs:362-366`).
3. `CommandListener` off → `connection.RetireSubscription(topics.Commands)`; `CommandResultIngestor` off →
   `connection.RetireSubscription(TopicFilter)`.
4. Edge test (one `TestMqttBroker`; persistent sessions survive host restarts there): run an Edge with commands on
   (the session gets the subscription), stop it; while it is down publish 30 commands to `iiot/test-edge/commands`
   (they queue in the session); start an Edge with commands off on the same broker; then publish telemetry acks for
   appended samples. Assert: no command was executed (fake driver untouched) or answered; the acks still remove the
   samples; no "Ignoring a message" warning; the broker recorded an UNSUBSCRIBE (`TestMqttBroker.Server`'s
   `InterceptingUnsubscriptionAsync`); then stop that Edge host and start another commands-off Edge on the **same
   broker** (the session survives a host restart; a broker restart via `StopServerAsync`/`StartServerAsync` builds a new
   server and loses sessions, `tests/Edge.Tests/Fakes/TestMqttBroker.cs:92-104`, so it must not be used here) → no
   "Discarded a message" log and no execution: nothing on the retired topic was redelivered.
5. Cloud test (`CloudTestHost` creates its own broker, `tests/Cloud.Tests/Infrastructure/CloudTestHost.cs:43-44`, and the
   host is built lazily): before the host is built, connect a raw MQTTnet client to `host.Broker` as client id
   `iiot-cloud` with the host's credentials, `CleanStart(false)` and a session expiry, subscribe `iiot/+/command-results`
   and disconnect; inject 30 command results on `iiot/edge-a/command-results` (queued in that session); then build the
   host with commands off. Assert: the queued results were discarded (30 "Discarded a message" logs), telemetry
   samples are still stored and acked, no "Ignoring a message" warning, an UNSUBSCRIBE was recorded; then
   `CloudMqttConnection.ForceReconnectAsync()` (same broker, same session) → after the reconnect no further "Discarded a
   message" log: nothing was redelivered. (A `commands`-row check is not used: no row can exist before the host's
   migration, so it would prove nothing.)

## Acceptance
- AC-05: the Edge (Step 4) and Cloud (Step 5) cases pass.

## Dependencies
- task-01-edge-commands-switch.md
- task-02-cloud-commands-switch.md

## Verification Plan
- Command: `dotnet test tests/Edge.Tests --filter "FullyQualifiedName~CommandsSwitchTests|FullyQualifiedName~TelemetryPublisherTests" && dotnet test tests/Cloud.Tests --filter "FullyQualifiedName~CommandsDisabledTests|FullyQualifiedName~TelemetryIngestionTests|FullyQualifiedName~TelemetryAckTests"`
- Named probe: the Step 4 case in `CommandsSwitchTests` and the Step 5 case in `CommandsDisabledTests`; publisher/ingestion/ack tests as regression.
- Reachability: source — real hosts on the in-process TLS `TestMqttBroker` with persistent sessions.
- Oracle: `Failed: 0`, `Skipped: 0`, > 0 tests in each named class.
- Counterexample: UNSUBSCRIBE alone still delivers the 30 queued messages, logs "Ignoring a message" and redelivers them after the reconnect; no retirement at all does the same and keeps the subscription.
- Artifacts: ephemeral.

## Failure Protocol
On a failed Step or Verification Plan run: stop; do not widen scope, change the Command, or weaken a test; record observed versus expected; repair only the cited cause; after three failed rounds, stop and ask the user.

## Receipt

Verification: PASS
Command: dotnet test tests/Edge.Tests --filter "FullyQualifiedName~CommandsSwitchTests|FullyQualifiedName~TelemetryPublisherTests" && dotnet test tests/Cloud.Tests --filter "FullyQualifiedName~CommandsDisabledTests|FullyQualifiedName~TelemetryIngestionTests|FullyQualifiedName~TelemetryAckTests"
Exit: 0
Base: 2dd6c6feaf16dc24a691c8f999b04b84b824f237
Head: 4f150eec4f5b9baf7648260d178f7a9e7659858f57f51ee3d0cc28fac9205e24
```text
$ dotnet test tests/Edge.Tests --filter "FullyQualifiedName~CommandsSwitchTests|FullyQualifiedName~TelemetryPublisherTests" && dotnet test tests/Cloud.Tests --filter "FullyQualifiedName~CommandsDisabledTests|FullyQualifiedName~TelemetryIngestionTests|FullyQualifiedName~TelemetryAckTests"
Test run for C:\Users\case 046\Documents\Project\PlcProtocol\tests\Edge.Tests\bin\Debug\net10.0\Edge.Tests.dll (.NETCoreApp,Version=v10.0)
Passed!  - Failed:     0, Passed:    33, Skipped:     0, Total:    33, Duration: 29 s - Edge.Tests.dll (net10.0)
Test run for C:\Users\case 046\Documents\Project\PlcProtocol\tests\Cloud.Tests\bin\Debug\net10.0\Cloud.Tests.dll (.NETCoreApp,Version=v10.0)
Passed!  - Failed:     0, Passed:    32, Skipped:     0, Total:    32, Duration: 28 s - Cloud.Tests.dll (net10.0)
```
- Named probes: CommandsSwitchTests.RestartFromWriteMode_QueuedCommandsDiscarded_NotRedelivered (Edge) and CommandsDisabledTests.RestartFromWriteMode_QueuedResultsDiscarded_NotRedelivered (Cloud) pass.
- Counterexample: with both RetireSubscription calls commented out, both probes failed (timed out waiting for: 30 queued ... discarded); code restored.
- Reopened after the feature-level run: the Edge probe was flaky in the full suite (acked only up to the first published sample). Test-only fix: ack 0..newest sequence of the second host's telemetry until the buffer is empty. Full Edge suite afterwards 9/9 runs 304/304.
- Review: code-auditor PASS (initial and follow-up, 0 Critical/High/Medium); follow-up Low 1 (telemetry of the earlier host) fixed before this run; open Lows: restart phase proves UNSUBSCRIBE was received, not that the session dropped the subscription; sync-over-async in the test predicate.
