# S7 batch reads: many tags per request instead of one request per tag
Specs-Contract: process-first-ready-v1

## Scope decision (GATE-SCOPE — 2026-10-27)
- Request: read Siemens S7 tags faster than one request per tag.
- Existing:
  - The S7 batch read loops over a single-tag read, one `ReadBytesAsync` request per tag
    (`src/Edge/Drivers/SiemensS7Driver.cs:41-42`, request at `:74-75`), measured at ~17.5 ms/tag → ~55 tags per 1 s cycle
    (`specs/telemetry-delivery-ack/plan.md:13`).
  - The batch contract and its rules already exist (`IPlcDriver.ReadAsync(IReadOnlyList<ResolvedTag>)`,
    `src/Edge/Drivers/SequentialBatchReader.cs`; plan D-03 of `specs/plc-multi-protocol`): stop all I/O after the first
    `PlcUnavailableException`, per-tag errors stay per tag, cancellation propagates; the poller logic is unchanged.
  - S7netplus 0.20.0 `ReadMultipleVarsAsync` (verified against the v0.20.0 source by the round-1 reviewer:
    `PlcAsynchronous.cs`, `PLCHelpers.cs`, `PLC.cs`): checks request `19 + 12·n ≤ MaxPDUSize` and response
    `14 + 4·n + Σ even(len) ≤ MaxPDUSize` and throws a plain `Exception` otherwise (no splitting); wraps non-cancel errors
    in `PlcException(ReadData, inner)`; does not say which item a PLC refused; parses item lengths from the request types,
    not from the reply (a short reply gives a null/short value silently); `VarType.Byte` with Count 1 returns `byte`.
    `MaxPDUSize` defaults to 240 and is set from the setup reply.
  - `FakeS7Server` answers exactly one item per job from a flat byte array, ignoring the address
    (`tests/Edge.Tests/Fakes/FakeS7Server.cs:203-204,210-235`); its setup reply offers PDU 960 (`:44`).
  - `PollCycle` lives in `src/Edge/Monitoring/LiveTagSnapshot.cs:10` (built at `src/Edge/Polling/TagPoller.cs:124`); the
    Monitor status has no read-duration figure (`src/Edge/Monitoring/MonitorServer.cs:180-194`).
  - The lab S7-1200 (192.168.3.20) with 21 configured tags is available; at PDU 240 they fit in 2 requests (18 + 3).
- Minimum change: multi-variable reads packed to the negotiated PDU, bounded isolation of refused items, all current
  contracts kept, an address-aware multi-item fake, and a live before/after measurement with a read-duration figure.
- Expansion signals: none beyond ~7 files.
- User decisions: **KEEP**; lab S7-1200 available; target = clearly faster (no fixed number; measured and reported).
- GATE-REVIEW round 1 (2026-10-27): all 14 findings accepted; on a complete PLC error reply the multi-variable path keeps
  the connection (the single-tag path and its tests stay as they are).

## Out of scope
- Contiguous byte-block merging (the EXPAND option); write path changes; SLMP; poll interval changes.

