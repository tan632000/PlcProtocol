# Wave 1 — Edge PLC driver + shared contracts
Specs-Contract: process-first-ready-v1

## Scope decision (GATE-SCOPE — 2026-10-13, re-opened same day)
- Existing: repo has only `.gitignore`, `AGENTS.md`, `CLAUDE.md`, specs (commit `fec73b1`). No source code.
- Environment: `dotnet --info` → runtime 8.0.29, `No SDKs were found`; Docker + Git present.
- Re-open reason: user supplied target architecture `Edge (on-prem worker) ⇄ MQTT/TLS ⇄ Cloud (API + SignalR)`; the earlier direct HTTP→PLC API (old task-04/05, kept in git history `fec73b1`) is superseded.
- Split (≥ 3 independent subsystems) into sequential waves; **this packet is wave 1 only**:
  1. `edge-plc-driver` (this) — solution skeleton, `src/Shared` contracts, `src/Edge/Drivers`.
  2. `edge-agent` — poller, buffer + local DB, MQTT publisher, `CommandExecutor`.
  3. `cloud-platform` — MQTT ingestion, central DB, REST + JWT, SignalR.
  4. `deployments-ci` — compose files, Mosquitto TLS, Nginx, systemd, CI/CD.
- User decisions: KEEP (4 waves); repo root `PLCProtocol` is the system root (no nested `industrial-iot-system/`); protocol S7comm PUT/GET; contracts include command TTL, `CommandResult`, telemetry `edgeId` + `sequence`; `tests/` folder.

## Prerequisites (operator, before any task leaves `blocked`)
- Install .NET 10 SDK so `dotnet --list-sdks` lists `10.0.x`.

## Target layout produced by this wave
```text
IndustrialIot.sln, global.json, Directory.Build.props, .gitattributes
src/Shared/IndustrialIot.Shared.csproj      Contracts/, Common/
src/Edge/IndustrialIot.Edge.csproj          Program.cs (minimal host), Drivers/, Drivers/Models/
tests/Shared.Tests/, tests/Edge.Tests/
docs/plc-setup.md
```

## Out of scope (this wave)
- Polling, buffering, local DB, MQTT, command execution loop (wave 2); anything in `src/Cloud` (wave 3); Dockerfiles, compose, CI, `deployments/` (wave 4).
- OPC UA, `String`/`DTL` types, live S7-1200 / G2 verification (no hardware — known limitation).

## Decisions
| ID | Chosen | Rejected | Why | Load-bearing assumption | Break signal → response |
|---|---|---|---|---|---|
| D-01 | S7comm PUT/GET via NuGet `S7netplus` 0.20.x | OPC UA; Sharp7 | user choice; managed MIT lib, cross-platform x64/ARM64 | PLC has PUT/GET enabled and DBs non-optimized; G2 server-side PUT/GET `[UNVERIFIED]` | live read returns 0x8104 / access error → OPC UA driver behind `IPlcDriver` (new GATE-SCOPE) |
| D-02 | .NET 10 LTS | .NET 8 (EOL 2026-11-10) | support window | SDK installable on dev + CI | SDK unavailable → stop, ask user |
| D-03 | Commands address PLC data by **`tagId`** resolved through a configured tag catalog; only tags marked `Writable` may be written, within optional `Min`/`Max`; writable tags may not overlap other tags' bytes; catalog default = no writable tags | raw address in commands | Edge is last line of defense even if Cloud is compromised; removes address-spelling bypasses | operators maintain catalog | write rejected for valid tag → fix catalog, never widen default |
| D-04 | `IPlcDriver` interface; `SiemensS7Driver` sole implementation | direct S7netplus use in services | testability with fakes; future OPC UA | — | — |
| D-05 | One connection per PLC, all driver ops serialized by `SemaphoreSlim(1,1)` | connection pool | S7netplus `ConnectAsync`/`Close` run outside its internal queue; S7-1200 has few connection slots | single Edge instance per PLC | throughput too low → revisit (new scope) |
| D-06 | Contracts: System.Text.Json, camelCase, enums as strings, `schemaVersion: 1` (bumped only on breaking change), timestamps ISO-8601 with mandatory `Z` | Protobuf / MessagePack | readable on MQTT tooling; no extra deps | payload sizes small | bandwidth issue → wave 2/3 decision |
| D-07 | A write whose request may have reached the PLC but was not acknowledged is reported as outcome `unknown` and never auto-retried | treat as `unavailable` and retry | double actuation of physical equipment (R-01) | wave 2 reads the tag back before any re-issue | `unknown` rate high in field → investigate network/timeout, not add retries |
| D-08 | Command freshness: reject when `now ≥ expiresAtUtc` or `issuedAtUtc > now + 2 min` | Edge-clock-only TTL | stale or clock-skewed commands executing on equipment (R-07) | Edge and Cloud hosts NTP-synced | frequent `clock_skew` → fix NTP, never widen silently |

