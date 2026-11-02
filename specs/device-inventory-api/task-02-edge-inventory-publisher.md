# Task 02 — The Edge publishes complete, bounded inventory rounds on start, state change, heartbeat and reconnect

Status: done

## Outcome
A hosted `InventoryPublisher` in the Edge agent builds a round from the device registry (tag `Name` and normalized
`Description` carried through the resolver, D-02) and the live snapshot, packs it into at most 200 parts of at most
12 KiB and publishes them on `iiot/{edgeId}/inventory` at the D-03 moments, without touching telemetry.

## Scope
- In: `Name`/`Description` on `PlcTagDefinition` and in `EdgeDeviceResolver` (v2 and v1 migration); description
  normalization; `MqttTopics.Inventory`; the publisher (bootId per instance, round building, packing, 1 s snapshot comparison,
  state-change debounce, heartbeat, reconnect trigger via `ConnectionGeneration`, failure logging); registration next to
  `TelemetryPublisher`.
- Out: broker ACL (task-03), Cloud side (task-04/05).

## Coverage
- CP-02

## Ownership
- Create: `src/Edge/Messaging/InventoryPublisher.cs`, `tests/Edge.Tests/InventoryPublisherTests.cs`
- Modify: `src/Edge/Messaging/MqttTopics.cs`, `src/Edge/EdgeServiceCollectionExtensions.cs`,
  `src/Edge/Drivers/Models/PlcTagDefinition.cs`, `src/Edge/Configuration/EdgeDeviceResolver.cs`
- Read: `src/Edge/Messaging/MqttConnection.cs`, `src/Edge/Messaging/TelemetryPublisher.cs`, `src/Edge/Monitoring/LiveTagSnapshot.cs`,
  `src/Edge/Monitoring/MonitorServer.cs`, `src/Edge/Drivers/PlcDeviceRegistry.cs`, `src/Edge/Settings/EdgeSupervisor.cs`,
  `tests/Edge.Tests/Fakes/TestMqttBroker.cs`, `tests/Edge.Tests/TelemetryPublisherTests.cs`

## Steps
1. Write `InventoryPublisherTests` against `TestMqttBroker` (intercepting publishes to answer `NotAuthorized`) and a
   `FakeTimeProvider` → red.
2. Carry `Name`/`Description` through the resolver; implement the publisher: state per device
   as the monitor derives it (`disabled`, `waiting`, `connected`, `unavailable`); `lastError` = `unavailable` when the
   last cycle had the PLC unavailable, `tagErrors` when any reading was not Good, else null → green.

## Acceptance
- AC-02:
  - Start: one round arrives at QoS 1; its parts carry the same `bootId`/`round` 0, `part` 0..`partCount`-1, every part
    ≤ 12 KiB, and together list every device (also disabled ones) and every tag in configuration order
    with `name` and `description`; a v1 (migrated) configuration sends `name` = TagId and `description` null.
  - Descriptions `"\n"`, `"   "`, `"a\tb"` and one whose 256th unit is a high surrogate are sent as null, null, `"a b"`
    and 255 units, and the round validates.
  - 3 devices × 100 tags with 256-char descriptions produce a round of several parts, none over 12 KiB.
  - A configuration whose round needs more than 200 parts publishes nothing, logs one warning naming the part count and
    the limit, and repeats the warning no more than once per 10 minutes.
  - A device turning unavailable produces a new round within 5 s; two state flips within 5 s produce one round; a
    `lastError` change alone produces no extra round and appears in the next heartbeat.
  - 60 s on the fake clock produce the next round with `round` + 1.
  - A reconnect produces a round at once; while disconnected no round is attempted.
  - A settings apply through `EdgeSupervisor` stops the old publisher before the new one starts; the next round has a new
    `bootId` and `round` 0.
  - A broker answering `NotAuthorized` leaves telemetry flowing and logs one warning per 10 minutes.
  - No part contains the device host or a tag address (asserted on the raw JSON).

## Dependencies
- task-01-inventory-contract.md

