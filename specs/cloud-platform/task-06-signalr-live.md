# Task 06 — Connected viewers receive live telemetry and command updates per edge

Status: done

## Outcome
SignalR hub `/hubs/live` lets an authenticated Viewer join allow-listed edge groups and receive `telemetry` and `commandUpdated` messages as soon as they are stored; connections end when the token expires, and tokens passed in the query string never reach the logs.

## Scope
- In: hub, JWT over WebSocket (`access_token` query only for paths under `/hubs/live`), `CloseOnAuthenticationExpiration`, group join/leave with authorization, push after commits from `TelemetryIngestor`, `CommandService`, `CommandResultIngestor`, `CommandTimeoutSweeper`, hosting log level so query strings are not logged.
- Out: scale-out backplane (single instance), client SDK.

## Coverage
- CP-06

## Ownership
- Create: `src/Cloud/Live/{LiveHub.cs,LiveNotifier.cs}`, `tests/Cloud.Tests/LiveHubTests.cs`
- Modify: `src/Cloud/CloudServiceCollectionExtensions.cs`, `src/Cloud/Program.cs` (map hub with `CloseOnAuthenticationExpiration`), `src/Cloud/appsettings.json` (`Logging:LogLevel:Microsoft.AspNetCore.Hosting.Diagnostics` = `Warning`), `src/Cloud/Security/AuthenticationSetup.cs` (`OnMessageReceived`: query token only when `Request.Path.StartsWithSegments("/hubs/live")`), `src/Cloud/Ingestion/TelemetryIngestor.cs`, `src/Cloud/Commands/{CommandService.cs,CommandResultIngestor.cs,CommandTimeoutSweeper.cs}` (notify after commit)
- Modify: `tests/Cloud.Tests/Cloud.Tests.csproj` (`Microsoft.AspNetCore.SignalR.Client` 10.0.13)
- Read: `src/Cloud/Security/Policies.cs`

## Contracts
- Hub (Viewer policy): `JoinEdge(string edgeId)` — allow-listed → group `edge:{edgeId}`; otherwise `HubException("edge_not_found")`. `LeaveEdge(string edgeId)`.
- Server → client: `telemetry` (the stored `TelemetryPayload`, only for newly inserted rows, after commit); `commandUpdated` (the `GET /api/v1/commands/{id}` shape) after every status-change commit.
- Push failures never fail ingestion or command handling (logged).
- `access_token` from the query string is accepted only for `/hubs/live` paths; request logs that include query strings are disabled (`Microsoft.AspNetCore.Hosting.Diagnostics` at Warning); the hub is mapped with `CloseOnAuthenticationExpiration = true`.

## Steps
1. Hub, notifier, auth/query-token handling, log level; registration.
2. Notify calls after commits.
3. Tests with `HubConnection` (header token) and raw negotiate requests (query token), plus a capturing logger provider.