## Coverage profile
| ID | Outcome | Change kinds | Material surfaces | Ambiguity/action | Risk/evidence | Required proof |
|---|---|---|---|---|---|---|
| CP-01 | Address strings parse to typed S7 addresses or are rejected | add | Data/schema (address grammar) | none | elevated — config contract | source |
| CP-02 | Edge⇄Cloud message contracts serialize/deserialize deterministically, validate, and expose freshness | add | Data/schema (wire format, versioning, dedupe key), Time (TTL, clock skew, `Z` timestamps) | none | critical — command freshness gates actuation | source |
| CP-03 | Typed values convert to S7 bytes; tag catalog decides writability (bounds, overlap) before any I/O | add | Data (ranges, integer-token rule), Security (catalog, overlap) | none | critical — physical actuation | source |
| CP-04 | Driver reads/writes S7-1200 with serialized connection, bounded timeout, reconnect, and classified failures incl. write-outcome-unknown | add, integrate | Integration (S7netplus, TCP 102), Async/state (lock, cancellation, discard) | none | critical — write outcome classification | source via fake S7 server (live = limitation) |

## Acceptance criteria
| ID | EARS criterion | Proof |
|---|---|---|
| AC-01 | When an address matching the grammar in task-01 is parsed, the system shall return area, DB number, byte offset, bit offset, and size; otherwise it shall raise `PlcAddressException`. | `dotnet test tests/Edge.Tests --filter FullyQualifiedName~PlcAddressParserTests` |
| AC-02 | When a `TelemetryPayload`, `DeviceCommand`, or `CommandResult` is serialized and deserialized, the system shall produce an equal object and the JSON field names defined in task-02; if a message is malformed, it shall raise `ContractValidationException`. | `dotnet test tests/Shared.Tests` |
| AC-03 | If a `DeviceCommand` is evaluated at or after `expiresAtUtc`, the system shall report `Expired`; if `issuedAtUtc` is more than 2 min ahead of the Edge clock, it shall report `ClockSkew`. | `dotnet test tests/Shared.Tests` |
| AC-04 | If a value is outside its `PlcDataType` range, is not an integer token for an integer type, or mismatches the address size, the system shall reject it before any PLC I/O. | `dotnet test tests/Edge.Tests --filter "FullyQualifiedName~PlcValueConverterTests\|FullyQualifiedName~PlcTagCatalogTests"` |
| AC-05 | If a write targets a tag that is unknown, in area `I`, not `Writable`, has an invalid value, or is outside `Min`/`Max`, the tag catalog shall deny it with the matching `CommandReasons` code before any PLC I/O; if a writable tag overlaps another tag, catalog construction shall fail. | same as AC-04 |
| AC-06 | If the PLC is unreachable, the driver shall fail within `TimeoutMs + 1000` ms with `PlcUnavailableException` and reconnect on the next call. | `dotnet test tests/Edge.Tests --filter FullyQualifiedName~SiemensS7DriverTests` |
| AC-07 | If a write request was sent but not acknowledged, the driver shall raise `PlcWriteOutcomeUnknownException`, never `PlcUnavailableException`. | same as AC-06 |
| AC-08 | If the PLC returns an error response, the driver shall raise `PlcAccessDeniedException`; if the lock wait times out, it shall raise `PlcBusyException` without closing the active connection. | same as AC-06 |

## Tasks
| # | Task | Priority | Criteria | Primary ownership | Dependencies | Status |
|---|---|---|---|---|---|---|
| 01 | Solution skeleton + S7 address parser | P1 | AC-01 | root build files, all 4 `*.csproj`, `src/Edge/Program.cs`, `src/Edge/Drivers/PlcException.cs`, `src/Edge/Drivers/Models/PlcA*.cs`, `tests/Edge.Tests/PlcAddressParserTests.cs` | - | done |
| 02 | Shared message contracts | P1 | AC-02, AC-03 | `src/Shared/{Contracts,Common}/**`, `tests/Shared.Tests/**` | task-01 | blocked |
| 03 | Value conversion, `IPlcDriver`, tag catalog | P1 | AC-04, AC-05 | `src/Edge/Drivers/{PlcDataType,PlcValueConverter,IPlcDriver,PlcExceptions,PlcTagCatalog}.cs`, `Drivers/Models/PlcTagDefinition.cs`, matching tests | task-01, task-02 | blocked |
| 04 | `SiemensS7Driver` + PLC setup doc | P1 | AC-06, AC-07, AC-08 | `src/Edge/Drivers/{SiemensS7Options,SiemensS7Driver,DriverServiceCollectionExtensions}.cs`, `tests/Edge.Tests/{SiemensS7DriverTests.cs,Fakes/}`, `docs/plc-setup.md` | task-03 | blocked |

