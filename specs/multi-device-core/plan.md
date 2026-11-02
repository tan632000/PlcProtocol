# Multi-device core: one Edge reads and writes many PLCs (FX3U, FX5U, S7-1200)
Specs-Contract: process-first-ready-v1

## Scope decision (GATE-SCOPE — 2026-10-30)
- Request: implement `plans/plc-multi-device-roadmap.md` (approved). This packet is the Edge core, milestone M1 (13/11):
  Channel → Device → Tag, many PLCs per Edge, models FX3U / FX5U / S7-1200, 100 tags per PLC at 1 s, types
  String / LReal / DTL, two-tier writes. Settings UI v2, Cloud inventory API and the 72 h release run are separate packets
  (`multi-device-settings-ui`, `device-inventory-api`, `multi-device-release`), planned later.
- Existing:
  - One driver contract with batch reads and an outage short-circuit (`src/Edge/Drivers/IPlcDriver.cs:10-27`); S7 packs
    up to 20 items per request (`src/Edge/Drivers/S7ReadPlanner.cs:10-12`); SLMP 3E reads D/M/X/Y and refuses writes
    before I/O (`src/Edge/Drivers/Slmp/MitsubishiSlmpDriver.cs:98-99`).
  - Seven data types (`src/Edge/Drivers/PlcDataType.cs:10-29`); telemetry and command values are JSON scalars limited to
    true/false/number (`src/Shared/Common/ContractJson.cs:231-244`); `TagId` allows dots, 1..64 chars (`src/Shared/Common/ContractJson.cs:188`).
- Single-PLC seams: the driver is a DI singleton (`src/Edge/Drivers/DriverServiceCollectionExtensions.cs:76,94-95`); the
  settings file is schema 1 and owns only `Edge:Plc`, `Edge:Poll`, `Edge:Tags` (`src/Edge/Settings/EdgeSettingsFile.cs:14,19`);
  one driver and one catalog are injected into `TagPoller` (`src/Edge/Polling/TagPoller.cs:17-26`), `CommandExecutor`
  (`src/Edge/Commands/CommandExecutor.cs:23-25`), `MonitorServer` (`src/Edge/Monitoring/MonitorServer.cs:22-30`),
  `SettingsService` (`src/Edge/Settings/SettingsService.cs:22-32`) and `PlcConnectionTester`
  (`src/Edge/Settings/PlcConnectionTester.cs:80,147`); the host wires them in `src/Edge/EdgeServiceCollectionExtensions.cs:27-81`;
  `LiveTagSnapshot` keeps one latest cycle (`src/Edge/Monitoring/LiveTagSnapshot.cs:16-27`).
- Minimum change: schema v2 with in-memory v1 migration; a driver and a read loop per Device; per-device command routing and
  Monitor status; a model catalog; string values in the shared contract; three data types; an FX3U MC 1E driver;
  two-tier writes with SLMP 3E and MC 1E writes; docs and a live check with a measured read time.
- Expansion signals: three independent subsystems (Edge core, settings UI, Cloud API) → split into four packets (user).
- User decision: **KEEP** (2026-10-30), with all six proposals:
  1. v1 is migrated in memory at every start; the file on disk is unchanged; only a Save from the UI writes v2, after a
     `edge.settings.v1.bak` copy (the Save is the UI packet; this packet only reads v2 and keeps the v1 page working on v1 files).
  2. A device migrated from v1 carries `LegacyTagIds: true`: every tag on it, including tags added later, publishes its
     `Name` verbatim. Any other device publishes `<Device>.<Name>`.
  3. DTL is published as the PLC's local time without an offset, `yyyy-MM-ddTHH:mm:ss.fff`.
  4. `String` tags require `Length` (characters). S7: matches `STRING[n]`. FX: two ASCII characters per D register.
     `LReal` only on S7-1200 and FX5U; `DTL` only on S7-1200.
  5. Acceptance threshold: 100 tags on one PLC read in ≤ 500 ms per cycle, measured on the real S7-1200 and FX5U.
  6. `plc-multi-protocol` task-07 (FX5U live check) is superseded by task-10 here; its file records that and stays
     `blocked`.
