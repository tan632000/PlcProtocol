# Edge settings page — PLC, protocol, poll interval and tags edited on the Edge
Specs-Contract: process-first-ready-v1

## Scope decision (GATE-SCOPE — 2026-10-29)

### Existing
- Settings: `src/Edge/appsettings.json:10-36` (`Edge:Plc`, `Edge:Tags`, `Edge:Poll`).
- Startup validators that can be reused:
  - `PlcTagCatalogOptionsValidator` (`src/Edge/EdgeServiceCollectionExtensions.cs:75-89`).
  - `PlcTagCatalog`: duplicates, address/type, writable overlap (`src/Edge/Drivers/PlcTagCatalog.cs:49-68`).
  - `PlcProtocolOptionsValidator` (`src/Edge/Drivers/PlcProtocolOptions.cs:36-56`).
- The driver is chosen when services are registered (`src/Edge/Drivers/DriverServiceCollectionExtensions.cs:45-55`).
- Monitor:
  - Loopback only, no login, Host-header guard (`src/Edge/Monitoring/MonitorOptions.cs:6-7`, `MonitorServer.cs:56,149-162`).
  - A failure to bind its port is logged and swallowed (`MonitorServer.cs:70-74`).
- Test fakes: `tests/Edge.Tests/Fakes/FakeS7Server.cs` (`OpenConnections` at line 123), `FakeSlmpServer.cs`, `EdgeTestHost.cs`.
- `IPlcDriver.IsReachableAsync` (`src/Edge/Drivers/IPlcDriver.cs:26`).

### Constraints found
- The installed service can only read its program folder (`deployments/edge/install-edge.ps1:224`).
- `Edge__Buffer__Path` and `Edge__Plc__Host` exist only in the service's registry Environment (`install-edge.ps1:232-240`).
- The SCM restarts the service at most 3 times per 24 h (`install-edge.ps1:250`).
- `AddWindowsService` is called on the one host in `src/Edge/Program.cs:10`. VERIFY-STATIC greps `Program.cs` for it (`deployments/edge/verify-edge.ps1:33-41`).
- The dev S7 profile and the FX5U lab profile both keep their buffer databases in `src/Edge/data/` (`src/Edge/lab/fx5u/appsettings.json`).

### Minimum change
- A settings page on the Edge monitor (5080) that edits the PLC protocol and parameters, the poll interval and tags.
- Validate, then save with a local settings password.
- Apply by rebuilding the agent host under a long-lived outer host, with rollback.
- Test the PLC connection.

### Expansion signals
- About 14 touched files and 7 tasks. One subsystem (Edge).
- Risk: **critical**. The change touches the write path (`Writable`) and whether the Edge starts at all.

### User decisions
- KEEP: PLC settings, protocol switch, poll, tags, test connection.
- Saving needs a local password.
- `Writable` is display-only on the page.
- Apply by in-process restart.

## Out of scope
- Cloud-side or central configuration, or pushing configuration over MQTT.
- Editing `Writable`, `Min` or `Max` on the page.
- Editing EdgeId, MQTT settings or password, monitor port, `Commands`.
- CSV import/export, browsing tags on the PLC, hot reload without a restart, access over the network (non-loopback).

