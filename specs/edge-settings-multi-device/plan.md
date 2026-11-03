# Edge settings page for several PLCs (roadmap GĐ4)
Specs-Contract: process-first-ready-v1

## Scope decision (GATE-SCOPE — 2026-11-03)
- Request: roadmap `plans/plc-multi-device-roadmap.md` GĐ4 (4.1–4.5):
  - save and check the configuration with the existing password, anti-CSRF and rollback;
  - a Channel → Device → Tag tree with add/edit/delete and model defaults;
  - a connection test per PLC;
  - tag import/export as CSV with errors by line number.
  - Acceptance: "2 brands × 2 PLCs configured entirely in the browser" and "a CSV exported then imported gives the
    same result".
- Existing:
  - The settings API speaks schema 1 only:
    - GET/validate/PUT at `src/Edge/Settings/SettingsService.cs:45-139`.
    - A schema-2 file is refused with 409 `multi-device-file` (`:89-95`).
    - Body guard: cross-site, JSON only, 1 MB (`:147-195`).
    - The startup validators decide, run by registering the agent on the proposed settings (`:440-487`).
    - Errors give key paths and fixed text, never submitted values (`:489-517`).
    - The page cannot make a tag writable — D-06 of `specs/edge-settings-ui` (`:316-362`).
    - At most 1000 tags (`:34`).
  - The settings file accepts schema 2 and then owns `Edge:Plc`, `Edge:Poll`, `Edge:Tags` and `Edge:Channels`
    (`src/Edge/Settings/EdgeSettingsFile.cs:19,32,184-190`). `Compose` lays the file over the raw configuration
    (`:155`).
  - The inner host receives the raw outer configuration (`src/Edge/Settings/EdgeSupervisor.cs:295`).
  - Apply: the supervisor rebuilds the whole inner host and rolls back on a failed start (`:73-79`). One apply runs at
    a time (`src/Edge/Settings/EdgeRestarter.cs:48-62`).
  - v2 configuration:
    - Validator: `src/Edge/Configuration/EdgeChannelsOptions.cs:98-172`. It returns a list, and every error starts with
      `Edge:Channels:c:Devices:d:Tags:t:…`.
    - The resolver and the registry join their errors into one exception "Edge:Channels is invalid. …"
      (`src/Edge/Configuration/EdgeDeviceResolver.cs:63-66`, `src/Edge/Drivers/PlcDeviceRegistry.cs:109-111`).
    - The v1 → v2 migration makes one device `Plc1` with legacy TagIds. Its `WritesEnabled` is taken from
      `Edge:Commands:Enabled` (`src/Edge/Configuration/EdgeDeviceResolver.cs:37,107-143`).
    - The model catalog holds defaults, `PortHint` and `WritesVerified` (`src/Edge/Drivers/PlcModelCatalog.cs:11-19,37-45`).
  - Connection test: a proposed v1 PLC, or a saved PLC by name. It answers `ok`/`unreachable` only, with the password,
    one test at a time and 10 a minute (`src/Edge/Settings/PlcConnectionTester.cs:69-160,223-244`).
  - Routes are on the loopback monitor (`src/Edge/Monitoring/MonitorServer.cs:106-118`). The page is one static HTML
    file (`src/Edge/Monitoring/settings.html`, 484 lines).
- Minimum change: the settings API and page speak schema 2; a connection test for an unsaved PLC; CSV parse/format.
- Expansion signals: about 12 touched files (> 8). Applying one PLC without stopping the others would also touch the
  registry, poller, snapshot, commands and inventory, all built once per inner host. This was offered as EXPAND and
  not taken.
- User decision: **KEEP**.
  - Schema-2 API and tree page, model defaults, a test per PLC including unsaved ones, CSV per PLC.
  - Apply keeps rebuilding the whole agent: every PLC pauses a few seconds and rollback is unchanged. Roadmap item 4.5
    is therefore met only in part, recorded as a limitation.
  - Writes: **the page may switch a device's `WritesEnabled` and a tag's `Writable`**. This reverses D-06 of
    `specs/edge-settings-ui` at the user's request.
  - CSV: **an 8th column `Length`**.
  - Limits: **1000 tags per PLC, 5000 per Edge, request body 4 MB**.

