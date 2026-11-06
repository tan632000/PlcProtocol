# Task 05 — The WPF window and dialogs look like a classic desktop driver tool

Status: done

## Outcome
The exe opens the D-03 window and the D-05 dialogs, bound to the Core view models. A Linux-runnable test proves that
the listed XAML files exist and that every binding path in them exists on its view model.

## Scope
- In:
  - `MainWindow.xaml(.cs)` rewritten:
    - menu, toolbar, tree, tag grid, log (Xem → Log sự kiện hides it), status bar;
    - key bindings Ctrl+S, F5, Enter, Del;
    - commands disabled as the view model says, including while busy; the PLC dialog's Test kết nối button is
      disabled while its test runs (carried from the task-02 review); tree and grid double-click and Enter call the
      same view-model commands (no handler acts on its own), so busy blocks them too;
    - tree items bind `IsSelected` and `IsExpanded` of `TreeNode` two-way, and the tree's selection change sets
      `MainViewModel.SelectedNode` (carried from the task-03 review); a dialog whose `DialogRequest.Ok` logged a node
      no longer in the draft closes, even the tag dialog in Add mode;
    - the 4 cards and the web-copy tables removed;
    - window closing routed to the close guard.
  - Dialog windows:
    - `ChannelDialog`, `DriverDialog`, `DeviceDialog`, `TagDialog`, `PasswordDialog`;
    - modal, owner = main window, Enter = default button, Esc = cancel, first field focused.
  - `WpfPrompts.cs` implements `UserPrompts` with these dialogs and MessageBox.
  - `PasswordDialog` (D-06):
    - uses `PasswordBox`, with no binding to the password;
    - OK disabled while the box is empty;
    - the value is read once into the request and the box is cleared after it.
  - `App.xaml.cs` creates `MainViewModel` and its window.
  - `XamlBindingTests` (net10.0, Linux-runnable):
    - finds the repository root by walking up to `IndustrialIot.sln`;
    - has a table in the test listing each expected file (`MainWindow.xaml` and the five dialogs) with its view model
      type, and asserts each file exists;
    - parses each file and requires every `{Binding …}` path, dotted and checked segment by segment, to name a public
      property of the right type:
      - bindings inside a `DataTemplate` or a column resolve against the type named by
        `d:DataContext="{d:DesignInstance …}"` on the nearest annotated element, and a template without one fails;
      - bindings with `ElementName` or `RelativeSource` are skipped.
- Out: Cut/Copy/Paste, right-click menus, icons beyond text and tooltips, dark mode.

## Coverage
- CP-01, CP-02, CP-04

## Ownership
- Modify:
  - `src/EdgeDesktop/MainWindow.xaml`
  - `src/EdgeDesktop/MainWindow.xaml.cs`
  - `src/EdgeDesktop/App.xaml.cs`
  - `src/EdgeDesktop.Core/DialogViewModel.cs`, `src/EdgeDesktop.Core/MainViewModel.cs`, `tests/Edge.Tests/Desktop/MainViewModelTests.cs` (amendment approved by the user 2026-11-05: `IsDetached`, so a tag dialog in Add mode closes when its PLC is gone from the draft; `DialogRequest.Ok` returns true both then and after an add that keeps the dialog open; extended by the user the same day: `GridRow` updated in place by each reading, so the tag grid keeps its row, focus and current cell)
- Create:
  - `src/EdgeDesktop/ChannelDialog.xaml(.cs)`
  - `src/EdgeDesktop/DriverDialog.xaml(.cs)`
  - `src/EdgeDesktop/DeviceDialog.xaml(.cs)`
  - `src/EdgeDesktop/TagDialog.xaml(.cs)`
  - `src/EdgeDesktop/PasswordDialog.xaml(.cs)`
  - `src/EdgeDesktop/WpfPrompts.cs`
  - `tests/Edge.Tests/Desktop/XamlBindingTests.cs`
- Read: `src/EdgeDesktop.Core/*.cs`

## Steps
1. Write `XamlBindingTests`.
2. Run the Command; it must fail, because the dialog files listed in the test do not exist and `MainWindow.xaml` binds
   `MonitorViewModel` properties that `MainViewModel` does not have.
3. Write the XAML and code-behind; run the Command.
4. Smoke on Windows:
   - Release exe with `--port 5999` (no Edge): the window opens with the red status text, no XAML exception for 5 s,
     then closes;
   - against a running Edge (from source), every dialog is opened once from the menu and closed with Esc;
   - during a save, a double-click on a tree node opens nothing.

## Acceptance
- The Command passes. `XamlBindingTests` finds every listed file, zero unknown paths and zero unannotated templates.
- The smoke shows the window and each dialog without a XAML exception.
- The live behaviour (AC-01–AC-06, AC-09) is checked by the user in task-06.

## Dependencies
- task-04-save-flow.md

## Verification Plan
- Command: `dotnet build src/EdgeDesktop/IndustrialIot.EdgeDesktop.csproj -c Release && dotnet test tests/Edge.Tests/Edge.Tests.csproj --filter "FullyQualifiedName~IndustrialIot.Edge.Tests.Desktop" -- RunConfiguration.TreatNoTestsAsError=true`
- Named probe: `XamlBindingTests`, plus all Desktop Core tests.
- Reachability: source; the Windows smoke for the runtime.
- Oracle: exit 0, failed 0, skipped 0; smoke without a XAML exception.
- Counterexample: each of these must fail `XamlBindingTests`, shown with a mutant on a disposable copy:
  - a misspelled binding path;
  - a `DataTemplate` without `d:DataContext`;
  - a deleted `TagDialog.xaml`.
