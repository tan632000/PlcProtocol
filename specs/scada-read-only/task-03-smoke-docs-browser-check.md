# Task 03 — Smoke checks and docs; a Viewer uses the page on the compose stack

Status: blocked
Blocker: `bash deployments/scripts/smoke-test.sh` exits 2 on the dev machine (port 8080 held by Windows http.sys, PID 4); per the user (2026-10-28) it runs in CI on the pull request instead, and the browser check runs on the trial stack at port 8090 (now in the realm). Waiting for that CI run and the user's browser check.

## Outcome
The smoke test proves on a fresh compose stack that the page is served and that Keycloak refuses `iiot-scada` a
password grant for the right reason; the docs explain opening the SCADA page and upgrading an existing stack; the user
logs in as `viewer` in a browser and sees rows keep updating for more than 6 minutes.

## Scope
- In:
  - Smoke checks (next free `SMOKE-NN`, header comment updated): `/scada/` anonymous 200 with the CSP header;
    `/scada/config` `clientId` = `iiot-scada`; a password grant for `iiot-scada` with the real `viewer` password
    (file-mounted, run through `docker run` with a digest-pinned `*_IMAGE`, as `get-token.sh:37-43` does; never on
    argv) answers HTTP 400 with `"error":"unauthorized_client"`, and the same request with `iiot-cli` returns an
    `access_token` (control).
  - `docs/deployment.md`: section "Mở trang SCADA" (hosts entry, importing the dev `ca.pem` into the Windows trust
    store as the main path, URL `http://localhost:8080/scada/`, `viewer`/`operator`, the page states, the 30 s stale
    limitation, read-only and Viewer-only token notes, `docker compose up -d --force-recreate keycloak` to load the new
    realm on an existing stack); correct the browser-token sentence (`:85-86`) and the 401 troubleshooting row
    (`:152`).
  - `docs/server-deploy.md`: one note that this realm change needs the manual upgrade (`remote-deploy.sh:52-53`).
  - The user's browser check with an Edge running against an unreachable PLC.
- Out: production URL/domain; numeric trend on live data (no PLC; covered by task-02 tests).

## Coverage
- CP-03

## Ownership
- Modify: `deployments/scripts/smoke-test.sh`, `docs/deployment.md`, `docs/server-deploy.md`
- Read: `deployments/compose.yaml`, `deployments/scripts/get-token.sh`, `deployments/scripts/remote-deploy.sh`

## Steps
1. Add the smoke checks → they fail on the base commit's image/realm, pass after.
2. Write and correct the docs.
3. User check: start the compose stack (recreate Keycloak), hosts entry and CA in place, start an Edge (no PLC), open
   `http://localhost:8080/scada/`, log in as `viewer`, select the Edge, watch rows refresh for more than 6 minutes
   (past one access-token lifetime), open a 15 min trend, log out. Then log in as `operator`: the data shows, and the
   access token's `roles` claim (decoded in the browser devtools, never copied out) holds `Viewer` only. Record what
   was seen (no secrets, no token) in the Receipt notes as a `- Live:` line.

## Acceptance
- AC-02 (installed level): the refusal check passes with the control succeeding.
- AC-02 (live level): the user's `operator` login shows data and a `roles` claim of `Viewer` only.
- AC-07: smoke passes and the user's > 6 min browser check is recorded.

## Dependencies
- task-02-page-login-live-trend.md

## Verification Plan
- Command: `bash deployments/scripts/smoke-test.sh`
- Named probe: the new `SMOKE-NN` lines for `/scada/`, `/scada/config` and the password-grant refusal + control,
  plus every existing check.
- Reachability: Git Bash + Docker on the user's machine (the script refuses to run when the trial stack's ports are
  in use) and the CI smoke job; the browser check needs the user.
- Oracle: every line `PASS`, exit 0; the user reports login and row refresh after > 6 minutes, and the `operator`
  token's `roles` = `Viewer` only.
- Counterexample: enabling `directAccessGrantsEnabled` for `iiot-scada` must turn the refusal check into `FAIL`. Keycloak
  answers `unauthorized_client` before checking the password, so the refusal line passes only when the `iiot-cli`
  control with the same password returns an `access_token`; a wrong password therefore fails the line via the control.
- Artifacts: ephemeral (the smoke project removes itself).

## Failure Protocol
On a failed Step or Verification Plan run: stop; do not widen scope, change the Command, or weaken a test; record observed versus expected; repair only the cited cause; after three failed rounds, stop and ask the user.

## Receipt