## Acceptance
- AC-08: a Viewer joined to edge A receives A's new telemetry and command updates, not edge B's; a duplicate telemetry message produces no second push; no token → refused (401); valid token without any role → refused (403); `JoinEdge` for a non-allow-listed edge → `HubException`; `POST /hubs/live/negotiate?access_token=<valid>` without a header → 200 while `GET /api/v1/edges?access_token=<valid>` → 401; a connection whose token expires is closed (a token with a short real lifetime, e.g. 5 s, and `ClockSkewSeconds` 0 in the test — SignalR's expiry check is not assumed to follow a fake clock); the token string never appears in captured logs.

## Dependencies
- task-05-command-api.md

## Verification Plan
- Command: `dotnet test IndustrialIot.sln`
- Named probe: `LiveHubTests.Telemetry_PushedToJoinedEdgeOnly`, `LiveHubTests.CommandUpdates_Pushed`, `LiveHubTests.Duplicate_NotPushedTwice`, `LiveHubTests.Unauthorized_Refused`, `LiveHubTests.NoViewerRole_Refused`, `LiveHubTests.JoinUnknownEdge_Fails`, `LiveHubTests.QueryToken_OnlyForHub`, `LiveHubTests.TokenExpiry_ClosesConnection`, `LiveHubTests.QueryToken_NotLogged`
- Reachability: source — .NET 10 SDK + Docker engine + loopback TLS broker; `HubConnection` via the test server handler.
- Oracle: exit 0, 0 failed and `Skipped: 0` across the solution; the nine probes listed by `--list-tests` and passing.
- Counterexample: pushing before commit, broadcasting to all clients, accepting the query token on API routes, rejecting it on the hub, keeping expired connections, or logging the query string must fail the named probes.
- Artifacts: ephemeral.

## Failure Protocol
On a failed Step or Verification Plan run: stop; do not widen scope, change the Command, or weaken a test; record observed versus expected; repair only the cited cause; after three failed rounds, stop and ask the user.

## Receipt

Verification: PASS
Command: dotnet test IndustrialIot.sln
Exit: 0
Base: 8f23ff1bb1d7b0e5c78cd79ee7ff9fed2bbdcb72
Head: 460c9b4fefc6580707b38eca15ed966b8e61fc9a3df3cb2ee3fa49eb86ee6e8b
```text
$ dotnet test IndustrialIot.sln
Exit: 0
Passed!  - Failed:     0, Passed:    47, Skipped:     0, Total:    47, Duration: 291 ms - Shared.Tests.dll (net10.0)
Passed!  - Failed:     0, Passed:   279, Skipped:     0, Total:   279, Duration: 14 s - Edge.Tests.dll (net10.0)
Passed!  - Failed:     0, Passed:    90, Skipped:     0, Total:    90, Duration: 1 m 12 s - Cloud.Tests.dll (net10.0)
```
- Named probes (`dotnet test tests/Cloud.Tests --list-tests`): `LiveHubTests.Telemetry_PushedToJoinedEdgeOnly`, `CommandUpdates_Pushed`, `Duplicate_NotPushedTwice`, `Unauthorized_Refused`, `NoViewerRole_Refused`, `JoinUnknownEdge_Fails`, `QueryToken_OnlyForHub`, `TokenExpiry_ClosesConnection`, `QueryToken_NotLogged`, plus `LiveHubTests.TimedOut_Pushed`; `Skipped: 0` in every assembly. Output above is the summary lines of the final run.
- Pre-change run: exit 0 with 0 `LiveHubTests` listed → oracle not met, as expected.
- How "after commit" is proven: a test `DbTransactionInterceptor` delays every commit by 300 ms and the client handler checks the row is visible in the database when the push arrives.
- Counterexamples (mutations in a scratch copy), each caught: push before commit, broadcast to all clients → `Telemetry_PushedToJoinedEdgeOnly`; duplicates pushed → `Duplicate_NotPushedTwice`; query token accepted on API routes, query token rejected on the hub → `QueryToken_OnlyForHub`; `CloseOnAuthenticationExpiration` false → `TokenExpiry_ClosesConnection`; `Hosting.Diagnostics` at Information → `QueryToken_NotLogged`; hub without policy → `Unauthorized_Refused`; `JoinEdge` for any edge → `JoinUnknownEdge_Fails`; no push from `CommandService` or from the result ingestor → `CommandUpdates_Pushed`; no push from the sweeper → `TimedOut_Pushed`.
- Repair rounds: implementation — `CommandUpdates_Pushed` first accepted "published or applied" so a missing API push survived; made deterministic (no auto-answering Edge: exactly `published`, then `applied` after an injected result). R1 review PASS_WITH_WARNINGS — Medium: a stale `commandUpdated` could arrive after a newer one; `CommandService` pushed uncommitted/unchanged states; no push timeout (slow client could stall sequential ingestion) → writers only signal committed changes (`CommandChangedAsync(commandId)`), pushes serialized and each re-reads the row before sending, 2 s timeout per push; result-ingestor push moved outside the retry. Final re-review: code-auditor PASS (0 Critical/High/Medium).
- Known limitations: whether `SendAsync` stops after the 2 s timeout while a client's pipe is full is inferred, not reproduced; one shared push lane — with a stalled client, a POST's 202 can wait behind a sweep (≈ N × 2 s); the sweeper may re-push an unchanged state for an id whose result won; a push is lost if its re-read fails; telemetry pushes the received sample (stored value may be canonicalized, e.g. `1e-20000` → `0`); the `CommandDetail` mapping is restated from `CommandEndpoints`; expiry and query tokens are tested with long polling and raw negotiate only (not WebSockets); `Release()` after `Dispose()` of the notifier's semaphore is possible only during container disposal; a later environment that lowers `Microsoft.AspNetCore.Hosting.Diagnostics` or enables HTTP/W3C logging would log query tokens.
