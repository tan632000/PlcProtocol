# Task 04 — Viewers read latest values and bounded history

Status: done

## Outcome
Viewers get the latest value of every tag of an edge and the history of one tag over a bounded time range.

## Scope
- In: two read endpoints, input validation, limits, problem responses.
- Out: aggregation/downsampling (excluded), commands (task-05).

## Coverage
- CP-04

## Ownership
- Create: `src/Cloud/Api/TelemetryEndpoints.cs` (endpoint module), `tests/Cloud.Tests/TelemetryApiTests.cs`
- Modify: `src/Cloud/CloudServiceCollectionExtensions.cs` (register the module)
- Read: `src/Cloud/Data/*`, `src/Cloud/EndpointModules.cs`, `src/Cloud/Security/Policies.cs`, `src/Cloud/Api/ProblemMapping.cs`

## Contracts
| Route (Viewer) | Query | 200 body |
|---|---|---|
| `GET /api/v1/edges/{edgeId}/tags/latest` | — | `[{"tagId","value","quality","timestampUtc"}]` ordered by `tagId` |
| `GET /api/v1/edges/{edgeId}/tags/{tagId}/history` | `from`, `to` (ISO-8601 with `Z`, required), `limit` (1..10,000, default 1,000) | `{"items":[{"value","quality","timestampUtc","sequence"}],"truncated":bool}` ordered by `timestampUtc` ascending |
- `edgeId` not allow-listed → 404 `edge_not_found`. Invalid `tagId` format, missing/invalid `from`/`to`, `from ≥ to`, range > 31 days, `limit` outside range → 400 `invalid_request` naming the parameter.
- `value` is the stored JSON (`true`/`false`/number/`null`); `truncated` = more rows exist in the range.
- Queries use the (`edge_id`,`tag_id`,`timestamp_utc`) index; no unbounded scans.

## Steps
1. Endpoints + validation.
2. Tests seeding the database directly through `CloudDbContext`.

## Acceptance
- AC-05: latest returns one row per tag; history returns ascending rows in range, honours `limit` with `truncated=true`; unknown edge 404; 32-day range, `from ≥ to`, `limit=0`, missing `Z` → 400; Viewer token works, no token 401.

## Dependencies
- task-03-jwt-auth-edges.md

## Verification Plan
- Command: `dotnet test IndustrialIot.sln`
- Named probe: `TelemetryApiTests.Latest_OnePerTag`, `TelemetryApiTests.History_RangeOrderLimit`, `TelemetryApiTests.History_InvalidInput_400`, `TelemetryApiTests.UnknownEdge_404`
- Reachability: source — .NET 10 SDK + Docker engine.
- Oracle: exit 0, 0 failed and `Skipped: 0` across the solution; the four probes listed by `dotnet test tests/Cloud.Tests --list-tests` and passing.
- Counterexample: accepting a 90-day range or ignoring `limit` must fail `History_InvalidInput_400` / `History_RangeOrderLimit`.
- Artifacts: ephemeral.

## Failure Protocol
On a failed Step or Verification Plan run: stop; do not widen scope, change the Command, or weaken a test; record observed versus expected; repair only the cited cause; after three failed rounds, stop and ask the user.

## Receipt

Verification: PASS
Command: dotnet test IndustrialIot.sln
Exit: 0
Base: 8f23ff1bb1d7b0e5c78cd79ee7ff9fed2bbdcb72
Head: 8d9cbd30896c05e30b7d2d2a505370f3c0e60f215b3e619f3d3a7626d803ba16
```text
$ dotnet test IndustrialIot.sln
Exit: 0
Passed!  - Failed:     0, Passed:    47, Skipped:     0, Total:    47, Duration: 534 ms - Shared.Tests.dll (net10.0)
Passed!  - Failed:     0, Passed:   279, Skipped:     0, Total:   279, Duration: 14 s - Edge.Tests.dll (net10.0)
Passed!  - Failed:     0, Passed:    56, Skipped:     0, Total:    56, Duration: 36 s - Cloud.Tests.dll (net10.0)
```
- Named probes (`dotnet test tests/Cloud.Tests --list-tests`): `TelemetryApiTests.Latest_OnePerTag`, `TelemetryApiTests.History_RangeOrderLimit`, `TelemetryApiTests.History_InvalidInput_400` (12 rows: missing/no-`Z`/offset `from`, missing `to`, `from` = and > `to`, 32 and 90 days, `limit` 0/10001/`abc`, invalid `tagId`; each with an exact-31-day `limit=10000` control), `TelemetryApiTests.UnknownEdge_404`; `Skipped: 0` in every assembly. Output above is the summary lines of the final run.
- Pre-change run: exit 0 with 0 `TelemetryApiTests` listed → oracle not met, as expected.
- Counterexamples (mutations in a scratch copy), each caught: 90-day range → `History_InvalidInput_400`; limit ignored in SQL, `ORDER BY` removed, inclusive `to`, `truncated` off by one → `History_RangeOrderLimit`; latest not filtered by edge → `Latest_OnePerTag`; allow-list check removed → `UnknownEdge_404`; limit bounds removed → `History_InvalidInput_400`.
- Implementation note: the SQL-level `LIMIT` and `ORDER BY` mutations first survived (in-memory `Take` and an index plan made the output identical); `History_RangeOrderLimit` now asserts both in the SQL EF executes (captured command log, parameter values not logged).
- Review: code-auditor PASS on the first round (0 Critical/High/Medium).
- Known limitations: `JsonDocument` is not disposed per value (pooled buffers not returned; GC reclaims them); a repeated query parameter is reported as "is required"; 401/403 are asserted on the latest route only (both routes share one group policy); a malformed `to` is not tested separately (shared parser); index use with incremental sort for the `stream_id, sequence` tie-break is inferred, not checked with EXPLAIN.