- GATE-REVIEW additions (user, 2026-10-30, "accept all as recommended"):
  - The shared contract changes: telemetry and command values may be bounded JSON strings (D-09). Cloud ingest and its
    tests change with it.
  - FX3U writes are implemented and tested against the fake but **refused at start** until a write is verified on a real
    FX3U (D-05); lifting that guard is a later change that needs a live receipt.
  - `s7-batch-read` task-02 (live measurement) is superseded by task-10 here; its file records that and stays `blocked`.
  - Writes to Y stay allowed; the docs warn that a running program's `OUT` overwrites them on the next scan.
  - One invalid device still fails the whole Edge start (unchanged behaviour); a per-device degrade is for the UI packet.

## Out of scope
- Settings UI v2 (tree, CSV, per-device apply, v2 Save and `.v1.bak`), Cloud `EdgeInventory`/API, 72 h run and release.
- S7-1500, Omron/Modbus/OPC UA, serial links, any import from other driver software.
- Writes to X inputs, to S7 inputs, or of any frame other than device read/write (no RUN/STOP, no remote operations).
- FX3U writes on a real plant (guarded, see D-05).

## Decisions
| ID | Chosen | Rejected | Why | Load-bearing assumption | Break signal → response |
|---|---|---|---|---|---|
| D-01 | Schema v2 keys: `Edge:Channels:[i]` {`Name`, `Driver` = `S7` \| `Slmp3E` \| `Mc1E`, `Devices:[j]` {`Name`, `Model` = `S7-1200` \| `FX5U` \| `FX3U`, `Host`, `Port`, `Rack`, `Slot`, `TimeoutMs`, `PollIntervalMs`, `Enabled` (default true), `WritesEnabled` (default false), `LegacyTagIds` (default false), `Tags:[k]` {`Name`, `Address`, `DataType`, `Length`, `Writable`, `Min`, `Max`, `Description`}}}. Names: Channel and Device `[A-Za-z0-9_-]{1,32}`, unique (Device unique across the Edge, case-insensitive); tag `Name` `[A-Za-z0-9_.-]`, published TagId ≤ 64 chars and unique across the Edge **case-insensitively** (the catalog looks ids up with `OrdinalIgnoreCase`, `src/Edge/Drivers/PlcTagCatalog.cs:42,76`). Two devices with the same `Host`:`Port` fail the start. A schema-2 settings file owns `Edge:Channels` **and** the v1 keys: it removes `Edge:Plc`/`Edge:Poll`/`Edge:Tags` coming from `appsettings.json` or environment (`src/Edge/appsettings.json:10-11`, `deployments/edge/install-edge.ps1:240`). Without a schema-2 file, a configuration holding both v1 keys and `Edge:Channels` fails the start naming both. The file accepts `SchemaVersion` 1 (owns the v1 keys) or 2. Any invalid device fails the whole start, as today. | keep `Edge:Plc` and add `Edge:Devices` | one tree as approved; explicit errors; installed Edges keep starting | configuration binder handles nested arrays (it does for `Edge:Tags`) | — |
| D-02 | v1 → v2 in memory: one Channel named after the protocol (`S7`→`S7`, `Slmp`→`Slmp3E`), one Device `Plc1` with `LegacyTagIds: true`, every `Edge:Plc` key copied (`Host`, `Port`, `Rack`, `Slot`, `TimeoutMs`), `Model` = `S7-1200` for S7 and `FX5U` for SLMP. A v1 `CpuType=S71500` keeps that CPU type inside the S7 driver options (the connection stays as today, `src/Edge/Drivers/SiemensS7Options.cs:39-45`) and logs "S7-1500 is not supported; treated as S7-1200 for model rules". `PollIntervalMs` from `Edge:Poll:IntervalMs`, `WritesEnabled` = `Edge:Commands:Enabled`, all tags copied (`TagId` → `Name`). The file is never written. | rewrite the file at start | user decision 1; reversible; existing sites keep their connection | — | a v1 lab file starts with different TagIds → stop, fix migration |
| D-03 | A `PlcDeviceRegistry` singleton builds one `DeviceRuntime` per enabled Device: its options, its own `IPlcDriver` instance made by an `IPlcDriverFactory` (DI singleton; tests register a factory returning fakes), its `IPlcAddressSyntax`, its `PlcTagCatalog` slice; plus a TagId → device map that includes disabled devices. `TagPoller` runs one loop per device (own interval, back-off, last read duration `readMs`, request count of the last cycle, state `Connected`/`Unavailable`/`Disabled`) and writes to the shared buffer and to `LiveTagSnapshot`, which keeps the latest cycle **per device**. A disabled device has no driver and no loop. Until task-03, consumers that take one driver/catalog get an adapter: with one device it is that device; with several it exposes the union catalog read-only, its driver refuses every write before I/O with `PlcUnavailableException`, and the connection tester refuses. | one loop for all devices; a hosted service per device | isolation is the approved goal; one hosted service keeps start/stop order | driver instances are independent (each owns its socket and lock) | a test shows device B's cycle delayed by device A's timeout → isolation broken |
| D-04 | Model catalog (static, in code): `S7-1200` → `S7`, Port 102, Rack 0, Slot 1; `FX5U` → `Slmp3E`, Port required; `FX3U` → `Mc1E`, Port required, 102 rejected; each model lists its allowed data types and address ranges (FX3U: D0–D7999, M0–M7679, X/Y 0–377 octal [UNVERIFIED — corrected in task-07 from the manual]); an omitted Port/Rack/Slot takes the model default. | per-protocol options classes only | the user picks a model, the system derives the rest | — | — |
| D-05 | MC 1E (A-compatible 1E frame, binary) for FX3U Ethernet modules: batch read and batch write in word and bit units, PC No. 0xFF, monitoring timer from `TimeoutMs`; one connection, one request at a time; blocks of touching tags up to the manual's point limit (64 words / 32 bit-words per request [UNVERIFIED]); X/Y ranges and word-read head alignment from the manual. Source: FX3U-ENET-ADP manual JY997D45801 and FX3U-ENET-L manual JY997D18101; task-07 obtains the frame examples and limits **before** coding and the fake enforces them. A frame builder with a closed device enum and private command constants; no other command can be built. **FX3U writes are refused at start** (`WritesEnabled=true` on an FX3U device or a `Writable` FX3U tag) with "FX3U writes are disabled until verified on a real FX3U", although the write frames exist and pass fake tests. | ASCII mode; a library; allow FX3U writes on fake tests alone | small code, testable, same pattern as SLMP; a wrong write frame on a plant PLC is unacceptable | FX3U-ENET-ADP accepts binary MC 1E on a TCP port set in GX Works2 | the manuals cannot be obtained → task-07 blocked, ask the user; live read fails → fix against the manual, keep the fake in step |
| D-06 | New types. Address grammar for S7: `String`, `LReal`, `DTL` use a byte address (`DBn.DBBx`, `MBx`); the size comes from the type (String `Length`+2, LReal 8, DTL 12). S7 `String`: `Length` 1..218 (one item must fit a 240-byte PDU); read checks byte 0 (declared max) equals `Length`, else that tag is Bad with "STRING length mismatch"; value = the current-length chars. A DTL with out-of-range fields makes that tag Bad, not the cycle. FX `String`: `Length` 1..254 on FX5U and 1..128 on FX3U (one MC 1E read reaches 64 D registers, FX3U-ENET-ADP manual JY997D45801 section 7.4; amended by the user on 2026-11-02), `ceil(Length/2)` D registers, low byte first, stop at the first NUL. `LReal` 8 bytes (S7 big-endian; FX5U four words, low word first). `DTL` → `yyyy-MM-ddTHH:mm:ss.fff`, no offset. JSON values: string, number, string. `Min`/`Max` apply to `LReal` only. Writes: S7 String writes byte 1 (current length) and the chars, never byte 0; FX String pads with NUL to `Length`; text longer than `Length` and a DTL text that does not parse are rejected before I/O. | DTL as UTC; S7 `STRING[254]` | user decisions 3 and 4; a 274-byte item cannot be read in a 240-byte PDU (`src/Edge/Drivers/S7ReadPlanner.cs:19-21`) | S7-1200 `STRING` and `DTL` layouts per TIA help [UNVERIFIED until the live check] | live value differs from TIA → fix the decoder |
| D-07 | Writes: a write runs only when `Edge:Commands:Enabled` **and** the Device's `WritesEnabled` **and** the tag's `Writable`. The global switch keeps its current form: when off, `CommandListener` does not subscribe (`src/Edge/Commands/CommandListener.cs:34-46`); this is not changed. New reasons in `CommandReasons`: `device_writes_disabled` (device `WritesEnabled=false`) and `device_disabled` (device `Enabled=false`). SLMP 3E Device Write (0x1401, subcommand 0x0000 word for D, 0x0001 bit for M/Y) and MC 1E batch write; X and S7 input areas are never writable (start fails for a `Writable` tag there). Writable overlap is checked for every driver: S7 by byte range with the new sizes, Mitsubishi by device word/bit range (String `ceil(Length/2)` words, LReal 4, DInt/Real 2). Every refusal has a negative test that asserts no frame was sent. Command codes are an allow-list: each driver keeps its command codes alone in a static class (`SlmpCommands` = {0x0401, 0x1401}; `Mc1eCommands` = {batch read bit/word, batch write bit/word}); a test reflects over that class and each fake asserts every received command is in that list. | one global switch; a negative grep | approved two-tier rule; read-only by default; a grep misses one-byte and decimal codes | — | any test shows a frame sent for a refused write → stop |
| D-08 | Every code task's Command starts with `dotnet build IndustrialIot.sln -c Release` (warnings are errors in CI) and runs `tests/Edge.Tests`; tasks that change host wiring, the driver contract, the settings file or the shared contract also run `tests/Deployment.Tests`; tasks that change `src/Shared` run `tests/Shared.Tests` and `tests/Cloud.Tests`. An existing test assertion may change only where the task names the changed contract; the receipt lists such changes. Test fixtures that the tests read are committed under `tests/` (never `src/Edge/data/`, which is gitignored, `.gitignore:27`). | Edge tests only | Deployment tests start the Edge host; CI has no ignored files | Docker available for Testcontainers | — |
| D-09 | Shared contract: a telemetry or command value is `true`, `false`, a number, or a JSON string of at most 256 UTF-16 chars. Edge `TagPoller` publishes `string` and `double` values, and a value the contract would reject becomes a Bad sample with no value, so one tag never breaks the store's batch transaction (`src/Edge/Buffering/TelemetryStore.cs:96`); Cloud ingest canonicalises string values as JSON strings (`src/Cloud/Ingestion/TelemetryIngestor.cs:256-261`). | keep strings out of the contract | DTL and String are approved types; JSON text keeps the schema | value columns are `jsonb` (`src/Cloud/Data/CloudDbContext.cs:24,41,55`), so no migration | a stored string reads back differently → fix the ingest |
| D-10 | Read-time evidence: the user runs the current Edge (profile `lab/fx5u`) against the FX5U on 02/11 and records `readMs` and the tag layout in the Review log (no code; an early warning for AC-10). Task-10 adds `src/Edge/lab/measure-read-ms.ps1`: it samples `/api/status` for ≥ 30 distinct cycles per device and prints median `readMs` and `requests`, exit 1 when a median exceeds 500 or a device named in `-Devices` is missing. | measure once by hand | a single sample hides jitter; a scattered FX layout may need one request per tag | — | FX5U median > 500 ms on the early run → raise before task-10, consider gap reads |

