# Edge desktop app (WPF), part 1: live monitor
Specs-Contract: process-first-ready-v1

## Scope decision (GATE-SCOPE — 2026-11-03)
- Request: a Windows `.exe` to work with the multi-PLC Edge, next to the web pages, which stay unchanged.
- Existing:
  - Web monitor `src/Edge/Monitoring/monitor.html` (243 lines):
    - It polls `GET /api/status` 1 s after the previous poll finishes (`monitor.html:226-240`).
    - It shows 4 cards (PLC, last cycle, MQTT, buffer), a device table and a tag table (`monitor.html:60-100`).
    - The stale rule applies only to a single-PLC Edge (`s.plc`) and uses the server's `nowUtc`
      (`monitor.html:162-176`).
  - The `/api/status` JSON:
    - It comes from `MonitorServer.BuildStatusAsync` (`src/Edge/Monitoring/MonitorServer.cs:142-197`), written with
      private web (camelCase) options (`:38`).
    - The records are `internal` (`:297-349`).
    - `Plc` and `PollIntervalMs` are null on a multi-PLC Edge (`:187-190`).
  - The monitor:
    - listens on localhost only (`MonitorServer.cs:101`);
    - accepts host names localhost, 127.0.0.1 and ::1 (`:43-46,259-265`);
    - uses port 5080 by default (`MonitorOptions.cs:16`).
  - Packaging and install:
    - `deployments/edge/package-edge.ps1:25` publishes the Edge self-contained for win-x64 into one zip.
    - `install-edge.ps1` expands the zip into `%ProgramFiles%\IndustrialIot\Edge`. Only SYSTEM, Administrators and the
      service can open that folder (`install-edge.ps1:203,223`).
    - Folder safety uses the pattern `IndustrialIot\Edge[A-Za-z]*` (`install-edge.ps1:52`, `uninstall-edge.ps1:24`).
    - `verify-edge.ps1` has static checks (VERIFY-PACKAGE, …) and elevated `-Live` checks into a separate
      `EdgeVerify` folder (`verify-edge.ps1:6-11,121-126`).
  - CI builds and tests the solution on `ubuntu-latest`, then fails on any `NotExecuted` TRX result
    (`.github/workflows/ci.yml:23-46`). `Cloud.Tests` and `Deployment.Tests` need Docker.
- Minimum change: a WPF app that shows what the web monitor shows, packaged and installed with the Edge.
- Expansion signals: about 15 touched files (> 8). Two new projects. A native settings UI would be a second
  independently deliverable subsystem.
- User decision: **EXPAND**, a native WPF app (not a web view). The web pages stay as they are.
  - Screens: **Configuration and Monitor**. They are split into two features, Monitor first. This packet is the
    monitor. It also builds the app foundation, packaging and install.
  - The WebView2 runtime question no longer applies: there is no web view.
- Precondition: feature `edge-settings-multi-device` is committed before `/cf:develop` starts here, so that proofs
  and the `docs/deployment.md` diff contain only this feature (G4, G11).

## Out of scope
- The native configuration screens: next feature `edge-desktop-settings`, planned separately. Until then a button
  opens the web settings page in the default browser.
- Any change to the Edge server, `/api/*`, `monitor.html` or `settings.html`.
- Writes to PLCs, and calls to any route except `GET /api/status`.
- Using the app from another machine. Edge listens on localhost only.
- MSI or MSIX installers, auto-update, and code signing (see D-07).

