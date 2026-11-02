# Task 03 — The broker lets each Edge write only its own inventory topic and the Cloud read them all

Status: done

## Outcome
Both ACL files grant `pattern write iiot/%u/inventory` and, for `iiot-cloud`, `topic read iiot/+/inventory`; nothing
else changes, so the non-comment lines of `acl` and `acl-commands` still differ only by the Cloud's commands write; and
on the real broker an offline Cloud session that holds a QoS 1 telemetry subscription and a QoS 0 inventory subscription
queues the telemetry but no inventory (D-04).

## Scope
- In: the two ACL files; broker tests on the real mosquitto image for both ACL fixtures; the test helper returning the
  publish result and subscribing with a QoS per topic filter.
- Out: `mosquitto.conf` (unchanged), the secrets script.

## Coverage
- CP-03

## Ownership
- Modify: `deployments/mosquitto/acl`, `deployments/mosquitto/acl-commands`, `tests/Deployment.Tests/BrokerTests.cs`
- Read: `deployments/compose.yaml`, `deployments/mosquitto/mosquitto.conf`

## Steps
1. In `BrokerTests.cs`: make `MqttClientHelper.PublishAsync` return `MqttClientPublishResult` and let it subscribe with a
   QoS per filter; add `Inventory_OnlyOwnerWrites_CloudReads` to class `BrokerTests` (`acl`) and to class
   `CommandsBrokerTests` (`acl-commands`); add `AclFiles_DifferOnlyByTheCommandsWrite` (non-comment, non-blank lines) and
   `OfflineCloud_QueuesTelemetryButNoInventory` to class `BrokerTests` → red on the current ACL.
2. Edit both ACL files → green.

## Acceptance
- AC-03: with each ACL file, an Edge publishing on its own `iiot/<id>/inventory` reaches the Cloud subscriber; an Edge
  publishing on another Edge's inventory topic gets `NotAuthorized` and nothing is delivered; an Edge cannot subscribe to
  `iiot/+/inventory`; the non-comment lines of the two files differ only by `topic write iiot/+/commands`; with the
  Cloud's persistent session offline (telemetry QoS 1, inventory QoS 0), inventory published meanwhile is not delivered
  on reconnect and telemetry published meanwhile is.

## Dependencies
- none

## Verification Plan
- Command: `dotnet test tests/Deployment.Tests --filter "FullyQualifiedName~BrokerTests" -- RunConfiguration.TreatNoTestsAsError=true`
- Named probe: `BrokerTests.Inventory_OnlyOwnerWrites_CloudReads`, `CommandsBrokerTests.Inventory_OnlyOwnerWrites_CloudReads`,
  `BrokerTests.AclFiles_DifferOnlyByTheCommandsWrite`, `BrokerTests.OfflineCloud_QueuesTelemetryButNoInventory`, plus the
  existing broker cases (the filter matches both classes).
- Reachability: installed — the pinned mosquitto image the deployment uses (`deployments/compose.yaml`; Docker required).
- Oracle: all cases pass; a filter matching no test fails the command.
- Counterexample: `topic write iiot/+/inventory` for Edges, a change made in only one ACL file, or a QoS 1 inventory
  subscription (queued while offline) fails a named case.
- Artifacts: ephemeral containers removed by the harness.

## Failure Protocol
On a failed Step or Verification Plan run: stop; do not widen scope, change the Command, or weaken a test; record observed versus expected; repair only the cited cause; after three failed rounds, stop and ask the user.

## Receipt

Verification: PASS
Command: dotnet test tests/Deployment.Tests --filter "FullyQualifiedName~BrokerTests" -- RunConfiguration.TreatNoTestsAsError=true
Exit: 0
Base: 3e970eadd264a8548f3bdcabfab097ab95c8a6a0
Head: 14b32a1f92305f0de643bc042b5e37050155564b57fb48b84ee67817e4be74f7
Process: the new broker tests were written first and both Inventory_OnlyOwnerWrites_CloudReads cases failed on the old ACL (the Edge's own inventory publish answered NotAuthorized); OfflineCloud_QueuesTelemetryButNoInventory and AclFiles_DifferOnlyByTheCommandsWrite passed before the change for a stated reason (the old ACL denied inventory entirely; the two files already differed by one line), and their discrimination was shown by mutants. Mutants that each failed tests: Edges get `topic write iiot/+/inventory`; acl-commands not updated; the Cloud lacks the read; the offline probe subscribes inventory at QoS 1; Edges get `pattern readwrite iiot/%u/inventory`; Edges lose the inventory write (now also failing the offline probe). Files restored after every mutant. Full Deployment.Tests 41/41 against the pinned mosquitto image.
Review: fresh-context code review, PASS; its two Low test gaps (an Edge reading its own inventory; unasserted publishes in the offline probe) were repaired and re-reviewed PASS.
Changed helper: MqttClientHelper.PublishAsync returns MqttClientPublishResult and SubscribeAsync takes an optional QoS; existing callers are unchanged (QoS 1 by default).
Limitations: the read denials are proven by non-delivery (mosquitto 2.0 checks read rights at delivery, so the SUBACK is granted); the Cloud identity also matches `pattern write iiot/%u/inventory` (as for telemetry); docs (upgrade order, troubleshooting row for "refused iiot/+/inventory", server manual broker step) are task-06's.
```text
$ dotnet test tests/Deployment.Tests --filter "FullyQualifiedName~BrokerTests" -- RunConfiguration.TreatNoTestsAsError=true
  Determining projects to restore...
  All projects are up-to-date for restore.
  IndustrialIot.Shared -> C:\Users\case 046\Documents\Project\PlcProtocol\src\Shared\bin\Debug\net10.0\IndustrialIot.Shared.dll
  IndustrialIot.Edge -> C:\Users\case 046\Documents\Project\PlcProtocol\src\Edge\bin\Debug\net10.0\IndustrialIot.Edge.dll
  IndustrialIot.Cloud -> C:\Users\case 046\Documents\Project\PlcProtocol\src\Cloud\bin\Debug\net10.0\IndustrialIot.Cloud.dll
  Deployment.Tests -> C:\Users\case 046\Documents\Project\PlcProtocol\tests\Deployment.Tests\bin\Debug\net10.0\Deployment.Tests.dll
Test run for C:\Users\case 046\Documents\Project\PlcProtocol\tests\Deployment.Tests\bin\Debug\net10.0\Deployment.Tests.dll (.NETCoreApp,Version=v10.0)
A total of 1 test files matched the specified pattern.

Passed!  - Failed:     0, Passed:    17, Skipped:     0, Total:    17, Duration: 11 s - Deployment.Tests.dll (net10.0)
```
