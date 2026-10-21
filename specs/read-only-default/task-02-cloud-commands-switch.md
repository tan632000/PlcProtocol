# Task 02 — Cloud commands switch, default off

Status: done

## Outcome
Unless `Cloud:Commands:Enabled` is true, the Cloud maps no command route (404 for Viewer and Operator on all three),
subscribes no `iiot/+/command-results`, runs no timeout sweep, and leaves existing command rows untouched (plan D-03, D-06).

## Scope
- In: `CommandOptions.Enabled` (default false, validated on start); gating at run time through `IOptions<CommandOptions>`
  in `CommandEndpoints.Map`, `CommandResultIngestor.StartAsync` and `CommandTimeoutSweeper.ExecuteAsync`;
  `Cloud:Commands:Enabled=false` in `src/Cloud/appsettings.json`; `CloudTestHost` default `true` (plan D-07).
- Out: unsubscribing a stale session subscription (task-03); `CommandService` and the `commands` table.

## Coverage
- CP-02

## Ownership
- Modify: `src/Cloud/CloudOptions.cs`
- Modify: `src/Cloud/Api/CommandEndpoints.cs`
- Modify: `src/Cloud/Commands/CommandResultIngestor.cs`
- Modify: `src/Cloud/Commands/CommandTimeoutSweeper.cs`
- Modify: `src/Cloud/appsettings.json`
- Modify: `tests/Cloud.Tests/Infrastructure/CloudTestHost.cs`
- Create: `tests/Cloud.Tests/CommandsDisabledTests.cs`
- Read: `src/Cloud/EndpointModules.cs`, `src/Cloud/CloudServiceCollectionExtensions.cs`, `tests/Cloud.Tests/CommandApiTests.cs`, `tests/Edge.Tests/Fakes/TestMqttBroker.cs`

## Steps
1. `CommandOptions.Enabled` (a non-boolean value fails the host at start through the configuration binder's
   exception, which names `Cloud:Commands:Enabled`; no extra validator code needed).
2. `CommandEndpoints.Map`: resolve `IOptions<CommandOptions>` from `endpoints.ServiceProvider`; off → return without
   mapping. `CommandResultIngestor.StartAsync`: off → no `RegisterSubscription`. `CommandTimeoutSweeper`: off → return.
3. `appsettings.json`; `CloudTestHost` `["Cloud:Commands:Enabled"] = "true"`.
4. `CommandsDisabledTests` (key overridden with null): POST `/api/v1/edges/edge-a/commands` as Operator → 404; GET
   `/api/v1/commands/{guid}` and GET `/api/v1/edges/edge-a/commands` as Viewer and Operator → 404; seed one `commands`
   row with status `published` through `CloudDbContext`, publish a matching `CommandResult` on
   `iiot/edge-a/command-results`, then publish a telemetry marker and wait until it is stored → the row is still
   `published`; the broker recorded no subscription to `iiot/+/command-results` from the Cloud
   (`TestMqttBroker.Server.InterceptingSubscriptionAsync`); `Cloud:Commands:Enabled=yes` → host start fails with an exception
   whose message names the key. Control (default test host): the POST returns 202.

## Acceptance
- AC-03: every disabled-mode case passes.
- AC-04: the control passes and the full Cloud suite (`CommandApiTests`, `CommandFlowTests`, `LiveHubTests`,
  `TestGapTests`, `CloudHostTests` included) passes.

## Dependencies
- none

## Verification Plan
- Command: `dotnet test tests/Cloud.Tests`
- Named probe: `CommandsDisabledTests`; the command-dependent classes listed in Acceptance as regression.
- Reachability: source — real Cloud host via `CloudTestHost : WebApplicationFactory<Program>` on Testcontainers
  PostgreSQL; requires Docker.
- Oracle: `Failed: 0`, `Skipped: 0`; `CommandsDisabledTests` contributes ≥ 5 passed tests.
- Counterexample: today's always-mapped routes answer 202/200 and the ingestor turns the seeded row into a final status.
- Artifacts: ephemeral.

## Failure Protocol
On a failed Step or Verification Plan run: stop; do not widen scope, change the Command, or weaken a test; record observed versus expected; repair only the cited cause; after three failed rounds, stop and ask the user.

## Receipt

Verification: PASS
Command: dotnet test tests/Cloud.Tests
Exit: 0
Base: 2dd6c6feaf16dc24a691c8f999b04b84b824f237
Head: 5aba3a14d986665cd2b0888ae1cbd143aa686a1c64c3d620c02d12afc5a71c0e
```text
$ dotnet test tests/Cloud.Tests
Test run for C:\Users\case 046\Documents\Project\PlcProtocol\tests\Cloud.Tests\bin\Debug\net10.0\Cloud.Tests.dll (.NETCoreApp,Version=v10.0)
Passed!  - Failed:     0, Passed:   117, Skipped:     0, Total:   117, Duration: 1 m 27 s - Cloud.Tests.dll (net10.0)
```
- Pre-change run: 112 passed with 0 CommandsDisabledTests (oracle unmet); first run of this invocation found Docker Desktop stopped (task blocked, no edits), resumed once it was running.
- CommandsDisabledTests contributes 5 tests: every command route 404 for Operator and Viewer; a seeded published row stays published after a matching result, no command-results subscription (the recorder also proves the Cloud subscribed telemetry), sweeper never armed; non-boolean value fails start naming the key; enabled control returns 202 and subscribes results.
- Review: code-auditor PASS (0 critical/high/medium); only publish to iiot/+/commands is CommandService.cs:125, reachable only from the unmapped routes. Lows kept: sweeper WaitsArmed==0 is a weak oracle against today's code; control checks POST only (GETs covered by CommandApiTests).
