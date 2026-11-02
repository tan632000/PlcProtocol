# Task 05 — String values travel from the Edge to the Cloud and back in commands

Status: done

## Outcome
The shared contract accepts a bounded JSON string as a telemetry or command value (plan D-09); the Edge publishes
`string` and `double` driver values; a value the contract would reject becomes a Bad sample instead of breaking the
batch; the Cloud stores and returns string values unchanged.

## Scope
- In:
  - `ContractRules.ScalarValue`: `true`, `false`, finite number, or a string of at most 256 UTF-16 chars; the error text
    names the limit. Used by telemetry samples and `DeviceCommand.TargetValue` (`src/Shared/Contracts/DeviceCommand.cs:78`).
  - `TagPoller.ToSample`: `string` → JSON string, `double` → number; any other or over-long value → Bad sample with a
    per-tag error, the rest of the cycle published.
  - Cloud `CanonicalJson`: a JSON string stays a JSON string (today it calls `GetDouble()`, `src/Cloud/Ingestion/TelemetryIngestor.cs:256-261`).
- Out: drivers producing strings (task-06/07); the `/scada/` viewer's formatting.

## Coverage
- CP-08

## Ownership
- Modify: `src/Shared/Common/ContractJson.cs`, `src/Edge/Polling/TagPoller.cs` (`ToSample` only), `src/Cloud/Ingestion/TelemetryIngestor.cs` (`CanonicalJson` only)
- Modify: `tests/Shared.Tests/ContractRoundTripTests.cs`, `tests/Edge.Tests/TagPollerTests.cs`, `tests/Cloud.Tests/TelemetryIngestionTests.cs`,
  `tests/Cloud.Tests/CommandApiTests.cs`
- Read: `src/Shared/Contracts/TelemetryPayload.cs`, `src/Shared/Contracts/DeviceCommand.cs`, `src/Edge/Buffering/TelemetryStore.cs`,
  `src/Cloud/Data/CloudDbContext.cs`

## Steps
1. Tests first: a telemetry payload and a command with value `"AB"` round-trip; a 257-char string is rejected naming the
   limit; an object or array value is still rejected; a fake driver returning `"2026-11-02T10:15:30.123"` and `1.5`
   publishes a string and a number; a fake driver returning an unsupported object makes that tag Bad and the other tags
   of the cycle are stored; the Cloud stores `"AB"` and `GET` returns `"AB"`; a command with a string target is accepted
   by the Cloud API.
2. Implement the three changes.

## Acceptance
- AC-11: every case above passes.

## Dependencies
- task-03-commands-and-monitor.md

## Verification Plan
- Command: `dotnet build IndustrialIot.sln -c Release && dotnet test tests/Shared.Tests && dotnet test tests/Edge.Tests && dotnet test tests/Cloud.Tests && dotnet test tests/Deployment.Tests`
- Named probe: `ContractRoundTripTests`, `TagPollerTests` (string, double, Bad sample), `TelemetryIngestionTests` (stored string), `CommandApiTests` (string target).
- Reachability: source — real contract types, the real poller with a fake driver, the real Cloud ingest against PostgreSQL (Testcontainers).
- Oracle: build exit 0; `Failed: 0`, `Skipped: 0` in all four suites.
- Counterexample: a contract that still rejects strings fails the round-trip; `GetDouble()` on a string throws in ingest;
  one unsupported value that throws in the store drops the whole cycle.
- Artifacts: ephemeral.

## Failure Protocol
On a failed Step or Verification Plan run: stop; do not widen scope, change the Command, or weaken a test; record observed versus expected; repair only the cited cause; after three failed rounds, stop and ask the user.

## Receipt