## Decisions
| ID | Chosen | Rejected | Why | Load-bearing assumption | Break signal → response |
|---|---|---|---|---|---|
| D-01 | The settings file is `<db>.settings.json` beside the buffer database `<db>.<ext>` named by `Edge:Buffer:Path`. Examples: installed `edge.settings.json` in DataDir; dev `data/edge.settings.json`; FX5U lab `data/fx5u-lab.settings.json`. The password file is `<db>.settings-password.json`, and the apply marker is `<db>.settings.pending`. | Writing `appsettings.json`; using only the directory | The service cannot write its program folder. Naming by DB keeps the dev and lab profiles apart (R-10). | The buffer DB's directory is writable | Not writable → save answers 500 naming the path |
| D-02 | While the file exists, it is the **only** source of `Edge:Plc`, `Edge:Poll` and `Edge:Tags`, for options binding and for raw `IConfiguration` reads (`DriverServiceCollectionExtensions.cs:25,32`). No merge with `appsettings.json` or environment variables (including `Edge__Plc__Host`). | Layering the file over the others | .NET configuration merges arrays by index, and the installer's environment would hide a Host change. | — | Test: 21 base tags + 2 in the file → exactly 2 |
| D-03 | File shape: `{"SchemaVersion":1,"Edge":{"Plc":{…},"Poll":{"IntervalMs":…},"Tags":[…]}}`, using the key names of `appsettings.json`. Any other key fails the start, naming the key (R-11). The API answers 400 for it. | A new schema; ignoring unknown keys | Operators know these keys. A hand-edited file must not turn on `Commands`, change MQTT or move `Buffer.Path`. | — | — |
| D-04 | Settings password: PBKDF2-SHA256, 600 000 iterations, 16-byte salt, minimum **12** characters. It is set with `IndustrialIot.Edge.exe set-settings-password [--service] [--contentRoot <dir>]`, which prompts twice without echo and never takes the password on argv. The target directory is found like this: with `--service`, from `Edge__Buffer__Path` in the registry Environment of the service `IndustrialIot.Edge`; without it, from the effective configuration of that content root, even when a service is installed (R-09, N-5). The command refuses a directory that is a reparse point or writable by Users, Authenticated Users or Everyone. Each save or test request carries the password; there is no session. | Cookie sessions; a password made by the installer | Stateless; DataDir is readable only by SYSTEM, Administrators and the service. | — | No password file → 403 "chưa đặt mật khẩu settings" |
| D-05 | 5 wrong passwords, counted across save and test → every save and test answers 429 for 60 s. Attempts during the lockout are not counted. Each failure and each lockout is written to the log (Event Log under the service). The local-DoS risk is accepted and documented: an Administrator can still edit the file (R-13). | Per-request backoff | User decision. | — | — |
| D-06 | Writable rule (R-06, R-07): the page sends every tag with `Writable=false`. A saved tag is writable only if it matches a currently writable tag exactly on ordinal TagId, canonical Address, DataType, Min and Max. These answer 400: a payload tag that matches a writable tag's TagId case-insensitively but differs in any of those fields; any `Writable=true` in the payload; and, while any writable tag is kept, any change to `Plc.*` (Protocol, Host, Port, Rack, Slot, CpuType). Deleting a writable tag is allowed. | Free editing; silently dropping Writable | Keeps the four-switch read-only default and stops writes from being redirected to another PLC or address. | — | — |
| D-07 | Apply (R-01..R-05): `Program.cs` builds one **outer** host that owns `AddWindowsService` and the console/SCM lifetime. It runs `EdgeSupervisor`, which builds the **inner** agent host with a no-op `IHostLifetime`. On a restart request the supervisor: waits until the save response has completed; stops the inner host and disposes it (with an explicit `ShutdownTimeout`); builds a new one; and starts it. **Start failure** means an exception from configuration load, registration, `Build` or `StartAsync`, or the monitor not listening while it is enabled; during an apply the monitor retries its bind for up to 5 s before that counts (N-1). Long-lived state (`EdgeRestarter`, the password verifier with its lockout, the test rate limit) is created once by the supervisor and registered as the same instances in every inner host (N-3). On a failure the supervisor restores the previous file bytes it holds in memory (or deletes the file if there was none), starts again, and records `rolledBack`. If that start also fails, the outer host stops with exit code 1. Before the swap it writes the `pending` marker (previous bytes, or "absent"). On a cold start with the marker present and a failing start, it restores from the marker. After a successful start it deletes the marker. When the outer host stops (SCM, Ctrl+C), the loop ends at any point. | One host per process; restart through SCM; `Restart-Service` | SCM allows 3 restarts per day; the lifetime must survive rebuilds. | Inner hosts work without a Windows lifetime | Live check in task-07: after Save the service is still Running |
| D-08 | Revisions: `GET /api/settings` returns `revision` = SHA-256 hex of the bytes the **running** inner host was built from (`"none"` when there is no file). A save must send that revision; a mismatch → 409. PUT returns `applyId`. `/api/status` returns `settingsApply: {revision, applyId, state: applying|applied|rolledBack, error}`, where `error` belongs to that `applyId` and is a fixed message, never exception text. | Hash of the file on disk | The page waits for its own `applyId` to reach a final state. | — | — |
| D-09 | Writes: temporary file in the same directory, then rename over the target. Saves are serialized. A save while an apply is in progress → 409. | Overwriting in place | No half-written file. | — | — |
| D-10 | Test connection (R-14): `IsReachableAsync` on a separate driver built from the proposed `Plc`, then disposed. If the proposed protocol, Host and Port equal the running driver's, it answers from the running driver's state instead of opening a second connection. The result is `ok` or `unreachable` only, because `IsReachableAsync` returns a bool (`IPlcDriver.cs:26`, N-2). One test at a time (409), at most 10 per minute (429). Each test is logged as host:port + result. | Free-text messages; reusing the running driver for another host | Do not scan or fingerprint the OT network; do not take the PLC's connection slots. | Drivers can be constructed outside DI | — |
| D-11 | Request guard for every non-GET settings route (R-08), run **before** the password check: `Content-Type` parsed as exactly `application/json` (else 415); `Origin`, if present, must be `http://localhost:{port}` or `http://127.0.0.1:{port}`, and `Sec-Fetch-Site: cross-site` is refused (both 403); body ≤ 1 MB. Responses never repeat field values; they give key paths only. | Host check only | Blocks requests forged from another site in the operator's browser. | — | — |

