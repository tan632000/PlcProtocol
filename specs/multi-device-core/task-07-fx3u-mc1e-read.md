# Task 07 — The Edge reads an FX3U over MC 1E in blocks, following the module manual

Status: done

## Outcome
A `Mc1E` device reads D (word, including `String`) and M/X/Y (bit) tags from an FX3U Ethernet module in block reads
(plan D-05), with frame layout, point limits and address ranges taken from the module manuals; the frame builder can
produce only batch read and batch write frames; writes still refuse before I/O until task-09.

## Scope
- In:
  - Step 0: obtain the frame examples, point limits, X/Y ranges and word-read alignment from FX3U-ENET-ADP
    (JY997D45801) and FX3U-ENET-L (JY997D18101); record page references in the receipt. If neither manual can be
    obtained, the task becomes `blocked` and the user is asked.
  - `Mc1eAddressSyntax` (ranges from the manual; FX3U entries of `PlcModelCatalog` corrected if they differ),
    `Mc1eFrame` (closed device enum, private command constants), `MitsubishiMc1eDriver` (one connection, one request at
    a time, monitoring timer, strict reply parsing, error code → `PlcAccessDeniedException` naming code and range, lost
    connection → `PlcUnavailableException` and no further blocks, request count reported).
  - Block planning: a separate `Mc1eBlockPlanner` (touching tags only, the manual's point limit); `SlmpBlockPlanner` is
    not changed.
  - Command codes live only in a static class `Mc1eCommands` (no subcommands or other constants there).
  - `String` over D registers as in task-06, `Length` 1..128 on FX3U (one read reaches 64 D registers; amended by the user
    on 2026-11-02); `IPlcDriverFactory` creates `Mc1E`; `PlcConnectionTester` builds the MC 1E
    driver for an FX3U device.
  - `FakeMc1eServer` enforcing the manual's limits, with a request log, range check, `UnexpectedFrames` and a log of
    received command codes.
- Out: writes (task-09).

## Coverage
- CP-05, CP-06

## Ownership
- Create: `src/Edge/Drivers/Mc1e/Mc1eAddressSyntax.cs`, `src/Edge/Drivers/Mc1e/Mc1eFrame.cs`, `src/Edge/Drivers/Mc1e/Mc1eCommands.cs`,
  `src/Edge/Drivers/Mc1e/Mc1eBlockPlanner.cs`, `src/Edge/Drivers/Mc1e/MitsubishiMc1eDriver.cs`
- Modify: `src/Edge/Drivers/IPlcDriverFactory.cs` (register `Mc1E`), `src/Edge/Settings/PlcConnectionTester.cs` (MC 1E case),
  `src/Edge/Drivers/PlcModelCatalog.cs` (FX3U ranges only, if the manual differs)
- Create: `tests/Edge.Tests/Fakes/FakeMc1eServer.cs`, `tests/Edge.Tests/Mc1eDriverTests.cs`
- Modify: `tests/Edge.Tests/PlcConnectionTesterTests.cs`, `tests/Edge.Tests/PlcModelCatalogTests.cs` (only if ranges change)
- Modify (added by the user on 2026-11-02): `src/Edge/Drivers/PlcDeviceRegistry.cs` (accept Mc1E devices: their address syntax
  and option checks) and `tests/Edge.Tests/DeviceRuntimeTests.cs` (the FX3U start test now expects the MC 1E driver)
- Read: `src/Edge/Drivers/Slmp/MitsubishiSlmpDriver.cs`, `src/Edge/Drivers/Slmp/SlmpBlockPlanner.cs`

## Steps
1. Step 0 above.
2. Tests first: golden bytes for a D batch read and a bit read copied from the manual's example; decode Int −1, DInt
   123456, Real 12.5, String "ABC"; X10 is the 9th input (octal); D0+D1 one request, D0 and D2 two; touching words above
   the point limit → two requests; error code on one block → its tags only; stopped server → one connection attempt per
   batch; the constants of `Mc1eCommands` found by reflection equal {batch read bit, batch read word,
   batch write bit, batch write word}; `UnexpectedFrames` empty and every received command code a read code in every test.
3. Implement and register.

## Acceptance
- AC-07 (read part) and AC-08 (FX3U String read): the tests pass; the receipt cites the manual pages used.

## Dependencies
- task-02-device-runtime.md
- task-03-commands-and-monitor.md
- task-04-model-catalog.md
- task-06-new-data-types.md

## Verification Plan
- Command: `dotnet build IndustrialIot.sln -c Release && dotnet test tests/Edge.Tests`
- Named probe: `Mc1eDriverTests`, `PlcConnectionTesterTests` (MC 1E case).
- Reachability: source — the real driver over TCP to the in-process fake; live in task-10 if an FX3U is available.
- Oracle: build exit 0; `Failed: 0`, `Skipped: 0`; `UnexpectedFrames` empty; golden bytes equal the manual's example.
- Counterexample: decimal X numbering reads X10 as the 11th input; big-endian words break DInt/Real; a block above the
  manual's point limit is rejected by the fake; a builder taking a command byte fails the reflection check.
- Artifacts: ephemeral; manual page references in the receipt.

## Failure Protocol
On a failed Step or Verification Plan run: stop; do not widen scope, change the Command, or weaken a test; record observed versus expected; repair only the cited cause; after three failed rounds, stop and ask the user.

## Receipt

Verification: PASS
Command: dotnet build IndustrialIot.sln -c Release && dotnet test tests/Edge.Tests
Exit: 0
Base: d8d498a1bdb6249b1b7d46c12d0092edd9cc3f65
Head: 4ac6d3716f1479c59f323b6c7fb11db6a4c0ca88e050380237e3023b9c4dd996
Manual used (Step 0): FX3U-ENET-ADP User's Manual JY997D45801, revision C (9/2012), printed pages: 54-56 (section 7.3.3: PC No. FFH, monitoring timer in 250 ms units, complete codes, 5BH followed by an abnormal code and 00H; binary example on p.56), 62 and 64 (section 7.4 and 7.5.1: 64 points per word read of word devices, 32 words of bit devices, bit-device head a multiple of 16, remote RUN 13H / STOP 14H exist), 65 (device codes D 4420H, M 4D20H, X 5820H, Y 5920H; FX3U ranges D0-D7999, M0-M7679, X0-X377, Y0-Y377), 72 (section 7.5.5 word read of Y100-Y137 and its reply), 154 (section 10.3.2 complete codes 56H/57H/58H), 169 (sample program "01FF000A4420000000000500"). The FX3U-ENET-L manual was not obtained; its limits are not confirmed.
Scope changes by the user (2026-11-02): `PlcDeviceRegistry.cs` and `DeviceRuntimeTests.cs` added to Ownership; FX3U String Length is 1..128 (plan D-06 and this task amended). The FX3U ranges matched the model catalog, which kept its values; only its comment now cites the manual.
Changed existing assertions: `DeviceRuntimeTests` FX3U case now expects the MC 1E driver instead of a start failure.
Process: the code was written before its tests in this task; discrimination was shown afterwards by mutants that each failed `Mc1eDriverTests` (device code byte order: 8 tests; X/Y decimal: 4; D limit 65: 2; 5BH branch reading one byte: the in-step test).
Review: fresh-context code review, PASS after one repair round (abnormal-reply tests keep the stream in step; the 00H after an abnormal code is checked).
Limitations: FX3U range rules live in both `PlcModelCatalog` and `Mc1eAddressSyntax`; 5BH and 60H map to `PlcAccessDeniedException` as the task states; only command 01H (word read) is sent; FX3U is not tested on hardware.
```text
$ dotnet build IndustrialIot.sln -c Release && dotnet test tests/Edge.Tests
  Determining projects to restore...
  All projects are up-to-date for restore.
  IndustrialIot.Shared -> C:\Users\case 046\Documents\Project\PlcProtocol\src\Shared\bin\Release\net10.0\IndustrialIot.Shared.dll
  Shared.Tests -> C:\Users\case 046\Documents\Project\PlcProtocol\tests\Shared.Tests\bin\Release\net10.0\Shared.Tests.dll
  IndustrialIot.Cloud -> C:\Users\case 046\Documents\Project\PlcProtocol\src\Cloud\bin\Release\net10.0\IndustrialIot.Cloud.dll
  Cloud.Tests -> C:\Users\case 046\Documents\Project\PlcProtocol\tests\Cloud.Tests\bin\Release\net10.0\Cloud.Tests.dll
  IndustrialIot.Edge -> C:\Users\case 046\Documents\Project\PlcProtocol\src\Edge\bin\Release\net10.0\IndustrialIot.Edge.dll
  Edge.Tests -> C:\Users\case 046\Documents\Project\PlcProtocol\tests\Edge.Tests\bin\Release\net10.0\Edge.Tests.dll
  Deployment.Tests -> C:\Users\case 046\Documents\Project\PlcProtocol\tests\Deployment.Tests\bin\Release\net10.0\Deployment.Tests.dll

Build succeeded.
    0 Warning(s)
    0 Error(s)

Time Elapsed 00:00:02.53
  Determining projects to restore...
  All projects are up-to-date for restore.
  IndustrialIot.Shared -> C:\Users\case 046\Documents\Project\PlcProtocol\src\Shared\bin\Debug\net10.0\IndustrialIot.Shared.dll
  IndustrialIot.Edge -> C:\Users\case 046\Documents\Project\PlcProtocol\src\Edge\bin\Debug\net10.0\IndustrialIot.Edge.dll
  Edge.Tests -> C:\Users\case 046\Documents\Project\PlcProtocol\tests\Edge.Tests\bin\Debug\net10.0\Edge.Tests.dll
Test run for C:\Users\case 046\Documents\Project\PlcProtocol\tests\Edge.Tests\bin\Debug\net10.0\Edge.Tests.dll (.NETCoreApp,Version=v10.0)
A total of 1 test files matched the specified pattern.

Passed!  - Failed:     0, Passed:   721, Skipped:     0, Total:   721, Duration: 29 s - Edge.Tests.dll (net10.0)
```
