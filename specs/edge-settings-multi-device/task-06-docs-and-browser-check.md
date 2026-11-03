# Task 06 — Docs match the new page; the user configures 2 brands × 2 PLCs in a browser

Status: done

## Outcome
`docs/deployment.md` describes the tree page, CSV, write permissions on the page, the write-change log and the apply
pause. The user has done the roadmap GĐ4 acceptance, and the page behaviours of AC-08, in a browser without PLCs.

## Scope
- In, `docs/deployment.md`:
  - Rewrite the settings-page section for the tree.
  - Replace the text saying `Writable` is edited only in the file. It is at about lines 149, 158, 292 and 387-390;
    re-find it before editing. The new text says:
    - the page edits `WritesEnabled` and `Writable`;
    - the global `Edge:Commands:Enabled` is still needed;
    - applied changes are logged at Warning.
  - The CSV columns, encoding and separator, and the `'` added before formula characters.
  - A save restarts every PLC for a few seconds (the 4.5 limitation).
  - Saving a single-PLC Edge:
    - converts its file to schema 2 and keeps its TagIds;
    - fixes `WritesEnabled` at the value `Edge:Commands:Enabled` had at that save;
    - drops a `CpuType` other than S71200 (D-08).
  - Troubleshooting rows for the new errors (`endpoint-in-use`, CSV line errors, 413).
- In, a user check on the dev machine with the profile "Multi-PLC lab (no broker)". There is no real PLC, so the
  devices show Unavailable.
  - First, set the settings password for that profile's settings file (`set-settings-password` with that profile's
    content root).
  - The user does each item and reports what happened, without the password:
    1. In the browser only, build 2 channels (S7, Slmp3E) × 2 devices each, with 3 tags per device. Save. The monitor
       shows 4 devices.
    2. Export one device's CSV, delete its tags, import the file and save. The tags are the same as before.
    3. Import a CSV with a wrong data type on one line. The page names that line.
    4. Press Test kết nối on a new, unsaved device. It gives a clear `unreachable`.
    5. Choose S7-1200. Port 102, Rack 0 and Slot 1 are filled in. Choose FX5U. Port is empty and the hint is shown.
    6. Turn on WritesEnabled and one tag's Writable on an S7 device and save. The Edge log shows two Warning lines.
    7. Choose FX3U. WritesEnabled cannot be turned on, or a save names that device's `WritesEnabled`.
    8. Delete one device and save. The monitor shows 3 devices.
    9. Type a wrong address on one tag of a device and save. The error is shown on that tag.
- Out: tests on real PLCs.

## Coverage
- CP-06, plus the live level of CP-05

## Ownership
- Modify: `docs/deployment.md`
- Read: `src/Edge/Settings/*.cs`, `src/Edge/Monitoring/settings.html`, `src/Edge/Properties/launchSettings.json`,
  `src/Edge/lab/multi/appsettings.json`

## Steps
1. Write the docs.
2. The user runs the check and reports. Record the report in the Receipt.

## Acceptance
- AC-08 (live) and AC-09: the user reports items 1–9 as described.
- Code review confirms the docs match the code.

## Dependencies
- task-05-tree-settings-page.md

## Verification Plan
- Command: `dotnet test IndustrialIot.sln -- RunConfiguration.TreatNoTestsAsError=true`
- Named probe: the whole solution on the final tree, plus the user's report of items 1–9.
- Reachability: source on the dev machine. The browser check is done by the user.
- Oracle: exit 0, failed 0, skipped 0, and the user reports all nine items as expected.
- Counterexample: any of these fails this task:
  - a doc that still says `Writable` is file-only;
  - a user report that the CSV round trip changed a tag;
  - a missing Warning line in item 6.
- Artifacts: none kept. The settings file written during the check is deleted afterwards, back to the profile's
  appsettings.

## Failure Protocol
On a failed Step or Verification Plan run: stop; do not widen scope, change the Command, or weaken a test; record observed versus expected; repair only the cited cause; after three failed rounds, stop and ask the user.

## Receipt