## Out of scope
- Applying one PLC without restarting the others (the EXPAND option).
- `Edge:Commands:Enabled` (the global write switch) stays outside the page and the settings file. The page shows it
  read-only.
- Rule changes to the model catalog, drivers or validators. FX3U writes stay refused by `PlcModelCatalog.CheckWrites`.
- A `CpuType` field in schema 2: a v1 `CpuType` other than S71200 is not kept (D-08).
- Tests on real PLCs (roadmap GĐ6).
- CSV dialects other than comma-separated UTF-8.
- The monitor page and the Cloud.
- Delivery: tasks 01–05 ship in **one PR**. Between task-01 and task-05 the old page sends schema-1 bodies and does
  not work.

## Decisions
| ID | Chosen | Rejected | Why | Load-bearing assumption | Break signal → response |
|---|---|---|---|---|---|
| D-01 | **Contract.** The API body becomes `settings: {Channels: [...]}`, shaped like `EdgeChannelsOptions`. Every save writes `SchemaVersion 2`.<br>**GET source.** Compose the current file over the configuration (`EdgeSettingsFile.Compose`) and resolve it. When it holds `Edge:Channels`, that is the tree. Otherwise the tree is the v1 migration: one channel named after the driver, device `Plc1`, `LegacyTagIds: true`, tag `Name` = the old TagId. Saving it keeps every published TagId.<br>**Removed.** The schema-1 body is no longer accepted. | Keep two API shapes; read the raw configuration. | One page and one shape. The raw configuration still holds the appsettings channels after a schema-2 save (F1). | The loopback page is the only client of `/api/settings`; confirmed at review: tests and `settings.html` only. | — |
| D-02 | **Order of checks on a proposed tree:**<br>1. Shape. Every channel, device and tag is a non-empty object. Only `EdgeChannelsOptions` keys are allowed.<br>2. Limits, counted on the JSON.<br>3. `EdgeChannelsValidator.Validate` on the bound tree. It returns a list, and every entry starts with its key.<br>4. Per enabled device, the registry's driver-option and tag-catalog checks. They are exposed as a list of (device name, TagId?, field) and mapped to `Edge:Channels:c:Devices:d[:Tags:t[:Field]]`. `PlcTagCatalog` reports the field of its error; its rules do not change.<br>5. Only when steps 1–4 found nothing: `AddEdgeAgent` on the configuration with every **schema-2-owned** key removed, plus the proposed keys.<br>Each path gets a fixed text per field. No error path is ever exactly `Edge:Channels`. The catalog reports only the first bad tag of a device (limitation). | Parse the joined exception text; copy the rules into the API or the page. | Keyed errors already exist before they are joined (F2). Removing only schema-1 keys would mix the appsettings channels into the proposal by index (F1). | — | A check that yields no device/tag key → the error is pinned to the nearest known node, never to `Edge:Channels` alone. |
| D-03 | **What counts as a change.** The **effective write set**: the devices with `WritesEnabled`, and the TagIds with `WritesEnabled && Writable`. The old set comes from the running inner host, the new set from the saved file. Additions, removals and renames all count.<br>**When to log.** The diff goes with the apply request and is logged at Warning, one line per device or TagId (old → new, names only, with the applyId), **only when the apply completes as Applied**. A rollback logs nothing about write changes. | Log at 202; compare by name only. | A write permission on a plant PLC must leave a true trace. Delete-and-re-add, or a rename, must not bypass it (F3). | — | — |
| D-04 | **Limits on the page path only:** 1000 tags per device and 5000 per Edge, counted on the JSON. Body: 4 MB for settings and CSV routes, 1 MB for test-connection (`ReadGuardedAsync` takes the limit). | Limit in the validator for every config; one 4 MB limit for all routes. | appsettings files keep today's behaviour (F11). | — | — |
| D-05 | **Where.** CSV parse and format are C# (`TagCsv`), behind `POST /api/settings/csv/parse` and `POST /api/settings/csv/format`. Both use the request guard. There is no password because nothing is saved.<br>**Columns.** In this order, header required: `Tag Name, Address, Data Type, Client Access, Description, Min, Max, Length`. Header cells are trimmed and compared case-insensitively.<br>**Format of the file.** Comma separator. RFC 4180 quotes. UTF-8 with or without BOM. CRLF or LF. Invariant numbers. An empty cell and `""` both mean not set (null), on the page and in `TagCsv`. Cells are not trimmed.<br>**Cell values.** `Data Type` is kept as written. `Client Access` is `Read Only` or `Read/Write`, case-insensitive. A `Length` on a non-String tag is left for validate.<br>**Formula guard.** On export, a leading `'` is added to a text cell that starts with `=` `+` `-` `@`, tab or CR. It is also added to a text cell that starts with `'` followed by one of those or by another `'`. On import, one leading `'` is removed only when the next character is `'` or one of those. Both rules together make the guard reversible (N1).<br>**Errors.** They name the physical line where the record starts (header = line 1) and the column, never the cell value.<br>**Import.** It replaces the device's whole tag list in the page. Nothing is saved until Save.<br>**Round trip.** Export → import compares fields; Min/Max are compared by decimal value. | Parse in the page's JavaScript; merge on import. | xUnit can prove the round trip, and one formatter means export → import is identical. Excel runs formulas found in a CSV (F7). | Excel saves comma CSV on the operator's machine. | Operators report `;` files → a later change, not this packet. |
| D-06 | **Input.** The connection test also takes an unsaved device: `{password, proposed: {driver, model, host, port, rack, slot, timeoutMs}}`, with defaults from the model. MC 1E is supported.<br>**Reusing a running driver.** Hosts are trimmed and compared case-insensitively. The running driver is reused only when an enabled device matches every connection field: driver, host, port, and for S7 also rack and slot. The same host and port with any other field, the driver included, → 409 `endpoint-in-use` ("save first"), so no second connection is opened (N2).<br>**Answer.** Stays `ok`/`unreachable`.<br>**Removed.** The schema-1 `plc` body. | Test only saved PLCs; reuse by host and port only. | An operator checks a new PLC before saving. A loose match gives a false `ok` or opens a second connection (F10). | — | — |
| D-07 | The page's model list comes from `GET /api/settings` (`models`: name, driver, default port/rack/slot, `portHint`, data types, `writesAllowed`), built from `PlcModelCatalog`. | A table in the page. | One source. FX models have no default port (F12). | — | — |
| D-08 | **Migration losses.** A v1 S7 `CpuType` other than S71200 is not kept by schema 2. GET returns `warnings: ["cpuType-not-kept"]`, the page shows it, and the save is allowed. v1 already treats S71500 as S7-1200 for model rules (`src/Edge/Configuration/EdgeDeviceResolver.cs:135-138`).<br>The migrated `WritesEnabled` is fixed at the value of `Edge:Commands:Enabled` at the time of the first save. Docs say so. | Refuse the save. | User decision at GATE-REVIEW (F5). | The roadmap supports S7-1200 only. | — |

