# Task 06 — A sample client lists an Edge's PLCs, tags and realtime values from the documented API

Status: done

## Outcome
`docs/scada-integration.md` explains how a SCADA project gets a token, lists Edges, devices and tags, reads latest
values and history, joins `/hubs/live` for `telemetry` and `devicesUpdated`, and sends commands; `samples/InventoryClient`
(D-07) does exactly what the doc shows and never prints its token; the upgrade order (D-08) is documented for the trial
stack and the CD-managed server; an end-to-end test (D-09) runs the sample's logic against a real broker, Postgres,
Cloud on Kestrel and a two-device Edge and sees the PLCs, tags and at least one pushed value.

## Scope
- In: sample project (added to `IndustrialIot.sln`), integration doc, upgrade notes and one troubleshooting row in
  `docs/deployment.md`, the manual broker step in `docs/server-deploy.md`, the shared simulated PLC, end-to-end test.
- Out: changes to the API or contract (tasks 01–05).

## Coverage
- CP-06

## Ownership
- Create: `samples/InventoryClient/InventoryClient.csproj`, `samples/InventoryClient/Program.cs`,
  `docs/scada-integration.md`, `tests/Deployment.Tests/EndToEndInventoryTests.cs`, `tests/Deployment.Tests/SimulatedPlc.cs`
- Modify: `IndustrialIot.sln`, `docs/deployment.md`, `docs/server-deploy.md`, `tests/Deployment.Tests/Deployment.Tests.csproj`
  (project reference to the sample), `tests/Deployment.Tests/EndToEndTelemetryTests.cs` (only to use the moved simulated PLC)
- Read: `deployments/scripts/get-token.sh`, `deployments/scripts/remote-deploy.sh`, `src/Cloud/Security/AuthenticationSetup.cs`,
  `tests/Cloud.Tests/Infrastructure/CloudTestHost.cs`

## Steps
1. Move the simulated PLC driver out of `EndToEndTelemetryTests` into `SimulatedPlc.cs` (behavior unchanged; the
   existing end-to-end tests stay green).
