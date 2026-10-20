# Task 01 — Cloud host starts only with valid configuration, migrates PostgreSQL and reports health

Status: done

## Outcome
`src/Cloud` is an ASP.NET Core 10 host whose `AddCloudPlatform(IConfiguration)` validates every `Cloud:*` section at start, applies EF Core migrations to PostgreSQL before serving, and exposes `/health/live` and `/health/ready`; `tests/Cloud.Tests` runs the real host against a Testcontainers PostgreSQL with switchable database fault injection.

## Scope
- In: project + solution entry, local `dotnet-ef` tool, options + validators (`Database`, `Edges`, `Commands`, `Retention`, `Health`, `ForwardedHeaders`), `CloudDbContext` with all tables of this wave, design-time factory, initial migration, migrate-at-start hosted step, endpoint-module hook, health endpoints with `TimeProvider`-driven cached readiness, test infrastructure (`PostgresFixture`, `CloudTestHost`, `DatabaseFaultInterceptor`).
- Out: MQTT (task-02), auth (task-03), API (tasks 03–05), SignalR (task-06).

## Coverage
- CP-01

## Ownership
- Create: `.config/dotnet-tools.json` (`dotnet-ef` 10.0.13)
- Create: `src/Cloud/IndustrialIot.Cloud.csproj` (`Microsoft.NET.Sdk.Web`; `Npgsql.EntityFrameworkCore.PostgreSQL` 10.0.3, `Microsoft.EntityFrameworkCore.Design` 10.0.13 with private assets; `ProjectReference` Shared; `InternalsVisibleTo Cloud.Tests`), `src/Cloud/Program.cs` (`public partial class Program`), `src/Cloud/appsettings.json`, `src/Cloud/CloudOptions.cs`, `src/Cloud/CloudServiceCollectionExtensions.cs`, `src/Cloud/EndpointModules.cs`
- Create: `src/Cloud/Data/{CloudDbContext.cs,Entities.cs,CloudDbContextDesignTimeFactory.cs,DatabaseMigrator.cs,Migrations/*}`, `src/Cloud/Health/{ReadinessState.cs,ReadinessMonitor.cs,HealthEndpoints.cs}`
- Create: `tests/Cloud.Tests/{Cloud.Tests.csproj,CloudHostTests.cs}` (`Testcontainers.PostgreSql` 4.16.0, `Microsoft.AspNetCore.Mvc.Testing` 10.0.13, `Microsoft.Extensions.TimeProvider.Testing` 10.10.0, xunit/test SDK versions as in `tests/Edge.Tests`), `tests/Cloud.Tests/Infrastructure/{PostgresFixture.cs,CloudTestHost.cs,DatabaseFaultInterceptor.cs}`
- Modify: `IndustrialIot.sln` (add both projects)
- Read: `src/Shared/Contracts/*`, `src/Edge/EdgeServiceCollectionExtensions.cs` (validation pattern)

## Contracts
**Configuration** (`Cloud` section; env overrides `Cloud__...`; secrets only from env):
| Key | Default | Rule |
|---|---|---|
| `Database:ConnectionString` | — | required; from env `Cloud__Database__ConnectionString`; never logged or returned |
| `Edges` | `[]` | `{ "EdgeId", "DisplayName" }`; `EdgeId` contract rule `^[A-Za-z0-9_-]{1,64}\z`; unique (ordinal) |
| `Commands:DefaultTtlSeconds` | 60 | 5..`MaxTtlSeconds` |
| `Commands:MaxTtlSeconds` | 600 | ≤ 86,400 |
| `Commands:TimeoutGraceSeconds` | 120 | 0..3,600 |
| `Retention:TelemetryDays` | 90 | 1..3,650 |
| `Health:RefreshSeconds` | 15 | 1..300 |
| `ForwardedHeaders:KnownProxies` | `[]` | each a valid IP address |
Invalid → `OptionsValidationException` from `StartAsync`; messages name the key, never the value.

**Schema** (`CloudDbContext`, snake_case, migration `InitialSchema`, all timestamps `timestamptz`):
| Table | Columns | Keys / indexes |
|---|---|---|
| `telemetry` | `edge_id` text, `stream_id` uuid, `sequence` bigint, `tag_id` text, `value` jsonb null, `quality` text, `timestamp_utc`, `received_utc` | PK (`edge_id`,`stream_id`,`sequence`); index (`edge_id`,`tag_id`,`timestamp_utc`); index (`received_utc`) |
| `latest_values` | `edge_id`, `tag_id`, `value` jsonb null, `quality`, `timestamp_utc`, `stream_id`, `sequence` | PK (`edge_id`,`tag_id`) |
| `commands` | `command_id` uuid, `edge_id`, `tag_id`, `target_value` jsonb, `issued_at_utc`, `expires_at_utc`, `issued_by` text **not null**, `idempotency_key` text null, `request_hash` text null, `status` text, `reason` text null, `completed_at_utc` null, `result_received_utc` null, `created_utc` | PK `command_id`; unique (`issued_by`,`idempotency_key`) where key not null (forever); index (`status`,`expires_at_utc`); index (`edge_id`,`created_utc`) |

