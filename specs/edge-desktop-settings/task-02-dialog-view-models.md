# Task 02 — Dialog view models offer only what the PLC model allows

Status: pending

## Outcome
The Channel (with its driver picker), PLC and Tag dialogs exist as Core view models. They edit a copy of one draft
node under the model catalog, check it with `validate`, and the PLC dialog tests its connection.

## Scope
- In (D-05, D-06):
  - `ChannelDialogViewModel`:
    - name;
    - driver chosen from Siemens S7 | Mitsubishi SLMP 3E | Mitsubishi MC 1E, with the note "Hỗ trợ dòng PLC: …" from
      the catalog;
    - the driver can change only while the channel has no PLC.
  - `DeviceDialogViewModel`:
    - models of the channel's driver only; choosing one fills port/rack/slot defaults and shows the port hint;
    - Rack/Slot only for S7;
    - new-PLC defaults 3000/1000;
    - "Cho phép ghi" disabled with the app's text "{Model} chưa cho phép ghi (chưa kiểm chứng trên PLC thật)" when
      `writesAllowed` is false;
    - LegacyTagIds shown only when set, with a warning on untick;
    - Test kết nối:
      - through `SettingsClient.TestConnectionAsync`, with a password asked from `UserPrompts`;
      - the D-05 refusal texts shown in the dialog;
      - `proposed:{key}` errors mapped to the dialog's fields.
  - `TagDialogViewModel`:
    - data types of the device's model; Độ dài only for String;
    - Ghi disabled when the model does not allow writes; Min/Max only with Ghi;
    - Kiểm tra: validate the draft with this tag, keep only this tag's errors, or "Hợp lệ";
    - Áp dụng in Add mode adds the tag and clears name and address; in Edit mode it updates and closes.
  - All dialogs:
    - number fields reject non-digits locally ("phải là số");
    - OK/Áp dụng is refused while a local error exists;
    - Huỷ leaves the draft unchanged;
    - each dialog edits a copy and applies it to the node it was opened for, identified by reference, never by index.
  - The delete confirmation text "Xoá {Channel|PLC|tag} {name}?" with what goes with it.
  - `UserPrompts.cs`: the prompt interface for password and confirmations. A password prompt whose answer is empty
    counts as cancelled.
- Out: the main view model, XAML.

## Coverage
- CP-02, CP-03 (test-connection), CP-04

## Ownership
- Create:
  - `src/EdgeDesktop.Core/ChannelDialogViewModel.cs`
  - `src/EdgeDesktop.Core/DeviceDialogViewModel.cs`
  - `src/EdgeDesktop.Core/TagDialogViewModel.cs`
  - `src/EdgeDesktop.Core/UserPrompts.cs`
  - `tests/Edge.Tests/Desktop/DialogViewModelTests.cs`
- Read:
  - `src/EdgeDesktop.Core/ConfigDraft.cs`
  - `src/EdgeDesktop.Core/SettingsClient.cs`
  - `src/Edge/Drivers/PlcModelCatalog.cs`
  - `src/Edge/Monitoring/settings.html`

## Steps
1. Write `DialogViewModelTests`. They cover:
   - catalog filtering per driver;
   - defaults filled on model change;
   - writes disabled for FX3U, with its text;
   - driver locked while the channel has PLCs;
   - local number errors;
   - Kiểm tra keeps only this tag's errors (scripted handler replies);
   - Áp dụng in Add vs Edit mode;
   - Huỷ leaves the draft unchanged;
   - Test kết nối asks the password once; cancelling or leaving it empty sends nothing; each refusal text;
     `proposed:port` mapped to Port.
2. Run the Command; it must fail.
3. Implement; run the Command.

## Acceptance
- AC-03: the tests above pass; the draft changes only through OK/Áp dụng.
- AC-04: a tag dialog shows exactly its own `validate` errors, or "Hợp lệ".
- AC-07: a cancelled or empty password prompt sends no request.
- AC-09: Test kết nối sends the dialog's current values with the typed password and shows the reply or the refusal.

## Dependencies
- task-01-settings-client.md

## Verification Plan
- Command: `dotnet test tests/Edge.Tests/Edge.Tests.csproj --filter "FullyQualifiedName~IndustrialIot.Edge.Tests.Desktop" -- RunConfiguration.TreatNoTestsAsError=true`
- Named probe: `DialogViewModelTests`.
- Reachability: source.
- Oracle: exit 0, failed 0, skipped 0.
- Counterexample: each of these must fail a test, shown with a mutant on a disposable copy:
  - FX3U allows "Cho phép ghi";
  - a Slmp3E channel offers S7-1200;
  - Huỷ keeps the edit;
  - Kiểm tra shows another tag's error;
  - `rate-limited` shows the lockout text.
- Artifacts: none.

## Failure Protocol
On a failed Step or Verification Plan run: stop; do not widen scope, change the Command, or weaken a test; record observed versus expected; repair only the cited cause; after three failed rounds, stop and ask the user.

## Receipt