2. Write `EndToEndInventoryTests` per D-09 (Cloud on Kestrel with a static RSA key, a test-signed Viewer token, an Edge
   with `Edge:Channels` and two simulated PLCs, the sample's entry point given the Cloud URL, token and Edge id) → red.
3. Write the sample (its logic in a public method the test calls with an output writer; `Main` only reads the
   environment) and the docs → green.

## Acceptance
- AC-06:
  - Within 30 s of the Edge starting, the sample prints both devices with their model and state, every tag of each
    device, and at least one `telemetry` value received over SignalR; with `--seconds 5` it exits 0.
  - With a wrong token it exits non-zero printing the 401 status only; in every case its output never contains the token
    text (asserted on the captured stdout/stderr).
  - `SampleSource_SendsTheTokenOnlyAsAHeader`: the sample's source contains no `access_token` and sets the token only on
    the `Authorization` header and SignalR's `AccessTokenProvider`.
  - `IntegrationDoc_ListsEveryRequestTheSampleMakes`: every route the sample calls (recorded by the test from the Cloud's
    request log) appears in `docs/scada-integration.md`.
  - `UpgradeDocs_StateTheBrokerFirst`: `docs/deployment.md` states broker → Cloud → Edges and has a row for "Broker
    refused subscription(s): iiot/+/inventory"; `docs/server-deploy.md` states that the ACL change is a manual broker
    upgrade done before CD deploys this commit.
  - `EndToEndTelemetryTests` still pass with the moved simulated PLC.

## Dependencies
- task-02-edge-inventory-publisher.md
- task-03-broker-acl.md
- task-05-devices-api.md

## Verification Plan
- Command: `dotnet build IndustrialIot.sln -c Release && dotnet test tests/Deployment.Tests -c Release --no-build --filter "FullyQualifiedName~EndToEndInventoryTests|FullyQualifiedName~EndToEndTelemetryTests" -- RunConfiguration.TreatNoTestsAsError=true`
- Named probe: `EndToEndInventoryTests` (devices, tags, pushed value, `--seconds` exit, 401 exit, no token in output,
  `SampleSource_SendsTheTokenOnlyAsAHeader`, `IntegrationDoc_ListsEveryRequestTheSampleMakes`, `UpgradeDocs_StateTheBrokerFirst`) and
  `EndToEndTelemetryTests` (unchanged behavior).
- Reachability: live — real mosquitto with the task-03 ACL, Postgres, Cloud on Kestrel and an Edge as D-09 runs them
  (Docker required).
- Oracle: every case passes; a filter matching no test fails the command.
- Counterexample: an Edge without the publisher, or a broker with the old ACL, leaves `devices` empty; a sample that
  prints the token fails the output assertion.
- Artifacts: ephemeral containers removed by the harness.

## Failure Protocol
On a failed Step or Verification Plan run: stop; do not widen scope, change the Command, or weaken a test; record observed versus expected; repair only the cited cause; after three failed rounds, stop and ask the user.

## Receipt

Verification: PASS
Command: dotnet build IndustrialIot.sln -c Release && dotnet test tests/Deployment.Tests -c Release --no-build --filter "FullyQualifiedName~EndToEndInventoryTests|FullyQualifiedName~EndToEndTelemetryTests" -- RunConfiguration.TreatNoTestsAsError=true
Exit: 0
Base: 3e970eadd264a8548f3bdcabfab097ab95c8a6a0
Head: d334576d4579ccc3471095fcfb6c63678bb5887a203d1d176b4fe2586895ecd5
Process: the simulated PLC moved out of EndToEndTelemetryTests unchanged (both end-to-end telemetry tests still pass in the exact Command); the end-to-end inventory tests were written first and all failed against an empty sample and missing docs while the stack itself (Postgres, the repository broker, the Cloud on Kestrel with a static signing key, a two-PLC Edge) already came up and stored the inventory; discrimination shown by mutants that each failed tests (sample prints the token on failure; sample puts the token in the hub URL; sample never joins the hub; Edge without the inventory publisher; broker with the old ACL; doc missing the tags route; sample ignoring a closed hub); files restored and rebuilt after every mutant run.
Review: fresh-context code review, PASS after one repair round. Medium: the Cloud closes the hub when the token expires (CloseOnAuthenticationExpiration, Keycloak default about 5 minutes), so docs/scada-integration.md now tells long-running clients to handle Closed, get a new token, reconnect and re-join, and the sample reports "hub closed" and exits 1 (test with a 10 s token). Lows fixed: /edges time format, 401/403 titles, [from, to) history paging, null value on bad quality, canWrite ignores stale (documented as "treat as not writable"), --seconds validation, state asserted, no access_token in any request query (runtime check), cleanup that runs every step.
Docs: docs/scada-integration.md (new), docs/deployment.md (broker → Cloud → Edge for the inventory topic, troubleshooting row "Broker refused subscription(s): iiot/+/inventory"), docs/server-deploy.md (the ACL change is a manual broker upgrade before CD deploys the Cloud).
Open question for the user at GATE-DONE: should canWrite be false while the Edge is stale (D-06 does not involve staleness today).
Limitations: FreePort then UseKestrel is check-then-use and could rarely collide with a parallel test; the /devices and /tags doc examples show ".900Z"/".000Z" while System.Text.Json writes the shortest fraction; _secrets.Dispose in the fixture cleanup is not wrapped; the sample does not reconnect by itself (it reports the close, as the doc describes); the end-to-end tests need Docker.
```text
$ dotnet build IndustrialIot.sln -c Release && dotnet test tests/Deployment.Tests -c Release --no-build --filter "FullyQualifiedName~EndToEndInventoryTests|FullyQualifiedName~EndToEndTelemetryTests" -- RunConfiguration.TreatNoTestsAsError=true
  Determining projects to restore...
  All projects are up-to-date for restore.
  InventoryClient -> C:\Users\case 046\Documents\Project\PlcProtocol\samples\InventoryClient\bin\Release\net10.0\InventoryClient.dll
  IndustrialIot.Shared -> C:\Users\case 046\Documents\Project\PlcProtocol\src\Shared\bin\Release\net10.0\IndustrialIot.Shared.dll
  Shared.Tests -> C:\Users\case 046\Documents\Project\PlcProtocol\tests\Shared.Tests\bin\Release\net10.0\Shared.Tests.dll
  IndustrialIot.Edge -> C:\Users\case 046\Documents\Project\PlcProtocol\src\Edge\bin\Release\net10.0\IndustrialIot.Edge.dll
  IndustrialIot.Cloud -> C:\Users\case 046\Documents\Project\PlcProtocol\src\Cloud\bin\Release\net10.0\IndustrialIot.Cloud.dll
  Edge.Tests -> C:\Users\case 046\Documents\Project\PlcProtocol\tests\Edge.Tests\bin\Release\net10.0\Edge.Tests.dll
  Cloud.Tests -> C:\Users\case 046\Documents\Project\PlcProtocol\tests\Cloud.Tests\bin\Release\net10.0\Cloud.Tests.dll
  Deployment.Tests -> C:\Users\case 046\Documents\Project\PlcProtocol\tests\Deployment.Tests\bin\Release\net10.0\Deployment.Tests.dll

Build succeeded.
    0 Warning(s)
    0 Error(s)

Time Elapsed 00:00:03.04
Test run for C:\Users\case 046\Documents\Project\PlcProtocol\tests\Deployment.Tests\bin\Release\net10.0\Deployment.Tests.dll (.NETCoreApp,Version=v10.0)
A total of 1 test files matched the specified pattern.

Passed!  - Failed:     0, Passed:     8, Skipped:     0, Total:     8, Duration: 3 m 10 s - Deployment.Tests.dll (net10.0)
```
