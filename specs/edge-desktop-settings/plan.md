# Edge desktop settings — classic Channel → PLC → Tag window
Specs-Contract: process-first-ready-v1

## Scope decision (GATE-SCOPE — 2026-11-04)
- Existing:
  - Settings API: `GET /api/settings` returns the tree, the model catalog, `revision`, `passwordSet`, `commandsEnabled`
    and `warnings` (`src/Edge/Settings/SettingsService.cs:40-54`). Routes for validate, PUT, CSV and test-connection:
    `src/Edge/Monitoring/MonitorServer.cs:108-121`. Request guard (Origin, Sec-Fetch-Site, JSON only, size):
    `SettingsService.cs:137-185`. PUT checks in order: body 400 → stale revision 409 → password 401/403/429 →
    apply-in-progress 409 (`SettingsService.cs:91-119`, verdicts `:378-398`). Test-connection body `{password, proposed}`,
    refusals `endpoint-in-use`, `test-running`, `rate-limited`, error paths `proposed:{key}`:
    `src/Edge/Settings/PlcConnectionTester.cs:86,125-147,340-347`.
  - Model catalog S7-1200 (S7), FX5U (Slmp3E), FX3U (Mc1E, writes not allowed) with default port/rack/slot and data
    types: `src/Edge/Drivers/PlcModelCatalog.cs:37-46`, served as `writesAllowed` (a bool, no reason text) by
    `src/Edge/Settings/SettingsTree.cs:70-81`. Drivers: `src/Edge/Configuration/EdgeChannelsOptions.cs:7-12`. TagId rule:
    `EdgeChannelsOptions.cs:176`. No rule rejects a channel without PLC (`EdgeChannelsOptions.cs:109-121`), and GET builds
    channels from devices only (`SettingsTree.cs:23-33`).
  - Every apply stops and rebuilds the inner host, which runs the monitor server, so `/api/status` fails for a few
    seconds (`src/Edge/Settings/EdgeSupervisor.cs:216-222`, `src/Edge/EdgeServiceCollectionExtensions.cs:101`); only the
    latest apply is reported (`src/Edge/Settings/EdgeRestarter.cs:41,83,134-140`). The web page waits through it
    (`src/Edge/Monitoring/settings.html:541-552`) and locks its UI while busy (`settings.html:565-584`).
  - The settings password lockout is shared by save and test and by every client (`src/Edge/Settings/SettingsPassword.cs:124-140`);
    the web never sends an empty password (`settings.html:473-478`).
  - Desktop app (edge-desktop-monitor): `StatusClient`, `MonitorViewModel` (poll loop, banner, stale rule, device and
    tag rows `src/EdgeDesktop.Core/MonitorViewModel.cs:18-22`, device texts `:42-48`, `Apply` without `SettingsApply`
    `:88-126`), `LaunchOptions`, the window `src/EdgeDesktop/MainWindow.xaml`, package/install/shortcut `deployments/edge/*.ps1`.
  - Test host with the supervisor and a settings password: `tests/Edge.Tests/SettingsApiTests.cs:27-40,545-571`.
- Minimum change: replace the window by a classic desktop layout (menu, toolbar, tree, tag grid with live values, event
  log, status bar) and add dialogs that edit a draft saved through the existing settings API. No change to the Edge.
- Expansion signals: about 18 new or rewritten files and 4+ new Core classes; one exe delivered at once, so one packet.
- Risk: critical — the app changes PLC write permissions and handles the settings password.
- User decisions:
  - KEEP: layout + Add/Edit/Delete dialogs for channel, PLC and tag, including write permissions and Test connection.
  - Draft + "Lưu & áp dụng" with one password prompt per save.
  - edge-desktop-monitor task-04 keeps only items 5–7; the live check of the window moves here (task-06).

## Precondition
- edge-desktop-monitor closed at its GATE-DONE (this feature replaces its window and reuses its Core and package).

