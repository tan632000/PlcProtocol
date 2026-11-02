# Task 04 — The Cloud keeps bounded buffers and stores the latest consistent inventory round of each Edge

Status: done

## Outcome
An `InventoryIngestor` subscribes to `iiot/+/inventory` at QoS 0, assembles parts per D-04 (one bounded buffer per Edge,
round-level checks, superseded boots, rate limit, a background loop that stores waiting rounds) and keeps one row per Edge in
`edge_inventory` (D-05), created by an EF migration; telemetry keeps flowing whatever inventory does.

## Scope
- In: a QoS per topic filter in `CloudMqttConnection.RegisterSubscription` (default QoS 1; the grant check accepts the
  requested QoS per filter), ingestor, entity, `DbContext` mapping, migration, registration, ingestion tests on Postgres.
- Out: read API and SignalR push (task-05).

## Coverage
- CP-04

## Ownership
- Create: `src/Cloud/Ingestion/InventoryIngestor.cs`, `src/Cloud/Data/Migrations/<timestamp>_EdgeInventory.cs` (+ Designer),
  `tests/Cloud.Tests/InventoryIngestionTests.cs`
- Modify: `src/Cloud/Messaging/CloudMqttConnection.cs`, `src/Cloud/Data/Entities.cs`, `src/Cloud/Data/CloudDbContext.cs`, `src/Cloud/Data/Migrations/CloudDbContextModelSnapshot.cs`,
  `src/Cloud/CloudServiceCollectionExtensions.cs`
- Read: `src/Cloud/Ingestion/TelemetryIngestor.cs`, `src/Cloud/Commands/CommandResultIngestor.cs`,
  `tests/Cloud.Tests/TelemetryIngestionTests.cs`, `tests/Cloud.Tests/Infrastructure/CloudTestHost.cs`,
  `tests/Cloud.Tests/Infrastructure/DatabaseFaultInterceptor.cs`

## Steps
1. Write `InventoryIngestionTests` (same host and broker setup as `TelemetryIngestionTests`, fake clock) → red.
2. Implement entity, mapping, migration (`dotnet ef migrations add EdgeInventory`, tool from `.config/dotnet-tools.json`),
   ingestor → green.

## Acceptance
- AC-04:
  - The broker grants the inventory filter at QoS 0 and the connection accepts it; the telemetry and command-result
    filters are still requested at QoS 1 and a lower grant for them still fails the connection as before.
  - A complete 3-part round is stored as one row whose document lists devices and tags in Edge order; `received_utc`
    is the Cloud clock when the round completed.
  - Parts arriving out of order are assembled; a duplicate part index is ignored (0,1,1 of 3 stores nothing); a part with
    a different `partCount` is dropped; a round missing a part stores nothing, and the next round of the same boot replaces that buffer and is stored.
  - Round checks drop the round (nothing stored, one warning with a fixed reason) when: two devices differ only by case;
    a `tags.device` has no status entry; a device's items differ from its `tagCount`; a `tagId` repeats; `commandsEnabled`
    differs between parts.
  - Bounds: a stream of parts with a new `bootId` each keeps one buffer for that Edge; a buffer refuses parts beyond
    2400 KiB, while a valid 200-part round of 12 KiB parts completes; an incomplete buffer expires after 5 minutes (fake clock).
  - Order: a same-boot round with a lower or equal `round` does not replace the row; a new `bootId` with `round` 0 does;
    after boot B's round replaced boot A's stored document, a later round of A is dropped with "two Edge processes share
    edgeId" (A→B→A); a boot that never had a stored document is not remembered.
  - Rate: complete rounds of one Edge arriving 1 s apart are stored at most once per 5 s, the newest winning; with no
    further message the waiting round is stored as soon as the gap has passed (within one 1 s tick), with its completion
    time as `received_utc`; a late older round of the same boot never replaces the newer waiting one.
  - Database down when a round completes (fault interceptor): inventory messages are still handled at once (the handler
    never waits on the database), the ingestor does not force a reconnect, the round stays waiting past the 30 s retry
    budget, and it is stored, with its completion time, once the database is back.
  - A part on `iiot/<unknown>/inventory`, a part whose `edgeId` differs from the topic, and an invalid part are dropped
    with a warning naming topic and reason only.
  - The migration applies on an empty database and on one holding the current schema.

## Dependencies
- task-01-inventory-contract.md

## Verification Plan
- Command: `dotnet test tests/Cloud.Tests --filter "FullyQualifiedName~InventoryIngestionTests|FullyQualifiedName~TelemetryIngestionTests|FullyQualifiedName~CommandFlowTests|FullyQualifiedName~CommandsDisabledTests" -- RunConfiguration.TreatNoTestsAsError=true`
- Named probe: `InventoryIngestionTests` (QoS 0 grant, complete round, out-of-order, duplicate index, partCount mismatch, missing part,
  round checks, lost part repaired by the next round, buffer bounds, expiry, newer-wins, superseded boot, rate limit and
  store-after-the-gap, database down, foreign edge, invalid, migration); `TelemetryIngestionTests`, `CommandFlowTests`, `CommandsDisabledTests`
  (unchanged behavior of the shared `CloudMqttConnection`).
