# Task 04 — Broker limits and end-to-end 100 tags/s exactly-once proof

Status: done

## Outcome
With real Mosquitto, a real Cloud host on PostgreSQL and a real Edge host polling 100 tags every second, every produced sample is stored exactly once — during normal operation, across a Cloud outage with the repo broker config, and across an outage where a broker capped at 1 000 queued messages drops messages — and the backlog drains at ≥ 200 samples/s. Broker limits, rollout order and the changed buffer meaning are documented.

## Scope
- In: `max_queued_messages 20000` + `max_queued_bytes 134217728` with the sizing rule as a comment (plan D-04); one end-to-end test class; docs for rollout order (D-06), the two new options, the sizing rule, and the buffer meaning (it now grows while the Cloud is offline).
- Out: a real PLC (an in-test `IPlcDriver` returns values); tuning beyond the AC-07 thresholds.

## Coverage
- CP-04

## Ownership
- Modify: `deployments/mosquitto/mosquitto.conf`
- Modify: `tests/Deployment.Tests/Deployment.Tests.csproj`
- Create: `tests/Deployment.Tests/EndToEndTelemetryTests.cs`
- Modify: `docs/deployment.md`
- Modify: `docs/setup-checklist.md`
- Read: `tests/Deployment.Tests/BrokerTests.cs` (`BrokerFixture`, `SecretsDirectory`, `:491-545`), `tests/Cloud.Tests/Infrastructure/CloudTestHost.cs` (pattern only — not referenceable), `tests/Edge.Tests/Fakes/EdgeTestHost.cs` (pattern only)

## Steps
1. `mosquitto.conf`: the two limits + comment "max_queued_messages ≥ 2 × (Edges × Edge:Mqtt:MaxUnacknowledged); a drop only delays data, the Edge keeps it".
2. `Deployment.Tests.csproj`: reference `src/Cloud` and `src/Edge`; add `Microsoft.AspNetCore.Mvc.Testing` and `Testcontainers.PostgreSql` at the versions in `tests/Cloud.Tests/Cloud.Tests.csproj:9,14`.
3. `EndToEndTelemetryTests` (`[Trait("Category", "Slow")]`): generated secrets; PostgreSQL container; Cloud via `WebApplicationFactory<Program>` configured in the test (MQTT host **`127.0.0.1`** + fixture port + dev CA, allow-list the test Edge, auth settings with a local static configuration as `CloudTestHost` does); Edge via `AddEdgeAgent` with an in-test `IPlcDriver`, 100 tags, `Poll:IntervalMs=1000`, MQTT host **`127.0.0.1`**, `AckTimeoutSeconds=5`, `Monitor:Enabled=false`, buffer under a temp directory.
   - Phase A (repo broker config, 60 s): every sequence `0..next_sequence-1` of the stream is present exactly once in `telemetry` (set comparison), Edge `CountAsync() == 0`.
   - Phase B (same broker): stop the Cloud host for 40 s, restart; gross drain = (backlog at restart + samples produced until the buffer is empty) / elapsed; assert ≥ 200/s, the Phase A set check, and no `dropped` in broker logs; print `drain rate: <gross> samples/s (backlog <n>, produced <m>, <s> s)`.
   - Phase C (second broker container from a temp copy of the repo config with `max_queued_messages 1000`, same ACL/secrets): Cloud offline for ≥ 40 s (> 1 500 samples, past the second resend reset), restart; assert the broker log contains `dropped` (the scenario really happened) and the set check (exactly once, no gaps) — the old delete-on-PUBACK design loses samples here.
   - All Cloud and Edge settings are supplied in the test; nothing relies on the `appsettings.json` copied to the test output (both referenced hosts ship one).
4. Docs: `docs/deployment.md` — rollout order + refused-subscription symptom, the two options, the sizing rule; reword the PLC Monitor buffer line (`:195`). `docs/setup-checklist.md:108,116` — "Buffer chờ gửi" rises while the Cloud is offline and falls back to ~0 once it returns.

## Acceptance
- AC-07: phases A, B and C pass; the drain line is printed with gross ≥ 200.

## Dependencies
- task-02-cloud-acks-stored-telemetry.md
- task-03-edge-delete-on-ack.md

