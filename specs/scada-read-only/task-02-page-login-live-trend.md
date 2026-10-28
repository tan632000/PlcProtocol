# Task 02 — The page logs a Viewer in with PKCE and shows live values and a one-tag trend

Status: done

## Outcome
After login the page lists the Edges, shows the selected Edge's latest values kept current by live pushes across
reconnects and token-expiry closes, and draws a 15 min / 1 h trend for one tag, following plan D-04..D-07; the core
tests also run in CI.

## Scope
- In: `scada-core.js` — pure, DOM-free ES module with injected `crypto`, storage, `fetch`, clock and hub connection:
  PKCE pair + `state`, authorize URL (`scope=openid`, `code_challenge_method=S256`), callback validation, token
  request bodies, refresh timing and `accessTokenFactory`, API wrapper (401 → login with the D-04 loop guard, 403 →
  no-permission), connection lifecycle (join after start/reconnect, leave on change, restart backoff 1/2/5/10/30 s),
  selection generations, row merge by `Date.parse`, history query builder, trend points (number / Bool step / gaps),
  stale detection (> 30 s). `app.js` — DOM wiring only (native `select`/`button`, `textContent`); `index.html` gains the
  layout and styles; the two scripts join the Cloud allow-list and embed; one CI step.
- Out: aggregation, metadata, alarms, writes; any Cloud API change.

## Coverage
- CP-02

## Ownership
- Create: `src/Cloud/Scada/app.js`, `src/Cloud/Scada/scada-core.js`, `tests/scada/scada-core.test.mjs`
- Modify: `src/Cloud/Scada/index.html`, `src/Cloud/Api/ScadaEndpoints.cs` (allow-list only),
  `src/Cloud/IndustrialIot.Cloud.csproj` (embed only), `tests/Cloud.Tests/ScadaPageTests.cs` (served-path cases only),
  `.github/workflows/ci.yml` (one `node --test tests/scada/scada-core.test.mjs` step in the test job)
- Read: `src/Cloud/Api/TelemetryEndpoints.cs`, `src/Cloud/Api/EdgesEndpoints.cs`, `src/Cloud/Live/LiveHub.cs`,
  `src/Cloud/Live/LiveNotifier.cs`, `src/Shared/Common/ContractJson.cs`, the Shared telemetry payload contract

## Steps
1. Write `scada-core.test.mjs` first (node:test, node:assert) → fails (module missing).
2. Implement `scada-core.js` → tests pass.
3. Implement `app.js` and the `index.html` layout; add both scripts to the allow-list and embed → `ScadaPageTests`
   serves them with the D-01 headers (`text/javascript; charset=utf-8`).
4. Add the CI step.
5. Grep `app.js` and `scada-core.js` for `localStorage`, `console.` near a token, `eval`, `innerHTML` → none.

## Acceptance
- AC-01 (scripts part): `app.js` and `scada-core.js` served anonymously with the D-01 headers.
- AC-03: tests prove no token request on missing/wrong `state` or `error`; storage holds only `state`,
  `code_verifier` and the loop-guard flag, and nothing token-like; URLs built by the module never contain a token.
- AC-04: tests prove newer-wins with mixed timestamp formats (`…:00Z` vs `…:00.500Z`), older ignored, other Edge
  ignored, unknown tag appended, a `latest` response or push from an earlier selection dropped, `JoinEdge(current)`
  after reconnect and after a restart following `onclose`, backoff sequence.
- AC-05: tests prove the exact history URL (ISO `Z`, 15 min / 1 h, `limit=10000`, encoded `tagId`), the truncation
  notice, Bool step and null gaps.
- AC-06: tests prove 401 → login, 401 within 60 s after login or twice in a row → stop with the message (no
  redirect), 403 → no-permission state.

## Dependencies
- task-01-scada-shell-and-realm.md

