# Task 01 — Edge commands switch, default off

Status: done

## Outcome
Unless `Edge:Commands:Enabled` is true, the Edge's command path is inert: no subscription to `iiot/{edgeId}/commands`,
no command executed or answered, no replay/purge loop, one start-up log line saying remote writes are disabled; a
non-boolean value fails the start (plan D-02, D-06).

## Scope
- In: `CommandsOptions` (`Edge:Commands`, `Enabled` default false) bound with validation on start; `CommandListener`
  reads it (`IOptions<CommandsOptions>`): when off, `StartAsync` registers nothing and `ExecuteAsync` returns after the
  log line; `"Commands": { "Enabled": false }` in `src/Edge/appsettings.json`; `EdgeTestHost` default `true` (plan D-07).
- Out: unsubscribing a stale session subscription (task-03); the Cloud and broker layers.

## Coverage
- CP-01

## Ownership
- Create: `src/Edge/Commands/CommandsOptions.cs`
- Modify: `src/Edge/Commands/CommandListener.cs`
- Modify: `src/Edge/EdgeServiceCollectionExtensions.cs` (options binding only; the three services stay registered)
- Modify: `src/Edge/appsettings.json`
- Modify: `tests/Edge.Tests/Fakes/EdgeTestHost.cs`
- Create: `tests/Edge.Tests/CommandsSwitchTests.cs`
- Read: `tests/Edge.Tests/CommandFlowTests.cs`, `tests/Edge.Tests/Fakes/TestMqttBroker.cs`, `tests/Edge.Tests/Fakes/FakePlcDriver.cs`

## Steps
1. `CommandsOptions` + binding with `ValidateOnStart` in `AddEdgeAgent` (a value the binder cannot parse as bool must
   fail start with a message naming `Edge:Commands:Enabled`).
2. `CommandListener`: inject `IOptions<CommandsOptions>`; off → no `RegisterSubscription`, Information log "Remote writes
   are disabled (Edge:Commands:Enabled=false); the Edge does not subscribe to commands.", `ExecuteAsync` returns.
3. `appsettings.json` `Commands` section; `EdgeTestHost.DefaultSettings` `["Edge:Commands:Enabled"] = "true"`.
4. `CommandsSwitchTests` (own `ILoggerProvider` capture; `runAgentServices: true`; a `FakePlcDriver`; tag override
   `Edge:Tags:0:Writable=true` so today's code would write and answer):
   (a) key removed (null) → after the host is connected (marker: a telemetry sample reaches the broker), publish a valid
   `DeviceCommand` on `iiot/test-edge/commands`; wait 2 s; assert no write on the fake driver, no message on
   `iiot/test-edge/command-results`, the broker saw no subscription to `iiot/test-edge/commands` from the Edge (record
   with `TestMqttBroker.Server.InterceptingSubscriptionAsync`), and the log line is present;
   (b) `Edge:Commands:Enabled=true` → the same command is written to the fake driver and answered (control);
   (c) `Edge:Commands:Enabled=yes` → `StartAsync` throws `OptionsValidationException` (or the binder's exception) naming
   the key; (d) the shipped `src/Edge/appsettings.json` has `Edge:Commands:Enabled` = false.

## Acceptance
- AC-01: (a), (c), (d) pass.
- AC-02: (b) passes and the full Edge suite (command, publisher and monitor tests included) passes.

## Dependencies
- none

## Verification Plan
- Command: `dotnet test tests/Edge.Tests`
- Named probe: `CommandsSwitchTests` (a)–(d); `CommandFlowTests`, `CommandExecutorTests`, `TelemetryPublisherTests` as regression.
- Reachability: source — real Edge host through `EdgeTestHost` with the in-process TLS `TestMqttBroker`.
- Oracle: `Failed: 0`, `Skipped: 0`; `CommandsSwitchTests` contributes ≥ 4 passed tests.
- Counterexample: today's listener subscribes and answers → (a) fails on the result message and the subscription record;
  a default of true fails (a) or (d).
- Artifacts: ephemeral.

## Failure Protocol
On a failed Step or Verification Plan run: stop; do not widen scope, change the Command, or weaken a test; record observed versus expected; repair only the cited cause; after three failed rounds, stop and ask the user.

## Receipt

Verification: PASS
Command: dotnet test tests/Edge.Tests
Exit: 0
Base: 2dd6c6feaf16dc24a691c8f999b04b84b824f237
Head: 2a2b793ba9fbe5d3bffac2712038099133929b2a117d546070333c8ad2da3f18
```text
$ dotnet test tests/Edge.Tests
Test run for C:\Users\case 046\Documents\Project\PlcProtocol\tests\Edge.Tests\bin\Debug\net10.0\Edge.Tests.dll (.NETCoreApp,Version=v10.0)
Passed!  - Failed:     0, Passed:   303, Skipped:     0, Total:   303, Duration: 28 s - Edge.Tests.dll (net10.0)
```
- Pre-change run: 299 passed with 0 CommandsSwitchTests (oracle unmet).
- CommandsSwitchTests contributes 4 tests: default (no subscription, no write, no result, log line; the recorder also proves the Edge subscribed telemetry-acks), enabled control, non-boolean value fails start naming the key, shipped appsettings keeps commands off.
- Review: code-auditor PASS (0 critical/high/medium). Low kept: the non-boolean test checks the key in the exception text, not the exception type.
