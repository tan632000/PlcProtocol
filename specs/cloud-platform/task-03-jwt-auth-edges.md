# Task 03 — Every API call needs a valid JWT and the right role

Status: done

## Outcome
The Cloud validates bearer JWTs from an external IdP, maps roles `Viewer` / `Operator`, requires an authenticated user by default (fallback policy), honours forwarded headers only from configured proxies, returns RFC 7807 problems for 401/403, and serves `GET /api/v1/edges` to Viewers.

## Scope
- In: `AuthOptions` + validator, JwtBearer configuration (Authority/Audience/RoleClaimType, HTTPS metadata, algorithms, token types), policies incl. `Operator` requiring a valid `sub`, fallback policy, ProblemDetails mapping, forwarded headers, edges endpoint (as an endpoint module), test token issuer.
- Out: other endpoints (tasks 04–05), SignalR token handling (task-06).

## Coverage
- CP-03, CP-04 (edges endpoint), CP-01 (auth config validation)

## Ownership
- Create: `src/Cloud/Security/{AuthOptions.cs,AuthenticationSetup.cs,Policies.cs}`, `src/Cloud/Api/{EdgesEndpoints.cs,ProblemMapping.cs}`
- Modify: `src/Cloud/CloudServiceCollectionExtensions.cs`, `src/Cloud/Program.cs` (middleware order, forwarded headers), `src/Cloud/appsettings.json` (`Cloud:Auth`), `src/Cloud/IndustrialIot.Cloud.csproj` (`Microsoft.AspNetCore.Authentication.JwtBearer` 10.0.13)
- Modify: `tests/Cloud.Tests/Infrastructure/CloudTestHost.cs` (RSA test key via `StaticConfigurationManager`, token helper, test endpoint modules)
- Create: `tests/Cloud.Tests/AuthTests.cs`
- Scope amendment (user decision 2026-10-19, after review Medium: unbounded `MAX(received_utc)` per silent edge): Modify `src/Cloud/Data/CloudDbContext.cs` (index `ix_telemetry_edge_received` on (`edge_id`, `received_utc`)); Create `src/Cloud/Data/Migrations/*_TelemetryEdgeReceivedIndex*.cs` (+ snapshot update).
- Read: `src/Cloud/EndpointModules.cs`, `src/Cloud/Health/HealthEndpoints.cs`, `src/Shared/Common/ContractJson.cs` (`IssuedBy` rule)

## Contracts
- Options `Cloud:Auth`: `Authority` (required, absolute `https://`), `Audience` (required; the API resource identifier, never a client id), `RoleClaimType` = `roles`, `ViewerRole` = `Viewer`, `OperatorRole` = `Operator`, `ValidTypes` = [`at+jwt`, `JWT`], `ClockSkewSeconds` = 30 (0..300). Invalid → `OptionsValidationException` at start.
- JwtBearer: `RequireHttpsMetadata = true`; validate issuer, audience, lifetime (skew), signature from OIDC metadata; `ValidAlgorithms` = RS256, ES256, PS256; `ValidTypes` from options; `MapInboundClaims = false`; name claim `sub`.
- Policies: `Viewer` = role Viewer or Operator; `Operator` = role Operator **and** a `sub` claim that satisfies the contract `IssuedBy` rule (1..128 chars, no control characters) — otherwise 403. **Fallback policy = authenticated user**; only health endpoints are `AllowAnonymous`.
- Errors: 401 → problem `title` `unauthorized` (+ `WWW-Authenticate: Bearer`), 403 → `forbidden`; never token contents; unhandled → 500 `internal_error` without stack trace.
- Forwarded headers: `X-Forwarded-For`/`-Proto` honoured only from `Cloud:ForwardedHeaders:KnownProxies`; the default `KnownNetworks`/`KnownProxies` (loopback) are cleared; `ForwardLimit = 1`.
- `GET /api/v1/edges` (Viewer) → 200 `[{"edgeId","displayName","lastTelemetryUtc"|null}]` ordered by `edgeId`, allow-listed only (`lastTelemetryUtc` = max `received_utc`).
- Tests: `CloudTestHost` sets `JwtBearerOptions.ConfigurationManager` to a `StaticConfigurationManager<OpenIdConnectConfiguration>` holding a locally generated RSA key (works regardless of post-configure order); test endpoint modules (Operator-only, no-policy) are registered through DI and mapped by the production hook.

## Steps
1. Options, authentication, policies, fallback, problem mapping, forwarded headers.
2. Edges endpoint module.
3. Token helper, test modules, tests.