**Start-up**: options validation → `DatabaseMigrator` (first hosted service; `MigrateAsync`; failure → start fails, connection string not included) → other services. `CloudDbContextDesignTimeFactory` supplies a placeholder connection string so `dotnet ef migrations add` never needs a live database or the host.

**Endpoint modules**: `Program` maps every registered `IEndpointModule` (`void Map(IEndpointRouteBuilder)`) after `UseAuthentication`/`UseAuthorization`; feature tasks register their modules; tests may register extra modules through DI.

**Health**: `/health/live` → 200 `{"status":"live"}` (no I/O). `/health/ready` → 200 `{"status":"ready"}` / 503 `{"status":"not_ready","checks":{"database":bool,…}}` from `ReadinessState`, refreshed by `ReadinessMonitor` every `RefreshSeconds` on the injected `TimeProvider` (`SELECT 1`); MQTT added by task-02. Both anonymous; no I/O per request.

**Test infrastructure**: `PostgresFixture` — one container per run (`[CollectionDefinition]` shared by every Cloud test class), a fresh database per `CloudTestHost`. `CloudTestHost` — `WebApplicationFactory<Program>`; settings (incl. connection string) applied with `UseSetting` before the host builds; valid defaults for every section; per-test overrides; `TimeProvider` replaceable; `DatabaseFaultInterceptor` registered on the `DbContext` and switchable at runtime (throws `NpgsqlException`-like failures for commands while enabled) — outages are never simulated by stopping the shared container.

## Steps
1. Tool manifest, projects, solution; options + validators; `AddCloudPlatform`; endpoint-module hook; minimal `Program`.
2. `CloudDbContext`, entities, design-time factory, `InitialSchema` via `dotnet tool run dotnet-ef migrations add InitialSchema`, `DatabaseMigrator`.
3. Health + readiness monitor.
4. Fixture, test host, fault interceptor, tests.

## Acceptance
- AC-01 (part): valid config → host starts, the three tables and indexes exist, `/health/live` 200 anonymously, `/health/ready` 200 after the first refresh; invalid `Edges:0:EdgeId` (`a b`), duplicate edge ids, `DefaultTtlSeconds` > max, `TelemetryDays` 0, `Health:RefreshSeconds` 0, `KnownProxies:0` = `not-an-ip`, missing connection string → `OptionsValidationException` naming the key; unreachable database (bad port) → start fails; with the fault interceptor enabled, `/health/ready` becomes 503 after one fake-clock refresh period and 200 again after disabling + one period; 50 readiness requests between refreshes cause no database command (counted by the interceptor).

## Dependencies
- none

## Verification Plan
- Command: `dotnet test IndustrialIot.sln`
- Named probe: `CloudHostTests.ValidConfig_MigratesAndServesHealth`, `CloudHostTests.InvalidConfig_FailsAtStart`, `CloudHostTests.DatabaseUnreachable_FailsAtStart`, `CloudHostTests.Readiness_UsesCachedState`
- Reachability: source — .NET 10 SDK + Docker engine (Testcontainers `postgres:17-alpine`); `dotnet tool restore` for `dotnet-ef`.
- Oracle: exit 0, 0 failed and `Skipped: 0` across the solution; the four probes listed by `dotnet test tests/Cloud.Tests --list-tests` and passing.
- Counterexample: pinging the DB inside `/health/ready`, or serving before migrations completed, must fail the named probes.
- Artifacts: ephemeral containers (removed by Testcontainers).

## Failure Protocol
On a failed Step or Verification Plan run: stop; do not widen scope, change the Command, or weaken a test; record observed versus expected; repair only the cited cause; after three failed rounds, stop and ask the user.

## Receipt

