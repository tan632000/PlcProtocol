# Task 04 — Read-only default ACL and `acl-commands` variant

Status: done

## Outcome
With the default `deployments/mosquitto/acl` no identity can deliver a command to an Edge, while every Edge's
subscription to its commands topic is granted and its telemetry and acks flow; `deployments/mosquitto/acl-commands`
restores today's rights (plan D-04).

## Scope
- In: drop `topic write iiot/+/commands` from `acl` with a comment; new `acl-commands` = today's file; `BrokerFixture`
  takes the ACL file name; `MqttClientHelper.SubscribeAsync` returns the SUBACK result; tests for both variants.
- Out: compose and docs (task-05); any other ACL rule.

## Coverage
- CP-04

## Ownership
- Modify: `deployments/mosquitto/acl`
- Create: `deployments/mosquitto/acl-commands`
- Modify: `tests/Deployment.Tests/BrokerTests.cs`
- Read: `deployments/mosquitto/mosquitto.conf`, `tests/Deployment.Tests/EndToEndTelemetryTests.cs` (its capped broker mounts `acl`)

## Steps
1. `acl-commands`: copy of today's `acl`, header "write mode: the Cloud may send commands to the Edges".
2. `acl`: remove `topic write iiot/+/commands`; comment that this is the read-only default and that the Edge read pattern
   stays on purpose.
3. `BrokerFixture` (today `sealed` with a private constructor, `tests/Deployment.Tests/BrokerTests.cs:514,528`): unseal it,
   make the constructor `protected` with an optional `aclFile` (default `"acl"`), add `StartAsync(secrets, aclFile)`;
   a second class fixture type (e.g. `CommandsBrokerFixture : BrokerFixture` using `acl-commands`) for a new test class
   that holds `EdgeAcl_OwnTopicsOnly`, `PersistentSession_RedeliversUnacked`, `EdgeAcl_WildcardSharedSysAndWillDenied`
   and `CloudAcl_CommandsOnly` (moved, unchanged).
4. `MqttClientHelper.SubscribeAsync` returns `MqttClientSubscribeResult` (existing callers ignore it).
5. New read-only test on the default fixture: Edge `line1-edge01` subscribes `iiot/line1-edge01/commands` → result code
   `GrantedQoS1`; the Cloud publishes a command there; the Cloud then publishes a telemetry ack to the Edge as marker →
   the ack arrives, the command does not; the Edge's telemetry reaches the Cloud.

## Acceptance
- AC-06: the read-only test passes on `acl`.
- AC-07: the four moved tests pass on `acl-commands`; all other broker tests pass on `acl`.

## Dependencies
- none

## Verification Plan
- Command: `dotnet test tests/Deployment.Tests --filter "FullyQualifiedName~BrokerTests"`
- Named probe: the new read-only test; the four moved tests on `acl-commands`; the remaining `BrokerTests` as regression.
- Reachability: installed — Testcontainers Mosquitto with the repository files bind-mounted
  (`tests/Deployment.Tests/BrokerTests.cs:552-563`); requires Docker and Git Bash.
- Oracle: `Failed: 0`, `Skipped: 0`, > 0 tests; the read-only test asserts `GrantedQoS1`.
- Counterexample: keeping `topic write iiot/+/commands` in `acl` delivers the command; removing the Edge read pattern
  turns the SUBACK into `NotAuthorized`; either fails the read-only test.
- Artifacts: ephemeral.

## Failure Protocol
On a failed Step or Verification Plan run: stop; do not widen scope, change the Command, or weaken a test; record observed versus expected; repair only the cited cause; after three failed rounds, stop and ask the user.

## Receipt

Verification: PASS
Command: dotnet test tests/Deployment.Tests --filter "FullyQualifiedName~BrokerTests"
Exit: 0
Base: 2dd6c6feaf16dc24a691c8f999b04b84b824f237
Head: fcbfce41a477c4b1d4ec3e002b11df6f0478e863922305845b27bda306f83092
```text
$ dotnet test tests/Deployment.Tests --filter "FullyQualifiedName~BrokerTests"
Test run for C:\Users\case 046\Documents\Project\PlcProtocol\tests\Deployment.Tests\bin\Debug\net10.0\Deployment.Tests.dll (.NETCoreApp,Version=v10.0)
Passed!  - Failed:     0, Passed:    13, Skipped:     0, Total:    13, Duration: 14 s - Deployment.Tests.dll (net10.0)
```
- Named probe: BrokerTests.ReadOnlyDefault_CommandNeverReachesTheEdge passes on acl (asserts GrantedQoS1); --list-tests shows EdgeAcl_OwnTopicsOnly, EdgeAcl_WildcardSharedSysAndWillDenied, CloudAcl_CommandsOnly, PersistentSession_RedeliversUnacked under CommandsBrokerTests (acl-commands).
- Counterexample 1 (topic write iiot/+/commands re-added to acl): read-only test failed, Actual [command, ack-marker].
- Limitation: counterexample 2 as written does not hold. With pattern read iiot/%u/commands removed, Mosquitto still answered GrantedQoS1 (read ACL is enforced at delivery, not in the SUBACK), so the GrantedQoS1 assertion cannot detect that removal; the read pattern is unchanged by static diff. acl restored before the run above.
- Review: code-auditor PASS (0 Critical/High/Medium); Low 1-2 (misplaced doc comment, read-pattern comment wording) fixed before this run; Low 4 carried to task-05 docs: an ACL mismatch shows as silent non-delivery, not a refused subscription.