## Coverage profile
| ID | Outcome | Change kinds | Material surfaces | Ambiguity/action | Risk/evidence | Required proof |
|---|---|---|---|---|---|---|
| CP-01 | Settings API reads and saves several PLCs; a v1 Edge keeps its TagIds after a save | modify, migrate | API, Data/schema, Async/state (apply) | none | elevated: config file of an installed Edge | source (xUnit, supervisor rebuild with fakes) |
| CP-02 | Write permissions edited on the page and logged only when applied | modify | API, security, Async/state | none | elevated: plant writes | source |
| CP-03 | Connection test for an unsaved PLC | modify | API | none | routine | source (local listeners) |
| CP-04 | CSV import/export per PLC, errors by line, round trip identical, formula guard | add | API, Data, security | none | routine | source |
| CP-05 | Tree page in the browser | modify | Interaction/UI | none | elevated: operator tool | source (static page tests) + live (user in a browser, items 1–9 of task-06) |
| CP-06 | Docs match the page | modify | docs | none | routine | source review + user check |

## Acceptance criteria
| ID | EARS criterion | Proof |
|---|---|---|
| AC-01 | When `GET /api/settings` is called, the Edge shall return the tree of its composed configuration (a schema-1 Edge as device `Plc1` with `LegacyTagIds: true`), the model list, `warnings` and the read-only `commandsEnabled`. | task-01 |
| AC-02 | When a valid schema-2 tree is saved, the Edge shall write `SchemaVersion 2` and run every device of it after the apply. A schema-1 Edge saved unchanged shall publish the same TagIds as before. | task-01 |
| AC-03 | If a proposed tree breaks a validator rule, a catalog rule or a limit, the Edge shall refuse it before writing anything, naming the node's key path and a fixed message, judging only the proposed tree. | task-01 |
| AC-04 | When an apply that changes the effective write set completes, the Edge shall log each change at Warning; a rolled-back apply shall log none. | task-02 |
| AC-05 | When an unsaved device is tested, the Edge shall answer `ok` or `unreachable` with the same password and rate limits as today, without a second connection to a running PLC. | task-03 |
| AC-06 | When a CSV is imported, the Edge shall return the device's tags or the errors by line and column. | task-04 |
| AC-07 | When a device's tags are exported then imported, the result shall equal the original tag list, and no exported text cell shall start with a formula character. | task-04 |
| AC-08 | The settings page shall show the tree, fill model defaults, test each PLC, import/export CSV and edit write permissions, writing text only through `textContent`/`value`. | task-05 (source), task-06 (live) |
| AC-09 | When the user configures 2 brands × 2 PLCs in a browser and round-trips a CSV, it shall work without editing a file. | task-06 |