## Review log
- Round 1 (paper): 3 fresh reviewers (2 on predecessor `plc-s7-base-core`, 1 on new contracts/catalog), 22 raw findings → 15 after dedupe; 4 API/HTTP-only findings (fallback auth policy, `/health/ready` I/O, HTTPS/audit identity, WebApplicationFactory config timing) deferred to wave 3 `cloud-platform`.
- GATE-REVIEW 2026-10-13: user accepted all R-01…R-15; R-08 accepted (optional Min/Max); R-07 maxSkew = 2 min fixed; R-06 integer types reject `1.0`.
- Applied: R-01 → task-04 table + D-07 + AC-07; R-02, R-08 → task-03 catalog; R-03, R-04, R-05, R-14 → task-04; R-06, R-07, R-09, R-10, R-11, R-12 → task-02 (+ D-08, AC-03); R-07 NTP → task-04 doc; R-13 → task-01 grammar; R-15 kept in D-05. Task-03 now depends on task-02 (shared `CommandReasons`).
- Closure pass (fresh reviewer, 2026-10-13):

| ID | Decision | Original counterexample | Repaired at | Proved at | Replay | Closure |
|---|---|---|---|---|---|---|
| R-01 | accepted | late ACK → retry-safe unavailable → double actuation | task-04 classification table | probe `WriteNoAck_OutcomeUnknown` + D-07 | defeated | PASS |
| R-02 | accepted | writable DBW0 overlaps read-only DBX0.3 | task-03 catalog overlap rule | probe `Overlap_WritableTag_Throws` | defeated | PASS |
| R-03 | accepted | plain `Exception`/`InvalidDataException` unclassified | task-04 classification table | probe `PlcErrorResponses_AccessDenied_ReadAndWrite` (sweep added write path + reconnect) | defeated | PASS |
| R-04 | accepted | lock-wait timeout closes holder's connection | task-04 lock rules | probe `LockWaitTimeout_DoesNotDropConnection` | defeated | PASS |
| R-05 | accepted | cancel mid-read leaves stale response | task-04 table (token-state rule) | probe `CallerCancelMidRead_NextCallUsesNewSocket` | defeated | PASS |
| R-06 | accepted | `object` value breaks equality; `1.0` ambiguity | task-02 Values, task-03 integer-token rule | probes `RoundTrip_Equal`, `Normalize_IntegerToken_Rule`; NaN→null producer rule proven in wave 2 | defeated | PASS |
| R-07 | accepted | slow Edge clock runs stale command; no-offset timestamp | task-02 freshness + timestamp rule, D-08 | probes `Boundaries`, `Invalid_ThrowsValidation` | defeated | PASS |
| R-08 | accepted | Int tag accepts 32767 beyond physical limit | task-03 Min/Max | probe `EvaluateWrite_ReasonCodes` | defeated | PASS |
| R-09 | accepted | sequence reset drops telemetry | task-02 `streamId` | probes `FieldNames_MatchSchema`, `Invalid_ThrowsValidation` (non-GUID) | defeated | PASS |
| R-10 | accepted | free-text reasons drift; no `edge_mismatch` | task-02 `CommandReasons` | probe `All_ExactSetUniqueSnakeCase` | defeated | PASS |
| R-11 | accepted | v1 rejects all v2; `JsonException` leaks | task-02 Versioning + `ContractJson` | probe `Invalid_ThrowsValidation` | defeated | PASS |
| R-12 | accepted | task-02 cannot add FakeTimeProvider | task-02 Ownership | probe `Boundaries` buildable | defeated | PASS |
| R-13 | accepted | Unicode digits / overflow | task-01 grammar | probe `Parse_InvalidForms_Throws` | defeated | PASS |
| R-14 | accepted | socket-count oracle flaky | task-04 `MaxObservedConcurrency` | probe `ConcurrentCalls_AreSerialized` | defeated | PASS |
| R-15 | accepted | wrong thread-safety rationale | D-05 | doc-only | n/a | PASS |

- Sweep (B3): files reread 5 / deltas: `PlcException` base moved to task-01, `EnsureCompatible` added (task-03/04), test `ProjectReference`s (task-01), write-path AccessDenied + `SizeMismatch_NoIo` probes (task-04), `streamId`/`reason` invalid cases (task-02; `reason` validated by snake_case pattern to stay forward compatible with R-11), plan ownership table aligned / stale references fixed 0 remaining / conflicts left 0.
- Remaining blocker for `pending`: .NET 10 SDK not installed (Prerequisites). Round count: 1 of 2.
