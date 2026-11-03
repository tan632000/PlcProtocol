# Task 04 — Tag CSV import and export

Status: done

## Outcome
`TagCsv` turns a device's tags into CSV and back by the rules of D-05. The page reaches it through two routes:
- `POST /api/settings/csv/format` `{tags}` → `{csv}`
- `POST /api/settings/csv/parse` `{csv}` → `{tags}`, or `{errors: [{line, column, message}]}`

Export then import gives the same tag list, and no exported text cell can run as an Excel formula.

## Scope
- In, the rules of D-05:
  - Header and columns.
  - Separator, quotes, encoding and line endings.
  - Null ≡ empty ≡ `""`.
  - No trimming of cells.
  - The formula guard on export and its reverse on import.
  - Error line = the physical line where the record starts.
  - Format writes CRLF, no BOM, quotes only where needed, and the header first.
- In, what parse checks:
  - The CSV shape and the cell types: a non-empty name, a known `Data Type`, number cells, `Length` an integer, and
    `Client Access`.
  - Address, model and Length-on-non-String rules stay with validate/save (task-01).
- In, errors: a 1-based line, a column name and a fixed message, never the cell value.
- In, the routes: in `MonitorServer` under the settings block, with `SettingsService.ReadGuardedAsync` at 4 MB and no
  password.
- Out:
  - other separators;
  - merging with existing tags.

## Coverage
- CP-04

## Ownership
- Create: `src/Edge/Settings/TagCsv.cs`, `tests/Edge.Tests/TagCsvTests.cs`
- Modify: `src/Edge/Monitoring/MonitorServer.cs` (two routes)
- Read: `src/Edge/Configuration/EdgeChannelsOptions.cs` (`DeviceTagOptions`), `src/Edge/Settings/SettingsService.cs`

## Steps
1. Write `TagCsvTests`. Run the Command; it fails to compile or fails.
2. Implement `TagCsv` and the routes. The Command passes.

## Acceptance
- AC-06:
  - A valid file whose quoted description holds a comma, a quote and a line break, followed by a record with `Data Type`
    `Foo` → an error that names that record's starting physical line and the column `Data Type`.
  - A missing header → an error on line 1.
  - `Client Access` `RW` → an error naming the column.
  - A file with a BOM and LF endings parses like the CRLF one.
  - Header cells with surrounding spaces or another case are accepted.
  - A cross-site request to either route → 403.
- AC-07:
  - 20 tags go through format → parse and come back equal field by field, Min/Max by decimal value. They cover:
    - every data type;
    - `Writable` true and false;
    - Min/Max;
    - `String` with `Length`;
    - descriptions with a comma, a quote, Vietnamese text and an emoji;
    - descriptions starting with `=`, `+`, `-`, `@` and `'`;
    - an empty description sent as `""`, which comes back as null;
    - a description `'=x`, which comes back as `'=x`;
    - a description and an address with leading and trailing spaces, which come back unchanged.
  - In the exported text, no cell of `Tag Name`, `Address` or `Description` starts with `=`, `+`, `-` or `@`.
  - A negative Min stays a plain number.

## Dependencies
- task-01-settings-api-v2.md

## Verification Plan
- Command: `dotnet test tests/Edge.Tests --filter "FullyQualifiedName~TagCsvTests" -- RunConfiguration.TreatNoTestsAsError=true`
- Named probe: `TagCsvTests` (the round trip, plus the route tests through the real monitor).
- Reachability: source.
- Oracle: exit 0, failed 0, skipped 0.
- Counterexample: each of these must fail a test:
  - a formatter that does not quote a comma;
  - an export that leaves `=` first;
  - a parser that counts records instead of physical lines;
  - a parser that counts from after the header.
- Artifacts: none.

## Failure Protocol
On a failed Step or Verification Plan run: stop; do not widen scope, change the Command, or weaken a test; record observed versus expected; repair only the cited cause; after three failed rounds, stop and ask the user.

## Receipt

Verification: PASS
Command: dotnet test tests/Edge.Tests --filter "FullyQualifiedName~TagCsvTests" -- RunConfiguration.TreatNoTestsAsError=true
Exit: 0
Base: e4f39e96710b817946ac2099e63e0a9bf7dbb80f
Head: bee456230a28aaf16120de640804f71b5d610f77b9a6a346b7105e3d3e1acc63
Process: TagCsvTests were written first; the Command then failed to compile (no TagCsv). Discrimination shown by mutants, each failing tests: no formula guard on export, apostrophes not escaped (the first wording of the rule, which breaks ''=x), records counted instead of physical lines, commas not quoted, cells trimmed; sources restored and rebuilt after every mutant run.
Review: fresh-context code review, PASS with 7 Lows. Repaired: the BOM is written as the escape ﻿ in source and tests instead of an invisible character. The guard also escapes a cell starting with two apostrophes, which reversibility needs; plan.md D-05 now says so ("followed by one of those or by another '").
Open question for the user at GATE-DONE: a row Excel saves as only commas (,,,,,,,) is refused (Tag Name, Data Type and Client Access required) rather than skipped.
Limitations: the format route answers 400 as {path, message} while the parse route answers {line, column, message}; format accepts any Data Type text, so a file exported from unsaved tags with a bad type does not import again; the error list is not capped; blank lines are skipped anywhere, not only at the end.
```text
$ dotnet test tests/Edge.Tests --filter "FullyQualifiedName~TagCsvTests" -- RunConfiguration.TreatNoTestsAsError=true
  Determining projects to restore...
  All projects are up-to-date for restore.
  IndustrialIot.Shared -> C:\Users\case 046\Documents\Project\PlcProtocol\src\Shared\bin\Debug\net10.0\IndustrialIot.Shared.dll
  IndustrialIot.Edge -> C:\Users\case 046\Documents\Project\PlcProtocol\src\Edge\bin\Debug\net10.0\IndustrialIot.Edge.dll
  Edge.Tests -> C:\Users\case 046\Documents\Project\PlcProtocol\tests\Edge.Tests\bin\Debug\net10.0\Edge.Tests.dll
Test run for C:\Users\case 046\Documents\Project\PlcProtocol\tests\Edge.Tests\bin\Debug\net10.0\Edge.Tests.dll (.NETCoreApp,Version=v10.0)
A total of 1 test files matched the specified pattern.

Passed!  - Failed:     0, Passed:    19, Skipped:     0, Total:    19, Duration: 424 ms - Edge.Tests.dll (net10.0)
```