## Tasks
| # | Task | Priority | Criteria | Primary ownership | Dependencies | Status |
|---|---|---|---|---|---|---|
| 01 | Settings API speaks schema 2 | P1 | AC-01, AC-02, AC-03 | `src/Edge/Settings/SettingsService.cs`, `src/Edge/Settings/SettingsTree.cs`, `src/Edge/Drivers/PlcDeviceRegistry.cs`, `src/Edge/Drivers/PlcTagCatalog.cs`, `src/Edge/Settings/EdgeSettingsFile.cs` | - | done |
| 02 | Write-permission changes logged on apply | P1 | AC-04 | `src/Edge/Settings/EdgeRestarter.cs`, `src/Edge/Settings/EdgeSupervisor.cs` | task-01-settings-api-v2.md | done |
| 03 | Connection test for an unsaved PLC | P2 | AC-05 | `src/Edge/Settings/PlcConnectionTester.cs` | task-01-settings-api-v2.md | done |
| 04 | Tag CSV import and export | P2 | AC-06, AC-07 | `src/Edge/Settings/TagCsv.cs`, `src/Edge/Monitoring/MonitorServer.cs` | task-01-settings-api-v2.md | done |
| 05 | Tree settings page | P1 | AC-08 | `src/Edge/Monitoring/settings.html` | task-02-write-change-log.md, task-03-test-unsaved-plc.md, task-04-tag-csv.md | done |
| 06 | Docs and user check in a browser | P2 | AC-08, AC-09 | `docs/deployment.md` | task-05-tree-settings-page.md | done |