## Out of scope
- Cut/Copy/Paste, right-click menus, CSV import/export (EXPAND, not chosen).
- New/Open/Save-as of files; writing `edge.settings.json` directly.
- Any change to the Edge, its API or the web pages.
- An Edge on another machine: the app talks to `127.0.0.1` only.
- Manual read-block settings (the Edge batches reads itself); a channel-level update rate (the rate is per PLC).
- Setting the settings password (`set-settings-password` stays) or `Edge:Commands:Enabled` from the app.
- Renaming the exe or shortcut; installer changes.
- A Windows-only WPF test project (CI runs the whole solution on Ubuntu, `.github/workflows/ci.yml:23-32`).
- Checking which process owns the port before sending the password (G11: accepted risk, same as the web page).

## Decisions
| ID | Chosen | Rejected | Why | Load-bearing assumption | Break signal → response |
|---|---|---|---|---|---|
| D-01 | The app is a second client of the settings API on `http://127.0.0.1:{port}`: `GET /api/settings`, `POST /api/settings/validate`, `PUT /api/settings`, `POST /api/settings/test-connection`, with the web page's JSON. Timeouts: 30 s for GET, validate and PUT; the PLC's `TimeoutMs` + 5 s for a test, as the web | Write `edge.settings.json` | The API already validates, checks revision and password, applies and rolls back; the file needs admin and races the supervisor | `HttpClient` sends no `Origin` and sends `application/json`, so the guard passes | 403 `cross-site` / 415 in the contract test → fix the client's headers, never the guard |
| D-02 | All logic in `IndustrialIot.EdgeDesktop.Core` (net10.0, tested on Linux): settings client, draft, main and dialog view models. WPF only binds. A contract test runs the real API through the supervisor host and reads `GET` strictly. A XAML binding test checks every binding path of the listed XAML files (task-05) | WPF UI automation tests | CI is Ubuntu; a net10.0-windows test project cannot run there | XAML holds no logic | A behaviour testable only in XAML → move it into a view model |
| D-03 | Layout (below) | A copy of the web pages | User decision: a classic desktop exe | — | — |
| D-04 | Draft and save flow (below) | Apply on every dialog OK | Each apply stops every PLC for a few seconds; user decision | — | — |
| D-05 | Dialogs (below) | — | Matches the reference screens, mapped onto Channel → PLC → Tag | — | — |
| D-06 | The password is typed in a modal PasswordBox for one save or one connection test, put into that one request body, and the box is cleared after the request. OK is disabled while the box is empty, and the client never sends an empty password (the lockout is shared with the web). The password is never stored in a field between requests and never written to the log, title, files, exception texts or the command line. Accepted risk (G11): while the Edge is stopped or applying, another local user could listen on the port and receive a typed password, exactly as with the web page; documented, not checked | Remember it for the session; check the port's owning process | The web page asks every time; a remembered password lets anyone at the screen save. The port check breaks running from source | — | A test finds the password in any log entry or property after the request, or an empty password sent → fail |
| D-07 | One poll loop: `MonitorViewModel.RunAsync`, with one small addition: an `EdgeStatus? LastStatus` property and a `StatusApplied` event, set and raised by `Apply`. Its existing behaviour and tests stay unchanged. The main view model reads `LastStatus` (`SettingsApply`, `NowUtc`, devices, tags), `Devices` by name and `Tags` by TagId (`{Device}.{Name}`, or `Name` with LegacyTagIds) | A second poll loop; `MonitorViewModel` untouched (it does not expose `SettingsApply`) | One tested loop; user decision G1 (b) | Device names and TagIds match between `/api/settings` and `/api/status` | A saved tag never gets a value in the grid → check the TagId rule |
| D-08 | When `LastStatus.SettingsApply.Revision` differs from the draft's revision and no save of this app is waiting: a clean draft with no dialog open reloads silently and logs "Cấu hình đã được đổi ở nơi khác, đã tải lại". A dirty draft, an open dialog, or a busy app logs a warning once and keeps the draft (a save then meets 409, D-04). After an unconfirmed save (D-04 step 4), the next revision change logs "Edge đã áp dụng một cấu hình mới, có thể là lần lưu vừa rồi" instead | Reload always | A reload under an open dialog would put its edit, write permissions included, on another PLC (G3) | The revision changes on every apply | — |

