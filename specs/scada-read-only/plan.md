# Display-only web SCADA served by the Cloud
Specs-Contract: process-first-ready-v1

## Scope decision (GATE-SCOPE — 2026-10-28)
- Request: a web SCADA that only displays PLC data from the Cloud; no PLC hardware is available, so proof is automated
  tests plus a browser check against the compose stack with an Edge whose PLC is unreachable.
- Existing:
  - Viewer read APIs: `GET /api/v1/edges` (`src/Cloud/Api/EdgesEndpoints.cs:15`), `.../tags/latest` and
    `.../tags/{tagId}/history` (`src/Cloud/Api/TelemetryEndpoints.cs:32-34`; history is raw, ascending, half-open
    `[from,to)`, ISO-8601 UTC with `Z`, ≤ 31 days, `limit` ≤ 10 000, `truncated` flag: `:26-28,73-112`). `LatestValue`
    has no `edgeId` and serialises `DateTime` with System.Text.Json defaults (`:12,50`).
  - Live push: SignalR hub `/hubs/live` with `JoinEdge`/`LeaveEdge` per connection (`src/Cloud/Live/LiveHub.cs:19-31`),
    method `telemetry` carrying the Shared contract JSON (`src/Cloud/Live/LiveNotifier.cs:17,27-38`; timestamps
    `yyyy-MM-dd'T'HH:mm:ss.fff'Z'`, `src/Shared/Common/ContractJson.cs:95`); the hub alone reads `access_token` from the
    query (`src/Cloud/Security/AuthenticationSetup.cs:68-80`) and closes a connection when its token expires
    (`src/Cloud/Program.cs:19`).
  - Every request without its own policy, matched or not, needs a valid token (`src/Cloud/Security/Policies.cs:25`);
    modules are registered at `src/Cloud/CloudServiceCollectionExtensions.cs:63-76`; the Edge monitor shows the
    embedded-page pattern (`src/Edge/IndustrialIot.Edge.csproj:21`, `src/Edge/Monitoring/MonitorServer.cs:149-177`).
  - Keycloak has only `iiot-cli` (public, password grant, no standard flow, full scope:
    `deployments/keycloak/iiot-realm.json:13-22`); users `viewer`/`operator` hold `Viewer`/`Operator` (`:53-71`); the
    issuer is fixed to `https://keycloak:8443` (`deployments/compose.yaml:50-52`) and the docs map `127.0.0.1 keycloak`
    in the hosts file (`docs/deployment.md:64`). The Cloud listens on `http://127.0.0.1:8080` (`compose.yaml:89-90`).
    A realm change makes CD refuse with "manual upgrade needed" (`deployments/scripts/remote-deploy.sh:52-53`).
  - Edges poll every 1000 ms by default (`src/Edge/appsettings.json:35`); with an unreachable PLC they still publish bad
    samples each cycle. Commands are off by default (`src/Cloud/appsettings.json:16`).
  - CI runs `dotnet test` and the smoke test only (`.github/workflows/ci.yml:30-32,54`).
- Minimum change: a Viewer-only browser login client in the realm, a page + scripts served by the Cloud under
  `/scada/`, live table, one-tag trend from the existing history API, CI step, smoke checks and docs.
- Expansion signals: ~16 touched files (realm, 3 Cloud sources, csproj, appsettings, 4 page files, 3 tests, CI, smoke,
  docs) — above eight because the page is split into testable files; one subsystem, so one packet.
- User decisions: **KEEP** (login + edge picker + live table + one-tag trend on raw history); page **served by the
  Cloud**; access = **existing Viewer policy**. GATE-REVIEW (2026-10-28): trend windows **15 min / 1 h** only; the
  SCADA token carries **only `Viewer`** (also for Operators); all other findings accepted.
- Scope clarification (2026-10-28, during task-03): the product SCADA (mimic screens, alarms, customer UI) is a
  **separate system in its own repository** that reads this platform's APIs. The page built here stays as a
  **diagnostic / commissioning viewer** of the Cloud data, documented as not being the product SCADA; the `iiot-scada`
  browser client and the API contracts are what an external SCADA reuses. Redirects also cover port 8090. The smoke
  test runs in CI on a pull request (port 8080 is held by Windows http.sys on the dev machine).