## Coverage profile
| ID | Outcome | Change kinds | Material surfaces | Ambiguity/action | Risk/evidence | Required proof |
|---|---|---|---|---|---|---|
| CP-01 | An Edge with a v1 configuration starts and publishes the same TagIds as before; a v2 configuration with several devices starts; invalid v2 fails naming the key; installed Edges with env `Edge__Plc__*` keep starting | add, migrate | Data/schema, Runtime/deploy | none | critical — installed Edges | source (settings/host tests, committed lab v1 fixture) |
| CP-02 | Each device is read in its own loop; one PLC down does not delay another; per-device state, read time and request count are visible | add, refactor | Async/state, Integration | none | elevated | source (fake drivers/servers) + live (two real PLCs) |
| CP-03 | Commands reach the device that owns the tag, and only when all three switches allow it | modify | Async/state, Security | none | critical — PLC writes | source (negative tests, no frame sent) + live (lab register) |
| CP-04 | Users pick a model; defaults and address/type limits follow it | add | Data/schema | none | elevated | source |
| CP-05 | FX3U is read over MC 1E; FX3U writes exist but are refused at start until verified live | add | Integration | frame layout from the manuals | critical — wrong frame on a plant PLC | source (fake enforcing manual limits + golden bytes from the manual) + live read if hardware |
| CP-06 | String, LReal, DTL read and write correctly where the model supports them | add | Data/schema, Time | none | elevated | source + live (S7-1200, FX5U) |
| CP-07 | 100 tags on one PLC are read in ≤ 500 ms per cycle | other:performance | Integration/proof | none | elevated | live (S7-1200, FX5U, median of ≥ 30 cycles) |
| CP-08 | String values travel from the Edge to the Cloud and back in commands | modify | Data/schema, Integration | none | critical — shared contract, Cloud batches | source (Shared, Cloud, Deployment tests) |

