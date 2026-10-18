# Task 04 — One TLS MQTT connection drains the telemetry queue in order and survives broker outages

Status: done

## Outcome
`MqttConnection` keeps a single authenticated TLS session to the broker with reconnect/backoff and strict certificate validation, connects only after every subscription is registered, never acknowledges a message nobody handled; `TelemetryPublisher` (hosted service) publishes queued samples in `sequence` order, deleting each only after its PUBACK.

## Scope
- In: `MqttOptions` + validator, `MqttTopics`, `MqttConnection` (TLS, credentials, persistent session, reconnect, publish, subscription registry, manual acknowledgement), `TelemetryPublisher`, hourly `PurgeAsync` call, registration, `EdgeTestHost` defaults, in-process TLS test broker.
- Out: command handling (task-05), Edge status / LWT (excluded), broker deployment and ACLs (wave 4, see plan D-04).

## Coverage
- CP-04, CP-01 (MQTT config validation)

## Ownership
- Create: `src/Edge/Messaging/{MqttOptions.cs,MqttTopics.cs,MqttConnection.cs,TelemetryPublisher.cs}`
- Modify: `src/Edge/EdgeServiceCollectionExtensions.cs`, `src/Edge/appsettings.json` (add `Edge:Mqtt` without any `Password` key), `src/Edge/IndustrialIot.Edge.csproj` (add `MQTTnet` 5.2.0.1603)
- Modify: `tests/Edge.Tests/Edge.Tests.csproj` (add `MQTTnet.Server` 5.2.0.1603), `tests/Edge.Tests/Fakes/EdgeTestHost.cs`
- Create: `tests/Edge.Tests/TelemetryPublisherTests.cs`, `tests/Edge.Tests/Fakes/TestMqttBroker.cs`
- Read: `src/Edge/Buffering/{TelemetryStore.cs,EdgeDatabase.cs}`

## Contracts
**Options** `Edge:Mqtt` (`MqttOptions` is a class; it does not override `ToString` and no member returns the password): `Host` (required), `Port` = 8883 (1..65535), `Username` (required, 1..256), `Password` (required, 1..1024; supplied via environment `Edge__Mqtt__Password`), `CaCertificatePath` (optional PEM), `KeepAliveSeconds` = 30 (5..1200), `ReconnectMinDelayMs` = 1000 (100..60_000), `ReconnectMaxDelayMs` = 30_000 (≥ min, ≤ 600_000), `PublishBatchSize` = 100 (1..1000). Invalid → `OptionsValidationException` at start whose messages name the key but never contain its value. No plaintext/insecure option exists.

**Topics** (`MqttTopics`, `edgeId` from `EdgeOptions`): `iiot/{edgeId}/telemetry`, `iiot/{edgeId}/commands`, `iiot/{edgeId}/command-results`.

**Connection**
- Client id = `edgeId`; MQTT 5; `CleanStart = false`, session expiry 24 h; `MaximumPacketSize` = 16 KiB announced in CONNECT; TLS 1.2+.
- Certificate validation: without `CaCertificatePath` → OS trust store with default rules. With it → the chain must build with `X509ChainTrustMode.CustomRootTrust` and `CustomTrustStore` = that CA only; `RemoteCertificateNameMismatch` and every other policy error still fail. No bypass.
- `RegisterSubscription(topic, handler)` must be called before the connection starts; the connection loop starts after `IHostApplicationLifetime.ApplicationStarted`, so every hosted service has registered first. Subscriptions use QoS 1 and `RetainHandling = DoNotSendOnSubscribe`, renewed after every connect.
- Incoming messages: `AutoAcknowledge = false`. A message is acknowledged only after its handler (matched by **exact** topic string) completes successfully; no handler, `Retain = true` (ignored), or a throwing handler → not acknowledged (broker redelivers on the next session).
- Reconnect: exponential backoff from `ReconnectMinDelayMs` doubling to `ReconnectMaxDelayMs`, reset only after a connection that held for at least `ReconnectMaxDelayMs` (amended during review: resetting on every successful connect let a broker that keeps dropping the session — e.g. a duplicate edgeId — cause a reconnect storm). Bad credentials follow the same backoff and log a warning without the password.
- `PublishAsync(topic, payload, ct)`: QoS 1, retain false; completes only on a success PUBACK; otherwise throws.
- The MQTTnet internal logger is not attached above Warning level.

