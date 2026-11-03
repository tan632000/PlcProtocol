# Task 02 — Write-permission changes are logged when an apply completes

Status: done

## Outcome
An accepted save carries the diff of the effective write set (D-03). The supervisor logs it at Warning only when the
apply completes as Applied. A rolled-back apply logs no write change.

## Scope
- In:
  - `SettingsService` computes the diff after its checks:
    - old set: the running inner host's devices;
    - new set: the proposed tree.
    - Each set holds the devices with `WritesEnabled` and the TagIds with `WritesEnabled && Writable`.
    - Additions, removals and renames all count.
  - The diff is passed with `EdgeRestarter.TryRequest`, which keeps it on the `ApplyRequest`.
  - `EdgeSupervisor` logs one Warning per entry, with the device or TagId, old → new and the applyId. It logs after a
    successful start and before `Complete(Applied)`; never on rollback or fatal.
- Out: any other audit trail; what the log sinks are.

## Coverage
- CP-02

## Ownership
- Modify: `src/Edge/Settings/EdgeRestarter.cs`, `src/Edge/Settings/EdgeSupervisor.cs`,
  `src/Edge/Settings/SettingsService.cs` (the diff and the `TryRequest` call only), `tests/Edge.Tests/EdgeSupervisorTests.cs`
- Read: `src/Edge/Settings/SettingsTree.cs`, `tests/Edge.Tests/SettingsApiTests.cs`

## Steps
1. Add the tests below to `EdgeSupervisorTests`, with a fake logger and a fake driver factory. Run the Command; it
   fails.
2. Implement. The Command passes.

## Acceptance
- AC-04:
  - A save turns on one device's `WritesEnabled` and one tag's `Writable`. After Applied there are two Warning entries
    naming them, each with the applyId.
  - A save deletes device `Line1`, which had writes off, and adds `Line1b`, with writes on, on the same host. There
    are Warning entries for `Line1b` and its writable TagIds.
  - A save renames a writable tag. There are two entries: the old TagId on → off, and the new TagId off → on.
  - A save that turns writes on but fails to start, so it rolls back. No write-change entry is logged.
  - A save that changes no write permission. No write-change entry is logged.

## Dependencies
- task-01-settings-api-v2.md

## Verification Plan
- Command: `dotnet test tests/Edge.Tests --filter "FullyQualifiedName~EdgeSupervisorTests|FullyQualifiedName~SettingsApiTests" -- RunConfiguration.TreatNoTestsAsError=true`
- Named probe: the new write-log tests in `EdgeSupervisorTests`; `SettingsApiTests` as regression.
- Reachability: source. The real supervisor runs with the inner host and fake drivers.
- Oracle: exit 0, failed 0, skipped 0.
- Counterexample: each of these must fail a test:
  - logging at 202;
  - comparing by device name only;
  - logging on rollback.
- Artifacts: none.

## Failure Protocol
On a failed Step or Verification Plan run: stop; do not widen scope, change the Command, or weaken a test; record observed versus expected; repair only the cited cause; after three failed rounds, stop and ask the user.

## Receipt

Verification: PASS
Command: dotnet test tests/Edge.Tests --filter "FullyQualifiedName~EdgeSupervisorTests|FullyQualifiedName~SettingsApiTests" -- RunConfiguration.TreatNoTestsAsError=true
Exit: 0
Base: e4f39e96710b817946ac2099e63e0a9bf7dbb80f
Head: eea05388200f7f23fd0b69b10eccc1c55f195854a0af879f179151aa3944e0ce
Process: the source edits were stashed, the tests written, and the Command then failed to compile (TryRequest takes no write changes, no WriteChange type, no SettingsService.WriteChanges); the edits were restored and the Command passed. Discrimination shown by mutants, each failing tests: write changes logged on rollback too, new writable tags ignored, only additions counted, the save not passing its changes; sources restored and rebuilt after every mutant run.
Review: fresh-context code review, PASS, no Critical/High/Medium.
Ownership note: tests/Edge.Tests/SettingsApiTests.cs (task-01's file): WritePermissions_AreSavedAsSent gained the log assertions that prove the PUT passes its changes to the apply.
Open questions for the user at GATE-DONE (D-03 as written, not code defects): turning a device's Enabled on while WritesEnabled is already on logs nothing, though commands then reach that PLC; keeping a device's or tag's name while changing its host, port, rack, slot or address logs nothing, though the permission then points elsewhere.
Limitations: names are compared case-insensitively, as the runtime looks them up; the order "logged before Complete(Applied)" is not pinned by a test.
```text
$ dotnet test tests/Edge.Tests --filter "FullyQualifiedName~EdgeSupervisorTests|FullyQualifiedName~SettingsApiTests" -- RunConfiguration.TreatNoTestsAsError=true
  Determining projects to restore...
  Restored C:\Users\case 046\Documents\Project\PlcProtocol\src\Shared\IndustrialIot.Shared.csproj (in 88 ms).
  Restored C:\Users\case 046\Documents\Project\PlcProtocol\src\Edge\IndustrialIot.Edge.csproj (in 257 ms).
  Restored C:\Users\case 046\Documents\Project\PlcProtocol\tests\Edge.Tests\Edge.Tests.csproj (in 269 ms).
  IndustrialIot.Shared -> C:\Users\case 046\Documents\Project\PlcProtocol\src\Shared\bin\Debug\net10.0\IndustrialIot.Shared.dll
  IndustrialIot.Edge -> C:\Users\case 046\Documents\Project\PlcProtocol\src\Edge\bin\Debug\net10.0\IndustrialIot.Edge.dll
  Edge.Tests -> C:\Users\case 046\Documents\Project\PlcProtocol\tests\Edge.Tests\bin\Debug\net10.0\Edge.Tests.dll
Test run for C:\Users\case 046\Documents\Project\PlcProtocol\tests\Edge.Tests\bin\Debug\net10.0\Edge.Tests.dll (.NETCoreApp,Version=v10.0)
A total of 1 test files matched the specified pattern.

Passed!  - Failed:     0, Passed:    62, Skipped:     0, Total:    62, Duration: 11 s - Edge.Tests.dll (net10.0)
```