- Artifacts: smoke processes stopped by the controller.

## Failure Protocol
On a failed Step or Verification Plan run: stop; do not widen scope, change the Command, or weaken a test; record observed versus expected; repair only the cited cause; after three failed rounds, stop and ask the user.

## Receipt

Verification: PASS
Command: dotnet build src/EdgeDesktop/IndustrialIot.EdgeDesktop.csproj -c Release && dotnet test tests/Edge.Tests/Edge.Tests.csproj --filter "FullyQualifiedName~IndustrialIot.Edge.Tests.Desktop" -- RunConfiguration.TreatNoTestsAsError=true
Exit: 0
Base: 9404c8f181c47081deb68a80762dd3689f9d5869
Head: 0196c2924952f6144de65dbfcff3686ce517e6b538176e5f16168616df3133f0
Process: with XamlBindingTests written and before the new XAML, the Command exited 1 with 6 failures: the five dialog files missing and MainWindow.xaml binding MonitorViewModel properties (Updated, EdgeId, …) and unannotated styles; the checker's own test passed. The in-place GridRow test failed to compile (no PropertyChanged) before GridRow changed.
Counterexamples: nine mutants on a disposable copy (deleted afterwards), each KILLED by a failing test with all 219 tests run: the three of the Verification Plan (a misspelled binding path, a DataTemplate without d:DataContext, a deleted TagDialog.xaml) and the password dialog binding a property, a tree item style without d:DataContext, a dotted path wrong in its second segment, a misspelled FieldErrors, a tag dialog on a PLC gone from the draft not marked detached, a newer reading replacing the grid row.
Smoke (Windows, Release exe): with `--port 5999` and no Edge the window opened with the red banner and "Mất kết nối tới Edge" logged, alive after 6 s, then stopped. Against the Edge run from source (port 5080), by UI Automation: Sửa > Thêm Channel and its "…" (Chọn driver), Sửa > Thêm PLC, Sửa > Thêm Tag, Công cụ > Test kết nối PLC (Sửa PLC) and its Test kết nối (Mật khẩu settings) each opened and closed with Esc; Del in the tag grid asked "Xoá tag Line1.Running?" (Esc = Huỷ); Enter in the grid 2 s after the selection opened Sửa Tag; the unedited window closed with exit code 0. No XAML exception. The Edge and the app were stopped by the controller.
Reachability: source; App creates MainViewModel with WpfPrompts as its IUserPrompts and IDialogOpener and shows MainWindow; every dialog is reached from the menu (smoke). Logic stays in the Core view models (D-02), whose tests run in the Command.
Review: fresh-context code review FAIL (High: Del swallowed by the read-only grid's own Delete binding; Medium: no Thoát không chờ for a save started by the close guard, no error line for Writable, Enter lost when the grid row is replaced, Core files changed outside Ownership), repaired. Re-review FAIL only on the Ownership change, which the user approved as an amendment (recorded in Ownership), and a Medium (Del on the tree deleting the grid's tag), repaired. Third review PASS_WITH_WARNINGS (Medium: grid rows replaced on each poll), repaired by the user's extended amendment (GridRow updated in place). Fourth fresh review PASS; afterwards only the comment of OnWindowKey was corrected (no code change).
Scope note: src/EdgeDesktop.Core/DialogViewModel.cs, src/EdgeDesktop.Core/MainViewModel.cs and tests/Edge.Tests/Desktop/MainViewModelTests.cs changed under the amendment approved by the user on 2026-11-05 (IsDetached; GridRow in place). WpfPrompts asks its choices in a small window of its own instead of MessageBox, so the buttons carry the plan's words (Lưu / Không lưu / Huỷ, Đợi / Thoát không chờ, Tải lại (bỏ thay đổi) / Giữ để xem); MessageBox remains for Giới thiệu and the browser error.
Limitation: "during a save, a double-click on a tree node opens nothing" was not run live (it needs the settings password and a real apply); ActivateAsync returning while busy is covered by the Core test Busy_DisablesEveryCommand_AndNothingRunsUntilItEnds, and the live check moves to task-06. Arrow keys in the grid while values change were not reproduced live (the lab PLC values did not change during the smoke). Reviews' Lows left: a missing FieldErrors key logs a WPF binding error (the text stays empty); a double-click on a channel also toggles its expansion; Đợi while busy keeps the window open after the save unless the save came from the close guard (Core task-04 semantics); no DispatcherUnhandledException handler; XamlBindingTests trusts d:DataContext against ItemsSource and does not check TwoWay setters or indexer keys.
```text
$ dotnet build src/EdgeDesktop/IndustrialIot.EdgeDesktop.csproj -c Release && dotnet test tests/Edge.Tests/Edge.Tests.csproj --filter "FullyQualifiedName~IndustrialIot.Edge.Tests.Desktop" -- RunConfiguration.TreatNoTestsAsError=true
Build succeeded.
Passed!  - Failed:     0, Passed:   219, Skipped:     0, Total:   219, Duration: 19 s - Edge.Tests.dll (net10.0)
exit=0
```