## Decisions
| ID | Chosen | Rejected | Why | Load-bearing assumption | Break signal → response |
|---|---|---|---|---|---|
| D-01 | Two projects: `IndustrialIot.EdgeDesktop.Core` (net10.0, namespace `IndustrialIot.EdgeDesktop.Core`, public types: status models, client, view-model logic, launch options) and `IndustrialIot.EdgeDesktop` (net10.0-windows WPF: XAML window only) | One WPF project | CI runs on Linux (`ci.yml:23`), where WPF tests cannot run; the logic must be testable there | WPF builds on Linux with `EnableWindowsTargeting` | Linux build of the WPF project fails → stop, return to GATE-REVIEW |
| D-02 | Core holds its own read-only models of the status JSON and reads them leniently in the app. A contract test in `Edge.Tests` gets real JSON from `GET /api/status` of `EdgeTestHost` (as `MonitorTests` does). The test then reads it strictly: web defaults plus `UnmappedMemberHandling.Disallow`, `RespectRequiredConstructorParameters` and `RespectNullableAnnotations`. A negative test shows that a removed or renamed property fails | Moving the Edge records to Shared; referencing the Edge project from the app; strict reading in the app | The Edge stays unchanged. A renamed field breaks a test. A version mismatch never blanks the operator's screen | — | — |
| D-03 | Poll `GET http://127.0.0.1:{port}/api/status` 1 s after the previous poll finishes, as the web does. The request times out after 2 s. On a failure, keep the last data and show the fixed banner "Không lấy được dữ liệu từ phần mềm Edge ({reason}). Service IndustrialIot.Edge có đang chạy không?". `{reason}` is one of `quá 2 giây không trả lời`, `HTTP {code}`, `không kết nối được`, `dữ liệu không đọc được` | `localhost` (it may try ::1 first, G8); exception text in the banner | The web's prefix and suffix, with a fixed reason instead of a browser or localized message | — | — |
| D-04 | Port: `--port N` (1..65535), default 5080. An invalid argument shows a message and exits with code 2 | Reading the Edge's appsettings | The Edge's folders are admin-only (`install-edge.ps1:203`); the app runs as a normal user | Monitor port is 5080 unless set | — |
| D-05 | Same content and labels as the web monitor: 4 cards, device table, tag table, state words, `TRUE`/`FALSE` for Bool, `vi-VN` time. The stale rule applies only when `plc` is present, compared with the server's `nowUtc` (`monitor.html:162-176`). A footer says the app is read-only. A button "Mở trang cấu hình" opens `http://localhost:{port}/settings` in the default browser | A new layout; the client's clock | Operators move between web and app without learning anything new | — | — |
| D-06 | Packaging and install:<br>• `package-edge.ps1` adds a self-contained win-x64 publish of the app under `EdgeDesktop\` in the zip.<br>• `install-edge.ps1` moves it to `"$InstallDir.Desktop"` (default `%ProgramFiles%\IndustrialIot\Edge.Desktop`; the dot keeps it outside `Edge[A-Za-z]*`). That folder gets SYSTEM and Administrators full control and Users read and execute.<br>• It creates the all-users shortcut `IIoT {leaf of InstallDir} Monitor.lnk`.<br>• Folder and shortcut must not exist before install (and must not be reparse points). Rollback removes only what this install created.<br>• `uninstall-edge.ps1` refuses, before touching the service, while any `IndustrialIot.EdgeDesktop` process runs from that folder, then removes both.<br>• `verify-edge.ps1 -Live` refuses when the EdgeVerify app folder or shortcut exists | Running the app from the Edge's install folder; fixed paths; closing the app automatically | Users cannot open the Edge folder (`install-edge.ps1:203`). A test run must never touch a real install. A locked exe must not leave a half-removed Edge (user decision: refuse) | — | — |
| D-07 | The exe is unsigned. Docs say SmartScreen may warn on first start | Code signing | No certificate exists | — | Customer policy blocks unsigned exe → new feature |
| D-08 | Plain `INotifyPropertyChanged` in Core, with no MVVM package | CommunityToolkit.Mvvm | One window does not need a package | — | — |
| D-09 | The poll loop takes a "post to UI" callback from the window and makes every view-model change through it. Cancelling (closing the window) is not a failure: no banner and no change after cancel | Core updating state on pool threads | WPF throws when a bound collection changes off its thread. The repo uses `ConfigureAwait(false)` (`MonitorServer.cs:67`) | — | — |

## Coverage profile
| ID | Outcome | Change kinds | Material surfaces | Ambiguity/action | Risk/evidence | Required proof |
|---|---|---|---|---|---|---|
| CP-01 | The app shows the Edge's live status like the web monitor | add | Interaction/UI, Integration/proof (`/api/status` contract) | none | elevated: operator tool, cross-component JSON | source (Core and contract tests) + live (user) |
| CP-02 | Edge unreachable: banner, retry, recovery; clean close | add | Async/state, Interaction/UI | none | elevated | source + live |
| CP-03 | The solution still builds and tests on Linux CI | modify | Runtime/deploy (build) | none | elevated: required CI check | source (Linux container, CI steps) |
| CP-04 | The packaged, installed app: folder ACL, shortcut, preconditions, non-admin start, refusal while running, uninstall | add, modify | Runtime/deploy, Filesystem/security | none | elevated: installer and ACL | source (`verify-edge.ps1`) + installed (`verify-edge.ps1 -Live` and a manual install, user) |
| CP-05 | Docs describe the app | modify | other:docs | none | routine | review + grep |

## Acceptance criteria
| ID | EARS criterion | Proof |
|---|---|---|
| AC-01 | When `/api/status` answers, the app shall show the Edge id, the 4 cards, every device and every tag with the web monitor's labels and rules (D-05), refreshed about every second. | Core tests; user live check |
| AC-02 | If `/api/status` fails or takes over 2 s, the app shall show the D-03 banner, keep the last data and retry. When the Edge answers again it shall hide the banner. Closing the window shall stop polling without a banner. | Core tests; user live check |
| AC-03 | A strict read of the real `/api/status` JSON into the Core models shall succeed, and shall fail when a property is removed or renamed. | contract tests |
| AC-04 | When started with no `--port`, the app shall use 5080; with a valid `--port N`, port N; with an invalid one, a message and exit code 2. | Core tests |
| AC-05 | The app shall send only `GET /api/status` to `127.0.0.1`, and "Mở trang cấu hình" shall open the settings page in the default browser. | Core test (client requests); user live check |
| AC-06 | In the .NET 10 SDK Linux container, the CI steps (restore, Release build, `dotnet test` of the solution with TRX, no `NotExecuted`) shall pass for the Edge and Shared tests, and the WPF dll shall be built. On Windows, `dotnet test` of the solution shall stay green. | Linux container; solution test |
| AC-07 | The package shall contain the app. Install shall create the Users-readable folder and the shortcut, and refuse when either exists. A non-admin user shall start the app. Uninstall shall refuse while the app runs, and otherwise remove both. | `verify-edge.ps1`; `verify-edge.ps1 -Live` + user |
| AC-08 | `docs/deployment.md` shall describe starting the app, `--port`, the read-only scope, the SmartScreen note, the uninstall refusal and troubleshooting. | review + grep |

## Tasks
| # | Task | Priority | Criteria | Primary ownership | Dependencies | Status |
|---|---|---|---|---|---|---|
| 01 | Core: status models, client, view model, launch options, tests | P1 | AC-01, AC-02, AC-03, AC-04, AC-05 | `src/EdgeDesktop.Core/`, `tests/Edge.Tests/Desktop/`, `tests/Edge.Tests/Edge.Tests.csproj`, `IndustrialIot.sln` | - | done |
| 02 | WPF window, wired into the solution and Linux CI | P1 | AC-01, AC-02, AC-05, AC-06 | `src/EdgeDesktop/`, `IndustrialIot.sln` | task-01-desktop-core.md | done |
| 03 | Package, install, uninstall, verify | P2 | AC-07 | `deployments/edge/` | task-02-wpf-window.md | done |
| 04 | Docs and user checks | P2 | AC-01, AC-02, AC-05, AC-07, AC-08 | `docs/deployment.md` | task-03-package-install.md | done |

`IndustrialIot.sln` has two owners, in order (task-01, then task-02). No parallel wave.

## Review log
- Round 1 (2026-11-03): two fresh reviewers (facts/contracts; failure/security/assumptions). They found 16 issues,
  deduplicated to 12 (G1–G12). All citations sampled were current, after `install-edge.ps1:205,221` was corrected
  to `:203,223`. The user accepted all 12. User decisions: G3 uninstall **refuses** while the app runs; G2 folder
  **`Edge.Desktop`** derived from `$InstallDir`.

| ID | Sev | Issue | Applied in |
|---|---|---|---|
| G1 | High | Contract test could not catch a renamed field | D-02, AC-03, task-01 |
| G2 | High | Fixed app path and shortcut; `-Live` could touch a real install; no preconditions or exact rollback; `EdgeDesktop` matched `Edge[A-Za-z]*` | D-06, AC-07, task-03 |
| G3 | High | Uninstall failed halfway while the app was running | D-06, AC-07, task-03 |
| G4 | Medium | CI proof built only, passed before the change, and used a tree with another feature's changes | AC-06, task-02, precondition |
| G5 | Medium | No UI-thread or cancel contract for the poll loop | D-09, AC-02, task-01, task-02 |
| G6 | Medium | Stale rule applied to multi-PLC; clock source unset | D-05, task-01 |
| G7 | Medium | No installed machine for the non-admin check | task-04 |
| G8 | Low | `localhost` may try ::1 first and hit the timeout [UNVERIFIED] | D-03, AC-05 |
| G9 | Low | "Same as web" was inexact; banner text had no oracle | D-03, task-01 |
| G10 | Low | Task-04 Command could not prove AC-08 | AC-08, task-04 |
| G11 | Low | Ownership table incomplete; docs had another feature's diff | Tasks table, precondition |
| G12 | Low | Core model names clash with Edge records | D-01, task-01 |

- Sweep: names (`Edge.Desktop`, shortcut rule), CP rows, AC↔task mapping, dependencies and ownership re-checked
  after the edits; all tasks promoted from `blocked` to `pending`.
- 2026-11-04, user decision during edge-desktop-settings GATE-SCOPE: task-04 keeps only user items 5–7; items 1–4 (live comparison of this window) move to edge-desktop-settings, which replaces the window layout.
- GATE-DONE 2026-11-04: the user accepted completion after seeing the four receipts and the limitations (items 1–4 dropped and moved to edge-desktop-settings; items 5–7 reported as a summary; the window is replaced next; nothing committed yet).