## Verification Plan
- Command: `node --test tests/scada/scada-core.test.mjs && dotnet test tests/Cloud.Tests --filter "FullyQualifiedName~ScadaPageTests"`
- Named probe: `tests/scada/scada-core.test.mjs` (one named case per rule above) and `ScadaPageTests`.
- Reachability: Node 24 on the dev machine (`node --version` → v24.18.0) and the GitHub `ubuntu-latest` runner's
  preinstalled Node; Cloud.Tests needs Docker.
- Oracle: node reports `fail 0` with `pass` > 0, dotnet tests pass, exit 0.
- Counterexample: accepting a callback with a wrong `state`, comparing timestamps as strings, applying a response from
  an earlier selection, or skipping the re-join after reconnect must each fail a test (checked on a disposable copy
  during review).
- Artifacts: ephemeral.

## Failure Protocol
On a failed Step or Verification Plan run: stop; do not widen scope, change the Command, or weaken a test; record observed versus expected; repair only the cited cause; after three failed rounds, stop and ask the user.

## Receipt

Verification: PASS
Command: node --test tests/scada/scada-core.test.mjs && dotnet test tests/Cloud.Tests --filter "FullyQualifiedName~ScadaPageTests"
Exit: 0
Base: a7d4c011c2a0f72dbb09e395d5fb7058658095c2
Head: 7141308b4f5c9e67b89edda5d4e891e563d34cfa99e55acaf55bba4ec5a187cb
```text
$ node --test tests/scada/scada-core.test.mjs && dotnet test tests/Cloud.Tests --filter "FullyQualifiedName~ScadaPageTests"
ℹ tests 40
ℹ suites 0
ℹ pass 40
ℹ fail 0
ℹ cancelled 0
ℹ skipped 0
Passed!  - Failed:     0, Passed:    25, Skipped:     0, Total:    25, Duration: 9 s - Cloud.Tests.dll (net10.0)
```
- Pre-change run: `node --test` reported "Could not find 'tests/scada/scada-core.test.mjs'" (exit 1) — the test file this task creates (Ownership, Step 1); after writing the tests and before the module: `ERR_MODULE_NOT_FOUND` for `src/Cloud/Scada/scada-core.js`.
- Repair round 1 (code review FAIL): High — the logout POST's redirect back to `/scada/` would be blocked by `form-action <idp>`; user chose `form-action 'self' <idp>` (D-01 updated, `ScadaEndpoints.cs` + `ExpectedCsp`). Also fixed: network retries for the Edge list and latest, a loading state, stopped page keeps "Dừng", trend failure message, `stop()` closes the hub, join-failed cleared by a later join, bad samples with a value are gaps, aria labels, async handlers guarded.
- Repair round 2 (review PASS_WITH_WARNINGS): Medium — `'connected'` was reported after a join skipped because the hub was down; now only while the hub is Connected (new test). Also: no API call once the page stopped, latest retries follow the selection generation, no token refresh once stopped.
- Counterexamples on a disposable copy (scratchpad `mut-t02`, current code): accepting a wrong state → 1 failed; string timestamp comparison → 1; latest ignoring the generation → 1; no re-join after reconnect → 2; no 401 loop guard → 2; `'connected'` while down → 1; unmutated control 40/40.
- Step 5 grep (`localStorage`, `console.`, `eval(`, `innerHTML`, `outerHTML`, `insertAdjacentHTML`, `document.write`, `new Function`) in `app.js`/`scada-core.js`: one match, a comment.
- CI: `actionlint 1.7.7` clean and every `uses:` SHA-pinned (`actions/setup-node@949feb2413d6458794dcd2491c4babbbce0c15c1` = v7.1.0, released 2026-10-08, Node 24) — run with the server-deploy task-04 command; the CI run itself happens on the next pull request.
- Review (code-auditor, fresh context, two re-reviews after repairs): PASS, 0 Critical/High/Medium. Low left as is by the controller: a non-network failure of `latest` shows "Đang tải…" with the error banner until the first push. The logout redirect under `form-action 'self'` is confirmed only by task-03's browser check.
- The CSP change touches task-01's `ScadaEndpoints.cs`; `ScadaPageTests` (task-01's probe) passes 25/25 above; the full Cloud suite runs again before GATE-DONE.
