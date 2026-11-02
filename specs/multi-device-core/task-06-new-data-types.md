# Task 06 — String, LReal and DTL read correctly on S7-1200 and FX5U

Status: done

## Outcome
`PlcDataType` gains `String`, `LReal`, `DTL`; S7-1200 and FX5U read them per plan D-06 with the S7 byte-address grammar
and real item sizes; values are published as string / number / string; an oversized or inconsistent tag is rejected at
start or turns only that tag Bad.

## Scope
- In:
  - Tag model: `Length` on the tag definition and the address; `Length` required for `String` (S7 1..218, FX 1..254) and
    rejected for other types.
  - S7: `String`/`LReal`/`DTL` on a byte address (`DBn.DBBx`, `MBx`), any other letter rejected for these types; item
    size String `Length`+2, LReal 8, DTL 12 used by the address model and the read planner; STRING byte 0 ≠ `Length` →
    that tag Bad "STRING length mismatch"; value = current-length chars; LReal big-endian; DTL → `yyyy-MM-ddTHH:mm:ss.fff`,
    out-of-range fields → that tag Bad.
  - SLMP (FX5U): `String` over `ceil(Length/2)` D registers (low byte first, stop at NUL); `LReal` over four D registers
    (low word first); the block planner sizes these items.
- Out: write encoding (task-08); MC 1E (task-07); writable overlap (task-08).

## Coverage
- CP-06

## Ownership
- Modify: `src/Edge/Drivers/PlcDataType.cs`, `src/Edge/Drivers/PlcValueConverter.cs`, `src/Edge/Drivers/S7AddressSyntax.cs`,
  `src/Edge/Drivers/S7ReadPlanner.cs`, `src/Edge/Drivers/SiemensS7Driver.cs` (read path), `src/Edge/Drivers/Models/IPlcAddress.cs`,
  `src/Edge/Drivers/Models/PlcAddress.cs`, `src/Edge/Drivers/Models/PlcAddressParser.cs`, `src/Edge/Drivers/Models/PlcTagDefinition.cs`,
  `src/Edge/Drivers/Slmp/SlmpAddressSyntax.cs`, `src/Edge/Drivers/Slmp/SlmpBlockPlanner.cs`, `src/Edge/Drivers/Slmp/MitsubishiSlmpDriver.cs` (read decode),
  `src/Edge/Drivers/PlcTagCatalog.cs` (`Length` carried, `Min`/`Max` only for `LReal` among the new types),
  the v2 tag → `PlcTagDefinition` mapping (`src/Edge/Configuration/EdgeDeviceResolver.cs`, or `src/Edge/Drivers/PlcDeviceRegistry.cs`
  if task-02 placed it there; `Length` carried only)
- Modify: `tests/Edge.Tests/PlcValueConverterTests.cs`, `tests/Edge.Tests/S7BatchReadTests.cs`, `tests/Edge.Tests/S7AddressSyntaxTests.cs`,
  `tests/Edge.Tests/PlcAddressParserTests.cs`, `tests/Edge.Tests/SlmpDriverTests.cs`, `tests/Edge.Tests/Fakes/FakeS7Server.cs` (PDU size option),
  `tests/Edge.Tests/Fakes/FakeSlmpServer.cs`, `tests/Edge.Tests/EdgeDeviceResolverTests.cs` (v2 String), `tests/Edge.Tests/PlcModelCatalogTests.cs`
  (model × new type cases)
- Modify (added by the user on 2026-11-02): `src/Edge/Monitoring/settings.html` (the single-PLC page offers LReal and DTL;
  String needs Length, which a schema-1 file has no key for) and `tests/Edge.Tests/SettingsPageTests.cs` (its choices are
  every data type except String)
- Read: `src/Edge/Drivers/PlcModelCatalog.cs`, `src/Edge/Configuration/EdgeChannelsOptions.cs`

## Steps
1. Tests first: S7 `STRING[10]` holding "AB" → "AB"; byte 0 = 20 with `Length` 10 → that tag Bad, other tags Good;
   `Length` 219 on S7 fails the start naming the tag; LReal 1.5e300 (S7 big-endian, FX5U low word first); DTL bytes for
   2026-11-02 10:15:30.123 → that text, month 13 → that tag Bad; `String` on `DB1.DBW10` rejected; FX5U String "ABC" over
   D100–D101 → "ABC"; a 100-tag S7 mix with ten `STRING[200]` read against the fake **at PDU 240** with no "Too much data";
   `Length` missing on `String` fails naming the tag; a **v2** configuration with a `String` tag `Length` 10 starts and
   reads "AB"; `DTL` on FX5U and `LReal` on FX3U fail the start with the model catalog's own message (not a type-parse error).
2. Implement the tag model, codecs and size rules.

## Acceptance
- AC-08 (read part, S7-1200 and FX5U): every case above passes; unsupported model/type pairs are rejected with the model catalog's message.

## Dependencies
- task-04-model-catalog.md
- task-05-string-values-contract.md

