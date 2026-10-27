# PLC multi-protocol: Siemens S7-1200/1500 and Mitsubishi FX5U (SLMP) behind one driver contract
Specs-Contract: process-first-ready-v1

## Scope decision (GATE-SCOPE — 2026-10-26)
- Request: the Edge must read PLCs of different vendors and protocols, starting with Siemens S7-1200/S7-1500 and
  Mitsubishi FX5U; later FX3U and Omron. Use the FX5U driver of `vn0043-afchem-wastewater-treatment` as the reference.
- Existing:
  - One driver contract, `IPlcDriver` (`src/Edge/Drivers/IPlcDriver.cs:10-23`), used by `TagPoller` (one read per tag,
    `src/Edge/Polling/TagPoller.cs:77-88`), `CommandExecutor` (`src/Edge/Commands/CommandExecutor.cs:24-25`) and the
    Monitor (`src/Edge/Monitoring/MonitorServer.cs:20-26`).
  - Siemens-only seams: `ResolvedTag.Address` is the S7 `PlcAddress` (DB/M/I/Q, `src/Edge/Drivers/PlcTagCatalog.cs:16-22`,
    `src/Edge/Drivers/Models/PlcAddress.cs:7`), parsed by `PlcAddressParser` (`src/Edge/Drivers/PlcTagCatalog.cs:141`);
    the "inputs are never written" rule tests `PlcArea.Input` (`src/Edge/Drivers/PlcTagCatalog.cs:81,149`); the host
    always registers the S7 driver (`src/Edge/EdgeServiceCollectionExtensions.cs:28`); the CPU type is fixed to S7-1200
    (`src/Edge/Drivers/SiemensS7Driver.cs:229`); the Monitor takes `IOptions<SiemensS7Options>`
    (`src/Edge/Monitoring/MonitorServer.cs:23`); `Edge:Plc:Port` defaults to 102 (`src/Edge/Drivers/SiemensS7Options.cs:14`).
  - Reference FX5U driver (`vn0043-afchem-wastewater-treatment/gateway/AfChem.PlcGateway`): own SLMP 3E binary
    implementation, no external library, read-only, D registers in word units (command 0401, subcommand 0000), one TCP
    connection and one request at a time, block reads of up to 960 words (`Drivers/MitsubishiSlmpDriver.cs`,
    `Drivers/PlcTagCatalog.cs:39,77-89`), a loopback `FakeSlmpServer` for tests. Its spec states the frame layout was
    verified against the fake server only.
  - Lab hardware: an S7-1200 (192.168.3.20); the user has a real FX5U for live checks.
- Minimum change: a protocol switch and protocol-owned addresses behind one contract with a batch read, S7 CPU type
  (1200/1500), a read-only FX5U SLMP driver for D, M, X and Y, docs, and one live check on each real PLC.
- Expansion signals: more than eight touched files across the driver layer, poller, Monitor, tests and docs — split into
  seven tasks below. Not taken: real batch reads for S7 (it keeps one read per tag inside the new contract).
- GATE-REVIEW round 1 (2026-10-26): all 15 findings accepted; installer parameters stay out (documentation only); X-input
  switching on the user's FX5U is unknown, so the live octal check may use X or Y.
- User decisions: **KEEP**; **one PLC per Edge**; FX5U devices **D + M/X/Y** in this wave; FX5U proof on **real
  hardware** in addition to the fake server.

## Out of scope
- Writing to a Mitsubishi PLC (SLMP stays read-only; a `Writable` SLMP tag is rejected at start); several PLCs per Edge.
- FX3U (1E frame / Ethernet module), Omron FINS, Modbus TCP, OPC UA — later waves on the same contract.
- Batched S7 reads (`ReadMultipleVars`), Cloud/MQTT/Monitor UI redesign, installer changes beyond documentation.

