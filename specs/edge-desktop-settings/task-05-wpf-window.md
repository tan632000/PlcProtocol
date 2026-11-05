# Task 05 — The WPF window and dialogs look like a classic desktop driver tool

Status: pending

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