## Verification Plan
- Command: `dotnet build IndustrialIot.sln -c Release && dotnet test tests/Edge.Tests && dotnet test tests/Deployment.Tests`
- Named probe: `PlcModelCatalogTests` (model × new type), `EdgeDeviceResolverTests` (v2 String), `PlcValueConverterTests` (codecs), `S7BatchReadTests` (packing at PDU 240), `S7AddressSyntaxTests`, `PlcAddressParserTests`, `SlmpDriverTests`.
- Reachability: source — real drivers against `FakeS7Server` (PDU 240) and `FakeSlmpServer`.
- Oracle: build exit 0; `Failed: 0`, `Skipped: 0` in both suites.
- Counterexample: reading the full `STRING[n]` buffer returns trailing garbage; DTL converted to UTC shifts by the Edge's
  offset; LReal little-endian on S7 returns a wrong number; a fake at PDU 960 hides an oversized item; a v2 mapping that
  drops `Length` fails the v2 String start; a catalog keyed by driver accepts `LReal` on FX3U.
- Artifacts: ephemeral.

## Failure Protocol
On a failed Step or Verification Plan run: stop; do not widen scope, change the Command, or weaken a test; record observed versus expected; repair only the cited cause; after three failed rounds, stop and ask the user.

## Receipt

Verification: PASS
Command: dotnet build IndustrialIot.sln -c Release && dotnet test tests/Edge.Tests && dotnet test tests/Deployment.Tests
Exit: 0
Base: d8d498a1bdb6249b1b7d46c12d0092edd9cc3f65
Head: 53f3122a1989cad063445ec06525d25e4c025c30db75f2171d69df877e497fb2
Changed existing assertions: `SettingsPageTests.DataTypeChoices_AreExactlyTheEdgesDataTypes` now expects every data type except String (the user added `settings.html` and this test to the Ownership on 2026-11-02; the single-PLC page offers LReal and DTL, and String needs a Length a schema-1 file has no key for); `PlcValueConverterTests` gains matching byte addresses for String, LReal and DTL in its existing size tables.
Review: fresh-context code review, PASS after one repair round (an FX String ends at its Length even when the last register's high byte is not NUL; DTL is refused after 2262-04-11 23:47:16.854).
Notes for later tasks: writable overlap still uses the address letter size, so task-08 must size String/LReal/DTL; write encoding of the new types is task-08 (today a write of them is refused as invalid_value).
Limitations: a decode failure on the solo/isolation read path has no dedicated test (the packed path has one); tags of different types at one byte address share the isolation key, which can cost extra solo requests; the single-PLC settings page does not show `Length` for a String tag configured through appsettings or the environment; the DTL bound compares milliseconds, so up to 0.2 ms past the TIA maximum is accepted; the DTL weekday is not checked.
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

Time Elapsed 00:00:01.62
  Determining projects to restore...
  All projects are up-to-date for restore.
  IndustrialIot.Shared -> C:\Users\case 046\Documents\Project\PlcProtocol\src\Shared\bin\Debug\net10.0\IndustrialIot.Shared.dll
  IndustrialIot.Edge -> C:\Users\case 046\Documents\Project\PlcProtocol\src\Edge\bin\Debug\net10.0\IndustrialIot.Edge.dll
  Edge.Tests -> C:\Users\case 046\Documents\Project\PlcProtocol\tests\Edge.Tests\bin\Debug\net10.0\Edge.Tests.dll
Test run for C:\Users\case 046\Documents\Project\PlcProtocol\tests\Edge.Tests\bin\Debug\net10.0\Edge.Tests.dll (.NETCoreApp,Version=v10.0)
A total of 1 test files matched the specified pattern.

Passed!  - Failed:     0, Passed:   692, Skipped:     0, Total:   692, Duration: 29 s - Edge.Tests.dll (net10.0)
  Determining projects to restore...
  All projects are up-to-date for restore.
  IndustrialIot.Shared -> C:\Users\case 046\Documents\Project\PlcProtocol\src\Shared\bin\Debug\net10.0\IndustrialIot.Shared.dll
  IndustrialIot.Edge -> C:\Users\case 046\Documents\Project\PlcProtocol\src\Edge\bin\Debug\net10.0\IndustrialIot.Edge.dll
  IndustrialIot.Cloud -> C:\Users\case 046\Documents\Project\PlcProtocol\src\Cloud\bin\Debug\net10.0\IndustrialIot.Cloud.dll
  Deployment.Tests -> C:\Users\case 046\Documents\Project\PlcProtocol\tests\Deployment.Tests\bin\Debug\net10.0\Deployment.Tests.dll
Test run for C:\Users\case 046\Documents\Project\PlcProtocol\tests\Deployment.Tests\bin\Debug\net10.0\Deployment.Tests.dll (.NETCoreApp,Version=v10.0)
A total of 1 test files matched the specified pattern.

Passed!  - Failed:     0, Passed:    37, Skipped:     0, Total:    37, Duration: 3 m 13 s - Deployment.Tests.dll (net10.0)
```
