# Task 03 — The main view model shows the tree with live state and values

Status: pending

## Outcome
One Core view model drives the window's display: the tree with state texts, the tag grid with live values, the event
log, the status bar, the title, and the commands with their enabled rules. Opening a dialog and delete come through it.

## Scope
- In (D-03, D-07):
  - `MonitorViewModel`: add `EdgeStatus? LastStatus` and a `StatusApplied` event, set and raised in `Apply`; nothing
    else changes, and `MonitorViewModelTests` stay unchanged and pass.
  - `MainViewModel` composes `MonitorViewModel`, `SettingsClient`, `ConfigDraft`, the dialog view models and `UserPrompts`.
  - Tree:
    - the root "Bấm để thêm Channel", then channels `{Name} ({driver display name})` and PLCs;
    - PLC texts from `MonitorViewModel.Devices` by name;
    - Đang đọc becomes "Có tag lỗi" when a tag of that PLC has a quality other than good;
    - "Chưa lưu" for draft-only PLCs.
  - Grid:
    - the selected PLC's tags merged with `MonitorViewModel.Tags` by the TagId rule (`EdgeChannelsOptions.cs:176`);
    - `—` for draft-only tags;
    - "Chọn một PLC để xem tag" otherwise.
  - Log:
    - newest first, capped at 500;
    - Edge lost / back and PLC state changes logged once per transition;
    - an unmapped error path logged verbatim.
  - Status bar fields and the red banner text (D-03), "Chu kỳ gần nhất" from `MonitorViewModel.LastCycle`.
  - Title with ` *` while dirty.
  - Commands and their enabled rules (D-03 toolbar, without the busy rule, which is task-04). Delete asks the D-05
    confirmation. Add/Edit open the task-02 dialog view models through `UserPrompts` and apply OK to the draft.
  - Start: GET → draft.
  - Carried from the task-02 review: a dialog command that meets a node no longer in the draft
    (`InvalidOperationException`) is caught where the main view model calls it and logged, never thrown to the
    window; the PLC dialog's Test kết nối result (`TestMessage`) is also written to the log.
- Out: the save flow, the busy state, the close guard and D-08 (task-04); XAML.

## Coverage
- CP-01, CP-02

## Ownership
- Create:
  - `src/EdgeDesktop.Core/MainViewModel.cs`
  - `tests/Edge.Tests/Desktop/MainViewModelTests.cs`
- Modify:
  - `src/EdgeDesktop.Core/MonitorViewModel.cs` (D-07 addition only)
  - `src/EdgeDesktop.Core/UserPrompts.cs` (dialog openers)
- Read:
  - `src/EdgeDesktop.Core/SettingsClient.cs`
  - `src/EdgeDesktop.Core/ConfigDraft.cs`
  - `src/EdgeDesktop.Core/*DialogViewModel.cs`
  - `tests/Edge.Tests/Desktop/MonitorViewModelTests.cs`

## Steps
1. Write `MainViewModelTests` with a scripted `HttpMessageHandler` for `/api/settings` and `/api/status`, a
   `FakeTimeProvider` and fake prompts. They cover:
   - tree and grid from GET + status;
   - each state text, including "Có tag lỗi" and "Chưa lưu";
   - a draft-only tag shows `—`;
   - lost/back logged once each across several failed polls;
   - status bar texts and the red text;
   - title ` *`;
   - enabled rules;
   - add/edit/delete through fake prompts change the draft;
   - `LastStatus` and `StatusApplied` after `Apply`.
2. Run the Command; it must fail.
3. Implement; run the Command.

## Acceptance
- AC-01, AC-02, AC-03 (main window side): the tests above pass.
- `MonitorViewModelTests` pass unchanged.

## Dependencies
- task-02-dialog-view-models.md

## Verification Plan
- Command: `dotnet test tests/Edge.Tests/Edge.Tests.csproj --filter "FullyQualifiedName~IndustrialIot.Edge.Tests.Desktop" -- RunConfiguration.TreatNoTestsAsError=true`
- Named probe: `MainViewModelTests`, `MonitorViewModelTests`.
- Reachability: source.
- Oracle: exit 0, failed 0, skipped 0; `git diff --stat tests/Edge.Tests/Desktop/MonitorViewModelTests.cs` empty.
- Counterexample: each of these must fail a test, shown with a mutant on a disposable copy:
  - Edge lost logged on every failed poll;
  - a tag of another PLC turning the state to "Có tag lỗi";
  - a draft-only tag showing another tag's value.
- Artifacts: none.

## Failure Protocol
On a failed Step or Verification Plan run: stop; do not widen scope, change the Command, or weaken a test; record observed versus expected; repair only the cited cause; after three failed rounds, stop and ask the user.

## Receipt