Verification: PASS
Command: dotnet test IndustrialIot.sln -- RunConfiguration.TreatNoTestsAsError=true
Exit: 0
Base: e4f39e96710b817946ac2099e63e0a9bf7dbb80f
Head: 6652a4cabc4af7ee55c433ff96f03f89eb9e6aadc2568d05bf032596a7fd4be6
Docs: docs/deployment.md section "Trang cấu hình PLC và tag" rewritten for the tree page (apply pause, v1 migration with WritesEnabled fixed and CpuType dropped, write permissions on the page and the Warning log, CSV rules, limits, troubleshooting for 413, 409 endpoint-in-use and CSV line errors); switch-table row 4 and the other Writable-only-in-the-file sentences updated.
User check (2026-11-03, Edge run from source with the lab/multi content root given as an absolute path, settings password set for that root): the user reported items 1-9 all as expected. Item 6 evidence pasted by the user, one apply id: "Write permission changed by settings apply 79619a05fc1e49cd9fa395b31d8603fa: device S7A off -> on." and "... tag S7A.I0 off -> on.". An earlier paste without these lines was of a later save that changed nothing (the saved file showed S7A WritesEnabled=True, I0 Writable=True), so no line was due. The user did not report the password.
Regression found by this Command and repaired during this task: task-01's TagCatalogException (a subclass) broke 41 Edge tests that use Assert.Throws<InvalidOperationException> (exact type), and task-01's filtered Command had not run them. src/Edge/Drivers/PlcTagCatalog.cs now throws InvalidOperationException with unchanged messages and puts the tag id and field in Exception.Data (TagIdKey, FieldKey); src/Edge/Drivers/PlcDeviceRegistry.cs reads them, falling back to "Tags". The mutant dropping Data[FieldKey] failed 4 SettingsApiTests. A rerun after restoring that mutant still failed because the restored file kept an older mtime and MSBuild reused the mutant build; the file was touched and rebuilt before this run.
Review: fresh-context code review of the docs against the code and of the regression repair, PASS. Lows not fixed: PlcTagCatalog.cs:87 (Union-only constructor) and :190 (null entry) set no Data, which the fallback "Tags" covers; the docs list = + - @ as formula characters but the code also guards tab and CR and a leading quote before them; the troubleshooting row joins 409 apply-in-progress/test-running with stale-revision and says reload the page, which loses unsaved edits (the page itself says try again shortly); the 4 MB limit applies to the JSON body wrapping the CSV, so a file just under 4 MB can still get 413.
Limitations: the launch profile "Multi-PLC lab (no broker)" fails from Visual Studio and dotnet run with a relative --contentRoot (DirectoryNotFoundException under bin); an absolute path works. D-03: turning a device's Enabled on, or changing its host while keeping its name, logs no write-permission line. A CSV row of only commas (Excel blank row) is refused. No real PLC was used; every device showed unavailable.
```text
$ dotnet test IndustrialIot.sln -- RunConfiguration.TreatNoTestsAsError=true
  Determining projects to restore...
  All projects are up-to-date for restore.
  InventoryClient -> C:\Users\case 046\Documents\Project\PlcProtocol\samples\InventoryClient\bin\Debug\net10.0\InventoryClient.dll
  IndustrialIot.Shared -> C:\Users\case 046\Documents\Project\PlcProtocol\src\Shared\bin\Debug\net10.0\IndustrialIot.Shared.dll
  Shared.Tests -> C:\Users\case 046\Documents\Project\PlcProtocol\tests\Shared.Tests\bin\Debug\net10.0\Shared.Tests.dll
  IndustrialIot.Edge -> C:\Users\case 046\Documents\Project\PlcProtocol\src\Edge\bin\Debug\net10.0\IndustrialIot.Edge.dll
Test run for C:\Users\case 046\Documents\Project\PlcProtocol\tests\Shared.Tests\bin\Debug\net10.0\Shared.Tests.dll (.NETCoreApp,Version=v10.0)
  IndustrialIot.Cloud -> C:\Users\case 046\Documents\Project\PlcProtocol\src\Cloud\bin\Debug\net10.0\IndustrialIot.Cloud.dll
A total of 1 test files matched the specified pattern.
  Edge.Tests -> C:\Users\case 046\Documents\Project\PlcProtocol\tests\Edge.Tests\bin\Debug\net10.0\Edge.Tests.dll
Test run for C:\Users\case 046\Documents\Project\PlcProtocol\tests\Edge.Tests\bin\Debug\net10.0\Edge.Tests.dll (.NETCoreApp,Version=v10.0)
A total of 1 test files matched the specified pattern.
  Deployment.Tests -> C:\Users\case 046\Documents\Project\PlcProtocol\tests\Deployment.Tests\bin\Debug\net10.0\Deployment.Tests.dll
  Cloud.Tests -> C:\Users\case 046\Documents\Project\PlcProtocol\tests\Cloud.Tests\bin\Debug\net10.0\Cloud.Tests.dll
Test run for C:\Users\case 046\Documents\Project\PlcProtocol\tests\Deployment.Tests\bin\Debug\net10.0\Deployment.Tests.dll (.NETCoreApp,Version=v10.0)
Test run for C:\Users\case 046\Documents\Project\PlcProtocol\tests\Cloud.Tests\bin\Debug\net10.0\Cloud.Tests.dll (.NETCoreApp,Version=v10.0)
A total of 1 test files matched the specified pattern.
A total of 1 test files matched the specified pattern.

Passed!  - Failed:     0, Passed:   101, Skipped:     0, Total:   101, Duration: 307 ms - Shared.Tests.dll (net10.0)

Passed!  - Failed:     0, Passed:   829, Skipped:     0, Total:   829, Duration: 1 m 21 s - Edge.Tests.dll (net10.0)

Passed!  - Failed:     0, Passed:   175, Skipped:     0, Total:   175, Duration: 2 m 14 s - Cloud.Tests.dll (net10.0)

Passed!  - Failed:     0, Passed:    47, Skipped:     0, Total:    47, Duration: 3 m 38 s - Deployment.Tests.dll (net10.0)
```
