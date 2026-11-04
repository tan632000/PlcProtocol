# Task 06 — Docs describe the new window; the user checks it live and installed

Status: pending

## Outcome
The subsection "Ứng dụng IIoT Edge Monitor (Windows)" of `docs/deployment.md` describes the new window, the dialogs,
saving and the accepted port risk. The user has seen the window work against a running Edge, and from an installed
package as a non-admin user.

## Scope
- In, `docs/deployment.md`, the existing subsection rewritten:
  - layout;
  - dialogs and the catalog rules;
  - draft, ` *`, Lưu & áp dụng with the settings password;
  - "Đang áp dụng (Edge khởi động lại)…";
  - each D-04 and D-05 message and what to do;
  - the D-08 reload;
  - Test kết nối;
  - what stays web-only (CSV, Cut/Copy/Paste);
  - the accepted port risk (D-06, G11);
  - `--port`, SmartScreen and uninstall as today.
- In, a user check. Items 1–8 run against the Edge from source:
  - in `src\Edge`, with `DOTNET_ENVIRONMENT=Development` and the no-broker MQTT variables:
    `dotnet run --no-launch-profile -- --contentRoot "$PWD\lab\multi"`;
  - the app runs with `dotnet run --project src\EdgeDesktop`;
  - before item 3, back up `src\Edge\data\multi-lab.settings.json`; restore it after item 6.

  1. The tree shows the channels and PLCs of `/settings`, with state texts. Selecting a PLC shows its tags with
     Value/Chất lượng equal to the web monitor at the same moment. The status bar shows the Edge ID, MQTT and buffer.
  2. Stop the Edge: within about 3 s the red status text appears with one log entry, and the values stay. Start it
     again: one "back" entry, and the red text goes.
  3. Add a tag to a PLC:
     - Kiểm tra with a wrong address shows its error; a valid one shows "Hợp lệ";
     - Áp dụng adds it, and the title shows ` *`;
     - Lưu & áp dụng with the password shows "Đang áp dụng…", with the buttons disabled, then logs "Đã áp dụng", with no
       red text or lost/back entries in between;
     - the web `/settings` shows the tag.
  4. A wrong password shows "Sai mật khẩu" and keeps the draft. An empty password cannot be confirmed.
  5. Changes made elsewhere:
     - with no unsaved change, edit something on the web page: the app reloads and logs it;
     - with an unsaved change, edit on the web, then save in the app: the "đã đổi ở nơi khác" choice appears, and
       Giữ để xem leaves Lưu disabled until Tải lại.
  6. Add Channel with Mitsubishi MC 1E, then Add PLC: only FX3U is offered, and "Cho phép ghi" is disabled with its text.
     Saving a new channel without PLC is refused. Then Tải lại (bỏ thay đổi).
  7. Test kết nối on a PLC shows a result (a failure reason is fine without the PLC). Clicking it again at once shows
     "Một lần test khác đang chạy" or the result.
  8. Close with an unsaved change: Lưu / Không lưu / Huỷ appears, and Huỷ keeps the window.

  Item 9 is on an installed test machine, as items 5–7 of edge-desktop-monitor:
  9. Package and install with throw-away values, then run `set-settings-password --service` as admin. A non-admin user
     opens the shortcut, sees the tree, and saves one change with that password. Close the app, then
     `uninstall-edge.ps1 -RemoveData`.
- Out: tests with real PLCs.

## Coverage
- CP-01, CP-02, CP-03, CP-05, CP-06

## Ownership
- Modify: `docs/deployment.md`
- Read: `src/EdgeDesktop/*`, `src/EdgeDesktop.Core/*`

## Steps
1. Run the Command before changing the docs; it must fail at the greps.
2. Write the docs; run the Command.
3. The user runs items 1–9 and reports; the report is recorded in the Receipt.

## Acceptance
- AC-01–AC-09 (live) and AC-10 (installed): the user reports items 1–9 as described.
- AC-11:
  - the Command's greps find the new texts "Tải lại từ Edge", "Đang áp dụng (Edge khởi động lại)",
    "Bấm để thêm Channel" and "Giữ để xem" in `docs/deployment.md`; none of them is in the file before the change;
  - a code review confirms the docs match the code, including the accepted port risk.

## Dependencies
- task-05-wpf-window.md

## Verification Plan
- Command: `grep -q "Tải lại từ Edge" docs/deployment.md && grep -q "Đang áp dụng (Edge khởi động lại)" docs/deployment.md && grep -q "Bấm để thêm Channel" docs/deployment.md && grep -q "Giữ để xem" docs/deployment.md && dotnet test IndustrialIot.sln -- RunConfiguration.TreatNoTestsAsError=true`
- Named probe: the four greps, the whole solution, the user's items 1–9.
- Reachability: source; live and installed by the user.
- Oracle:
  - exit 0, failed 0, skipped 0;
  - items 1–9 as described;
  - review PASS.
- Counterexample: any of these fails the task:
  - item 2 logs on every poll or never clears;
  - item 3 shows red text or lost/back during the apply;
  - item 4 drops the draft;
  - item 6 allows writes on FX3U;
  - item 9 needs administrator rights to open or save.
- Artifacts: `multi-lab.settings.json` restored after item 6; item 9 removes the test install.

## Failure Protocol
On a failed Step or Verification Plan run: stop; do not widen scope, change the Command, or weaken a test; record observed versus expected; repair only the cited cause; after three failed rounds, stop and ask the user.

## Receipt