## Review log
- Round 1 (2026-11-03): three fresh reviewers (security + contract; failure modes + flow; fact check + scope). All
  citations held. 13 deduplicated findings (4 High, 5 Medium, 4 Low). User: accept all; F5 as limitation + warning.
  - F1: validate/GET mixed with appsettings `Edge:Channels` → D-01, D-02.
  - F2: v2 errors lost their key path → D-02.
  - F3: write log bypassable and wrong on rollback → D-03, new task-02.
  - F4: AC-08 unproven → task-06 items 5–8.
  - F5: lossy v1 migration → D-08.
  - F6: CSV ambiguity → D-05.
  - F7: CSV formula injection → D-05.
  - F8: empty objects shift indices → D-02 shape.
  - F9: task-01 too large, page broken mid-way → task split, one PR, wider filters.
  - F10: loose driver reuse → D-06.
  - F11: per-route body limit → D-04.
  - F12: `portHint`/`writesAllowed` → D-07.
  - F13: test seam and password step → task-01 and task-06 Steps.
- Closure pass (2026-11-03, fresh reviewer): F1–F3, F5, F7–F13 PASS. Two findings were only partly closed:
  - F4: error pinning was not proven → task-06 item 9.
  - F6: "no trim" was not tested → task-04 AC-07.
- Round 2 (2026-11-03, last paper round): 5 new findings from the repairs. User: accept all.
  - N1: the formula guard was not reversible for `'=x` → D-05, task-04.
  - N2: task-03 contradicted D-06 for a different driver on the same endpoint → task-03.
  - N3: the catalog field was not reachable in task-01's ownership → D-02, task-01 owns `PlcTagCatalog.cs` (field
    only, no rule change).
  - N4: step 5 now runs only after clean steps 1–4 → D-02, task-01.
  - N5: statuses moved to `pending`; 0-based indices in task-01.
  - Sweep: 7 files reread, 7 deltas, 0 stale references, 0 conflicts. Later findings need runtime evidence (B4).
- Amendment (2026-11-03, during task-05): user decision, AC-08 password field uses `autocomplete="new-password"` instead of `"off"` (browsers ignore "off" on password fields; the existing test asserts new-password).

| ID | Decision | Original counterexample | Repaired at | Proved at | Replay | Closure |
|---|---|---|---|---|---|---|
| F1 | accept | appsettings channels mixed into the proposal; FX5U inherits Port; GET shows the raw tree | D-01, D-02 step 5 | task-01 AC-01 (file tree over appsettings), AC-03 (FX5U Port → 400) | fails a named test | PASS |
| F2 | accept | every v2 error collapses to `Edge:Channels` | D-02 steps 3–4, N3 | task-01 AC-03 node paths, two errors on two devices | fails a named test | PASS |
| F3 | accept | delete + re-add or rename skips the log; log on rollback | D-03, task-02 | task-02 AC-04 five cases | fails a named test | PASS |
| F4 | accept | AC-08 behaviours unproven | task-06 items 5–9 | user report items 5–9 | live check | PASS (on paper; live at task-06) |
| F5 | accept (limitation) | silent CpuType loss, fixed WritesEnabled | D-08 | task-01 AC-01 warning; task-06 docs | warning shown | PASS |
| F6 | accept | CSV line/null/trim/Length ambiguity | D-05 | task-04 AC-06 physical line; AC-07 `""`→null and surrounding spaces | fails a named test | PASS |
| F7 | accept | `=` runs in Excel | D-05, N1 | task-04 AC-07 formula cells and `'=x` | fails a named test | PASS |
| F8 | accept | `{}` shifts indices | D-02 step 1 | task-01 AC-03 empty tag | fails a named test | PASS |
| F9 | accept | task-01 too large; page broken mid-way | task split, one PR | plan Out of scope; task-01 filter | — | PASS |
| F10 | accept | loose reuse of a running driver | D-06, N2 | task-03 AC-05 trim/case, slot, driver → 409 | fails a named test | PASS |
| F11 | accept | one 4 MB limit for all routes | D-04 | task-01 413 at 4 MB; task-03 413 at 1 MB | fails a named test | PASS |
| F12 | accept | no `portHint`/`writesAllowed` | D-07 | task-01 AC-01 models | fails a named test | PASS |
| F13 | accept | no test seam; no password step | task-01 Steps, task-06 Scope | `EdgeSupervisorTests` pattern; task-06 first step | — | PASS |
