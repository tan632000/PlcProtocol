# Task 03 — Smoke checks and docs; a Viewer uses the page on the compose stack

Status: done

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

Verification: PASS
Command: bash deployments/scripts/smoke-test.sh
Exit: 0
Base: 1f88343f9a04911635220c8479c77c0afd785cec
Head: fc16b52cab7c4a5afea86e49551e520e765ed1e4593bb2544fbfff7328c6fedc
```text
$ bash deployments/scripts/smoke-test.sh   # CI run 40664556749, job smoke (122735665318), head 99953c82a751b496691ab2c0b53ae6fb45fc4d33, conclusion success
PASS SMOKE-00 static: per-file secret mounts, no ca.key, digests everywhere, .dockerignore, no password on argv
PASS SMOKE-01 /health/ready 200
PASS SMOKE-02 no token -> 401
PASS SMOKE-03 viewer token -> 200 with smoke-edge (labelled smoke-edge)
PASS SMOKE-04 viewer POST -> 404 (read-only default)
PASS SMOKE-05 operator POST -> 404 (read-only default)
PASS SMOKE-06 no private key or generated secret in any image layer
PASS SMOKE-08 /scada/ without a token -> 200 with the CSP
PASS SMOKE-09 /scada/config -> iiot-scada at https://keycloak:8443/realms/iiot
PASS SMOKE-10 iiot-scada password grant -> 400 unauthorized_client (iiot-cli control -> 200)
PASS SMOKE-07 repository tree unchanged
```
- Where it ran: the CI `smoke` job, as decided by the user; locally the script exits 2 because Windows http.sys holds port 8080. Since 99953c8 only `docs/` and `specs/` changed (`git diff --quiet 99953c8 -- deployments/scripts/smoke-test.sh deployments/compose.yaml deployments/keycloak src` exit 0), so the smoke inputs are the ones CI ran.
- Live (user, 2026-10-29, trial stack at http://localhost:8090/scada/, Edge from Visual Studio with profile "PLC + local broker", no PLC reachable): `viewer` logged in through Keycloak; the "Không nhận dữ liệu mới" banner cleared and row timestamps kept advancing after more than 6 minutes; the 15 min trend showed "Không có mẫu hợp lệ trong khoảng này" (every sample is `bad` without a PLC, drawn as gaps by design); logout returned without a CSP error; `operator` logged in and saw the same table, no chart for the same reason. Before that the user hit `/scada` 401 (Cloud container from an image older than 846187b, fixed by `up -d --build --force-recreate keycloak cloud`) and `DNS_PROBE_FINISHED_NXDOMAIN` (hosts line missing); both are now troubleshooting rows in `docs/deployment.md`.
- AC-02 live level: the user could not copy a full token from devtools, so the controller checked the mapping in Keycloak instead: `generate-example-access-token` for client `iiot-scada` gives `roles=["Viewer"]` for both `operator` and `viewer` (admin password read from file, no token printed); and a real PKCE login replayed with curl for both users reached `GET /api/v1/edges` 200 and `.../tags/latest` 200 (21 tags). This proves the realm mapping and the Cloud side, not the token held by the user's browser tab.
- Issuer probe: a token requested via `https://127.0.0.1:8443` also carries `iss=https://keycloak:8443/realms/iiot`; with `--cacert ca.pem` the 127.0.0.1 TLS handshake fails (certificate name). The docs now say the hosts line is for DNS/TLS, not the issuer.
- Review (code-auditor, fresh context): first PASS_WITH_WARNINGS — Medium: docs blamed a missing hosts line for issuer/401 errors; the realm reload command dropped `IIOT_API_PORT=8090`. Repair round 1 fixed both plus Low 1 (reconnect delays), Low 3 (smoke on http.sys machines), Low 4 (server tunnel needs the CA). Re-review: PASS, 0 Critical/High/Medium. Left as is by the controller: `docs/deployment.md:65` says 127.0.0.1:8443 is for `get-token.sh` (pre-existing), the clock-skew wording, `app.js:117` mentioning the hosts file (task-02), the two missing page states in the table, and the duplicated curl digest.
- Final checks on this tree: `node --test tests/scada/scada-core.test.mjs` 40/40; `dotnet test tests/Cloud.Tests` 143/143; `ScadaRealmTests` 5/5.
