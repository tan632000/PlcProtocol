# Task 05 — Viewers list devices and tags with state, staleness, values and truthful `canWrite`; `devicesUpdated` is pushed

Status: done

## Outcome
`GET /api/v1/edges/{edgeId}/devices` and `GET /api/v1/edges/{edgeId}/devices/{device}/tags` answer per D-06 under the
Viewer policy (module registered in DI), and the ingestor pushes `devicesUpdated` to the Edge's SignalR group after
each stored round.

## Scope
- In: endpoint module and its DI registration, response records, stale rule on the Cloud `TimeProvider`, the Cloud
  command switch in `commandsEnabled`/`canWrite`, `LiveNotifier.DevicesUpdatedAsync`, the ingestor's call after a stored round.
- Out: sample client and docs (task-06).

## Coverage
- CP-05

## Ownership
- Create: `src/Cloud/Api/DevicesEndpoints.cs`, `tests/Cloud.Tests/DevicesApiTests.cs`
- Modify: `src/Cloud/CloudServiceCollectionExtensions.cs` (`TryAddEnumerable` of the module), `src/Cloud/Live/LiveNotifier.cs`,
  `src/Cloud/Ingestion/InventoryIngestor.cs`
- Read: `src/Cloud/Api/TelemetryEndpoints.cs`, `src/Cloud/EndpointModules.cs`, `src/Cloud/Live/LiveHub.cs`,
  `src/Cloud/CloudOptions.cs`, `tests/Cloud.Tests/TelemetryApiTests.cs`, `tests/Cloud.Tests/LiveHubTests.cs`,
  `tests/Cloud.Tests/AuthTests.cs`, `tests/Cloud.Tests/CommandsDisabledTests.cs`

## Steps
1. Write `DevicesApiTests` → red.
2. Implement and register the module, the notifier method and the ingestor call → green.

## Acceptance
- AC-05:
  - `/devices` for a stored Edge returns the D-06 body; within 150 s `stale` is false and states are as sent; after 150 s
    on the fake clock `stale` is true and every `state` is `stale`.
  - An allow-listed Edge without a document → `reportedUtc` null, `stale` true, `devices` [].
  - Not allow-listed Edge → 404 `edge_not_found` on both routes; unknown device → 404 `device_not_found`; device name
    matched case-insensitively.
  - `/tags` lists tags in Edge order with `value`/`quality`/`timestampUtc` from the latest values (null when none).
  - With `Cloud:Commands:Enabled` true, `canWrite` is true only when the Edge's `commandsEnabled`, device `enabled`,
    `writesEnabled` and tag `writable` all hold; with it false (the default), `commandsEnabled` and every `canWrite` are false.
  - No token → 401; a token without Viewer or Operator → 403 (same as the existing read routes).
  - A SignalR client that joined the Edge receives `devicesUpdated` with the `/devices` body after a round is stored; a
    client of another Edge does not.
  - No response contains `host`, `port`, `address` or raw error text.

## Dependencies
- task-04-cloud-inventory-ingest.md

## Verification Plan
- Command: `dotnet test tests/Cloud.Tests --filter "FullyQualifiedName~DevicesApiTests" -- RunConfiguration.TreatNoTestsAsError=true`
- Named probe: `DevicesApiTests` (body, stale, empty, 404s, order and values, canWrite with the Cloud switch on and off,
  auth, push isolation, no network detail).
- Reachability: source — the Cloud host as `CloudTestHost` builds it, real Postgres and SignalR client.
- Oracle: every case passes; a filter matching no test fails the command.
- Counterexample: an unregistered module (404 everywhere), a stale rule on the Edge's `sentUtc`, `canWrite` true on a
  read-only Cloud, or a push to every client fails a named case.
- Artifacts: ephemeral test database.

## Failure Protocol
On a failed Step or Verification Plan run: stop; do not widen scope, change the Command, or weaken a test; record observed versus expected; repair only the cited cause; after three failed rounds, stop and ask the user.

## Receipt

Verification: PASS
Command: dotnet test tests/Cloud.Tests --filter "FullyQualifiedName~DevicesApiTests" -- RunConfiguration.TreatNoTestsAsError=true
Exit: 0
Base: 3e970eadd264a8548f3bdcabfab097ab95c8a6a0
Head: c393cdce6bc431d2de72a87f7f09b479d78ce1b0002a62dc056ac46917f94b46
Process: tests were written first and all 9 failed before the module existed; discrimination shown by mutants that each failed tests (module not registered; any authenticated user; stale at exactly 150 s; never stale; states not replaced when stale; canWrite ignoring the Cloud switch; canWrite ignoring a disabled device, after the test gained a disabled device with writes on, since the first version let WritesEnabled=false mask it; device lookup case-sensitive; no push; push to every client); sources restored and rebuilt after every mutant run. Full Cloud.Tests 175/175 (the task-04 ingestion tests included, since this task also changed InventoryIngestor); solution Release build clean.
Review: fresh-context code review, PASS; its Lows were repaired and re-reviewed PASS: the round completion time is truncated to milliseconds so the pushed reportedUtc equals what PostgreSQL returns (the push test now uses a sub-microsecond clock and failed before the fix), building the push body cannot throw into the store loop, and the tags route deserializes the document once.
Ownership note: InventoryIngestor (created by task-04) gained the push and the millisecond truncation; its constructor takes LiveNotifier and the command options.
Open question for the user at GATE-DONE: a stale Edge can still report canWrite true on /tags (D-06 does not involve staleness; the command route does not consult the inventory).
Limitations: a stale transition is not pushed (clients read reportedUtc); the push states stale=true when the stored round had waited past 150 s; /tags deserializes the stored document (up to about 2.4 MB) on each call.
```text
$ dotnet test tests/Cloud.Tests --filter "FullyQualifiedName~DevicesApiTests" -- RunConfiguration.TreatNoTestsAsError=true
  Determining projects to restore...
  All projects are up-to-date for restore.
  IndustrialIot.Shared -> C:\Users\case 046\Documents\Project\PlcProtocol\src\Shared\bin\Debug\net10.0\IndustrialIot.Shared.dll
  IndustrialIot.Cloud -> C:\Users\case 046\Documents\Project\PlcProtocol\src\Cloud\bin\Debug\net10.0\IndustrialIot.Cloud.dll
  Cloud.Tests -> C:\Users\case 046\Documents\Project\PlcProtocol\tests\Cloud.Tests\bin\Debug\net10.0\Cloud.Tests.dll
Test run for C:\Users\case 046\Documents\Project\PlcProtocol\tests\Cloud.Tests\bin\Debug\net10.0\Cloud.Tests.dll (.NETCoreApp,Version=v10.0)
A total of 1 test files matched the specified pattern.

Passed!  - Failed:     0, Passed:     9, Skipped:     0, Total:     9, Duration: 6 s - Cloud.Tests.dll (net10.0)
```
