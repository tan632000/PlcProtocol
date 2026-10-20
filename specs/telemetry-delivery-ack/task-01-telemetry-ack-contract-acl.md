# Task 01 — `TelemetryAck` contract, topic and broker ACL

Status: done

## Outcome
A validated `TelemetryAck` JSON contract exists in `src/Shared`, the Edge knows its topic `iiot/{edgeId}/telemetry-acks`, and the real broker ACL lets only the Cloud write that topic and only the owning Edge read it.

## Scope
- In: contract record + validation + JSON round trip, following the existing contracts (`src/Shared/Contracts/CommandResult.cs:6-38`) and the shared test theories (`tests/Shared.Tests/ContractRoundTripTests.cs:53-128`); `MqttTopics.TelemetryAcks` (`src/Edge/Messaging/MqttTopics.cs:9-16`); ACL lines (`deployments/mosquitto/acl:4-13`); broker ACL tests.
- Out: publishing or consuming acks (task-02, task-03).

## Coverage
- CP-01

## Ownership
- Create: `src/Shared/Contracts/TelemetryAck.cs`
- Modify: `src/Edge/Messaging/MqttTopics.cs`
- Modify: `deployments/mosquitto/acl`
- Modify: `tests/Shared.Tests/ContractRoundTripTests.cs`
- Modify: `tests/Deployment.Tests/BrokerTests.cs`
- Read: `src/Shared/Common/ContractJson.cs` (`ContractRules`, `IContract`, `ContractJson`), `src/Shared/Contracts/TelemetryPayload.cs`

## Steps
1. `TelemetryAck : IContract` with required `SchemaVersion` (1), `EdgeId`, `StreamId` (Guid, non-empty), `FromSequence`, `ToSequence` (`0 ≤ from ≤ to`, `to − from < 10 000`); `Validate()` uses `ContractRules`; serialized camelCase like the other contracts.
2. Add `TelemetryAck` rows to the existing theories (`RoundTrip_Equal`, `FieldNames_MatchSchema`, `Invalid_ThrowsValidation` with one `kind` per AC-01 rejection), plus at least one `[Fact]` whose name starts with `TelemetryAck_`.
3. `MqttTopics.TelemetryAcks => $"iiot/{edgeId}/telemetry-acks"`; update the class comment ("three topics" → four).
4. ACL: under `user iiot-cloud` add `topic write iiot/+/telemetry-acks`; under the patterns add `pattern read iiot/%u/telemetry-acks`.
5. `BrokerTests`: extend `CloudAcl_CommandsOnly` / `EdgeAcl_OwnTopicsOnly` (or add one test) proving: the Cloud can publish to `iiot/e1/telemetry-acks`; Edge `e1` receives it; Edge `e2` cannot subscribe to `iiot/e1/telemetry-acks`; an Edge cannot publish to its own `telemetry-acks`.

## Acceptance
- AC-01: every listed invalid ack throws `ContractValidationException`; a valid one round-trips equal with the expected camelCase field names.
- AC-02: the four broker cases in Step 5 pass against the real Mosquitto container with the repo ACL.

## Dependencies
- none

## Verification Plan
- Command: `dotnet test tests/Shared.Tests && dotnet test tests/Shared.Tests --list-tests | grep -c TelemetryAck && dotnet test tests/Deployment.Tests --filter "FullyQualifiedName~BrokerTests"`
- Named probe: `ContractRoundTripTests` theory rows and `TelemetryAck_*` facts; `BrokerTests` ACL tests covering `telemetry-acks`.
- Reachability: source (Shared.Tests) + installed (Testcontainers Mosquitto with `deployments/mosquitto/acl` bind-mounted, `tests/Deployment.Tests/BrokerTests.cs:529-545`); requires Docker and Git Bash.
- Oracle: both test runs `Failed: 0`, `Skipped: 0`; the `grep -c` prints a number ≥ 1.
- Counterexample: an ACL granting `pattern write iiot/%u/telemetry-acks` lets an Edge forge its own acks → the "Edge cannot publish" case fails.
- Artifacts: ephemeral (test output).

## Failure Protocol
On a failed Step or Verification Plan run: stop; do not widen scope, change the Command, or weaken a test; record observed versus expected; repair only the cited cause; after three failed rounds, stop and ask the user.

## Receipt

Verification: PASS
Command: dotnet test tests/Shared.Tests && dotnet test tests/Shared.Tests --list-tests | grep -c TelemetryAck && dotnet test tests/Deployment.Tests --filter "FullyQualifiedName~BrokerTests"
Exit: 0
Base: 8f7e6c753cf52c4a9e65d36b996f95edd7ab5959
Head: 81fce74ae00b2584759319feff1fd0cb1d29d8a4d20ff22e2e97b305e57f1869
```text
$ dotnet test tests/Shared.Tests && dotnet test tests/Shared.Tests --list-tests | grep -c TelemetryAck && dotnet test tests/Deployment.Tests --filter "FullyQualifiedName~BrokerTests"
Test run for C:\Users\case 046\Documents\Project\PlcProtocol\tests\Shared.Tests\bin\Debug\net10.0\Shared.Tests.dll (.NETCoreApp,Version=v10.0)
Passed!  - Failed:     0, Passed:    55, Skipped:     0, Total:    55, Duration: 112 ms - Shared.Tests.dll (net10.0)
1
Test run for C:\Users\case 046\Documents\Project\PlcProtocol\tests\Deployment.Tests\bin\Debug\net10.0\Deployment.Tests.dll (.NETCoreApp,Version=v10.0)
Passed!  - Failed:     0, Passed:    12, Skipped:     0, Total:    12, Duration: 13 s - Deployment.Tests.dll (net10.0)
```
- Review: code-auditor PASS (0 critical/high/medium); broker test confirmed as a real oracle (a permitted own-ack would be echoed back to line1; a readable foreign ack would reach line2).
- Final-Head rebind: fresh run at the feature's final Head (full solution 480/480 passed in the same pass).