## Acceptance
- AC-04: no token, bad signature, `alg` HS256, `typ` `id+jwt`, wrong issuer, wrong audience, expired beyond skew → 401 problem; Viewer on an Operator-only test endpoint → 403; Operator token without `sub` or with a 200-char `sub` → 403; health without token → 200; a no-policy test endpoint → 401 anonymously (fallback); `X-Forwarded-Proto: https` from an unlisted address is ignored and honoured from a listed one (the test sets the connection's remote address explicitly, e.g. 127.0.0.1 as "unlisted", because TestServer leaves it null).
- AC-05 (part): Viewer `GET /api/v1/edges` → only configured edges with `lastTelemetryUtc` from stored telemetry.
- AC-01 (part): `Authority` `http://…`, missing `Audience` → start fails naming the key.

## Dependencies
- task-02-mqtt-telemetry-ingestion.md

## Verification Plan
- Command: `dotnet test IndustrialIot.sln`
- Named probe: `AuthTests.InvalidTokens_Return401`, `AuthTests.WrongRole_Returns403`, `AuthTests.OperatorWithoutValidSub_Returns403`, `AuthTests.Fallback_RequiresAuthentication`, `AuthTests.Health_IsAnonymous`, `AuthTests.ForwardedHeaders_OnlyFromKnownProxies`, `AuthTests.Edges_ListsAllowListOnly`, `AuthTests.InvalidAuthConfig_FailsAtStart`
- Reachability: source — .NET 10 SDK + Docker engine.
- Oracle: exit 0, 0 failed and `Skipped: 0` across the solution; the eight probes listed by `--list-tests` and passing.
- Counterexample: removing the fallback policy, skipping audience validation, accepting HS256 or ID tokens, treating Viewer as Operator, or trusting loopback forwarded headers must fail the named probes.
- Artifacts: ephemeral.

## Failure Protocol
On a failed Step or Verification Plan run: stop; do not widen scope, change the Command, or weaken a test; record observed versus expected; repair only the cited cause; after three failed rounds, stop and ask the user.

## Receipt

Verification: PASS
Command: dotnet test IndustrialIot.sln
Exit: 0
Base: 8f23ff1bb1d7b0e5c78cd79ee7ff9fed2bbdcb72
Head: d23fc12547330e4c87b4669b39ae2e2ea6baf3bf4da4a6ceb4443b7d3ab022d6
```text
$ dotnet test IndustrialIot.sln
Exit: 0
Passed!  - Failed:     0, Passed:    47, Skipped:     0, Total:    47, Duration: 501 ms - Shared.Tests.dll (net10.0)
Passed!  - Failed:     0, Passed:   279, Skipped:     0, Total:   279, Duration: 14 s - Edge.Tests.dll (net10.0)
Passed!  - Failed:     0, Passed:    41, Skipped:     0, Total:    41, Duration: 21 s - Cloud.Tests.dll (net10.0)
```
- Named probes (`dotnet test tests/Cloud.Tests --list-tests`): `AuthTests.InvalidTokens_Return401` (7 rows: none, bad signature, HS256 with a key published in metadata, `typ` `id+jwt`, wrong issuer, wrong audience, expired beyond skew), `AuthTests.WrongRole_Returns403`, `AuthTests.OperatorWithoutValidSub_Returns403`, `AuthTests.Fallback_RequiresAuthentication`, `AuthTests.Health_IsAnonymous`, `AuthTests.ForwardedHeaders_OnlyFromKnownProxies`, `AuthTests.Edges_ListsAllowListOnly`, `AuthTests.InvalidAuthConfig_FailsAtStart` (4 rows), plus `AuthTests.UnhandledError_Returns500Problem`; `Skipped: 0` in every assembly. Output above is the summary lines of the final run.
- Pre-change run: exit 0 with 0 `AuthTests` listed → oracle not met, as expected.
- Counterexamples (mutations in a scratch copy), each caught: fallback policy removed → `Fallback_RequiresAuthentication`; audience validation off, `ValidAlgorithms` removed, `ValidTypes` removed, lifetime validation off → `InvalidTokens_Return401`; Viewer accepted by the Operator policy → `WrongRole_Returns403`; `sub` check removed → `OperatorWithoutValidSub_Returns403`; auth validator unregistered → `InvalidAuthConfig_FailsAtStart`; forwarded headers trusted with no proxy configured, default `KnownProxies` kept, default `KnownIPNetworks` kept → `ForwardedHeaders_OnlyFromKnownProxies`; edge index removed from the migration → `Edges_ListsAllowListOnly`.
- Repair rounds: R1 review FAIL — Critical: with `KnownProxies` empty (the shipped default) ASP.NET Core skips the address check and trusts every client's `X-Forwarded-*` → fail closed (`ForwardedHeaders.None`) + no-proxy and IPv6-loopback cases in the probe; R2 review PASS_WITH_WARNINGS — Medium: per-Edge `MAX(received_utc)` unbounded for a silent Edge → user chose to add the index now (scope amendment above): `ix_telemetry_edge_received` + migration `TelemetryEdgeReceivedIndex`. Implementation note: the HS256 row originally passed only because no symmetric key existed; a symmetric key is now published in the test metadata so only the algorithm allow-list rejects it. Final re-review: code-auditor PASS (0 Critical/High/Medium).
- Known limitations: 401/403 problem bodies have no `traceId` (500 has); every 500 problem is rewritten to `internal_error`; `Policies.IsValidSubject` restates the internal Shared `IssuedBy` rule (can drift); the index migration is not `CONCURRENTLY` (locks writes while built on a large table); `displayName` falls back to the edgeId when not configured; real OIDC metadata retrieval over HTTPS is not exercised (tests use static metadata).