## Decisions
| ID | Chosen | Rejected | Why | Load-bearing assumption | Break signal → response |
|---|---|---|---|---|---|
| D-01 | Each request is planned inside the driver lock right after `ConnectAsync`, greedily from the remaining tags, using the current `plc.MaxPDUSize` and S7netplus's own limits: request `19 + 12·n ≤ PDU`, response `14 + 4·n + Σ even(len) ≤ PDU`, and at most 20 items (cap as in Snap7 `MaxVars`); the lock is released between requests so writes interleave; a limit exception from S7netplus is a planner bug and surfaces as unexpected, not as an unavailable PLC | planning before connect; contiguous byte blocks; one request per tag | the PDU is known only after the handshake and can change on reconnect; one byte off would be a permanent self-inflicted outage | S7netplus 0.20.0 formulas as cited above | a "Too many vars"/"Too much data" exception → fix the formula against the library |
| D-02 | Items are `VarType.Byte` with Count = `max(SizeBytes, 1)` only (a `byte` result normalised to `byte[1]`), converted with today's `PlcValueConverter`/bit logic, so values are identical; a null or wrongly sized item value is a protocol error (connection discarded, the request's and later tags unavailable) | `Word`/`DWord`/`Real` DataItems | raw bytes keep the existing conversions; a short reply must not pass silently | — | — |
| D-02b | Per-tag checks (`RequireS7`, `EnsureCompatible`) run before planning; an invalid tag gets its exception in its own result slot and is never sent (this pre-I/O error wins over a later outage's shared exception) | validating inside requests | today's per-tag, no-I/O behaviour (`SizeMismatch_NoIo`) | — | — |
| D-02c | Errors: a connection failure → `PlcUnavailableException` for the request's tags and every later tag, no further request (text names the first tag of the failed request: "Reading {first address} failed: …"); a complete PLC error reply for a request (refused item, or the header-only 0x8104 reply of PUT/GET off — the `IndexOutOfRangeException` S7netplus raises on it counts only when thrown inside `ReadMultipleVarsAsync`, checked by stack frame like `IsThrownBySingleWriteRequest`; a truncated frame elsewhere is a connection failure) keeps the connection and re-reads that request's tags one by one through `SequentialBatchReader` rules using an internal solo read that keeps the connection on a complete PLC error reply (`IsPlcErrorResponse`) and discards it otherwise — the public single-tag `ReadAsync` keeps today's discard, giving today's per-tag texts (access-denied with the TIA hint) and the outage short-circuit (worst case before short-circuit: 2 × `TimeoutMs`); cancellation propagates | treating a refused item as an outage; failing the whole request | PUT/GET off must keep today's access-denied hint; one wrong address must not blank others | — | an existing S7 or poller test changes expectation → fix the code, not the test |
| D-02a | A tag whose solo read raises `PlcAccessDeniedException` is remembered (key: canonical address); later cycles read remembered tags alone and pack the rest; an entry is removed on its first successful solo read and pruned when the tag is no longer in the batch; nothing else is remembered; the set is read and written only under the driver lock; with PUT/GET off every tag ends up remembered (as many requests as today, no reconnect) and packs again once reads succeed | remembering on any error | bounded: one extra request per bad tag, no reconnect (connection kept) | — | — |
| D-03 | `FakeS7Server` becomes address-aware for read jobs: parses the item count and each 12-byte item (area, DB, byte/bit address, count), answers from per-(area, DB) memory seeded with distinct bytes, writes a fill byte after odd-length items except the last, supports per-item return codes, a configurable PDU in the setup reply, PDU change on reconnect, a per-job script (go silent or close the socket after job k), and a job log with item counts; one hand-written reply frame (S7comm layout, odd item followed by Word/Real) checks the fake against itself | a flat-bytes fake | item mix-ups and fill-byte bugs must fail tests | — | — |
| D-04 | `PollCycle` records the batch-read duration measured with the injected `TimeProvider` timestamps (`GetTimestamp`/`GetElapsedTime`); the Monitor status shows `readMs` of the last cycle (null before the first) and the page renders it; the driver logs the negotiated PDU and the job count per batch; the `readMs` change is its own commit so the baseline can cherry-pick it | `Stopwatch` | deterministic tests can assert an exact value | — | — |

## Coverage profile
| ID | Outcome | Change kinds | Material surfaces | Ambiguity/action | Risk/evidence | Required proof |
|---|---|---|---|---|---|---|
| CP-01 | S7 batches use far fewer requests, with values, errors (incl. PUT/GET off), outage behaviour and write interleaving unchanged | modify | S7 driver, PLC I/O | none | elevated — runtime PLC I/O | source (real driver vs address-aware multi-item fake; all existing tests unchanged since the base commit) |
| CP-02 | The improvement is measured on the lab S7-1200 against a measured baseline and values match TIA Portal | add | Monitor, live PLC | none | elevated | live (lab S7-1200, baseline and after `readMs` medians) |

## Acceptance criteria
| ID | EARS criterion | Proof |
|---|---|---|
| AC-01 | When the S7 driver reads a batch, it shall send no more requests than the negotiated PDU and the 20-item cap require, and shall return the values and per-tag errors a one-request-per-tag read would. | task-01 command |
| AC-02 | If the PLC refuses an item or answers with a header-only error (PUT/GET off), the S7 driver shall report the affected tags with today's access-denied text while unaffected tags keep their values, keep the connection, and cost about one extra request per remembered bad tag in later cycles. | task-01 command |
| AC-03 | When the connection fails during a batch, the S7 driver shall send no further request in that batch. | task-01 command |
| AC-04 | The Monitor shall show the last cycle's read duration; on the lab S7-1200 with the 21 configured tags the median after the change shall be clearly lower than the measured baseline, with values equal to TIA Portal. | task-02 command |

## Tasks
| # | Task | Priority | Criteria | Primary ownership | Dependencies | Status |
|---|---|---|---|---|---|---|
| 01 | Multi-variable S7 batch reads with bounded isolation | P1 | AC-01, AC-02, AC-03 | `src/Edge/Drivers/SiemensS7Driver.cs`, `src/Edge/Drivers/S7ReadPlanner.cs`, `tests/Edge.Tests/Fakes/FakeS7Server.cs` | - | done |
| 02 | Read duration on the Monitor and live measurement on the lab S7-1200 | P1 | AC-04 | `src/Edge/Monitoring/LiveTagSnapshot.cs`, `src/Edge/Polling/TagPoller.cs`, `src/Edge/Monitoring/MonitorServer.cs` | task-01-s7-multi-var-reads.md | blocked |

## Review log
- Round 1 (2026-10-27, two fresh reviewers; one read the S7netplus v0.20.0 source): 20 raw findings → 14 (R-01 Critical:
  PUT/GET-off header reply became a whole-batch outage; R-02..R-06 High: library PDU formulas, address-unaware fake,
  DataItem types and silent short values, planning after connect, per-tag pre-I/O checks; R-07..R-14 Medium: 20-item cap,
  reconnect per bad tag, remembering rules, isolation via the batch rules, request-level texts, assertion guard against
  the base commit, task-02 ownership and TimeProvider timing, a mandatory measured baseline). User accepted all; R-08:
  keep the connection on a complete PLC error reply in the multi-variable path. Applied: D-01, D-02, D-02a, D-02b,
  D-02c, D-03, D-04, AC-01..AC-04, tasks 01-02; citation `SiemensS7Driver.cs:66-67` corrected to `:74-75`.
- Round 2 (2026-10-27, fresh closure reviewer): 12 PASS; R-08 FAIL (solo re-reads through the public single-tag read
  would discard the connection), R-14 partial; new N-01..N-06 (internal non-discarding solo read, truncated-frame vs
  0x8104 classifier, per-job drop script in the fake, separate `readMs` commit, job-count log, pre-I/O error precedence
  and lock for the remembered set) and the residual `[Fact]`-deletion gap. User accepted all; applied to D-02a, D-02b,
  D-02c, D-03, D-04 and both tasks. Paper review closed (two rounds); later claims need runtime evidence.