**D-03 Layout.**
- Title: `IIoT Edge Monitor — {EdgeId}`, plus ` *` while the draft has unsaved changes; `IIoT Edge Monitor` before the first status.
- Menu:
  - Tệp: Lưu & áp dụng (Ctrl+S), Tải lại từ Edge (F5), Thoát.
  - Sửa: Thêm Channel, Thêm PLC, Thêm Tag, Sửa… (Enter), Xoá (Del).
  - Xem: Log sự kiện (show/hide).
  - Công cụ: Kiểm tra cấu hình, Test kết nối PLC, Mở trang cấu hình web.
  - Trợ giúp: Giới thiệu (version, port).
- Toolbar with tooltips: Lưu, Thêm Channel, Thêm PLC, Thêm Tag, Sửa, Xoá, Kiểm tra.
  - Each button is disabled when its action does not apply: Thêm PLC needs a channel or PLC selected; Thêm Tag a PLC;
    Sửa/Xoá a channel, PLC or tag; Lưu unsaved changes and a draft that is not stale.
  - While busy (validate, PUT or the apply wait, D-04), Lưu, Thêm, Sửa, Xoá, Tải lại, Kiểm tra and Test kết nối are
    all disabled.
- Left tree:
  - The first node is "Bấm để thêm Channel" (double-click or Enter opens Add Channel).
  - Channels are shown as `{Name} ({driver display name})`.
  - PLCs show the state text of the web page's PLC table (`MonitorViewModel.Devices`): Đang đọc, Mất kết nối, Chưa có dữ
    liệu or Đã tắt. Đang đọc becomes "Có tag lỗi" when a tag of that PLC has a quality other than good. A PLC only in the
    draft shows "Chưa lưu".
  - Double-click on a channel or PLC opens Edit.
- Right grid (read-only):
  - With a PLC selected, its tags: Tag Name, Address, Data Type, Client Access (`Read Only` | `Read/Write`), Value,
    Chất lượng, Thời điểm đọc, Lỗi. A tag only in the draft shows `—`.
  - Otherwise the text "Chọn một PLC để xem tag".
  - Double-click on a tag opens Edit Tag.