- Reachability: source — the real `CloudMqttConnection` and Postgres from the existing fixture (Docker required).
- Oracle: every case passes; a filter matching no test fails the command.
- Counterexample: storing parts as they arrive, counting arrivals instead of indexes, or forcing a reconnect on a
  database error fails a named case.
- Artifacts: ephemeral test database.

## Failure Protocol
On a failed Step or Verification Plan run: stop; do not widen scope, change the Command, or weaken a test; record observed versus expected; repair only the cited cause; after three failed rounds, stop and ask the user.

## Receipt

Verification: PASS
Command: dotnet test tests/Cloud.Tests --filter "FullyQualifiedName~InventoryIngestionTests|FullyQualifiedName~TelemetryIngestionTests|FullyQualifiedName~CommandFlowTests|FullyQualifiedName~CommandsDisabledTests" -- RunConfiguration.TreatNoTestsAsError=true
Exit: 0
Base: 3e970eadd264a8548f3bdcabfab097ab95c8a6a0
Head: 28b53c347704511df56f77a72283ce228c486f675c839e151d01ab519acda6cc
Process: tests were written first and the build was red for the missing ingestor, row and document types; discrimination shown by mutants that each failed tests (inventory subscribed at QoS 1; grant check accepting any non-error code; partCount mismatch accepted; no buffer cap; no buffer expiry; an older or equal round of the same boot replacing the stored one; superseded boot accepted; no store gap; tagId uniqueness unchecked; received_utc = store time; a late older round replacing the waiting one; no timed flush; a database failure forcing a reconnect; no guard around the store pass); sources restored and rebuilt after every mutant run. Full Cloud.Tests 166/166; solution Release build clean.
Found while implementing: the in-process MQTTnet test broker returned SUBACK result items that did not line up with the filters when one SUBSCRIBE mixed QoS 0 and 1, so CloudMqttConnection now sends one SUBSCRIBE per QoS level and checks every item against that level's grant; telemetry and command results keep QoS 1 and a lower grant still fails the connection.
Review: fresh-context code review, PASS after three repair rounds. High: a waiting round was stored only on the Edge's next message and received_utc was the store time; the user chose a background loop (2026-11-02), so the MQTT handler never touches the database, a loop stores waiting rounds as soon as the 5 s gap allows, received_utc is the completion time, a late older round never replaces a newer waiting one; plan D-04/D-05 and this task's Acceptance were reworded. Medium: the loop gained a per-pass guard so a non-database failure cannot stop the Cloud host. The rate test was made deterministic after one failure caused by its own fake-clock timing (the clock now stops at the gap plus one tick).
Limitations: the per-pass guard wraps the whole flush, so a persistent non-database failure on one Edge delays the Edges after it in that pass; a silent Edge keeps one buffer and one waiting round (bounded by the allow-list); a complete round of another boot can replace a waiting round before it is stored (D-04 allows it); during a database outage the loop spends up to 30 s per waiting Edge in DatabaseRetry; the 5-minute buffer expiry is checked when a part of that Edge arrives.
```text
$ dotnet test tests/Cloud.Tests --filter "FullyQualifiedName~InventoryIngestionTests|FullyQualifiedName~TelemetryIngestionTests|FullyQualifiedName~CommandFlowTests|FullyQualifiedName~CommandsDisabledTests" -- RunConfiguration.TreatNoTestsAsError=true
  Determining projects to restore...
  All projects are up-to-date for restore.
  IndustrialIot.Shared -> C:\Users\case 046\Documents\Project\PlcProtocol\src\Shared\bin\Debug\net10.0\IndustrialIot.Shared.dll
  IndustrialIot.Cloud -> C:\Users\case 046\Documents\Project\PlcProtocol\src\Cloud\bin\Debug\net10.0\IndustrialIot.Cloud.dll
  Cloud.Tests -> C:\Users\case 046\Documents\Project\PlcProtocol\tests\Cloud.Tests\bin\Debug\net10.0\Cloud.Tests.dll
Test run for C:\Users\case 046\Documents\Project\PlcProtocol\tests\Cloud.Tests\bin\Debug\net10.0\Cloud.Tests.dll (.NETCoreApp,Version=v10.0)
A total of 1 test files matched the specified pattern.

Passed!  - Failed:     0, Passed:    44, Skipped:     0, Total:    44, Duration: 28 s - Cloud.Tests.dll (net10.0)
```
