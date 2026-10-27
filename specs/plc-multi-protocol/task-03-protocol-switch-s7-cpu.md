# Task 03 — `Edge:Plc:Protocol` selects the driver; S7 CPU type 1200/1500

Status: done

## Outcome
The host registers exactly one driver, its options validator and its address syntax, chosen by `Edge:Plc:Protocol`;
S7 is the default; the S7 driver connects as the configured `CpuType`; commands cannot be enabled on a read-only
protocol (plan D-01, D-04).

## Scope
- In:
  - `Edge:Plc:Protocol` = `S7` (default) | `Slmp` (until task-04, `Slmp` fails the start with "not available yet");
    an unknown value fails the start naming `Edge:Plc:Protocol`.
  - `Edge:Commands:Enabled=true` with a protocol other than `S7` fails the start naming both keys.
  - `Edge:Plc:CpuType` = `S71200` (default) | `S71500` → S7netplus `CpuType` (`src/Edge/Drivers/SiemensS7Driver.cs:229`);
    an invalid value fails the start naming the key.
  - `AddEdgeAgent` calls the switch (`src/Edge/EdgeServiceCollectionExtensions.cs:28`); `AddSiemensS7Driver` stays as
    the S7 branch so `SiemensS7DriverTests` (`:229,242`) keep compiling.
  - `src/Edge/appsettings.json` drops `"Port": 102` from `Edge:Plc` (S7 keeps 102 from its code default,
    `src/Edge/Drivers/SiemensS7Options.cs:14`) and adds `"Protocol": "S7"`.
- Out: SLMP itself; Monitor changes (task-06).

## Coverage
- CP-02, CP-03

## Ownership
- Modify: `src/Edge/Drivers/DriverServiceCollectionExtensions.cs`, `src/Edge/Drivers/SiemensS7Options.cs`,
  `src/Edge/Drivers/SiemensS7Driver.cs` (CPU type only), `src/Edge/EdgeServiceCollectionExtensions.cs`, `src/Edge/appsettings.json`
- Create: `tests/Edge.Tests/PlcProtocolSwitchTests.cs`
- Read: `tests/Edge.Tests/Fakes/EdgeTestHost.cs`, `src/Edge/Commands/CommandsOptions.cs`

## Steps
1. Tests first: default protocol is S7 with S7-1200; `S71500` reaches the S7netplus `Plc` as `CpuType.S71500`
   (internal factory seam, no network); unknown protocol / unknown CPU type fail naming the key; `Slmp` fails with
   "not available yet"; commands enabled + a non-S7 protocol fails naming both keys; the shipped `appsettings.json`
   has no `Edge:Plc:Port` and S7 still uses 102.
2. Implement.

## Acceptance
- AC-02, AC-03: the new tests pass; all suites stay green.

## Dependencies
- task-02-batch-read-contract.md

## Verification Plan
- Command: `dotnet build IndustrialIot.sln -c Release && dotnet test tests/Edge.Tests && dotnet test tests/Deployment.Tests --filter "FullyQualifiedName~EndToEndTelemetryTests"`
- Named probe: `PlcProtocolSwitchTests`; `EndToEndTelemetryTests` (default path with only `Edge:Plc:Host` set).
- Reachability: source — real host start through `EdgeTestHost`.
- Oracle: build exit 0; `Failed: 0`, `Skipped: 0`.
- Counterexample: a hard-coded `CpuType.S71200` fails the S71500 case; registering both drivers lets an unknown
  protocol start; a missing commands guard lets `Commands=true` + `Slmp` start.
- Artifacts: ephemeral.

## Failure Protocol
On a failed Step or Verification Plan run: stop; do not widen scope, change the Command, or weaken a test; record observed versus expected; repair only the cited cause; after three failed rounds, stop and ask the user.

## Receipt

Verification: PASS
Command: dotnet build IndustrialIot.sln -c Release && dotnet test tests/Edge.Tests && dotnet test tests/Deployment.Tests --filter "FullyQualifiedName~EndToEndTelemetryTests"
Exit: 0
Base: f985f23f58461cb5364cf96e0815c54fa444d6ec
Head: b7a424cb9998a65732a8d93c65f25cfef085ec33072a00e07c18fc217f915654
```text
$ dotnet build IndustrialIot.sln -c Release && dotnet test tests/Edge.Tests && dotnet test tests/Deployment.Tests --filter "FullyQualifiedName~EndToEndTelemetryTests"
Test run for C:\Users\case 046\Documents\Project\PlcProtocol\tests\Edge.Tests\bin\Debug\net10.0\Edge.Tests.dll (.NETCoreApp,Version=v10.0)
Passed!  - Failed:     0, Passed:   337, Skipped:     0, Total:   337, Duration: 29 s - Edge.Tests.dll (net10.0)
Test run for C:\Users\case 046\Documents\Project\PlcProtocol\tests\Deployment.Tests\bin\Debug\net10.0\Deployment.Tests.dll (.NETCoreApp,Version=v10.0)
Passed!  - Failed:     0, Passed:     2, Skipped:     0, Total:     2, Duration: 3 m 12 s - Deployment.Tests.dll (net10.0)
```
- Pre-change run passed (Edge 325, E2E 2): the unmet probe was the absent PlcProtocolSwitchTests. After: Edge 337, E2E 2, build 0 warnings.
- Named probes (PlcProtocolSwitchTests): default S7 with S7-1200; CpuType S71500 reaches S7netplus via CreatePlc; unknown/numeric protocol and CpuType fail naming the key; Slmp fails 'not available yet'; Slmp + Commands names both keys; IPlcDriver registrations 1 for S7/absent, 0 for Slmp/unknown; shipped appsettings.json has no Edge:Plc:Port and S7 still binds 102. Startup cases run the real path (EdgeTestHost runAgentServices: true + StartAsync) and assert no 'Unable to resolve'.
- Release exe check (controller, after the final build): DOTNET_ENVIRONMENT=Production Edge__Plc__Protocol=Slmp -> OptionsValidationException: Edge:Plc:Protocol=Slmp is not available yet.; =Modbus -> Edge:Plc:Protocol must be one of S7, Slmp (was 'Modbus').
- Review: code-auditor FAIL (real host died with a DI error before ValidateOnStart; EdgeTestHost hid it) -> repair round 1 (composition-time validation) -> PASS. Limitation: the composition-time check sees only configuration sources present when AddEdgeAgent runs.
