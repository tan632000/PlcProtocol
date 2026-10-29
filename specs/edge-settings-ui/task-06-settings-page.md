# Task 06 — A settings page at http://localhost:5080/settings

Status: done

## Outcome
The monitor serves an embedded settings page and links to it from the monitor page. On it the operator can:
- edit the protocol and its parameters, the poll interval and the tags;
- validate the settings;
- save them with the password and follow that save's `applyId` until it is applied or rolled back;
- test the PLC connection.

## Scope
- In:
  - `GET /settings` serves the embedded `settings.html` with the same CSP and headers as the monitor. `monitor.html` links to it.
  - Protocol select S7 / SLMP. The parameter fields change with it:
    - S7: Host, Port, Rack, Slot, CpuType, TimeoutMs.
    - SLMP: Host, Port, TimeoutMs.
  - Poll `IntervalMs`.
  - Tags table: TagId, Address, DataType (a select built from `PlcDataType`). Rows can be added, edited and deleted. Writable tags keep the Min and Max that `GET` returned and send them back unchanged (N-4).
  - Writable tags are locked, with a label and the hint "sửa trong file settings với quyền Admin". They can only be deleted. While any of them is kept, the PLC fields are locked too (D-06).
  - States:
    - loading
    - "chưa có file settings (đang dùng appsettings)"
    - "chưa đặt mật khẩu settings" (Save and Test disabled; shows the command)
    - validation errors per field or tag
    - applying, until its `applyId` is `applied` or `rolledBack`, or 60 s pass
    - rolled back (shows the fixed error)
    - 409 conflict (offers reload)
    - 429 (shows the wait)
  - The password field uses `autocomplete="new-password"` and is cleared after every request.
  - The Test request uses a fetch timeout of at least TimeoutMs + 1 s.
  - Text is set only through `textContent` / `value`.
  - The page is in Vietnamese.
- Out: CSV, PLC browsing.

## Coverage
- CP-05

## Ownership
- Modify: `src/Edge/Monitoring/monitor.html`, `src/Edge/IndustrialIot.Edge.csproj`, `src/Edge/Monitoring/MonitorServer.cs` (only the `/settings` route)
- Create: `src/Edge/Monitoring/settings.html`, `tests/Edge.Tests/SettingsPageTests.cs`
- Read: `src/Edge/Monitoring/monitor.html`, `src/Edge/Drivers/PlcDataType.cs`

## Steps
1. Write `SettingsPageTests`. Before the change, expect 404. Cases:
   - (a) `/settings` returns 200 HTML with the monitor's CSP and `no-store`.
   - (b) The page contains none of `innerHTML`, `outerHTML`, `insertAdjacentHTML`, `document.write`, `eval(` or `new Function`.
   - (c) The DataType options equal `Enum.GetNames<PlcDataType>()`.
   - (d) `monitor.html` links to `/settings`.
   - (e) A Host other than localhost gets 400.
   - (f) The password input has `autocomplete="new-password"`.
2. Build the page and add the route.
3. Controller browser check on the dev machine. Use the console run with the "PLC test (no broker)" profile; there is no PLC. Record what each item showed:
   - The page loads with the "no settings file" state.
   - Setting the password through the CLI enables Save.
   - Adding a tag and saving goes to "applying", then "applied".
   - The monitor lists the new tag as `bad`.
   - A bad address shows a validation error.
   - 5 wrong passwords give 429.
   - Test kết nối shows `unreachable`.
   - With one writable tag placed in the dev settings file by hand (Writable, Min, Max): the page shows it locked, the PLC fields are locked, and saving an edit to another tag gives "applied" with the tag still writable (`GET /api/settings` lists it in `writableTags`).

## Acceptance
- AC-07: (a)–(f) pass, the full Edge suite passes, and the Step 3 observations are recorded.

## Dependencies
- task-05-test-connection.md

## Verification Plan
- Command: `dotnet test tests/Edge.Tests`
- Named probe: `SettingsPageTests` (a)–(f), plus the Step 3 observations.
- Reachability: source level, and the dev-machine console run for the browser check.
- Oracle: exit 0, `Skipped: 0`, `SettingsPageTests` ≥ 6, and every Step 3 item observed.
- Counterexample: an `innerHTML` use makes (b) fail; a hard-coded DataType list missing a value makes (c) fail.
- Artifacts: ephemeral. The dev `src/Edge/data/edge.settings*.json` files are gitignored (`.gitignore:27`) and are deleted after the check.

## Failure Protocol
On a failed Step or Verification Plan run: stop; do not widen scope, change the Command, or weaken a test; record observed versus expected; repair only the cited cause; after three failed rounds, stop and ask the user.

## Receipt

Verification: PASS
Command: dotnet test tests/Edge.Tests
Exit: 0
Base: 927442f6c144893b2810cf30b14ac572eee996e8
Head: 7376d6bc7ee4c60e83e6f5837d49baef6c2cc5611e3d8e2edcd1554405c2e90c
```text
$ dotnet test tests/Edge.Tests
Passed!  - Failed:     0, Passed:   549, Skipped:     0, Total:   549, Duration: 29 s - Edge.Tests.dll (net10.0)
$ dotnet test tests/Edge.Tests --no-build --filter "FullyQualifiedName~SettingsPageTests"
Passed!  - Failed:     0, Passed:     6, Skipped:     0, Total:     6, Duration: 399 ms - Edge.Tests.dll (net10.0)
```
- Pre-change run: 5 of the 6 `SettingsPageTests` failed (404, no page). (e) passed before the change because the monitor's Host guard already existed.
- Linux: the full Edge suite passed 549/549 with `Skipped: 0` in the pinned SDK container.
- Counterexamples on a scratch copy: an `innerHTML` use fails (b); a DataType list missing `DInt` fails (c).
- Review (code-auditor, fresh context): first PASS_WITH_WARNINGS (an empty password was not blocked client-side; the locked PLC values were read back from the controls). After one repair round: PASS.
- Live (user, dev machine, console run with the "PLC test (no broker)" profile, 2026-10-29): every Step 3 item behaved as expected, including the writable-tag check (locked row, locked PLC fields, saving another tag's edit gave "Đã áp dụng cấu hình mới." and the tag stayed writable after reload). An earlier 404 at `/settings` came from Visual Studio running the `../PlcProtocol-baseline` worktree, not this tree.
- Limitation: the 429 message shows a static wait (`retryAfterSeconds`), with no countdown; out of scope.