Verification: PASS
Command: dotnet build IndustrialIot.sln -c Release && dotnet test tests/Shared.Tests && dotnet test tests/Edge.Tests && dotnet test tests/Cloud.Tests && dotnet test tests/Deployment.Tests
Exit: 0
Base: d8d498a1bdb6249b1b7d46c12d0092edd9cc3f65
Head: 656ea7d86205c9f94d90be539ba57f1b7d489099564ef6b5ab4b5c7190f9f424
Changed existing assertions (the contract this task changes): `ContractRoundTripTests` row "string targetValue" is now "array targetValue" (`["1500"]`); `CommandApiTests` row `"value":"abc"` is now `["abc"]`; both are still refused. The call site of `TagPoller.ToSample` in `RunCycleAsync` changed with its return type.
Runtime evidence: with the U+0000 rule disabled (mutation), `TelemetryIngestionTests.StringValueWithNul_IsRefused_WithoutStallingTheQueue` timed out waiting for the marker, so a NUL sample stalls the Cloud ingest queue; the rule was restored before the run below.
Review: fresh-context code review, PASS after one repair round (one shared rule, `ContractJson.StringValueProblem`: at most 256 chars, no U+0000, no lone surrogate, applied by the contract and by the Edge poller).
Notes for task-10 docs: upgrade the Cloud before the Edges once drivers produce strings (an older Cloud refuses string telemetry and the Edge drops it); the jsonb storage of strings assumes a UTF8 PostgreSQL database.
Limitations: the per-tag error texts of refused values are not asserted; a comment in `TagPoller.ToSample` still says "text over the limit" for every refused text.
```text
$ dotnet build IndustrialIot.sln -c Release && dotnet test tests/Shared.Tests && dotnet test tests/Edge.Tests && dotnet test tests/Cloud.Tests && dotnet test tests/Deployment.Tests
  Determining projects to restore...
  All projects are up-to-date for restore.
  IndustrialIot.Shared -> C:\Users\case 046\Documents\Project\PlcProtocol\src\Shared\bin\Release\net10.0\IndustrialIot.Shared.dll
  IndustrialIot.Cloud -> C:\Users\case 046\Documents\Project\PlcProtocol\src\Cloud\bin\Release\net10.0\IndustrialIot.Cloud.dll
  Shared.Tests -> C:\Users\case 046\Documents\Project\PlcProtocol\tests\Shared.Tests\bin\Release\net10.0\Shared.Tests.dll
  IndustrialIot.Edge -> C:\Users\case 046\Documents\Project\PlcProtocol\src\Edge\bin\Release\net10.0\IndustrialIot.Edge.dll
  Cloud.Tests -> C:\Users\case 046\Documents\Project\PlcProtocol\tests\Cloud.Tests\bin\Release\net10.0\Cloud.Tests.dll
  Deployment.Tests -> C:\Users\case 046\Documents\Project\PlcProtocol\tests\Deployment.Tests\bin\Release\net10.0\Deployment.Tests.dll
  Edge.Tests -> C:\Users\case 046\Documents\Project\PlcProtocol\tests\Edge.Tests\bin\Release\net10.0\Edge.Tests.dll

Build succeeded.
    0 Warning(s)
    0 Error(s)

Time Elapsed 00:00:02.32
  Determining projects to restore...
  All projects are up-to-date for restore.
  IndustrialIot.Shared -> C:\Users\case 046\Documents\Project\PlcProtocol\src\Shared\bin\Debug\net10.0\IndustrialIot.Shared.dll
  Shared.Tests -> C:\Users\case 046\Documents\Project\PlcProtocol\tests\Shared.Tests\bin\Debug\net10.0\Shared.Tests.dll
Test run for C:\Users\case 046\Documents\Project\PlcProtocol\tests\Shared.Tests\bin\Debug\net10.0\Shared.Tests.dll (.NETCoreApp,Version=v10.0)
A total of 1 test files matched the specified pattern.

Passed!  - Failed:     0, Passed:    61, Skipped:     0, Total:    61, Duration: 143 ms - Shared.Tests.dll (net10.0)
  Determining projects to restore...
  All projects are up-to-date for restore.
  IndustrialIot.Shared -> C:\Users\case 046\Documents\Project\PlcProtocol\src\Shared\bin\Debug\net10.0\IndustrialIot.Shared.dll
  IndustrialIot.Edge -> C:\Users\case 046\Documents\Project\PlcProtocol\src\Edge\bin\Debug\net10.0\IndustrialIot.Edge.dll
  Edge.Tests -> C:\Users\case 046\Documents\Project\PlcProtocol\tests\Edge.Tests\bin\Debug\net10.0\Edge.Tests.dll
Test run for C:\Users\case 046\Documents\Project\PlcProtocol\tests\Edge.Tests\bin\Debug\net10.0\Edge.Tests.dll (.NETCoreApp,Version=v10.0)
A total of 1 test files matched the specified pattern.

Passed!  - Failed:     0, Passed:   635, Skipped:     0, Total:   635, Duration: 29 s - Edge.Tests.dll (net10.0)
  Determining projects to restore...
  All projects are up-to-date for restore.
  IndustrialIot.Shared -> C:\Users\case 046\Documents\Project\PlcProtocol\src\Shared\bin\Debug\net10.0\IndustrialIot.Shared.dll
  IndustrialIot.Cloud -> C:\Users\case 046\Documents\Project\PlcProtocol\src\Cloud\bin\Debug\net10.0\IndustrialIot.Cloud.dll
  Cloud.Tests -> C:\Users\case 046\Documents\Project\PlcProtocol\tests\Cloud.Tests\bin\Debug\net10.0\Cloud.Tests.dll
Test run for C:\Users\case 046\Documents\Project\PlcProtocol\tests\Cloud.Tests\bin\Debug\net10.0\Cloud.Tests.dll (.NETCoreApp,Version=v10.0)
A total of 1 test files matched the specified pattern.

Passed!  - Failed:     0, Passed:   147, Skipped:     0, Total:   147, Duration: 1 m 35 s - Cloud.Tests.dll (net10.0)
  Determining projects to restore...
  All projects are up-to-date for restore.
  IndustrialIot.Shared -> C:\Users\case 046\Documents\Project\PlcProtocol\src\Shared\bin\Debug\net10.0\IndustrialIot.Shared.dll
  IndustrialIot.Edge -> C:\Users\case 046\Documents\Project\PlcProtocol\src\Edge\bin\Debug\net10.0\IndustrialIot.Edge.dll
  IndustrialIot.Cloud -> C:\Users\case 046\Documents\Project\PlcProtocol\src\Cloud\bin\Debug\net10.0\IndustrialIot.Cloud.dll
  Deployment.Tests -> C:\Users\case 046\Documents\Project\PlcProtocol\tests\Deployment.Tests\bin\Debug\net10.0\Deployment.Tests.dll
Test run for C:\Users\case 046\Documents\Project\PlcProtocol\tests\Deployment.Tests\bin\Debug\net10.0\Deployment.Tests.dll (.NETCoreApp,Version=v10.0)
A total of 1 test files matched the specified pattern.

Passed!  - Failed:     0, Passed:    37, Skipped:     0, Total:    37, Duration: 3 m 11 s - Deployment.Tests.dll (net10.0)
```
