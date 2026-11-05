# Task 04 — Saving is safe: busy state, apply wait, every refusal, close guard and outside changes

Status: done

## Outcome
"Lưu & áp dụng", Tải lại, closing the window and changes made elsewhere behave as D-04 and D-08: nothing is lost
silently, nothing is sent twice, and the password is used once.

## Scope
- In (D-04, D-06, D-08):
  - `SaveFlow` (used by `MainViewModel`):
    - busy state and the D-03 busy rule;
    - D-04 steps 1–5: the channel-without-PLC refusal, validate, the password prompt, PUT;
    - the apply wait on `LastStatus.SettingsApply`: poll failures ignored and not logged, our `applyId`, a foreign
      `applyId`, 60 s;
    - every refusal message, classified by the `error` key;
    - 409 `stale-revision` with Tải lại / Giữ để xem; a stale draft disables Lưu until Tải lại;
    - an unconfirmed save mark.
  - Tải lại, with a confirmation when dirty.
  - Close guard: Lưu / Không lưu / Huỷ, or while busy Đợi / Thoát không chờ.
  - D-08 on `StatusApplied`:
    - silent reload only when the draft is clean, no dialog is open and the app is not busy;
    - otherwise one warning;
    - the "có thể là lần lưu vừa rồi" text after an unconfirmed save.
- Out: XAML, CSV.

## Coverage
- CP-03, CP-04

## Ownership
- Create:
  - `src/EdgeDesktop.Core/SaveFlow.cs`
  - `tests/Edge.Tests/Desktop/SaveFlowTests.cs`
- Modify:
  - `src/EdgeDesktop.Core/MainViewModel.cs` (wiring)
  - `src/EdgeDesktop.Core/UserPrompts.cs` (close and stale choices)
  - `tests/Edge.Tests/Desktop/MainViewModelTests.cs`, `tests/Edge.Tests/Desktop/DialogViewModelTests.cs` (fake prompts implement the new IUserPrompts members)
- Read:
  - `src/EdgeDesktop.Core/SettingsClient.cs`
  - `src/EdgeDesktop.Core/ConfigDraft.cs`
  - `src/EdgeDesktop.Core/MonitorViewModel.cs`

## Steps
1. Write `SaveFlowTests` with a scripted handler, `FakeTimeProvider` and fake prompts. They cover:
   - the happy path to applied and reload, with a failing `/api/status` during the wait (no red text, no lost/back log);
   - rolled back;
   - a foreign `applyId` keeps the draft and marks it stale;
   - the 60 s timeout, then the next revision change logged as "có thể là lần lưu vừa rồi";
   - each refusal message with the draft kept; the password is asked again only after 401, never after 429 `locked`;
   - validation errors select the first mappable node;
   - a channel without PLC refused before validate;
   - stale → Giữ để xem disables Lưu;
   - busy disables the commands, and a second save is impossible while busy;
   - the close guard in both modes;
   - D-08 with clean, dirty, dialog-open and busy states;
   - the password absent from every log entry and public property after a save, and an empty password never sent.
2. Run the Command; it must fail.
3. Implement; run the Command.

## Acceptance
- AC-05, AC-06, AC-07, AC-08: the tests above pass.

## Dependencies
- task-03-main-view-model.md

## Verification Plan
- Command: `dotnet test tests/Edge.Tests/Edge.Tests.csproj --filter "FullyQualifiedName~IndustrialIot.Edge.Tests.Desktop" -- RunConfiguration.TreatNoTestsAsError=true`
- Named probe: `SaveFlowTests`.
- Reachability: source.
- Oracle: exit 0, failed 0, skipped 0.
- Counterexample: each of these must fail a test, shown with a mutant on a disposable copy:
  - 409 `stale-revision` drops the draft;
  - a save is sent while `validate` reports errors;
  - a poll failure during the wait logs "Edge lost";
  - a reload happens while a dialog is open;
  - Lưu stays enabled while busy;
  - a foreign `applyId` reloads the draft;
  - 429 `locked` asks for the password again;
  - the password is logged.
- Artifacts: none.

## Failure Protocol
On a failed Step or Verification Plan run: stop; do not widen scope, change the Command, or weaken a test; record observed versus expected; repair only the cited cause; after three failed rounds, stop and ask the user.

## Receipt

Verification: PASS
Command: dotnet test tests/Edge.Tests/Edge.Tests.csproj --filter "FullyQualifiedName~IndustrialIot.Edge.Tests.Desktop" -- RunConfiguration.TreatNoTestsAsError=true
Exit: 0
Base: c48846ba5d074c7fc33681f3571311c37d75266a
Head: 7589e49419f1599aff3bac8271c79b584ebca2a747bfd61ac85ed3e9f9266b64
Process: with SaveFlowTests written and before SaveFlow existed, the Command exited 1 at compile time on the missing CloseChoice type and SaveFlow members.
Counterexamples: twenty-one mutants on a disposable copy (deleted afterwards), each KILLED by a failing test with all 210 tests run (none by the hang timeout): the eight of the Verification Plan (409 stale-revision drops the draft, a save sent while validate reports errors, a poll failure during the wait logs Edge lost, a reload while a dialog is open, Lưu enabled while busy, a foreign applyId reloads the draft, 429 locked asks for the password again, the password logged) and a second save while busy, a status read before the PUT taken as a foreign apply, a timeout not marked unconfirmed, a channel without PLC not refused, close on Lưu when not applied, an outside change warned on every status, the previous applyId read before the password, our applied revision not noted, the noticed revision never reset, Lưu on a stale draft saying nothing, a dialog opened while busy, an outside change reloading a dirty draft. A first run showed four of them only hanging on the fake clock; saves expected to end without the apply wait are now bounded by a 5 s real-time limit, so they fail instead.
Reachability: source; SaveFlow is reached only through MainViewModel.SaveAsync, ReloadAsync, ConfirmCloseAsync and the status hook at the end of OnStatusApplied; requests go through the task-01 SettingsClient (scripted handler). The window binding is task-05.
Review: fresh-context code review PASS_WITH_WARNINGS (Medium: the previous applyId read before the password prompt; Lows: Lưu on a stale draft silent, a failed reload after applied taken for a change elsewhere, the noticed revision never reset, the timeout counted from after the wait showed), repaired with tests. Fresh re-review PASS; its Low on a flaky wait in ChangedElsewhere_Clean_ReloadsSilently was repaired. The 5 s bound on saves (test-only) was added after the re-review.
Scope note: MainViewModelTests.cs and DialogViewModelTests.cs got three NotSupported stubs each for the new IUserPrompts members, recorded in Ownership.
Limitation: if another client's apply ends within about a second before this app's PUT, before the next status poll, it can be taken for this save's answer. After a timeout, the next revision change is logged as "có thể là lần lưu vừa rồi" even when it is someone else's (as D-08 words it). The validate-refused branch (Refusal on ValidateAsync) has no own test. Carried to task-05: prompt wording when busy only because of a reload.
```text
$ dotnet test tests/Edge.Tests/Edge.Tests.csproj --filter "FullyQualifiedName~IndustrialIot.Edge.Tests.Desktop" -- RunConfiguration.TreatNoTestsAsError=true
Passed!  - Failed:     0, Passed:   210, Skipped:     0, Total:   210, Duration: 19 s - Edge.Tests.dll (net10.0)
exit=0
```
