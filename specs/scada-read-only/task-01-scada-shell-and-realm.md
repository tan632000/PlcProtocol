# Task 01 — The Cloud serves the SCADA shell without a token; the realm has a PKCE, Viewer-only browser client

Status: done

## Outcome
`/scada/`, `/scada/signalr.min.js` and `/scada/config` are served anonymously with the plan's D-01 headers, other
`/scada/…` paths and a disabled page answer 404, every data path still requires the Viewer policy, and the realm
contains `iiot-scada` plus the `Operator ⊃ Viewer` composite and Viewer-only scope mapping exactly as D-03.

## Scope
- In: `ScadaOptions` (D-02) with validation; `ScadaEndpoints` per D-01 (anonymous `/scada`, `/scada/`,
  `/scada/{**rest}`, fixed allow-list, exact headers); a minimal `index.html` shell (title, classic
  `<script src="signalr.min.js">`, `<script type="module" src="app.js">`, no inline script); the vendored
  `signalr.min.js` 8.0.7 (D-05); the realm changes (D-03); tests.
- Out: `app.js`/`scada-core.js` and page behaviour (task-02); smoke/docs (task-03); any change to existing endpoints,
  policies or the hub.

## Coverage
- CP-01

## Ownership
- Create: `src/Cloud/Api/ScadaEndpoints.cs`, `src/Cloud/Scada/index.html`, `src/Cloud/Scada/signalr.min.js`,
  `tests/Cloud.Tests/ScadaPageTests.cs`, `tests/Deployment.Tests/ScadaRealmTests.cs`
- Modify: `src/Cloud/CloudOptions.cs`, `src/Cloud/CloudServiceCollectionExtensions.cs`,
  `src/Cloud/IndustrialIot.Cloud.csproj`, `src/Cloud/appsettings.json`, `deployments/keycloak/iiot-realm.json`
- Read: `src/Cloud/Security/Policies.cs`, `src/Cloud/Security/AuthenticationSetup.cs`, `src/Cloud/Program.cs`,
  `src/Edge/Monitoring/MonitorServer.cs`, `tests/Cloud.Tests/Infrastructure/CloudTestHost.cs`,
  `tests/Deployment.Tests/BrokerTests.cs` (`Repo.Root` helper)

## Steps
1. Add `ScadaOptions` + validator and bind `Cloud:Scada`; defaults in `appsettings.json` → the host starts with
   defaults and fails at start with an invalid `ClientId`.
2. Add `ScadaEndpoints`, register it beside the other modules, embed the files with fixed `LogicalName`s →
   `GET /scada/` returns the shell with the D-01 headers.
3. Download the `@microsoft/signalr@8.0.7` npm tarball, verify it against the registry `integrity` value, copy
   `dist/browser/signalr.min.js` unchanged, record its SHA-256 in the Receipt notes.
4. Edit the realm per D-03 (composite `Operator`, client, `scopeMappings`).
5. Tests:
   - `ScadaPageTests`: anonymous 200 + exact headers (Content-Type, Cache-Control, nosniff, Referrer-Policy, full CSP
     string) for `/scada/`, `/scada/signalr.min.js`, `/scada/config`; `/scada` → redirect to `/scada/`; config JSON
     equals `Auth:Authority` and `ClientId`; anonymous `/scada/unknown.js` and `/scada/..%2fappsettings.json` → 404;
     anonymous `/scada/%2e%2e/appsettings.json` (normalised by `System.Uri` to `/appsettings.json`, outside `/scada`)
     → not 200 and the body is not the settings file; with `Enabled: false` `/scada`, `/scada/`, every allow-listed
     path, `/scada/config` and `/scada/unknown.js` → 404; anonymous `GET /api/v1/edges` and `POST /hubs/live/negotiate` → 401, with a role-less token →
     403.
   - `ScadaRealmTests` (reads the realm via `Repo.Root`): every D-03 field and value exactly (including the `##`
     string), mappers equal to `iiot-cli`'s, `Operator` composite contains `Viewer`, the only scope mapping of
     `iiot-scada` is `Viewer`, no `*` in any redirect URI or web origin, `iiot-cli` unchanged.

## Acceptance
- AC-01 (shell part): every `ScadaPageTests` case passes; no existing Cloud test changes.
- AC-02 (source level): `ScadaRealmTests` passes.

## Dependencies
- none

