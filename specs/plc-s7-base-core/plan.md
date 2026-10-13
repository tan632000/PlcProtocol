# PLC S7-1200 base core (.NET, cloud-deployable)
Specs-Contract: process-first-ready-v1

## Scope decision (GATE-SCOPE — 2026-10-13)
- Existing: greenfield; repo only has `CLAUDE.md`, `AGENTS.md`, `.gitignore` (directory listing 2026-10-13). Not a git repo yet.
- Environment: `dotnet --info` shows runtime 8.0.29 only, `No SDKs were found`; Docker and Git CLIs present.
- Minimum change: one .NET 10 solution with `Core` (address + types + write policy), `S7` (S7netplus adapter), `Api` (Minimal API with API key + write allow-list), Linux Dockerfile, unit/integration tests against a fake PLC.
- Expansion signals: ~25 new files across 4 projects (> 8 files), but one deliverable subsystem; kept as one packet.
- User decision: KEEP — protocol S7comm PUT/GET; write safety = API key + allow-list; no physical PLC available.

## Prerequisites (operator, before any task leaves `blocked`)
- Install .NET 10 SDK (LTS) so `dotnet --list-sdks` lists `10.0.x`.
- Docker Desktop daemon running (needed only by task-05).

## Out of scope
- OPC UA driver, multiple PLCs per instance, polling/subscription push (SignalR/MQTT), `String`/`DTL`/`S5Time` types.
- Network topology to the plant (VPN / edge placement) — documented only, not built.
- Live verification against a real S7-1200 / S7-1200 G2 (no hardware) — recorded as known limitation.
- Writing to input area `I` (rejected by design, see AC-04).

## Decisions
| ID | Chosen | Rejected | Why | Load-bearing assumption | Break signal → response |
|---|---|---|---|---|---|
| D-01 | S7comm PUT/GET via NuGet `S7netplus` | OPC UA; Sharp7/Snap7 | user choice; managed MIT lib, cross-platform | PLC has PUT/GET enabled and DBs use non-optimized access `[UNVERIFIED for S7-1200 G2]` | live read returns access error → add OPC UA driver behind `IPlcClient` (new GATE-SCOPE) |
| D-02 | .NET 10 LTS | .NET 8 (EOL 2026-11-10) | support window | .NET 10 SDK installable on dev + CI | SDK unavailable → stop, ask user |
| D-03 | Fail-closed writes: `Safety:ReadOnly` defaults `true`; write needs exact (address,type) in `Safety:WritableTags` | key-only auth | physical-equipment safety | operators maintain allow-list | write rejected for valid tag → fix config, never widen default |
| D-04 | `IPlcClient` interface in Core; one S7 implementation | direct S7netplus use in API | testability with fake; future OPC UA | — | — |
| D-05 | Serialize all PLC calls on one connection with `SemaphoreSlim(1,1)` | connection pool | S7netplus `Plc` is not thread-safe `[UNVERIFIED]`; S7-1200 has few connection slots | single instance per PLC | throughput too low → revisit (new scope) |

## Coverage profile
| ID | Outcome | Change kinds | Material surfaces | Ambiguity/action | Risk/evidence | Required proof |
|---|---|---|---|---|---|---|
| CP-01 | Address strings parse to typed S7 addresses or are rejected | add | Data/schema (address grammar) | none | elevated — grammar is public API contract | source |
| CP-02 | Read typed value from PLC | add, integrate | Integration (S7netplus, TCP 102), Async/state (lock, reconnect, timeout) | none | elevated — external integration | source (live = limitation) |
| CP-03 | Write typed value only when allowed | add | Data (value range), Security (allow-list, ReadOnly) | none | critical — physical actuation | source |
| CP-04 | HTTP API contract with API-key auth and ProblemDetails errors | add | API/CLI | none | critical — auth boundary | source |
| CP-05 | Runs as Linux container and Windows self-contained publish | add | Runtime/deploy | none | elevated — runtime | installed |

## Acceptance criteria
| ID | EARS criterion | Proof |
|---|---|---|
| AC-01 | When an address matching the grammar in task-01 is parsed, the system shall return area, DB number, byte offset, bit offset, and size; otherwise it shall raise `PlcAddressException`. | `dotnet test tests/PlcProtocol.Core.Tests` |
| AC-02 | If a value is outside the range of the requested `PlcDataType` or the type does not match the address size, the system shall reject it before any PLC I/O. | `dotnet test tests/PlcProtocol.Core.Tests` |
| AC-03 | While `Safety:ReadOnly` is `true`, or when (address,type) is not in `Safety:WritableTags`, the system shall refuse the write with HTTP 403 and perform no PLC I/O. | `dotnet test tests/PlcProtocol.Api.Tests` |
| AC-04 | If a write targets area `I`, the system shall refuse it with HTTP 400. | `dotnet test tests/PlcProtocol.Api.Tests` |
| AC-05 | If the PLC is unreachable, the system shall fail within `Plc:TimeoutMs` + 1000 ms with `PlcUnavailableException` (HTTP 503) and reconnect on the next call. | `dotnet test tests/PlcProtocol.S7.Tests` |
| AC-06 | When a request lacks a valid `X-Api-Key`, `/api/v1/*` shall return 401; `/health/live` shall not require a key. | `dotnet test tests/PlcProtocol.Api.Tests` |
| AC-07 | If no API key is configured, the application shall refuse to start. | `dotnet test tests/PlcProtocol.Api.Tests` |
| AC-08 | When the container image is run, `GET /health/live` shall return 200. | task-05 command |

## Tasks
| # | Task | Priority | Criteria | Primary ownership | Dependencies | Status |
|---|---|---|---|---|---|---|
| 01 | Solution skeleton + S7 address parser | P1 | AC-01 | `src/PlcProtocol.Core/Addressing/*` | - | blocked |
| 02 | Value conversion, `IPlcClient`, write policy | P1 | AC-02, AC-03 (policy unit) | `src/PlcProtocol.Core/*` (non-Addressing) | task-01 | blocked |
| 03 | S7netplus adapter with lock, timeout, reconnect | P1 | AC-05 | `src/PlcProtocol.S7/*` | task-02 | blocked |
| 04 | Minimal API: read/write/health, API key, allow-list | P1 | AC-03, AC-04, AC-06, AC-07 | `src/PlcProtocol.Api/*` | task-02, task-03 | blocked |
| 05 | Docker image + deployment and PLC-setup docs | P2 | AC-08 | `Dockerfile`, `docs/*` | task-04 | blocked |

## Review log
- Round 1: pending.