## Decisions
| ID | Chosen | Rejected | Why | Load-bearing assumption | Break signal → response |
|---|---|---|---|---|---|
| D-01 | `Edge:Plc:Protocol` = `S7` (default, so every existing configuration keeps working) or `Slmp`; the host registers exactly one driver, its options validator and address syntax for that value; an unknown value fails the start naming the key; `Edge:Commands:Enabled=true` with a protocol other than `S7` fails the start naming both keys | probing the PLC; one options class for all protocols | 1 PLC per Edge (user); explicit is safer than guessing; SLMP never writes | `Edge:Plc` stays one section | — |
| D-02 | Addresses become protocol-owned: `IPlcAddress` with `Canonical`, `IsBit` and an abstract `ReadOnlyReason` (null = writable area; no default implementation); an `IPlcAddressSyntax` from the selected protocol parses the address and checks the data type (today's `PlcValueConverter.EnsureCompatible` S7 rules and texts move into the S7 syntax byte-for-byte); the catalog asks the address for its read-only reason (S7 `Input` keeps the exact text and `input_area_not_writable`; SLMP says "SLMP is read-only"); the writable-overlap check stays S7-specific and throws on mixed address types; the catalog validator receives the syntax; existing entry points stay as thin S7 shims so no existing test line changes (`PlcValueConverter.EnsureCompatible(PlcAddress, …)`, a `PlcTagCatalog(options)` constructor defaulting to the S7 syntax, `SiemensS7Driver`'s single-tag `ReadAsync`); a non-null `ReadOnlyReason` maps to command reason `input_area_not_writable` for S7 | one union address type | each vendor's grammar stays in its driver; S7 behaviour and messages unchanged | — | an existing S7 test changes expectation → the refactor changed behaviour; fix the code, not the test |
| D-03 | Read contract `ReadAsync(IReadOnlyList<ResolvedTag>)` returning one value or one `PlcException` per tag, in order. Rules: after the first `PlcUnavailableException` (incl. `PlcBusyException`) the driver does no more I/O in that call and returns that exception for every remaining tag; `OperationCanceledException` always propagates; the poller keeps today's "Not read: {reason}" text, skips one interval when any result is `PlcUnavailableException`, and keeps today's per-tag handling of any other exception ("Unexpected error; see the Edge log" for that tag only, logged once per cycle) — `PlcReadResult` carries any `Exception`. A production helper reads sequentially with this short-circuit and is used by the S7 driver (lock taken per tag, so writes still interleave) and by the test fake. SLMP groups only touching/overlapping tags of one device into blocks of at most 960 words (never reading gap words); an error end code marks that block's tags with a `PlcAccessDeniedException` naming the end code (hex) and the block range; only for an address/range end code (0xC056 [UNVERIFIED list — SH-081951]) the block is split once to find the bad tags, which are remembered: later cycles read the good tags merged into blocks again and each bad tag alone (≈ 2 × bad tags + 1 requests per cycle, never one request per tag of the block), and a bad tag that reads again rejoins its neighbours; any other end code (busy, timer) gives that block's tags the block error with no fan-out (amended 2026-10-26 by the user, option c, after implementation review); logging stays in the poller (today's once-per-tag-per-cycle warning, a documented limitation), the SLMP driver does not log tag errors itself; writes stay `WriteAsync(IPlcAddress, type, value)` (S7 only; any other address type → `PlcAccessDeniedException` before I/O) | reading every tag after an outage; per-tag SLMP | today's outage behaviour is a contract (≈ one timeout per cycle, not N); one bad SLMP tag must not blank a block | — | a real-driver test sees more than one connect attempt per outage cycle → the short-circuit is broken |
| D-04 | S7 `CpuType` option: `S71200` (default) or `S71500` (S7netplus `CpuType`), Rack/Slot unchanged | auto-detect | explicit configuration, same PUT/GET path | S7-1500 with PUT/GET and non-optimized DBs behaves like S7-1200 for S7netplus [UNVERIFIED — no S7-1500 in the lab] | — |
| D-05 | SLMP driver ported from the reference (3E binary, own station header, one connection, one request at a time, monitoring timer, strict reply parsing); the only frame it can build is Device Read in word units: `BuildDeviceReadWords(SlmpDevice, head, count)` with a closed `SlmpDevice` {D=0xA8, M=0x90, X=0x9C, Y=0x9D} and command 0x0401 / subcommand 0x0000 as private constants; D as Int/Word/DInt/DWord/Real returned as short/ushort/int/uint/float (little-endian, low word first); M/X/Y as `Bool` (bool): device number = decimal for M, octal value for X/Y; word head = number & ~0xF, bit = number & 0xF, bit 0 = lowest byte's lowest bit; addresses limited to the FX5U default device ranges; `Edge:Plc:Port` required for SLMP and port 102 rejected with an "S7 port" hint; `appsettings.json` no longer sets `Port` (S7 keeps 102 as its code default). Device codes, the octal mapping and head alignment are [UNVERIFIED] until checked against the iQ-F SLMP manual (SH-081951) golden examples and the live check | an external SLMP library | small code, no dependency, a fake server for tests | the reference frame layout matches a real FX5U (it was only checked against its fake) | live check fails → fix against the manual and re-run live |
| D-06 | Monitor shows the protocol, host and port from the selected options; rack/slot only for S7 (`monitor.html` hides them otherwise) | S7-specific Monitor | one Monitor for all protocols | — | — |
| D-07 | Every code task's Command starts with `dotnet build IndustrialIot.sln -c Release` and runs `tests/Deployment.Tests` end-to-end tests where the Edge contract is touched; the refactor tasks also prove no pre-existing assertion or test datum was removed or changed (`git diff -U0` of existing test files shows no removed line containing `Assert.`, `InlineData`, `MemberData` or a `{ "` data row) against the recorded baselines Edge 304 / Deployment 32 | Edge.Tests only | Deployment.Tests implements `IPlcDriver` and starts the Edge host; CI builds the whole solution with warnings as errors | — | — |

## Coverage profile
| ID | Outcome | Change kinds | Material surfaces | Ambiguity/action | Risk/evidence | Required proof |
|---|---|---|---|---|---|---|
| CP-01 | Existing S7 configurations behave exactly as before (reads, writes, write guards, messages, outage short-circuit and back-off) under the new contract | modify | Edge driver contract, poller, commands, catalog | none | critical — PLC write path | source (existing Edge and Deployment tests unchanged and green; real-driver outage test) + live (S7-1200 lab read) |
| CP-02 | `Edge:Plc:Protocol` selects the driver; bad values, protocol-specific option errors and commands enabled on a read-only protocol fail the start naming the key | add | Configuration, DI | none | elevated | source (host tests) |
| CP-03 | S7-1500 is selectable by `CpuType` | add | S7 driver | S7-1500 unavailable | elevated | source (driver option test); live S7-1500 [UNVERIFIED] |
| CP-04 | The Edge reads FX5U D, M, X, Y tags over SLMP in blocks, read-only (no frame other than Device Read can be built), with per-tag quality | add | New driver, protocol, poller | X/Y numbering, device codes, alignment | critical — a wrong frame can stop a plant PLC | source (FakeSlmpServer, golden bytes from the manual, negative grep) + live (real FX5U with discriminating values) |
| CP-05 | Operators can configure and reinstall an FX5U Edge and see the protocol on the Monitor | add, modify | Docs, Monitor | none | routine | source (doc checks, Monitor test) |

## Acceptance criteria
| ID | EARS criterion | Proof |
|---|---|---|
| AC-01 | While `Edge:Plc:Protocol` is absent or `S7`, the Edge shall read, write, reject writes and back off after an outage exactly as before the change. | task-01, task-02 commands |
| AC-02 | When `Edge:Plc:Protocol` has an unknown value, the selected protocol's options or a tag address/type is invalid for it, or commands are enabled on a read-only protocol, the Edge shall fail to start naming the key or tag. | task-03 command |
| AC-03 | Where `Edge:Plc:CpuType` is `S71500`, the S7 driver shall connect as an S7-1500. | task-03 command |
| AC-04 | When the protocol is `Slmp`, the Edge shall read D tags as Int/Word/DInt/DWord/Real and M/X/Y tags as Bool in block reads of touching tags, isolate a bad address after an error end code, stop at a lost connection, never build a frame other than Device Read, and reject any `Writable` tag at start. | task-04, task-05 commands |
| AC-05 | The Monitor page shall show the selected protocol, host and port; the docs shall describe the FX5U GX Works3 setup (SLMP connection, Binary, partner IP, Alive Check, IP Filter, one Edge per connection), an FX5U configuration, and copying back the whole `Edge:Plc` section after a reinstall. | task-06 command |
| AC-06 | When run against the real PLCs with the prescribed discriminating values, the Edge shall read every configured S7-1200 and FX5U tag Good in both of its states with values equal to the engineering tool's readings. | task-07 command |

## Tasks
| # | Task | Priority | Criteria | Primary ownership | Dependencies | Status |
|---|---|---|---|---|---|---|
| 01 | Protocol-neutral addresses, S7 unchanged | P1 | AC-01 | `src/Edge/Drivers/Models/`, `src/Edge/Drivers/PlcTagCatalog.cs`, `src/Edge/Drivers/PlcValueConverter.cs` | - | done |
| 02 | Batch read contract with outage short-circuit, S7 unchanged | P1 | AC-01 | `src/Edge/Drivers/IPlcDriver.cs`, `src/Edge/Polling/TagPoller.cs`, `src/Edge/Drivers/SiemensS7Driver.cs` | task-01-protocol-neutral-addresses.md | done |
| 03 | Protocol switch and S7 CPU type | P1 | AC-02, AC-03 | `src/Edge/Drivers/DriverServiceCollectionExtensions.cs`, `src/Edge/Drivers/SiemensS7Options.cs` | task-02-batch-read-contract.md | done |
| 04 | FX5U SLMP driver for D registers | P1 | AC-04 | `src/Edge/Drivers/Slmp/` | task-03-protocol-switch-s7-cpu.md | done |
| 05 | FX5U SLMP bit devices M/X/Y | P1 | AC-04 | `src/Edge/Drivers/Slmp/` | task-04-slmp-word-devices.md | done |
| 06 | Monitor protocol display and FX5U docs | P2 | AC-05 | `src/Edge/Monitoring/`, `docs/plc-setup.md`, `docs/deployment.md` | task-03-protocol-switch-s7-cpu.md, task-05-slmp-bit-devices.md | done |
| 07 | Live check on the real S7-1200 and FX5U | P1 | AC-06 | `src/Edge/Properties/launchSettings.json`, `src/Edge/lab/fx5u/appsettings.json` | task-05-slmp-bit-devices.md, task-06-monitor-and-docs.md | blocked |

## Review log
- Round 1 (2026-10-26, three fresh reviewers: facts/contracts, protocol/safety, failure modes/scope): 30 raw findings →
  15 (R-01 Critical: batch read dropped the outage short-circuit; R-02..R-07 High: batch cancellation/exception rules,
  Deployment.Tests compile break and solution build, SLMP frame builder locked to Device Read, bit-device alignment,
  lab settings overlay/publish/port 102, discriminating live values; R-08..R-15 Medium: SLMP CLR types and end codes,
  block grouping and isolation, read-only reasons and commands guard, task-01 split and missing files, mechanical
  assertion check, Monitor page, operations docs, "port" vs new tests). User accepted all; installer parameters out;
  X switching unknown. Applied: R-01, R-02 → D-03, task-02; R-03, R-12 → D-07, tasks 01-05 Commands; R-04, R-05 →
  D-05, tasks 04-05; R-06 → D-05, task-04, task-07; R-07 → task-07; R-08, R-09 → D-03, D-05, task-04; R-10 → D-01,
  D-02, D-03, tasks 01, 03, 04; R-11 → task-01/task-02 split, D-02; R-13 → D-06, task-06; R-14 → AC-05, task-06,
  task-07; R-15 → task-04.
- Round 2 (2026-10-26, fresh closure reviewer): 11 PASS, R-06/R-08/R-11 partial, R-12 FAIL; new N1 (High: the
  removed-assertion grep would fire on edits task-01 itself required), N2 (grep missed test data rows), N3 (batch rule
  changed today's per-tag unexpected-error behaviour), N4 (task-07 source checks ran in no Command; lab buffer file
  untracked), N5 (command-code grep incomplete), N6 (S7 message texts not guarded by existing tests), plus notes
  (EdgeTestHost overrides for SLMP, recording GX Works3 device ranges, installer host override, reason-code mapping).
  User accepted all. Applied: N1 → D-02 shims, task-01, task-02; N2 → D-07, task-01/02 Commands; N3 → D-03, task-02;
  N4 → task-07; N5 → task-04/05 Commands; N6 → task-01; R-08 → D-03, task-04; notes → task-03/04, task-06, task-07.
  Paper review closed (two rounds); later claims need runtime evidence.
- Implementation (2026-10-26, task-04 review): the literal D-03 rule (re-read alone only when a block newly fails) would
  turn the neighbours Bad from the next cycle, and the implemented per-tag reading had no ceiling. User chose option (c):
  bounded split for address/range end codes only (D-03 amended); per-cycle warnings for Bad tags in the poller stay as a
  documented limitation (also S7, pre-existing).
- Implementation (2026-10-26): task-07 Command used `rm -rf "$TEMP/edge-pub"`, which the harness safety check refuses; user approved
  publishing into a fresh `mktemp -d` directory instead (same check). Live run deferred by the user; task-07 blocked on it.