- Bottom log: Thời gian | Nguồn | Sự kiện, newest first, at most 500 entries, in memory only. Entries are:
  - Edge lost / back, one entry per transition, none during this app's own apply wait (D-04);
  - a PLC state change;
  - Kiểm tra results;
  - save sent / applied / rolled back (with the Edge's error) / refused (D-04 reason);
  - Test kết nối results;
  - D-08.
  - An error whose path cannot be mapped to a node is logged with its path verbatim.
- Status bar:
  - `Edge: {EdgeId}` · `127.0.0.1:{port}` · `MQTT: Đã kết nối|Chưa kết nối` · `Buffer: {n}` ·
    `Chu kỳ gần nhất: {MonitorViewModel.LastCycle}` · `Lệnh ghi từ Cloud: bật|tắt` (`commandsEnabled`) ·
    `Đã lưu|Có thay đổi chưa lưu|Đang áp dụng (Edge khởi động lại)…`.
  - While `/api/status` fails outside an apply wait, the first field shows the banner text of edge-desktop-monitor
    D-03 in red.

**D-04 Draft and save.**
- Start and Tải lại (confirm first if dirty): `GET /api/settings` → the draft, its `revision`, clean, not stale.
- A dialog's OK or Áp dụng changes only the draft and makes it dirty.
- Lưu & áp dụng sets busy, then:
  1. Client checks: a channel without PLC is refused with "Channel {tên} chưa có PLC" (it would disappear after the
     save). Then `POST validate` with the draft (no password). On errors: one log entry each, the first mappable node
     selected, nothing else sent.
  2. Password dialog; Huỷ stops here.
  3. `PUT` `{revision, password, settings}`; on 202 the status shows "Đang áp dụng (Edge khởi động lại)…".
  4. Wait for this save's `applyId`, at most 60 s:
     - poll failures during the wait are expected (the monitor server restarts) and are neither shown red nor logged;
     - `LastStatus.SettingsApply.ApplyId` equals ours with a state other than `applying` → step 5;
     - another `applyId` with a state other than `applying` → keep the draft, mark it stale (Lưu disabled until Tải
       lại, as Giữ để xem), log "Không xác nhận được lần lưu vừa rồi (Edge đã áp dụng một lần lưu khác): Tải lại để
       xem cấu hình đang chạy";
     - 60 s → log "Edge chưa xác nhận lần lưu", keep the draft dirty, mark the save unconfirmed (D-08).
  5. `applied` → reload (as at start), log "Đã áp dụng"; `rolledBack` → log the Edge's error, keep the draft dirty.
- Busy ends after step 5 or any stop. Error replies are classified by the reply's `error` key, never by status code
  alone, and the draft is always kept:
  - 401 `wrong-password` → "Sai mật khẩu", ask again (only after 401; any other refusal ends the save);
  - 403 `password-not-set` → "Chưa đặt mật khẩu settings: chạy IndustrialIot.Edge.exe set-settings-password --service (admin)";
  - 429 `locked` → "Sai mật khẩu quá nhiều lần, thử lại sau {retryAfterSeconds} giây";
  - 409 `stale-revision` → dialog "Cấu hình trên Edge đã đổi ở nơi khác" with Tải lại (bỏ thay đổi) / Giữ để xem. Giữ
    để xem keeps the draft for reading and copying, marks it stale, and Lưu stays disabled until Tải lại;
  - 409 `apply-in-progress` → "Edge đang áp dụng một lần lưu khác, thử lại sau";
  - 400 errors → as step 1; 413 → "Cấu hình quá lớn";
  - anything else → the HTTP code, or "không kết nối được".
- Closing:
  - while dirty and not busy: Lưu / Không lưu / Huỷ; Lưu runs the save and closes only when applied;
  - while busy: Đợi / Thoát không chờ (the Edge finishes the apply on its own).

**D-05 Dialogs.** Modal; Enter = OK, Esc = Huỷ, first field focused; number fields reject non-digits locally ("phải là
số") before any request. Errors of `validate` map by the path grammar `Edge:Channels:c[:Devices:d[:Tags[:t]]][:Field]`
to the node and field; errors of test-connection map by `proposed:{key}` to the PLC dialog's field.
- Channel: Tên channel; Driver as text plus "…", which opens the driver dialog. That dialog has a dropdown (Siemens S7 |
  Mitsubishi SLMP 3E | Mitsubishi MC 1E) and the note "Hỗ trợ dòng PLC: {models of that driver}" from the catalog. A
  channel's driver can change only while it has no PLC.
- PLC:
  - Tên PLC.
  - Dòng PLC: only models of the channel's driver; choosing one fills port/rack/slot defaults and shows the port hint.
  - Địa chỉ IP, Port.
  - Rack, Slot: S7 only.
  - Timeout (ms) 3000, Chu kỳ đọc (ms) 1000 for a new PLC, as `src/Edge/Monitoring/settings.html:223-224`.
  - Đọc (Enabled).
  - Cho phép ghi: disabled, with the app's own text "{Model} chưa cho phép ghi (chưa kiểm chứng trên PLC thật)", when the
    catalog's `writesAllowed` is false.
  - "TagId giữ nguyên tên cũ": shown only when set; unticking warns that the Cloud will see new TagIds.
  - Test kết nối: password dialog → `test-connection` with the dialog's current values. The result shows in the dialog
    and the log; refusals by `error` key:
    - `endpoint-in-use` → "PLC này đang chạy với cấu hình khác: lưu trước rồi test";
    - `test-running` → "Một lần test khác đang chạy";
    - `rate-limited` → "Đã test quá 10 lần trong một phút, đợi một chút";
    - plus the password refusals of D-04.
- Tag:
  - Tên tag, Địa chỉ.
  - Kiểu dữ liệu: the model's data types.
  - Độ dài: String only.
  - Ghi: disabled when the model does not allow writes.
  - Min, Max: only with Ghi.
  - Mô tả.
  - Buttons:
    - Kiểm tra: validates the draft with this tag and shows only this tag's errors, or "Hợp lệ".
    - Áp dụng: in Add mode, adds the tag and clears name and address for the next one, keeping the dialog open; in Edit
      mode, updates it and closes.
    - Đóng.
- Delete: confirm "Xoá {Channel|PLC|tag} {name}?" naming what goes with it (N PLC, M tag); draft only.

## Coverage profile
| ID | Outcome | Change kinds | Material surfaces | Ambiguity/action | Risk/evidence | Required proof |
|---|---|---|---|---|---|---|
| CP-01 | The window shows the Edge's tree with live state and values, the log and the status bar (D-03, D-07) | modify, integrate | WPF window, Core view models, `/api/status`, `/api/settings` | none | elevated | source (Core tests, XAML binding test), live (user) |
| CP-02 | Dialogs edit a draft under the catalog rules (D-05) | add | dialogs, dialog view models, model catalog | none | critical: write permissions | source, live |
| CP-03 | Save, validate and test-connection go through the API with the password, busy state, apply wait and every error reply handled (D-01, D-04, D-08) | add, integrate | settings client, save flow, Edge API | none | critical | source (contract test on the real API), live |
| CP-04 | The password lives only in its one request and is never empty (D-06) | add | password dialog, client, log | none | critical | source |
| CP-05 | Installed app still starts for a non-admin user and saves with the settings password | other:reuse | package, shortcut | none | elevated | installed (user) |
| CP-06 | Docs describe the new window and the accepted port risk | modify | `docs/deployment.md` | none | routine | source (grep, review) |

## Acceptance criteria
| ID | EARS criterion | Proof |
|---|---|---|
| AC-01 | When the app starts with a reachable Edge, it shall show the channels and PLCs of `GET /api/settings` in the tree with the D-03 state texts and, for the selected PLC, its tags with Value, Chất lượng and time from `/api/status`, refreshed about every second. | Core tests; user |
| AC-02 | While `/api/status` fails outside an apply wait, the status bar shall show the banner text in red, the log shall get one entry when the Edge is lost and one when it is back, and the last values shall stay. | Core tests; user |
| AC-03 | When the user adds, edits or deletes a channel, PLC or tag, the app shall change only the draft, show ` *` in the title, and offer only the channel driver's models, the model's data types and defaults, and writes only where the model allows them. | Core tests; user |
| AC-04 | When the user clicks Kiểm tra, the app shall send the draft to `validate` and show each error at its node and field (or its path in the log), or "Hợp lệ". | Core + contract tests; user |
| AC-05 | When the user saves, the app shall be busy until done, refuse a channel without PLC, validate, ask the password, send `PUT` with the revision, wait for this save's apply through the monitor restart and log applied, rolled back or unconfirmed; on each D-04 error reply it shall show the D-04 message and keep the draft. | Core + contract tests; user |
| AC-06 | If the window is closed with unsaved changes, the app shall ask Lưu / Không lưu / Huỷ, and while busy Đợi / Thoát không chờ. | Core tests; user |
| AC-07 | The password shall appear only in the one request it was typed for, and an empty password shall never be sent. | Core tests |
| AC-08 | When the configuration changes elsewhere, the app shall reload a clean draft with no dialog open, and otherwise warn once and keep the draft. | Core tests; user |
| AC-09 | When the user clicks Test kết nối in the PLC dialog, the app shall send the dialog's values with the password to `test-connection` and show the result or the D-05 refusal. | Core + contract tests; user |
| AC-10 | The packaged app shall start for a non-admin user from the shortcut and save a change with the settings password. | user (installed) |
| AC-11 | `docs/deployment.md` shall describe the window, the dialogs, saving with the password, troubleshooting, and the accepted port risk. | grep + review |

## Tasks
| # | Task | Priority | Criteria | Primary ownership | Dependencies | Status |
|---|---|---|---|---|---|---|
| 01 | Settings client and draft, contract-tested on the real API | P1 | AC-04, AC-05, AC-07, AC-09 | `src/EdgeDesktop.Core/` (new files), `tests/Edge.Tests/Desktop/` (new files) | - | done |
| 02 | Dialog view models under the catalog rules | P1 | AC-03, AC-04, AC-07, AC-09 | `src/EdgeDesktop.Core/*DialogViewModel.cs`, `UserPrompts.cs`, tests | task-01-settings-client.md | done |
| 03 | Main view model: tree, grid, log, status bar, commands | P1 | AC-01, AC-02, AC-03 | `src/EdgeDesktop.Core/MainViewModel.cs`, `MonitorViewModel.cs` (D-07 addition), `UserPrompts.cs`, tests | task-02-dialog-view-models.md | done |
| 04 | Save flow: busy, apply wait, refusals, close guard, outside changes | P1 | AC-05, AC-06, AC-07, AC-08 | `src/EdgeDesktop.Core/SaveFlow.cs`, `MainViewModel.cs`, `UserPrompts.cs`, tests | task-03-main-view-model.md | done |
| 05 | WPF window and dialogs, XAML binding test | P1 | AC-01–AC-06, AC-09 | `src/EdgeDesktop/`, `tests/Edge.Tests/Desktop/XamlBindingTests.cs` | task-04-save-flow.md | pending |
| 06 | Docs and user checks | P2 | AC-01–AC-09 (live), AC-10, AC-11 | `docs/deployment.md` | task-05-wpf-window.md | pending |

## Review log
- Round 1 (2026-11-04), two fresh-context reviewers (facts/contracts; security/failure modes), deduplicated to G1–G15.
  User: accept all; G1 = (b) small `MonitorViewModel` addition; G2 = server device texts + "Có tag lỗi"; G11 = accepted
  risk, documented.

| ID | Sev | Finding | Repair |
|---|---|---|---|
| G1 | High | `MonitorViewModel` unchanged exposes no `SettingsApply`/`NowUtc` | D-07 `LastStatus` + `StatusApplied`; task-03 owns the change |
| G2 | High | Per-PLC "Có tag lỗi"/"Dữ liệu cũ" had no source | D-03 tree texts from the device rows + "Có tag lỗi" from tag quality |
| G3 | High | Silent reload under an open dialog moves its edit to another PLC | D-08: open dialog or busy counts as dirty |
| G4 | High | No busy state during save | D-03/D-04 busy rules; close while busy |
| G5 | High | Test-connection refusals, 429 ambiguity, `proposed:` paths, client timeouts | D-01 timeouts; D-04/D-05 classify by `error` key; D-05 test refusals |
| G6 | Medium | Monitor restarts during apply; `applyId` overwritten | D-04 step 4 |
| G7 | Medium | "Giữ thay đổi" dead end; timeout blamed on "elsewhere" | D-04 409 stale "Giữ để xem"; D-08 unconfirmed save |
| G8 | Medium | Error path grammar too narrow | D-05 grammar; unmapped paths logged verbatim (D-03) |
| G9 | Medium | Empty password / re-ask burns the shared lockout | D-06 |
| G10 | Medium | Task-03 too large | Split into task-03 and task-04 |
| G11 | Low/Med | Another local user could listen on the port | Accepted risk in D-06, Out of scope, docs (task-06) |
| G12 | Low | A channel without PLC disappears after a save | D-04 step 1 client refusal |
| G13 | Low | Round-trip casing; top-level keys; writes reason text; LastCycle format | task-01 round trip rule; D-05 own text; D-03 status bar |
| G14 | Low | Origin mutant could not fail; task-06 greps passed before the change | task-01 counterexample; task-06 greps |
| G15 | Low | `XamlBindingTests` underspecified | task-05 Scope |
- Closure pass (fresh context): G1–G15 all PASS. Five small follow-ups applied without changing user semantics: the
  foreign-`applyId` branch keeps the draft and marks it stale instead of reloading (the accepted "never lose typed
  changes" rule of G3/G7); `UserPrompts.cs` added to the ownership column; each task records its blocker; an
  `endpoint-in-use` contract test (task-01); tests for the client timeouts (task-01), no re-ask after 429 `locked`
  (task-04), and tree double-click going through the view-model commands (task-05).
- Sweep: 7 files reread / 5 deltas / 0 stale references / 0 conflicts left. Tasks stay `blocked` only on the
  precondition (edge-desktop-monitor GATE-DONE).
- 2026-11-04: precondition met (edge-desktop-monitor accepted at GATE-DONE); all tasks promoted from `blocked` to `pending`.