## Out of scope
- The product SCADA itself (separate repository), CORS for an external SCADA origin.
- Alarms, any command/write UI, user management, aggregated trend endpoint and windows longer than 1 h, tag metadata
  (display names, units), production domain / reverse proxy / public TLS for the page, multi-language UI, a
  configurable stale threshold.

## Decisions
| ID | Chosen | Rejected | Why | Load-bearing assumption | Break signal → response |
|---|---|---|---|---|---|
| D-01 | `ScadaEndpoints` always maps `/scada`, `/scada/` and `/scada/{**rest}` with `AllowAnonymous` (so the fallback policy never turns a miss into 401); it serves a fixed allow-list of embedded files — `/scada/` (`index.html`), `app.js`, `scada-core.js`, `signalr.min.js` — and `/scada/config` (`{authority, clientId}`); `/scada` redirects to `/scada/`; anything else under `/scada/`, and everything when `Enabled: false`, answers 404; no file-system lookup. Headers on every `/scada` response: exact `Content-Type` (`text/html; charset=utf-8`, `text/javascript; charset=utf-8`, `application/json; charset=utf-8`), `Cache-Control: no-store`, `X-Content-Type-Options: nosniff`, `Referrer-Policy: no-referrer`, CSP exactly `default-src 'none'; script-src 'self'; style-src 'self' 'unsafe-inline'; connect-src 'self' <authority origin>; img-src 'self' data:; frame-ancestors 'none'; base-uri 'none'; form-action 'self' <authority origin>` (`'self'` because browsers apply `form-action` to the IdP's redirect back after the logout POST). `index.html` loads `signalr.min.js` (UMD) with a classic `<script>` before `app.js` (`type="module"`); no inline script. Data APIs and the hub keep the Viewer policy | separate SPA; `wwwroot`; `ws: wss:` in `connect-src` | same origin, one image; `'self'` covers same-host ws/wss in CSP3 | the page holds no secret | data reachable without a token, or a browser refusing same-host ws under `'self'` → stop and fix |
| D-02 | `Cloud:Scada` options: `Enabled` (default true) and `ClientId` (default `iiot-scada`, `[A-Za-z0-9_.-]{1,64}`); the page's authority is always `Cloud:Auth:Authority` (no override) | a separate `Scada:Authority` | an override can only produce tokens the API rejects | issuer `https://keycloak:8443/realms/iiot` reachable from the browser via the hosts entry | — |
| D-03 | Realm: `Operator` becomes a composite role containing `Viewer`; client `iiot-scada` exactly: `publicClient` true, `standardFlowEnabled` true, `implicitFlowEnabled`/`directAccessGrantsEnabled`/`serviceAccountsEnabled` false, `fullScopeAllowed` false, `defaultClientScopes` `["basic"]`, `optionalClientScopes` `[]`, `redirectUris` `["http://localhost:8080/scada/", "http://127.0.0.1:8080/scada/", "http://localhost:8090/scada/", "http://127.0.0.1:8090/scada/"]`, `webOrigins` `["http://localhost:8080", "http://127.0.0.1:8080", "http://localhost:8090", "http://127.0.0.1:8090"]` (8090: the documented `IIOT_API_PORT` where Windows http.sys holds 8080), `"attributes": {"pkce.code.challenge.method": "S256", "post.logout.redirect.uris": "http://localhost:8080/scada/##http://127.0.0.1:8080/scada/##http://localhost:8090/scada/##http://127.0.0.1:8090/scada/"}`, the `roles` and `iiot-cloud-api` audience mappers equal to `iiot-cli`'s; a realm `scopeMappings` entry gives `iiot-scada` only `Viewer` | wildcard redirects; full scope | no open redirect; a browser token can never carry `Operator` | default API port 8080 | another port → documented realm edit |
| D-04 | Hand-written Authorization Code + PKCE (S256), `scope=openid`: `state` (≥ 128-bit random) and `code_verifier` in `sessionStorage` only until the callback, then removed; no token request when `state` is missing or differs or `error` is present; code/state removed from the URL with `history.replaceState`; access/refresh/id tokens in memory only — never web storage, console or a URL built by the page; the one accepted exception is the `access_token` query SignalR adds on `/hubs/live` (server-side request logging stays at `Warning`, `src/Cloud/appsettings.json:6`); refresh when the access token has < 30 s left (checked on a timer and inside `accessTokenFactory`), a failed refresh starts a new login; a 401 within 60 s of a completed login, or two 401s in a row, stops with "Token không được Cloud chấp nhận" and a "Đăng nhập lại" button instead of redirecting (flag in `sessionStorage`, no token in it); logout calls the end-session endpoint with `id_token_hint` | `keycloak-js`; implicit; password grant | small auditable code | Keycloak 26 token endpoint answers CORS for listed web origins | CORS error → check `webOrigins` |
| D-05 | Live values: vendored `@microsoft/signalr` 8.0.7 browser build (MIT, SHA-256 in the Receipt). Connection lifecycle lives in `scada-core.js` with an injected connection: `JoinEdge(current selection)` after start and after every reconnect, `LeaveEdge` on change; `onclose` (including token-expiry closes) restarts with backoff 1, 2, 5, 10, 30 s capped, forever. Each Edge/tag selection increments a generation; responses and pushes from an older generation are dropped. Rows merge by tag, newer wins by `Date.parse(timestampUtc)` (never string comparison), for both `latest` and pushes; pushes for another Edge are ignored | CDN; polling | works on a customer LAN; CSP `script-src 'self'` | hub payload shape as cited | contract change → update mapping + test |
| D-06 | Trend: one tag, windows 15 min and 1 h (≤ 3 600 samples at the default 1 s), one `history` request `from = now − window`, `to = now` (ISO UTC `Z`), `limit=10000`; numbers as an SVG polyline, Bool as a 0/1 step line, null/bad samples as gaps; `truncated: true` shows "Chỉ hiển thị 10 000 mẫu đầu tiên của khoảng này" | 8 h/24 h; paging; chart library | GATE-REVIEW R-01 | poll interval ≥ ~0.4 s | longer windows → new feature with aggregation |
| D-07 | Page states: logging in, no Edge, loading, empty, stale (no push for > 30 s: "Không nhận dữ liệu mới" — fixed, documented limitation for Edges polling slower than 30 s), 401 per D-04, 403 → "Tài khoản không có quyền xem (cần role Viewer)", network error banner; Vietnamese; native `select`/`button`; values written with `textContent` | — | Edge monitor state set | — | — |

## Coverage profile
| ID | Outcome | Change kinds | Material surfaces | Ambiguity/action | Risk/evidence | Required proof |
|---|---|---|---|---|---|---|
| CP-01 | The page is served without a token while every data path still requires a Viewer token; the realm offers a PKCE-only, Viewer-only browser client | add, modify | Cloud HTTP surface, CSP, Keycloak realm | none | critical — auth | source (Cloud.Tests host with test JWTs; realm JSON test) |
| CP-02 | Login, token handling, connection lifecycle, live table and trend behave per D-04..D-07, also in CI | add | browser script, CI | none | critical — tokens in the browser | source (`node --test` on `scada-core.js` locally and in CI; Cloud serving test) |
| CP-03 | A Viewer logs in through Keycloak in a real browser on the compose stack and sees rows keep updating past a token lifetime | integrate | compose stack, browser, docs | none | elevated | installed (smoke) + live (user browser check > 6 min) |

## Acceptance criteria
| ID | EARS criterion | Proof |
|---|---|---|
| AC-01 | When a request without a token reaches `/scada/`, an allow-listed script or `/scada/config`, the Cloud shall serve it with the D-01 headers; any other `/scada/…` path, and every `/scada` path while `Scada:Enabled` is false, shall answer 404; `/api/v1/*` and `/hubs/live` shall still answer 401 (403 without the role). | task-01 command, task-02 command |
| AC-02 | The realm shall contain `iiot-scada` and the composite/scope mapping exactly as D-03; Keycloak shall refuse it a password grant with `unauthorized_client`, and an Operator's token from it shall carry `Viewer` but not `Operator`. | task-01 command; task-03 smoke + user `operator` login |
| AC-03 | If the login callback has a missing or wrong `state` or an `error`, the page shall not request a token; tokens shall never be written to web storage, the console or a URL built by the page (D-04 exception for the hub query). | task-02 command |
| AC-04 | When a Viewer selects an Edge, the page shall show its latest values and apply newer `telemetry` pushes for that Edge, ignoring other Edges, older samples and responses from an earlier selection, and shall re-join the selected Edge after every reconnect or restart. | task-02 command |
| AC-05 | When a Viewer opens a tag's trend, the page shall request one 15 min or 1 h window per D-06 and show the truncation notice when `truncated` is true. | task-02 command |
| AC-06 | When the API answers 401 the page shall start a new login, except per D-04's loop guard; when it answers 403 it shall show the no-permission message. | task-02 command |
| AC-07 | On the compose stack, a Viewer shall log in through Keycloak in a browser and see the Edge's rows keep updating for more than 6 minutes without any PLC. | task-03 command + user check |

## Tasks
| # | Task | Priority | Criteria | Primary ownership | Dependencies | Status |
|---|---|---|---|---|---|---|
| 01 | Cloud serves the SCADA shell anonymously; realm browser client | P1 | AC-01, AC-02 | `src/Cloud/Api/ScadaEndpoints.cs`, `deployments/keycloak/iiot-realm.json` | - | done |
| 02 | Page logic: PKCE login, connection lifecycle, live table, trend; CI step | P1 | AC-01, AC-03, AC-04, AC-05, AC-06 | `src/Cloud/Scada/app.js`, `src/Cloud/Scada/scada-core.js`, `.github/workflows/ci.yml` | task-01-scada-shell-and-realm.md | done |
| 03 | Smoke checks and docs; user browser check on the compose stack | P2 | AC-02, AC-07 | `deployments/scripts/smoke-test.sh`, `docs/deployment.md`, `docs/server-deploy.md` | task-02-page-login-live-trend.md | blocked |

## Review log
- Round 1 (2026-10-28, three fresh reviewers: security, failure modes, facts/scope): 29 raw findings → 15. R-01 High
  8 h/24 h windows lose the newest data; R-02 High 401 redirect loop; R-03 High fallback policy makes anonymous 404s
  impossible; R-04 High hub lifecycle on token-expiry close untested; R-05 High vacuous password-grant smoke check;
  R-06 Medium Operator token in the browser can send commands; R-07 timestamp string comparison; R-08 Edge-switch race;
  R-09 AC-03 vs SignalR query token; R-10 CSP `ws: wss:`, Content-Type, Referrer-Policy, UMD script; R-11 realm
  attribute format, `scope=openid`, client scopes; R-12 realm upgrade path and contradicting docs; R-13 node tests not
  in CI; R-14 AC-01 mapping and stale threshold; R-15 five stale citations. User: R-01 → 15 min / 1 h only; R-06 →
  Viewer-only SCADA token; all others accepted. Applied to D-01..D-07, AC-01..AC-07, tasks 01-03; citations re-run.
- Round 2 (2026-10-28, fresh closure reviewer): R-01, R-02, R-04, R-05, R-07..R-15 PASS; R-03 FAIL on the
  `%2e%2e` case (normalised to `/appsettings.json`, answered 401 by the fallback policy — probed with `System.Uri`);
  R-06 PASS at source, runtime UNKNOWN. New: N-01 High (that oracle), N-02 Medium (no runtime proof of the
  Viewer-only Operator token), N-03 Low (smoke counterexample reasoning). User accepted all three; applied to
  task-01 Step 5, task-03 Step 3/Acceptance/Oracle/Counterexample and the AC-02 proof column. R-03 and R-06 close
  with these repairs (R-06 at live level in task-03). Paper review closed (two rounds); later claims need runtime
  evidence. Sweep: 4 files reread / 3 deltas / 0 stale references / 0 conflicts.
- Implementation (2026-10-28, task-02 code review): High — the logout form POST's redirect back to `/scada/` would be
  blocked by `form-action <idp>`. User chose to add `'self'` to `form-action` (D-01 updated; logout keeps the POST with
  `id_token_hint`).