Verification: PASS
Command: dotnet test IndustrialIot.sln
Exit: 0
Base: 8f23ff1bb1d7b0e5c78cd79ee7ff9fed2bbdcb72
Head: d8e96399f7dd49b5f981fd2826e54ab505e72dc7f7da451b20f734f2e32957da
```text
$ dotnet test IndustrialIot.sln
  Determining projects to restore...
  All projects are up-to-date for restore.
  IndustrialIot.Shared -> C:\Users\HOME\source\repos\PLCProtocol\src\Shared\bin\Debug\net10.0\IndustrialIot.Shared.dll
  Shared.Tests -> C:\Users\HOME\source\repos\PLCProtocol\tests\Shared.Tests\bin\Debug\net10.0\Shared.Tests.dll
  IndustrialIot.Edge -> C:\Users\HOME\source\repos\PLCProtocol\src\Edge\bin\Debug\net10.0\IndustrialIot.Edge.dll
Test run for C:\Users\HOME\source\repos\PLCProtocol\tests\Shared.Tests\bin\Debug\net10.0\Shared.Tests.dll (.NETCoreApp,Version=v10.0)
  IndustrialIot.Cloud -> C:\Users\HOME\source\repos\PLCProtocol\src\Cloud\bin\Debug\net10.0\IndustrialIot.Cloud.dll
A total of 1 test files matched the specified pattern.
  Edge.Tests -> C:\Users\HOME\source\repos\PLCProtocol\tests\Edge.Tests\bin\Debug\net10.0\Edge.Tests.dll
Test run for C:\Users\HOME\source\repos\PLCProtocol\tests\Edge.Tests\bin\Debug\net10.0\Edge.Tests.dll (.NETCoreApp,Version=v10.0)
A total of 1 test files matched the specified pattern.
  Cloud.Tests -> C:\Users\HOME\source\repos\PLCProtocol\tests\Cloud.Tests\bin\Debug\net10.0\Cloud.Tests.dll
Test run for C:\Users\HOME\source\repos\PLCProtocol\tests\Cloud.Tests\bin\Debug\net10.0\Cloud.Tests.dll (.NETCoreApp,Version=v10.0)
A total of 1 test files matched the specified pattern.

Passed!  - Failed:     0, Passed:    47, Skipped:     0, Total:    47, Duration: 462 ms - Shared.Tests.dll (net10.0)

Passed!  - Failed:     0, Passed:    11, Skipped:     0, Total:    11, Duration: 5 s - Cloud.Tests.dll (net10.0)

Passed!  - Failed:     0, Passed:   279, Skipped:     0, Total:   279, Duration: 15 s - Edge.Tests.dll (net10.0)
```
- Named probes (`dotnet test tests/Cloud.Tests --list-tests`): `CloudHostTests.ValidConfig_MigratesAndServesHealth`, `CloudHostTests.InvalidConfig_FailsAtStart` (8 rows: edge id format, duplicate edge id, DefaultTtlSeconds > max, TelemetryDays 0, RefreshSeconds 0, KnownProxies `not-an-ip` and shorthand `10.1`, empty connection string), `CloudHostTests.DatabaseUnreachable_FailsAtStart`, `CloudHostTests.Readiness_UsesCachedState`; `Skipped: 0` in every assembly.
- Pre-change run: exit 0 with no `tests/Cloud.Tests` project → oracle (four named probes) not met, as expected.
- Prerequisite: Docker engine 27.5.1 running; Testcontainers `postgres:17-alpine`. Tool: `dotnet-ef` 10.0.13 in `.config/dotnet-tools.json` (`dotnet new tool-manifest` on .NET 10 creates it at the root; moved to `.config`, which `dotnet tool` also resolves); migration `InitialSchema` generated through `CloudDbContextDesignTimeFactory` without a live database.
- Counterexamples (mutations in a scratch copy), each caught: `/health/ready` pinging the database → `Readiness_UsesCachedState`; migrator removed → `ValidConfig_MigratesAndServesHealth` (+ others); refresh interval hard-coded to 60 s instead of the option → `Readiness_UsesCachedState`.
- Repair rounds: R1 build — CS1705 (tests compiled against EF Core 10.0.4 from Npgsql while Cloud used 10.0.13 from the Design package; fixed by pinning `Microsoft.EntityFrameworkCore`/`.Relational` 10.0.13 in the Cloud project) and the obsolete parameterless Testcontainers builder (TreatWarningsAsErrors); R2 review PASS_WITH_WARNINGS (readiness test kept advancing the clock, so "one refresh period" and the option wiring were unproven) → `WaitsArmed` hook + exact one-period assertions with RefreshSeconds 7 (+ Lows: full-IP check for KnownProxies, readiness seeded not-ready, empty-string override, volatile flag). Final re-review: code-auditor PASS (0 Critical/High/Medium).
- Known limitations: binder conversion errors (e.g. `DefaultTtlSeconds=abc`) surface as `InvalidOperationException` containing the value (non-secret numeric fields only); the edgeId regex duplicates the internal Shared rule; the connection string is accepted from any configuration source (operational rule: environment only); two "still unchanged" assertions use a 300 ms real-time wait; `Program` does not add authentication middleware yet (task-03).
