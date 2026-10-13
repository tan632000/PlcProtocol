# Task 04 — HTTP API reads and writes PLC tags behind API key and write allow-list

Status: blocked

## Outcome
`PlcProtocol.Api` (ASP.NET Core Minimal API) exposes versioned read/write endpoints and health probes; every rejection path returns a defined status with RFC 7807 ProblemDetails, and every write attempt is audit-logged.

## Scope
- In: endpoints, API-key auth, `WritePolicy` enforcement, exception → HTTP mapping, config binding, startup fail-closed checks, integration tests with a fake `IPlcClient`.
- Out: batch read/write, Swagger UI in Production, rate limiting, user/role management, HTTPS termination (done by reverse proxy / platform).

## Coverage
- CP-03, CP-04

## Ownership
- Create: `src/PlcProtocol.Api/PlcProtocol.Api.csproj`, `src/PlcProtocol.Api/Program.cs`, `src/PlcProtocol.Api/Endpoints/PlcEndpoints.cs`, `src/PlcProtocol.Api/Security/ApiKeyAuthenticationHandler.cs`, `src/PlcProtocol.Api/appsettings.json`
- Create: `tests/PlcProtocol.Api.Tests/PlcProtocol.Api.Tests.csproj`, `tests/PlcProtocol.Api.Tests/{PlcApiTests.cs,FakePlcClient.cs}`
- Modify: `PlcProtocol.sln`
- Read: `src/PlcProtocol.Core/**`, `src/PlcProtocol.S7/S7ServiceCollectionExtensions.cs`

## Contracts
**Configuration** (env override uses `__`, e.g. `Plc__Host`, `Api__Keys__0`)
```json
{
  "Plc":    { "Host": "", "Port": 102, "Rack": 0, "Slot": 1, "TimeoutMs": 3000 },
  "Safety": { "ReadOnly": true, "WritableTags": [] },
  "Api":    { "Keys": [] }
}
```
- Startup fails (process exits non-zero) when `Api:Keys` is empty or any key is shorter than 32 chars, when `Plc:Host` is empty, or when `WritePolicy` rejects an entry.
- Keys are compared with `CryptographicOperations.FixedTimeEquals`; keys never appear in logs. Audit identity = `key#<index>`.

**Endpoints**
| Method + route | Auth | Request | Success |
|---|---|---|---|
| `GET /health/live` | none | — | 200 `{"status":"live"}` |
| `GET /health/ready` | none | — | 200 if `IsReachableAsync` true, else 503 |
| `GET /api/v1/tags?address=DB1.DBW2&type=Int` | `X-Api-Key` | query | 200 `{"address":"DB1.DBW2","type":"Int","value":123,"timestampUtc":"2026-10-13T08:00:00Z"}` |
| `POST /api/v1/tags/write` | `X-Api-Key` | `{"address":"DB1.DBW2","type":"Int","value":123}` | 200 same shape as read, echoing the written value |

**Error mapping** (all `application/problem+json`, `title` = stable code)
| Condition | Status | `title` |
|---|---|---|
| missing/invalid `X-Api-Key` | 401 | `unauthorized` |
| bad address / unknown type / bad value / size mismatch / missing field | 400 | `invalid_request` |
| write to area `I` | 400 | `input_area_not_writable` |
| `ReadOnlyMode` | 403 | `read_only_mode` |
| `NotInAllowList` | 403 | `tag_not_writable` |
| `PlcAccessDeniedException` | 502 | `plc_access_denied` |
| `PlcUnavailableException` | 503 | `plc_unavailable` |
| unhandled | 500 | `internal_error` (no stack trace in body) |

- Policy is evaluated **before** any `IPlcClient` call; denied writes perform zero PLC I/O.
- Every write attempt logs one `Information` (allowed) or `Warning` (denied/failed) entry with `keyId`, canonical address, type, value, outcome.

## Steps
1. Create project; bind options; register `AddS7PlcClient`, `WritePolicy` singleton, auth handler, ProblemDetails.
2. Implement endpoints + exception mapping.
3. Tests with `WebApplicationFactory<Program>` replacing `IPlcClient` by `FakePlcClient` (records calls, can be told to throw).

## Acceptance
- AC-03: with `ReadOnly=true` → 403 `read_only_mode`; with `ReadOnly=false` and unlisted tag → 403 `tag_not_writable`; fake records 0 writes in both.
- AC-04: write `I0.0` Bool → 400 `input_area_not_writable`.
- AC-06: no/invalid key on `/api/v1/tags` → 401; `/health/live` without key → 200.
- AC-07: factory with empty `Api:Keys` fails to start (exception on host build/start).

## Dependencies
- task-02-values-client-contract-write-policy.md
- task-03-s7netplus-adapter.md

## Verification Plan
- Command: `dotnet test tests/PlcProtocol.Api.Tests`
- Named probe: `PlcApiTests.Write_ReadOnly_Returns403_NoIo`, `PlcApiTests.Write_NotAllowListed_Returns403_NoIo`, `PlcApiTests.Write_AllowListed_Returns200_OneIo`, `PlcApiTests.Write_InputArea_Returns400`, `PlcApiTests.MissingKey_Returns401`, `PlcApiTests.HealthLive_NoKey_Returns200`, `PlcApiTests.EmptyKeys_FailsStartup`, `PlcApiTests.PlcUnavailable_Returns503`, `PlcApiTests.Read_InvalidAddress_Returns400`
- Reachability: source — .NET 10 SDK; in-memory test server.
- Oracle: exit 0, 0 failed, nine named probes pass.
- Counterexample: calling `IPlcClient.WriteAsync` before policy evaluation must fail the two `_NoIo` probes.
- Artifacts: ephemeral.

## Failure Protocol
On a failed Step or Verification Plan run: stop; do not widen scope, change the Command, or weaken a test; record observed versus expected; repair only the cited cause; after three failed rounds, stop and ask the user.

## Receipt
<!-- Fill only after execution; see canonical form in templates. -->
