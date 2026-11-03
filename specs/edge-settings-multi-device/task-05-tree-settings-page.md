# Task 05 — The settings page edits the Channel → Device → Tag tree

Status: done

## Outcome
`/settings` shows the Edge's channels, devices and tags as a tree. It edits them, fills model defaults, tests each
PLC, imports and exports CSV per device, and edits write permissions. It then validates and saves through the task-01
API.

## Scope
- In, the tree:
  - Add, edit and delete channels, devices and tags.
  - Choosing a channel driver limits the models.
  - Choosing a model fills Port, Rack and Slot from the defaults. When there is no default, the Port is left empty and
    `portHint` is shown. The model also limits the data types.
  - `WritesEnabled` is disabled with a note when the model has `writesAllowed: false`.
  - Per device: Enabled, WritesEnabled, PollIntervalMs, TimeoutMs, and LegacyTagIds (read-only).
  - Per tag: Name, Address, DataType, Length (only for `String`), Writable, Min, Max, Description.
  - An empty text field is sent as null.
- In, "Test kết nối" per device:
  - a saved, unchanged device is sent by name;
  - a new or edited one is sent as `proposed`;
  - `endpoint-in-use` is shown as "lưu trước rồi test".
- In, CSV per device:
  - Export calls the format route, then starts a download.
  - Import opens a file picker, then calls the parse route. The device's tags are replaced in the page.
  - Errors are listed as "dòng N, cột X".
- In, notices:
  - `commandsEnabled` is shown read-only, with a note that writes also need it.
  - `warnings` are shown (D-08).
- In, saving: validate, save, wait for the apply, the rolled-back state, the password prompt and the lockout work as
  today. Errors are pinned to the tree node named by the error path.
- In, safety: all text goes through `textContent` or `value`. There is no `innerHTML`, no inline handler, and no
  storage of the password.
- Out: new API behaviour; styling beyond what the tree needs.

## Coverage
- CP-05 (source level; the live level is task-06)

## Ownership
- Modify: `src/Edge/Monitoring/settings.html`, `tests/Edge.Tests/SettingsPageTests.cs`
- Read: `src/Edge/Monitoring/MonitorServer.cs`, `src/Edge/Settings/SettingsService.cs`, `src/Edge/Settings/SettingsTree.cs`,
  `src/Edge/Settings/TagCsv.cs`, `src/Edge/Settings/PlcConnectionTester.cs`

## Steps
1. Extend `SettingsPageTests` with the checks below. Run the Command; it fails.
2. Rewrite the page. The Command passes.

## Acceptance
- AC-08 (source):
  - The served page contains no `innerHTML`, `outerHTML`, `insertAdjacentHTML`, `document.write` or `on*=` attribute.
  - It calls `/api/settings`, `/api/settings/validate`, `/api/settings/test-connection`, `/api/settings/csv/parse` and
    `/api/settings/csv/format`.
  - It reads `models`, `portHint`, `writesAllowed`, `warnings` and `commandsEnabled` from the GET body.
  - It contains no literal list of data types or model names.
  - Its password input has `autocomplete="new-password"` (amended 2026-11-03 by the user: browsers ignore "off" on password fields) and is never written to `localStorage` or `sessionStorage`.
  - It is served with the monitor's headers (kept test).

## Dependencies
- task-02-write-change-log.md
- task-03-test-unsaved-plc.md
- task-04-tag-csv.md

## Verification Plan
- Command: `dotnet test tests/Edge.Tests --filter "FullyQualifiedName~SettingsPageTests" -- RunConfiguration.TreatNoTestsAsError=true`
- Named probe: `SettingsPageTests`.
- Reachability: source. The behaviour in a browser is proved by the user in task-06, items 1–9.
- Oracle: exit 0, failed 0, skipped 0.
- Counterexample: a page that renders a tag description with `innerHTML`, or hard-codes the data types, must fail a
  test.
- Artifacts: none.

## Failure Protocol
On a failed Step or Verification Plan run: stop; do not widen scope, change the Command, or weaken a test; record observed versus expected; repair only the cited cause; after three failed rounds, stop and ask the user.

## Receipt

Verification: PASS
Command: dotnet test tests/Edge.Tests --filter "FullyQualifiedName~SettingsPageTests" -- RunConfiguration.TreatNoTestsAsError=true
Exit: 0
Base: e4f39e96710b817946ac2099e63e0a9bf7dbb80f
Head: fc4d0281aba07981750e37dbc87f9873db1ab43dc3166cdf64727e9370072e21
Process: the SettingsPageTests were extended first; the Command then failed 2 of 9 (the CSV routes were not called; the page held its own data type list). The page script parses (node new Function). These are source-level checks of the page text; behaviour in a browser is task-06's live proof.
Headless smoke (not the proof): the Edge run with the lab/multi appsettings (3 devices, 100 tags each, absolute --contentRoot) and Chrome --headless --dump-dom of /settings rendered 3 channels, 3 device cards, 300 tag rows, Length disabled on the 296 non-String tags, Rack/Slot hidden for the 2 Mitsubishi devices, the writes-not-allowed note only on FX3U; nothing was saved; the Edge was stopped and port 5080 freed.
Review: fresh-context code review, PASS_WITH_WARNINGS (2 Medium), repaired and re-reviewed PASS. Repaired: a number field holding text it cannot read (validity.badInput, e.g. "1,5") is refused and marked instead of being sent as "not set", which would have dropped a Min/Max; save and test read the fields before taking the password; the exported CSV starts with a BOM so Excel reads Vietnamese text; a tag's Writable stays locked on a model whose writes are not allowed after it is unticked; the CpuType warning no longer names one CPU; new device names are numbered over the whole page.
Amendment: AC-08's password field uses autocomplete="new-password" instead of "off" (user decision 2026-11-03, recorded in plan.md and this task).
Finding for task-06: the launch profile "Multi-PLC lab (no broker)" fails under dotnet run (DirectoryNotFoundException for bin\Debug\net10.0\lab\multi), because src/Edge/IndustrialIot.Edge.csproj copies lab\** Never while --contentRoot lab/multi resolves against the app base directory; an absolute --contentRoot works.
Limitations: tag Name, Address and Host are trimmed by the page; Length is cleared on a non-String tag before validate sees it; Rack/Slot are cleared for a model the Edge does not offer; Min/Max go through JS Number (about 15 significant digits); an earlier error mark stays until the next validate; a default device name can repeat after a delete (validate names it).
```text
$ dotnet test tests/Edge.Tests --filter "FullyQualifiedName~SettingsPageTests" -- RunConfiguration.TreatNoTestsAsError=true
  Determining projects to restore...
  All projects are up-to-date for restore.
  IndustrialIot.Shared -> C:\Users\case 046\Documents\Project\PlcProtocol\src\Shared\bin\Debug\net10.0\IndustrialIot.Shared.dll
  IndustrialIot.Edge -> C:\Users\case 046\Documents\Project\PlcProtocol\src\Edge\bin\Debug\net10.0\IndustrialIot.Edge.dll
  Edge.Tests -> C:\Users\case 046\Documents\Project\PlcProtocol\tests\Edge.Tests\bin\Debug\net10.0\Edge.Tests.dll
Test run for C:\Users\case 046\Documents\Project\PlcProtocol\tests\Edge.Tests\bin\Debug\net10.0\Edge.Tests.dll (.NETCoreApp,Version=v10.0)
A total of 1 test files matched the specified pattern.

Passed!  - Failed:     0, Passed:    10, Skipped:     0, Total:    10, Duration: 507 ms - Edge.Tests.dll (net10.0)
```
