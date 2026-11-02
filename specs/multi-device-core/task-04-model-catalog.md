# Task 04 — Picking a model sets the driver defaults and the allowed addresses and types

Status: done

## Outcome
`PlcModelCatalog` (plan D-04) supplies, per model, the driver, default Port/Rack/Slot and the allowed data types and
address ranges; device validation uses it, so a wrong model, address or type fails the start naming the tag.

## Scope
- In: `S7-1200` (S7, 102, Rack 0, Slot 1, all S7 areas as today), `FX5U` (Slmp3E, Port required, today's D/M/X/Y ranges),
  `FX3U` (Mc1E, Port required and ≠ 102, D0–D7999, M0–M7679, X/Y 0–377 octal [UNVERIFIED — task-07 corrects the FX3U ranges from the manual]); omitted Port/Rack/Slot take
  the default; a Port given for S7-1200 overrides 102; type lists per model (String/LReal/DTL rules from D-06 are entered
  here; their tests run in task-06 once the types exist).
- Out: the FX3U driver itself (task-07); the FX3U write guard (task-09).

## Coverage
- CP-04

## Ownership
- Create: `src/Edge/Drivers/PlcModelCatalog.cs`, `tests/Edge.Tests/PlcModelCatalogTests.cs`
- Modify: `src/Edge/Configuration/EdgeChannelsOptions.cs` (validator calls the catalog)
- Read: `src/Edge/Drivers/S7AddressSyntax.cs`, `src/Edge/Drivers/Slmp/SlmpAddressSyntax.cs`

## Steps
1. Tests first: defaults applied when omitted; FX3U with Port 102 rejected with the S7-port hint; `X400` on FX3U rejected (outside octal range); `S7-1200` under a `Slmp3E`
   channel rejected.
2. Implement the catalog and call it from the validator.

## Acceptance
- AC-06: every case above behaves as stated.

## Dependencies
- task-01-schema-v2-migration.md

## Verification Plan
- Command: `dotnet build IndustrialIot.sln -c Release && dotnet test tests/Edge.Tests && dotnet test tests/Deployment.Tests`
- Named probe: `PlcModelCatalogTests`.
- Reachability: source — validation as the host runs it.
- Oracle: build exit 0; `Failed: 0`, `Skipped: 0`.
- Counterexample: a catalog keyed by driver instead of model accepts an `S7-1200` under a `Slmp3E` channel; a migrated
  v1 configuration that no longer starts fails the Deployment tests.
- Artifacts: ephemeral.

## Failure Protocol
On a failed Step or Verification Plan run: stop; do not widen scope, change the Command, or weaken a test; record observed versus expected; repair only the cited cause; after three failed rounds, stop and ask the user.

## Receipt

Verification: PASS
Command: dotnet build IndustrialIot.sln -c Release && dotnet test tests/Edge.Tests && dotnet test tests/Deployment.Tests
Exit: 0
Base: d8d498a1bdb6249b1b7d46c12d0092edd9cc3f65
Head: 0758fabe5e0e10022bc4df371482d3ef99dac278bb2f18269c30987909924ab1
Changed existing assertions: none.
Review: fresh-context code review, PASS (no repair round).
Notes for later tasks: `PlcModelCatalog.CheckTag` counts one word for every type other than DInt/DWord/Real, so FX3U `String` must pass `Length` and count `ceil(Length/2)` words once the type exists (task-06/07); the S7 defaults live in both the catalog and `PlcDeviceRegistry` (a test ties them); FX3U ranges stay [UNVERIFIED] until task-07 reads the module manual.
Limitations: an empty FX3U address reports two errors on the same key; the S7-1200-under-Slmp3E test asserts the key and the word S7, not the exact "needs a S7 channel" text.
```text
$ dotnet build IndustrialIot.sln -c Release && dotnet test tests/Edge.Tests && dotnet test tests/Deployment.Tests
  Determining projects to restore...
  All projects are up-to-date for restore.
  IndustrialIot.Shared -> C:\Users\case 046\Documents\Project\PlcProtocol\src\Shared\bin\Release\net10.0\IndustrialIot.Shared.dll
  Shared.Tests -> C:\Users\case 046\Documents\Project\PlcProtocol\tests\Shared.Tests\bin\Release\net10.0\Shared.Tests.dll
  IndustrialIot.Edge -> C:\Users\case 046\Documents\Project\PlcProtocol\src\Edge\bin\Release\net10.0\IndustrialIot.Edge.dll
  IndustrialIot.Cloud -> C:\Users\case 046\Documents\Project\PlcProtocol\src\Cloud\bin\Release\net10.0\IndustrialIot.Cloud.dll
  Edge.Tests -> C:\Users\case 046\Documents\Project\PlcProtocol\tests\Edge.Tests\bin\Release\net10.0\Edge.Tests.dll
  Deployment.Tests -> C:\Users\case 046\Documents\Project\PlcProtocol\tests\Deployment.Tests\bin\Release\net10.0\Deployment.Tests.dll
  Cloud.Tests -> C:\Users\case 046\Documents\Project\PlcProtocol\tests\Cloud.Tests\bin\Release\net10.0\Cloud.Tests.dll

Build succeeded.
    0 Warning(s)
    0 Error(s)

Time Elapsed 00:00:01.49
  Determining projects to restore...
  All projects are up-to-date for restore.
  IndustrialIot.Shared -> C:\Users\case 046\Documents\Project\PlcProtocol\src\Shared\bin\Debug\net10.0\IndustrialIot.Shared.dll
  IndustrialIot.Edge -> C:\Users\case 046\Documents\Project\PlcProtocol\src\Edge\bin\Debug\net10.0\IndustrialIot.Edge.dll
  Edge.Tests -> C:\Users\case 046\Documents\Project\PlcProtocol\tests\Edge.Tests\bin\Debug\net10.0\Edge.Tests.dll
Test run for C:\Users\case 046\Documents\Project\PlcProtocol\tests\Edge.Tests\bin\Debug\net10.0\Edge.Tests.dll (.NETCoreApp,Version=v10.0)
A total of 1 test files matched the specified pattern.

Passed!  - Failed:     0, Passed:   634, Skipped:     0, Total:   634, Duration: 29 s - Edge.Tests.dll (net10.0)
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