## Verification Plan
- Command: `dotnet test tests/Edge.Tests --filter "FullyQualifiedName~InventoryPublisherTests" -- RunConfiguration.TreatNoTestsAsError=true`
- Named probe: `InventoryPublisherTests` (start round, description normalization, packing bound, 200-part limit, state debounce,
  lastError on heartbeat, heartbeat, reconnect, settings apply, refused topic keeps telemetry, no network detail).
- Reachability: source — the real `MqttConnection` against the in-process `TestMqttBroker`; the publisher registered by
  `AddEdgeAgent` as the host and `EdgeSupervisor` run it.
- Oracle: every case passes; a filter matching no test fails the command.
- Counterexample: a publisher that sends one oversize message, keeps `bootId` across an apply, or stops telemetry when
  the topic is refused fails a named case.
- Artifacts: ephemeral test output.

## Failure Protocol
On a failed Step or Verification Plan run: stop; do not widen scope, change the Command, or weaken a test; record observed versus expected; repair only the cited cause; after three failed rounds, stop and ask the user.

## Receipt

Verification: PASS
Command: dotnet test tests/Edge.Tests --filter "FullyQualifiedName~InventoryPublisherTests" -- RunConfiguration.TreatNoTestsAsError=true
Exit: 0
Base: 3e970eadd264a8548f3bdcabfab097ab95c8a6a0
Head: 6004e3e2012c87808982b2d50f6c1f453da773665c403d61e5a2c0072699ddae
Process: tests were written first and the exact Command failed 10/10 before any source change; discrimination shown by mutants that each failed tests (bootId static: SettingsApply; lastError in the trigger key: LastErrorChange; no 5 s gap: StateChange; no 12 KiB packing: ThreeDevices and Over200; raw descriptions: Descriptions; warnings not rate-limited: Over200 and Refused; no round on reconnect: Reconnect, after the test gained a short outage before the next heartbeat, since the first version let the heartbeat mask it; model sent as configured: ModelSpelled; lone surrogate kept: Descriptions); sources restored and rebuilt after every mutant run. Full Edge.Tests 783/783; solution Release build clean.
Review: fresh-context code review, PASS after one repair round (High: a model spelled " s7-1200 " passes the catalog lookup but not the contract, which would skip every round; the Edge now sends the catalog name. Low: a lone surrogate from an environment variable is turned into a space).
Ownership note: `MqttConnection.cs` is read only (message expiry was dropped at GATE-REVIEW round 2); `SettingsService` does not serialize `PlcTagDefinition`, so `Name`/`Description` never reach `edge.settings.json`.
Limitations: "no round while disconnected" is proven indirectly (no publish failure is logged while the broker is down); the fake-clock tests leave about 0.3–0.8 s of real time for a round to cross the loopback broker; a state change and back (A→B→A) within 5 s sends no round (D-03); a round can be published partly when the connection drops mid-round (the Cloud discards incomplete rounds, D-04).
```text
$ dotnet test tests/Edge.Tests --filter "FullyQualifiedName~InventoryPublisherTests" -- RunConfiguration.TreatNoTestsAsError=true
  Determining projects to restore...
  All projects are up-to-date for restore.
  IndustrialIot.Shared -> C:\Users\case 046\Documents\Project\PlcProtocol\src\Shared\bin\Debug\net10.0\IndustrialIot.Shared.dll
  IndustrialIot.Edge -> C:\Users\case 046\Documents\Project\PlcProtocol\src\Edge\bin\Debug\net10.0\IndustrialIot.Edge.dll
  Edge.Tests -> C:\Users\case 046\Documents\Project\PlcProtocol\tests\Edge.Tests\bin\Debug\net10.0\Edge.Tests.dll
Test run for C:\Users\case 046\Documents\Project\PlcProtocol\tests\Edge.Tests\bin\Debug\net10.0\Edge.Tests.dll (.NETCoreApp,Version=v10.0)
A total of 1 test files matched the specified pattern.

Passed!  - Failed:     0, Passed:    11, Skipped:     0, Total:    11, Duration: 1 m 5 s - Edge.Tests.dll (net10.0)
```