## Verification Plan
- Command: `dotnet test tests/Cloud.Tests --filter "FullyQualifiedName~ScadaPageTests" && dotnet test tests/Deployment.Tests --filter "FullyQualifiedName~ScadaRealmTests" && dotnet test tests/Cloud.Tests`
- Named probe: `ScadaPageTests`, `ScadaRealmTests`, and the whole Cloud.Tests suite (existing tests stay green).
- Reachability: Cloud.Tests runs the real `Program` through `CloudTestHost` (Testcontainers Postgres, Docker needed);
  the realm test reads the committed JSON file.
- Oracle: all named tests pass, each filter runs > 0 tests, exit 0.
- Counterexample: removing `AllowAnonymous` from the catch-all, adding `ws: wss:` to the CSP, adding a wildcard
  redirect URI, setting `fullScopeAllowed` true or enabling `directAccessGrantsEnabled` must each fail a test (checked
  on a disposable copy during review).
- Artifacts: `src/Cloud/Scada/signalr.min.js` SHA-256 recorded in the Receipt notes.

## Failure Protocol
On a failed Step or Verification Plan run: stop; do not widen scope, change the Command, or weaken a test; record observed versus expected; repair only the cited cause; after three failed rounds, stop and ask the user.

## Receipt

Verification: PASS
Command: dotnet test tests/Cloud.Tests --filter "FullyQualifiedName~ScadaPageTests" && dotnet test tests/Deployment.Tests --filter "FullyQualifiedName~ScadaRealmTests" && dotnet test tests/Cloud.Tests
Exit: 0
Base: a7d4c011c2a0f72dbb09e395d5fb7058658095c2
Head: ddc31d6c9bf25f01435395e03ab9edba54b1e8d8db7af3029be2eaae1e7bfc44
```text
$ dotnet test tests/Cloud.Tests --filter "FullyQualifiedName~ScadaPageTests" && dotnet test tests/Deployment.Tests --filter "FullyQualifiedName~ScadaRealmTests" && dotnet test tests/Cloud.Tests
Test run for C:\Users\case 046\Documents\Project\PlcProtocol\tests\Cloud.Tests\bin\Debug\net10.0\Cloud.Tests.dll (.NETCoreApp,Version=v10.0)
Passed!  - Failed:     0, Passed:    21, Skipped:     0, Total:    21, Duration: 8 s - Cloud.Tests.dll (net10.0)
Test run for C:\Users\case 046\Documents\Project\PlcProtocol\tests\Deployment.Tests\bin\Debug\net10.0\Deployment.Tests.dll (.NETCoreApp,Version=v10.0)
Passed!  - Failed:     0, Passed:     5, Skipped:     0, Total:     5, Duration: 39 ms - Deployment.Tests.dll (net10.0)
Test run for C:\Users\case 046\Documents\Project\PlcProtocol\tests\Cloud.Tests\bin\Debug\net10.0\Cloud.Tests.dll (.NETCoreApp,Version=v10.0)
Passed!  - Failed:     0, Passed:   139, Skipped:     0, Total:   139, Duration: 1 m 43 s - Cloud.Tests.dll (net10.0)
```
- Pre-change run: the first filter matched no test ("No test matches the given testcase filter", exit 0) — fails the Oracle's "> 0 tests".
- Repair round 1: a separate `MapGet("/scada")` also matched `/scada/` (routing ignores a trailing slash) and redirected it to itself (2 tests failed with 302); fixed with one catch-all that redirects only a path without the trailing slash.
- Counterexamples on a disposable copy (scratchpad `mut-t01`): no `AllowAnonymous` on the catch-all → 12 failed; `ws: wss:` in the CSP → 7 failed; wildcard redirect URI, `fullScopeAllowed` true, `directAccessGrantsEnabled` true → 1 realm test failed each; unmutated control 5/5.
- Existing tests unchanged (`git diff --stat -- tests/` empty); 118 existing + 21 new Cloud tests.
- Artifact: `src/Cloud/Scada/signalr.min.js` = `@microsoft/signalr@8.0.7` `dist/browser/signalr.min.js`, tarball verified against the registry integrity (sha512), SHA-256 `e28a720a359b37cb015758d543f908730ed5bbe478db09506bb6887f18313538` (pinned by `Signalr_IsTheVendoredBuildByteForByte`), no CR bytes.
- Review (code-auditor, fresh context): PASS, 0 Critical/High/Medium. Lows left as they are: POST/HEAD on `/scada/*` get 405→401 without D-01 headers (method, not path; no data); `/scada//` behaviour untested; an `Auth:Authority` with userinfo would appear in the CSP and config; `Disabled_EveryScadaPath_Returns404` does not assert the headers; the `%2e%2e` test exercises the fallback, as N-01 decided. Keycloak 26 import of the composite/scopeMappings JSON is [UNVERIFIED] until task-03.