## Verification Plan
- Command: `dotnet test tests/Deployment.Tests --filter "FullyQualifiedName~EndToEndTelemetryTests" --logger "console;verbosity=normal"`
- Named probe: `EndToEndTelemetryTests` phases A, B, C.
- Reachability: installed — Testcontainers Mosquitto with the repo `mosquitto.conf` + `acl` (`tests/Deployment.Tests/BrokerTests.cs:529-545`) and a capped copy, Testcontainers PostgreSQL, real Cloud (`src/Cloud/Program.cs:24`) and Edge hosts; requires Docker and Git Bash; random host ports (no clash with a running dev stack).
- Oracle: `Failed: 0`, `Skipped: 0`, output contains `drain rate:` with gross ≥ 200.
- Counterexample: an Edge that deletes on PUBACK fails phase C (samples missing); an Edge that never resends after a reconnect/timeout leaves the dropped samples in its buffer and fails phase C (phase B cannot show it: the Cloud's persistent session still delivers the queued window). (The resend backoff schedule itself is proved by task-03, not here: a 40 s outage cannot distinguish fixed from doubling intervals under the 20000 limit.)
- Artifacts: ephemeral (containers removed by Testcontainers; secrets and buffer in temp directories deleted by the test).

## Failure Protocol
On a failed Step or Verification Plan run: stop; do not widen scope, change the Command, or weaken a test; record observed versus expected; repair only the cited cause; after three failed rounds, stop and ask the user. A gross drain < 200 samples/s on `127.0.0.1` is plan D-05's break signal: stop and return to GATE-SCOPE instead of tuning ad hoc.

## Receipt

Verification: PASS
Command: dotnet test tests/Deployment.Tests --filter "FullyQualifiedName~EndToEndTelemetryTests" --logger "console;verbosity=normal"
Exit: 0
Base: 8f7e6c753cf52c4a9e65d36b996f95edd7ab5959
Head: 81fce74ae00b2584759319feff1fd0cb1d29d8a4d20ff22e2e97b305e57f1869
```text
$ dotnet test tests/Deployment.Tests --filter "FullyQualifiedName~EndToEndTelemetryTests" --logger "console;verbosity=normal"
Test run for C:\Users\case 046\Documents\Project\PlcProtocol\tests\Deployment.Tests\bin\Debug\net10.0\Deployment.Tests.dll (.NETCoreApp,Version=v10.0)
  Passed IndustrialIot.Deployment.Tests.EndToEndTelemetryTests.CappedBroker_DropsQueuedMessages_EdgeStillDeliversEverySample [1 m 6 s]
drain rate: 329 samples/s (backlog 4339, produced 1900, 19.0 s)
  Passed IndustrialIot.Deployment.Tests.EndToEndTelemetryTests.RepoBroker_NormalRunAndCloudOutage_EveryProducedSampleStoredOnce [2 m 4 s]
Test Run Successful.
Total tests: 2
     Passed: 2
 Total time: 3.1925 Minutes
```
- Pre-change run: exit 0 with 'No test matches the given testcase filter' and no drain line (oracle unmet).
- Phases A+B run as RepoBroker_NormalRunAndCloudOutage_EveryProducedSampleStoredOnce (repo mosquitto.conf/acl); phase C as CappedBroker_DropsQueuedMessages_EdgeStillDeliversEverySample (copy of the repo config with max_queued_messages 1000, asserts the broker logged drops for client iiot-cloud and an outage backlog > 1000).
- Drain window: from the restarted Cloud's MQTT connect until the Edge buffer reads 0; gross = (backlog + produced during drain) / elapsed. The drain line is also written with Console.WriteLine because xUnit shows ITestOutputHelper output only for failed tests at verbosity normal.
- Review: code-auditor PASS (0 critical/high/medium); post-PASS Low fixes (client-specific drop assertion, outage backlog assertion, dispose on failed start, Ryuk pin, docs wording, counterexample text) re-reviewed PASS. Remaining Low: CappedBroker duplicates BrokerFixture's mount list (BrokerTests.cs outside ownership).
- Limitations: measured on the dev machine (Docker Desktop, 127.0.0.1); the Cloud stores one message per transaction, so the 346/s gross drain is this machine's figure, not a guarantee for other hardware.
- Final-Head rebind: fresh run at the feature's final Head.
