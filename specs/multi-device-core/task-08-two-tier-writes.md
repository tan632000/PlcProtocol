# Task 08 — FX5U can be written when all switches allow it, the new types can be written, and never X

Status: done

## Outcome
The SLMP 3E driver writes D (word units) and M/Y (bit units); S7 and SLMP write the task-06 types per D-06; writable
overlap is checked for every driver with real sizes; every refusal happens before I/O; X and S7 inputs are never
writable; the SLMP frame constants are exactly read and write.

## Scope
- In:
  - SLMP Device Write 0x1401 (subcommand 0x0000 word for D, 0x0001 bit for M/Y); one write request at a time per device,
    interleaving with reads as the S7 driver does.
  - Command codes move into a static class `SlmpCommands` holding only command codes; subcommands stay outside it.
  - Write encoding: String (S7 writes byte 1 and the chars, never byte 0; FX pads with NUL to `Length`; longer text
    rejected), LReal, DTL (text parsed exactly as D-06, else rejected) — all before I/O.
  - Start validation: the "only S7 supports writes" rule (`src/Edge/Drivers/PlcProtocolOptions.cs:47-52`) becomes "only
    S7 and Slmp3E" (Mc1E stays refused until task-09); a `Writable` tag on X, on an S7 input or of an unsupported type
    fails the start naming the tag; writable overlap (`src/Edge/Drivers/PlcTagCatalog.cs:194-224`) covers Mitsubishi
    word/bit ranges (String `ceil(Length/2)` words, LReal 4, DInt/Real 2) and S7 new sizes.
  - Assertions allowed to change: the "SLMP is read-only" expectations in `tests/Edge.Tests/SlmpDriverTests.cs`
    (lines 197, 422, 437-446, 477, 552 at planning time) and the protocol-write expectations in
    `tests/Edge.Tests/PlcProtocolSwitchTests.cs` (lines 80-82); the X refusal stays.
- Out: MC 1E writes (task-09); remote operations; any UI.

## Coverage
- CP-03, CP-06

## Ownership
- Create: `src/Edge/Drivers/Slmp/SlmpCommands.cs`
- Modify: `src/Edge/Drivers/Slmp/MitsubishiSlmpDriver.cs`, `src/Edge/Drivers/Slmp/SlmpAddressSyntax.cs`, `src/Edge/Drivers/PlcValueConverter.cs`,
  `src/Edge/Drivers/SiemensS7Driver.cs` (write path for new types), `src/Edge/Drivers/PlcTagCatalog.cs` (overlap),
  `src/Edge/Drivers/PlcProtocolOptions.cs`
- Modify: `tests/Edge.Tests/SlmpDriverTests.cs`, `tests/Edge.Tests/SiemensS7DriverTests.cs`, `tests/Edge.Tests/PlcTagCatalogTests.cs`,
  `tests/Edge.Tests/PlcProtocolSwitchTests.cs`, `tests/Edge.Tests/CommandFlowTests.cs`, `tests/Edge.Tests/Fakes/FakeSlmpServer.cs`
  (command-code log)
- Modify (added by the user on 2026-11-02): `tests/Edge.Tests/Fakes/FakeS7Server.cs` (write jobs applied to its memory) and
  `tests/Edge.Tests/SettingsApiTests.cs` (the single-PLC page may save SLMP with commands on),
  `tests/Edge.Tests/S7AddressSyntaxTests.cs` (the overlap message for an unknown address kind, three assertions)
- Read: `src/Edge/Commands/CommandExecutor.cs`

## Steps
1. Tests first: golden bytes for SLMP word and bit writes (SH-081951 example [UNVERIFIED]); D100 ← 1234 then read back
   1234; M5 ← true; Y7 ← true; a command to an X tag, to a device with `WritesEnabled=false`, with the global switch off,
   or on a non-writable tag → rejected with `UnexpectedFrames` and the write log both empty; S7 String write leaves byte 0
   unchanged in the fake; String longer than `Length` and a bad DTL text rejected with no frame; writable D100 DInt next
   to read-only D101 fails the start; a write during a read cycle waits and both complete; the constants of `SlmpCommands`
   found by reflection equal {0x0401, 0x1401} and every command the fake received is in that set.
2. Implement the frames, encoders and validation; change only the listed assertions.

## Acceptance
- AC-05 (write part), AC-08 (write part), AC-09: every case above holds.

## Dependencies
- task-03-commands-and-monitor.md
- task-06-new-data-types.md

## Verification Plan
- Command: `dotnet build IndustrialIot.sln -c Release && dotnet test tests/Edge.Tests && dotnet test tests/Deployment.Tests`
- Named probe: `SlmpDriverTests` (write frames, refusals, command allow-list), `SiemensS7DriverTests` (new-type writes),
  `PlcTagCatalogTests` (overlap), `PlcProtocolSwitchTests`, `CommandFlowTests` (end to end through the executor).
- Reachability: source — real drivers over TCP to the fakes, commands through the real executor.
- Oracle: build exit 0; `Failed: 0`, `Skipped: 0` in both suites; the receipt lists the changed assertions.
- Counterexample: a refusal checked after sending leaves a frame in the fake's log; writing X succeeds; an S7 String write
  that overwrites byte 0; a DInt write over a read-only neighbour starts; a builder that holds another command code fails
  the reflection check.
- Artifacts: ephemeral.

## Failure Protocol
On a failed Step or Verification Plan run: stop; do not widen scope, change the Command, or weaken a test; record observed versus expected; repair only the cited cause; after three failed rounds, stop and ask the user.

## Receipt