## Coverage profile
| ID | Outcome | Change kinds | Material surfaces | Ambiguity/action | Risk/evidence | Required proof |
|---|---|---|---|---|---|---|
| CP-01 | A saved settings file fully and solely defines PLC, poll and tags | add, modify | Runtime/deploy, Data/schema, Filesystem/security | none | critical; D-01..D-03 | source |
| CP-02 | A save applies by rebuilding the inner host under the service or console lifetime, with rollback and crash recovery | add, modify | Async/state, Runtime/deploy | none | critical; D-07, D-08 | source + live (installed service) |
| CP-03 | Only a password holder, from the operator's own page, can save or test; unsafe or invalid settings are refused before anything is written | add | API/CLI, Filesystem/security | none | critical; D-04..D-06, D-08, D-09, D-11 | source |
| CP-04 | The operator tests a PLC connection without saving | add | API/CLI, Integration/proof | none | elevated; D-10 | source with fakes; live needs a PLC (limitation) |
| CP-05 | The operator edits settings at `http://localhost:5080/settings` | add | Interaction/UI | none | elevated | source + browser check |
| CP-06 | Docs and installer text match the new precedence | modify | Runtime/deploy | none | routine | review + user check |

## Acceptance criteria
| ID | EARS criterion | Proof |
|---|---|---|
| AC-01 | While `<db>.settings.json` exists, the Edge shall take `Edge:Plc`, `Edge:Poll` and `Edge:Tags` only from it and shall fail to start on any other key or `SchemaVersion` ≠ 1; while it does not exist, behaviour shall be unchanged. | task-01 |
| AC-02 | When an apply is requested, the Edge shall dispose the old inner host and start a new one under the same outer lifetime; if that start fails, it shall restore the previous settings, start, and report `rolledBack` for that `applyId`; after a crash mid-apply, the next start shall recover the same way. | task-02 + task-07 live |
| AC-03 | `set-settings-password` shall store only a PBKDF2 hash in the directory the service uses, and the verifier shall accept only that password, with the D-05 lockout. | task-03 |
| AC-04 | If a save or test is cross-site, not `application/json`, from a Host other than localhost/127.0.0.1, or has a wrong, missing or locked-out password, the Edge shall refuse it (403/415/400/401/403/429), leave the file unchanged, and keep the lockout counter unchanged for requests rejected before the password check. | task-04, task-05 |
| AC-05 | If the proposed settings fail any startup validator, break D-06, have unknown keys, or name a stale revision, the Edge shall answer 400/409 with key paths and leave the file unchanged; a valid save shall answer 202 with an `applyId` that later reaches `applied`. | task-04 |
| AC-06 | When the operator tests a connection, the Edge shall answer `ok` against a reachable fake PLC and `unreachable` within TimeoutMs + 1 s against an unreachable one, without opening a second connection to the running PLC. | task-05 |
| AC-07 | The settings page shall load and show loading, empty, error, locked-writable, applying, applied and rolled-back states; it shall set text only with `textContent`/`value`. | task-06 |
| AC-08 | The docs and the installer message shall state the file location and precedence, the password command, editing `Writable` in the file, the lockout and recovery; the user shall confirm the page on the dev machine and that the installed service survives a save. | task-07 |