## Acceptance criteria
| ID | EARS criterion | Proof |
|---|---|---|
| AC-01 | When the Edge starts with a v1 configuration, it shall publish exactly the TagIds it published before, without changing the file. | task-01 |
| AC-02 | If a v2 configuration is invalid (unknown driver/model, model not of the channel's driver, duplicate device, duplicate TagId ignoring case, duplicate Host:Port, TagId over 64 chars, v1 and v2 keys together without a schema-2 file), the Edge shall fail to start naming the key. | task-01 |
| AC-03 | While one device is unreachable, the Edge shall keep reading every other device at its own interval. | task-02 |
| AC-04 | The Monitor shall show, per device, model, host, state, last good read, last error, last read duration and request count. | task-03 |
| AC-05 | When a command targets a tag, the Edge shall write only through the owning device and only when the global switch, the device's `Enabled` and `WritesEnabled`, and the tag's `Writable` all allow it; otherwise it shall reject with a reason and send no frame. | task-03, task-08 |
| AC-06 | Where a device names a model, the Edge shall apply that model's defaults and reject addresses and types the model does not support. | task-04 |
| AC-07 | When the model is FX3U, the Edge shall read D/M/X/Y over MC 1E in blocks, never build a frame other than batch read or batch write, and refuse to start with FX3U writes enabled. | task-07, task-09 |
| AC-08 | The Edge shall read and write `String`, `LReal` and `DTL` per D-06 on the models that support them and reject them elsewhere at start. | task-06, task-07, task-08 |
| AC-09 | When a write is allowed, the Edge shall write D/M/Y on FX5U; it shall never write X or S7 inputs. | task-08 |
| AC-10 | On the real S7-1200 and FX5U with 100 tags each, the median read time over ≥ 30 cycles shall be ≤ 500 ms and every value shall equal the engineering tool's reading. | task-10 |
| AC-11 | When a tag value is a string, the Edge shall publish it and the Cloud shall store and return it unchanged; a value the contract would reject shall be published as a Bad sample without losing the rest of its batch. | task-05 |

## Tasks
| # | Task | Priority | Criteria | Primary ownership | Dependencies | Status |
|---|---|---|---|---|---|---|
| 01 | Schema v2, validation and in-memory v1 migration | P1 | AC-01, AC-02 | `src/Edge/Settings/EdgeSettingsFile.cs`, `src/Edge/Configuration/` | - | done |
| 02 | A driver and a read loop per device | P1 | AC-03 | `src/Edge/Drivers/PlcDeviceRegistry.cs`, `src/Edge/Polling/TagPoller.cs`, `src/Edge/Monitoring/LiveTagSnapshot.cs` | task-01-schema-v2-migration.md | done |
| 03 | Per-device command routing and Monitor status | P1 | AC-04, AC-05 | `src/Edge/Commands/CommandExecutor.cs`, `src/Edge/Monitoring/`, `src/Edge/EdgeServiceCollectionExtensions.cs` | task-02-device-runtime.md | done |
| 04 | Model catalog with defaults and limits | P1 | AC-06 | `src/Edge/Drivers/PlcModelCatalog.cs` | task-01-schema-v2-migration.md | done |
| 05 | String values in the shared contract | P1 | AC-11 | `src/Shared/Common/ContractJson.cs`, `src/Cloud/Ingestion/TelemetryIngestor.cs` | task-03-commands-and-monitor.md | done |
| 06 | String, LReal and DTL reads on S7-1200 and FX5U | P2 | AC-08 | `src/Edge/Drivers/PlcDataType.cs`, `src/Edge/Drivers/PlcValueConverter.cs`, `src/Edge/Drivers/Models/` | task-04-model-catalog.md, task-05-string-values-contract.md | done |
| 07 | FX3U MC 1E read driver | P1 | AC-07, AC-08 | `src/Edge/Drivers/Mc1e/` | task-02-device-runtime.md, task-03-commands-and-monitor.md, task-04-model-catalog.md, task-06-new-data-types.md | done |
| 08 | Two-tier writes for FX5U and the new types | P1 | AC-05, AC-08, AC-09 | `src/Edge/Drivers/Slmp/`, `src/Edge/Drivers/PlcTagCatalog.cs`, `src/Edge/Drivers/PlcProtocolOptions.cs` | task-03-commands-and-monitor.md, task-06-new-data-types.md | done |
| 09 | MC 1E writes behind the FX3U guard | P1 | AC-07 | `src/Edge/Drivers/Mc1e/` | task-07-fx3u-mc1e-read.md, task-08-two-tier-writes.md | done |
| 10 | Setup docs and live check with read time | P1 | AC-10 | `docs/plc-setup.md`, `src/Edge/lab/` | task-08-two-tier-writes.md, task-09-mc1e-writes.md | blocked |

## Review log
- Round 1 (2026-10-30): three fresh-context reviews (contracts/ownership, runtime/tests, protocol assumptions/sizing);
  15 deduplicated findings R-01..R-15 plus Q-A, Q-B. User: accept all as recommended (R-01 a, R-12 a, R-13 supersede
  `s7-batch-read` task-02, Q-A keep Y with a docs warning, Q-B keep fail-whole-start). Applied: D-01..D-03, D-05..D-10,
  CP-01/02/05/08, AC-02/04/05/07/09/10/11; tasks renumbered 01–10 (contract task 05; writes split into 08 and 09).
- Round 2 (closure, 2026-10-30): R-01..R-15 closed except R-03 (recurred in task-03) and R-04 (partial); 9 new findings
  N-1..N-9 (2 High, 4 Medium, 3 Low). User: accept all. Applied: task-03 owns the host-resolving tests (N-1); task-06
  carries v2 `Length` and owns the model × new-type tests (N-2, N-6); task-02 owns the tester refusal, a union catalog
  and the schema-2 host start (N-3, N-4); task-09 owns the MC 1E read-only reason (N-5); command codes in
  `SlmpCommands`/`Mc1eCommands` (N-7); build/Deployment/`-Devices` in task-04/09/10 (N-8); separate `Mc1eBlockPlanner` (N-9).
  B3 sweep: task-05 now depends on task-03 (both own `TagPollerTests.cs`; task-06 inherits it for `SlmpDriverTests.cs`).
  Paper rounds closed; later findings need runtime evidence. All tasks promoted to pending.
- Early FX5U read time (D-10): pending, user, 02/11.