Verification: PASS
Command: dotnet build IndustrialIot.sln -c Release && dotnet test tests/Edge.Tests && dotnet test tests/Deployment.Tests
Exit: 0
Base: d8d498a1bdb6249b1b7d46c12d0092edd9cc3f65
Head: d0e4819e0d21b1d1380ab27d2f15c28dc3ede723a274c7d4651d1dbc986290d5
Changed existing assertions: `SlmpDriverTests` "SLMP is read-only" expectations (bit syntax, write refusal, catalog, D syntax, host start) now expect X alone to be read-only ("X inputs cannot be written") and D/M/Y writable; `PlcProtocolSwitchTests` SLMP with commands on is accepted instead of failing the start; `SettingsApiTests` "slmp-with-commands" refusal removed and a save of SLMP with commands on added; `S7AddressSyntaxTests` three overlap messages for an unknown address kind ("has an address the overlap check does not know (X1)").
Scope changes by the user (2026-11-02): `FakeS7Server.cs`, `SettingsApiTests.cs` and `S7AddressSyntaxTests.cs` added to Ownership. Also edited outside Ownership as a direct consequence: `DriverServiceCollectionExtensions.cs` (two composition lines built the protocol validator, which lost its commands parameter).
Process: the code was written before its tests; discrimination was shown by mutants that each failed tests (S7 String written from byte 0: 1; X writable: 6; String length unchecked: 1; S7 overlap one byte: 7; no STRING maximum check: 1; SLMP write after send reported unavailable: 1).
Review: fresh-context code review, PASS after one repair round (an S7 String write first reads byte 0 under the same lock and writes nothing on a mismatch; the S7 driver refuses the input area itself; SLMP outcome-unknown and refused-write tests).
Limitations: the SLMP write frame layout is [UNVERIFIED] against SH-081951 until the live check (task-10); `CommandExecutor` maps a driver `PlcValueException` to write_outcome_unknown (unreachable through the catalog; outside Ownership); `SettingsService` still says "only S7 allows commands" (outside Ownership); a STRING length mismatch on write is reported as plc_access_denied, and its message names the value's length.
```text
$ dotnet build IndustrialIot.sln -c Release && dotnet test tests/Edge.Tests && dotnet test tests/Deployment.Tests
  Determining projects to restore...
  All projects are up-to-date for restore.
  IndustrialIot.Shared -> C:\Users\case 046\Documents\Project\PlcProtocol\src\Shared\bin\Release\net10.0\IndustrialIot.Shared.dll
  Shared.Tests -> C:\Users\case 046\Documents\Project\PlcProtocol\tests\Shared.Tests\bin\Release\net10.0\Shared.Tests.dll
  IndustrialIot.Cloud -> C:\Users\case 046\Documents\Project\PlcProtocol\src\Cloud\bin\Release\net10.0\IndustrialIot.Cloud.dll
  IndustrialIot.Edge -> C:\Users\case 046\Documents\Project\PlcProtocol\src\Edge\bin\Release\net10.0\IndustrialIot.Edge.dll
  Cloud.Tests -> C:\Users\case 046\Documents\Project\PlcProtocol\tests\Cloud.Tests\bin\Release\net10.0\Cloud.Tests.dll
  Edge.Tests -> C:\Users\case 046\Documents\Project\PlcProtocol\tests\Edge.Tests\bin\Release\net10.0\Edge.Tests.dll
  Deployment.Tests -> C:\Users\case 046\Documents\Project\PlcProtocol\tests\Deployment.Tests\bin\Release\net10.0\Deployment.Tests.dll

Build succeeded.
    0 Warning(s)
    0 Error(s)

Time Elapsed 00:00:01.85
  Determining projects to restore...
  All projects are up-to-date for restore.
  IndustrialIot.Shared -> C:\Users\case 046\Documents\Project\PlcProtocol\src\Shared\bin\Debug\net10.0\IndustrialIot.Shared.dll
  IndustrialIot.Edge -> C:\Users\case 046\Documents\Project\PlcProtocol\src\Edge\bin\Debug\net10.0\IndustrialIot.Edge.dll
  Edge.Tests -> C:\Users\case 046\Documents\Project\PlcProtocol\tests\Edge.Tests\bin\Debug\net10.0\Edge.Tests.dll
Test run for C:\Users\case 046\Documents\Project\PlcProtocol\tests\Edge.Tests\bin\Debug\net10.0\Edge.Tests.dll (.NETCoreApp,Version=v10.0)
A total of 1 test files matched the specified pattern.

Passed!  - Failed:     0, Passed:   756, Skipped:     0, Total:   756, Duration: 29 s - Edge.Tests.dll (net10.0)
  Determining projects to restore...
  All projects are up-to-date for restore.
  IndustrialIot.Shared -> C:\Users\case 046\Documents\Project\PlcProtocol\src\Shared\bin\Debug\net10.0\IndustrialIot.Shared.dll
  IndustrialIot.Edge -> C:\Users\case 046\Documents\Project\PlcProtocol\src\Edge\bin\Debug\net10.0\IndustrialIot.Edge.dll
  IndustrialIot.Cloud -> C:\Users\case 046\Documents\Project\PlcProtocol\src\Cloud\bin\Debug\net10.0\IndustrialIot.Cloud.dll
  Deployment.Tests -> C:\Users\case 046\Documents\Project\PlcProtocol\tests\Deployment.Tests\bin\Debug\net10.0\Deployment.Tests.dll
Test run for C:\Users\case 046\Documents\Project\PlcProtocol\tests\Deployment.Tests\bin\Debug\net10.0\Deployment.Tests.dll (.NETCoreApp,Version=v10.0)
A total of 1 test files matched the specified pattern.

Passed!  - Failed:     0, Passed:    37, Skipped:     0, Total:    37, Duration: 3 m 16 s - Deployment.Tests.dll (net10.0)
```