## Tasks
| # | Task | Priority | Criteria | Primary ownership | Dependencies | Status |
|---|---|---|---|---|---|---|
| 01 | The settings file is the only source of PLC/poll/tags | P1 | AC-01 | `src/Edge/Settings/EdgeSettingsFile.cs`, `src/Edge/EdgeServiceCollectionExtensions.cs` | - | done |
| 02 | Outer host + supervisor: apply by rebuilding the inner host, with rollback and crash recovery | P1 | AC-02 | `src/Edge/Program.cs`, `src/Edge/Settings/EdgeSupervisor.cs`, `src/Edge/Settings/EdgeRestarter.cs`, `src/Edge/Monitoring/MonitorServer.cs` | task-01-settings-file.md | done |
| 03 | Settings password: CLI, hash file, verifier with lockout | P1 | AC-03 | `src/Edge/Settings/SettingsPassword.cs`, `src/Edge/Program.cs`, `src/Edge/Settings/EdgeSupervisor.cs` (shared-state list), `src/Edge/EdgeServiceCollectionExtensions.cs` | task-02-supervisor-and-restart.md | done |
| 04 | Settings API: guard, read, validate, save, apply | P1 | AC-04, AC-05 | `src/Edge/Settings/SettingsService.cs`, `src/Edge/Monitoring/MonitorServer.cs`, `src/Edge/EdgeServiceCollectionExtensions.cs` | task-03-settings-password.md | done |
| 05 | Test PLC connection endpoint | P2 | AC-04, AC-06 | `src/Edge/Settings/PlcConnectionTester.cs`, `src/Edge/Monitoring/MonitorServer.cs`, `src/Edge/Settings/EdgeSupervisor.cs` (shared-state list), `src/Edge/EdgeServiceCollectionExtensions.cs` | task-04-settings-api.md | done |
| 06 | Settings page | P1 | AC-07 | `src/Edge/Monitoring/settings.html`, `src/Edge/Monitoring/monitor.html`, `src/Edge/IndustrialIot.Edge.csproj` | task-05-test-connection.md | done |
| 07 | Docs, installer message, user check (dev page + installed service) | P2 | AC-02, AC-08 | `docs/deployment.md`, `deployments/edge/install-edge.ps1`, `deployments/edge/uninstall-edge.ps1` (message lines only) | task-06-settings-page.md | done |

## Review log
- Round 1 (2026-10-29): three fresh-context reviewers (fact/contract, security, failure-mode). There were 17 raw findings, deduplicated to R-01..R-15. The user accepted all 15:
  - R-06: refuse PLC changes while a writable tag is kept.
  - R-13: shared 60 s lockout with logging.

  Where each landed:
  - R-01 → D-07, task-02, task-07 live.
  - R-02 → D-07 failure definition and marker.
  - R-03 → D-07 dispose and `ShutdownTimeout`.
  - R-04 → D-08.
  - R-05 → D-07 "after the response", task-04 end-to-end test.
  - R-06, R-07 → D-06.
  - R-08 → D-11.
  - R-09 → D-04.
  - R-10 → D-01.
  - R-11 → D-03.
  - R-12 → task-01 owns DI registration; every task runs the full Edge suite; environment injected as a source.
  - R-13 → D-04 (12 chars), D-05, D-11.
  - R-14 → D-10.
  - R-15 → task-07.

  Two citation offsets were fixed (`appsettings.json:10-36`, `PlcTagCatalog.cs:49-68`). Task-01 was split into task-01 and task-02, giving 7 tasks.
- Round 2 (2026-10-29, closure pass, last paper round): R-01..R-11, R-13 and R-14 replay PASS; R-15 passes with one caveat; R-12 partly open. Five new issues were found and the user accepted all of them, along with the minor probes:
  - N-1: bind retry of up to 5 s during an apply; the test seam occupies the port for the first start only.
  - N-2: test result is `ok`/`unreachable`.
  - N-3: shared state is created by the supervisor and registered in each inner host; tasks 03-05 own registration; lockout survives a restart (probe).
  - N-4: the page sends Min/Max back; the "kept writable tag → 202, still writable" probe.
  - N-5: CLI `--service` / `--contentRoot`.
  - Minor: Port/Protocol and DataType probes; uninstall message added to task-07.

  Further findings need runtime evidence.