**Publisher loop**
1. Wait until connected.
2. `ReadBatchAsync(PublishBatchSize)`; if empty, `WaitForAppendAsync(1 s)`.
3. For each row in order: `PublishAsync(telemetry topic, payload)`; on success `AcknowledgeAsync(sequence)`; on failure stop the batch, keep the row, go to 1 (the same row is sent first next time — no reordering, no skipping).
4. `TelemetryStore.PurgeAsync` at least once per hour.
- At-least-once: a crash after PUBACK and before delete re-sends that row once (Cloud dedupes on `edgeId`,`streamId`,`sequence`).

**`TestMqttBroker`** (tests only): in-process `MQTTnet.Server` on a random loopback port with TLS; the server certificate (SAN `localhost`) is signed by a test CA generated at start and loaded through a PFX round-trip (Windows SChannel needs a persisted key); exports the CA as PEM for `CaCertificatePath`, and can generate a second, unrelated CA; validates username/password; records received messages in order; can publish as another client; stop/restart on the same port with `ReuseAddress`.

## Steps
1. Packages; options + validator; topics; `MqttConnection`; publisher; register in `AddEdgeAgent`; add MQTT defaults (pointing at no broker unless a test supplies one) to `EdgeTestHost`.
2. `TestMqttBroker`, then tests.

## Acceptance
- AC-05: 50 queued samples arrive at the broker in `sequence` order and the store is empty afterwards; with the broker stopped, 20 appended samples remain queued; after restart all 20 arrive in order with no reordering.
- AC-06: `CaCertificatePath` pointing to the unrelated CA → no connection; correct CA but connecting to `127.0.0.1` (name mismatch) → no connection; no `CaCertificatePath` with the test CA (not in OS store) → no connection.
- AC-09 (part): a message published to `iiot/{edgeId}/commands` while no handler is registered for it is not acknowledged and is redelivered after reconnect; a retained message is not delivered on subscribe; a handler registered for `iiot/{edgeId}/commands` is not invoked for `iiot/{edgeId}/Commands` or `iiot/{edgeId}/commands/x` (exact match).
- Validation messages for an invalid `Password` (e.g. 1025 chars) contain the key name and not the value.
- Wrong password → no connection, ≥ 2 attempts with growing delay; captured logs and exception messages never contain the password.
- AC-01 (part): missing `Host`/`Username`/`Password`, `Port = 0`, `ReconnectMaxDelayMs < ReconnectMinDelayMs` fail at host start; no `src/Edge/appsettings*.json` contains a `Password` key.

## Dependencies
- task-03-tag-poller.md

## Verification Plan
- Command: `dotnet test IndustrialIot.sln`
- Named probe: `TelemetryPublisherTests.Drains_InSequenceOrder_DeletesAfterPuback`, `TelemetryPublisherTests.BrokerOutage_BuffersThenDrainsInOrder`, `TelemetryPublisherTests.CertificateValidation_RejectsWrongCaAndName`, `TelemetryPublisherTests.UnhandledMessage_NotAcknowledged`, `TelemetryPublisherTests.Handler_ExactTopicMatchOnly`, `TelemetryPublisherTests.BadCredentials_RetriesWithBackoff_NoSecretLeak`, `TelemetryPublisherTests.InvalidMqttConfig_FailsAtStart`, `TelemetryPublisherTests.AppSettings_HaveNoPassword`; all earlier probes still pass.
- Reachability: source — .NET 10 SDK + loopback TLS; connection and publisher resolved through `EdgeTestHost`.
- Oracle: exit 0, 0 failed across the solution; eight named probes pass (`InvalidMqttConfig_FailsAtStart` also asserts the password value is absent from the validation message).
- Counterexample: acknowledging before PUBACK, skipping a failed row, trusting any chain that contains the CA, ignoring a name mismatch, or auto-acknowledging unhandled messages must fail the named probes.
- Artifacts: ephemeral (temp DB dirs, generated certificate files deleted per test).

## Failure Protocol
On a failed Step or Verification Plan run: stop; do not widen scope, change the Command, or weaken a test; record observed versus expected; repair only the cited cause; after three failed rounds, stop and ask the user.

## Receipt

Verification: PASS
Command: dotnet test IndustrialIot.sln
Exit: 0
Base: d50cbfdf5a122f42e9cba23409b986e85969ba2e
Head: 1a0d98c0bde97e99dcd8d651a8dbcd1c615b9330054aa46fa280e4abe20d336d
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

Passed!  - Failed:     0, Passed:    47, Skipped:     0, Total:    47, Duration: 365 ms - Shared.Tests.dll (net10.0)

Passed!  - Failed:     0, Passed:   264, Skipped:     0, Total:   264, Duration: 14 s - Edge.Tests.dll (net10.0)
```
- Pre-change run: exit 0 with 0 `TelemetryPublisherTests` listed → oracle (named probes) not met, as expected.
- Named probes present and passing: `Drains_InSequenceOrder_DeletesAfterPuback`, `BrokerOutage_BuffersThenDrainsInOrder` (incl. a failed-PUBACK phase), `CertificateValidation_RejectsWrongCaAndName`, `UnhandledMessage_NotAcknowledged` (redelivery + retained), `Handler_ExactTopicMatchOnly`, `BadCredentials_RetriesWithBackoff_NoSecretLeak`, `InvalidMqttConfig_FailsAtStart` (7 rows; asserts the value — incl. a 1,100-char password — is absent from the exception), `AppSettings_HaveNoPassword`; plus `Purge_RunsWhileDisconnected` and `Handler_RegisteredDuringHostStart`. `TelemetryPublisherTests` repeated 4 times after the last change, 16/16 each.
- Packages: MQTTnet 5.2.0.1603 (Edge), MQTTnet.Server 5.2.0.1603 (Edge.Tests); API checked by reflection over the package.
- Counterexamples (mutations in a scratch copy), each caught: acknowledging before PUBACK and acknowledging+skipping a failed row → `BrokerOutage_BuffersThenDrainsInOrder`; purge waiting for the broker → `Purge_RunsWhileDisconnected`; accepting any chain error with a configured CA and ignoring a name mismatch → `CertificateValidation_RejectsWrongCaAndName`; auto-acknowledging unhandled messages → `UnhandledMessage_NotAcknowledged`; closing registration at the top of `ExecuteAsync` → `Handler_RegisteredDuringHostStart`.
- Repair rounds: R1 wrong assertion in the new failed-PUBACK phase; R2 review FAIL (purge stuck behind the connection wait; registration guard racing .NET 10 `BackgroundService` start; reconnect storm after short-lived connections; rebuilt chain without serverAuth EKU / intermediates) → fixed; R3 compile errors in the R2 fix, then `Purge_RunsWhileDisconnected` raced (fake-clock advance during the start-up purge); extra round authorized by the user → test advances in 1 h steps (test-only). Final re-review: code-auditor PASS (0 Critical/High/Medium).
- Contract amended during review: back-off resets only after a connection held ≥ `ReconnectMaxDelayMs`.
- Known limitations: publishing is sequential (one PUBACK round-trip per sample; ~10 msg/s at 100 ms RTT) — a pipelined in-order window would be a later change; the serverAuth EKU requirement has no dedicated probe (the test broker certificate carries serverAuth); unacknowledged messages occupy broker in-flight slots until the next session (Mosquitto default 20); a disconnect between SUBACK and the connected signal can leave the publisher believing it is connected until the next reconnect (retries every `ReconnectMinDelayMs`, no data loss); rebuilt chain element certificates are not disposed per handshake; a PEM bundle in `CaCertificatePath` uses only its first certificate; no live broker (Mosquitto) proof — in-process MQTTnet broker only.
