# Task 02 — One command starts the trial stack and Keycloak tokens drive the Cloud API

Status: done

## Outcome
`docker compose -f deployments/compose.yaml up --build --wait` (after `dev-secrets.sh`) starts PostgreSQL, Mosquitto, Keycloak and the Cloud as project `iiot`; `smoke-test.sh` proves the API end to end with real Keycloak tokens in its own isolated project; `docs/deployment.md` explains the stack in Vietnamese.

## Scope
- In: Cloud Dockerfile, repository-root `.dockerignore`, compose file, Keycloak realm import, `get-token.sh`, `smoke-test.sh`, runbook (stack part), CVE scan of the pinned images.
- Out: Edge installation (task-03), CI (task-04), proxy/TLS for the API (excluded at GATE-SCOPE).

## Coverage
- CP-02

## Ownership
- Create: `deployments/cloud/Dockerfile`, `.dockerignore`, `deployments/compose.yaml`, `deployments/keycloak/iiot-realm.json`, `deployments/scripts/{get-token.sh,smoke-test.sh}`, `docs/deployment.md`
- Modify (user decision 2026-10-19, review M-4): `deployments/scripts/dev-secrets.sh` writes `Cloud__Edges__{i}__DisplayName=<EdgeId>` next to every `EdgeId` line (create and `--add-edge`), so the default `Line 1` label of `appsettings.json` never names another Edge.
- Modify (user decision 2026-10-19): `tests/Cloud.Tests/Infrastructure/PostgresFixture.cs` — test connections use `Timeout=60`: with `Deployment.Tests` loading Docker in parallel, `Cloud.Tests` hit Npgsql's 15 s connect timeout in 3 of 4 full runs on a 4-CPU/4 GB Docker Desktop.
- Read: `deployments/scripts/dev-secrets.sh` (outputs), `deployments/mosquitto/**`, `src/Cloud/appsettings.json`, `src/Cloud/Security/{AuthOptions.cs,Policies.cs}`, `src/Cloud/CloudOptions.cs`, `src/Cloud/Health/ReadinessMonitor.cs`

## Contracts
- Compose (`name: iiot` by default, overridable with `-p`); images pinned by digest: `postgres:17-alpine` (volume `pgdata`, healthcheck `pg_isready`), `eclipse-mosquitto:2.0.20` (`${IIOT_MQTT_PORT:-8883}:8883`, config and only `ca.pem`/`server.pem`/`server.key`/`passwd` mounted read-only, volume `mosquitto-data`), `quay.io/keycloak/keycloak:26.0.7` (D-04; only its `server.pem`/`server.key` mounted; `127.0.0.1:${IIOT_KEYCLOAK_PORT:-8443}:8443`), `cloud` (built from `deployments/cloud/Dockerfile` with context = repository root, `127.0.0.1:${IIOT_API_PORT:-8080}:8080`, `env_file: <secrets>/cloud.env`, only `ca.pem` mounted, `depends_on` healthy postgres/mosquitto/keycloak). The secrets directory is a variable (`IIOT_SECRETS`, default `./.secrets`).
- Cloud environment: `Cloud__Auth__Authority=https://keycloak:8443/realms/iiot`, `Cloud__Auth__Audience=iiot-cloud-api`, `Cloud__Mqtt__Host=mosquitto`, `Cloud__Mqtt__Username=iiot-cloud`, `Cloud__Mqtt__CaCertificatePath=/run/iiot/ca.pem`, `SSL_CERT_FILE=/run/iiot/ca.pem`; the Edge allow-list comes only from `cloud.env` (`Cloud__Edges__{i}__EdgeId`, written by `dev-secrets.sh`, D-03); after `--add-edge` the operator runs `docker compose -f deployments/compose.yaml up -d --force-recreate mosquitto cloud` (runbook; not `restart`, which keeps the old `env_file` values).
- Realm as D-04.
- `get-token.sh <user> [--secrets DIR] [--project NAME]`: runs `curl` in a pinned container on the compose network, reading the user's password from the mounted `passwords/<user>.txt` (never argv or `-e`); prints only the access token.
- `smoke-test.sh`: creates its own secrets with `dev-secrets.sh --out <temp> --edge smoke-edge`, runs compose as project `iiot-smoke` with `IIOT_SECRETS=<temp>` (so the Cloud allow-list is `smoke-edge` only) on the default ports; exits 2 without touching anything if those ports are in use (e.g. the trial stack is running) or the project already exists; never addresses project `iiot`; records `git status --porcelain` before starting; checks `SMOKE-00` static: `compose.yaml` mounts no secrets directory and no `ca.key`, every image reference in `deployments/**` (compose `image:`, Dockerfile `FROM`, `docker run` lines in `deployments/scripts/*.sh`) and in `tests/Deployment.Tests/**` carries `@sha256:`, the root `.dockerignore` lists `deployments/.secrets/`, `**/*.key`, `**/*.env`, and `get-token.sh` passes no password through argv or `-e`; `SMOKE-01` ready 200 within 120 s, `SMOKE-02` `/api/v1/edges` without token 401, `SMOKE-03` viewer token 200 listing `smoke-edge`, `SMOKE-04` viewer POST command 403, `SMOKE-05` operator POST command 202 `published` (tag `Smoke.Never`, edge `smoke-edge`), `SMOKE-06` the layers of the Cloud image (`docker save`, every layer, not only the final filesystem) contain no `PRIVATE KEY` block, no path under `.secrets`, and none of the password values present under `deployments/.secrets/passwords/` when that directory exists (values are compared, never printed); `SMOKE-07` `git status --porcelain` is identical to the one recorded before the run; prints `PASS <id>`/`FAIL <id>`; `down -v` and deletes the temp secrets at the end, also on failure; exit 0 only when all pass.
- Runbook `docs/deployment.md` (Vietnamese): prerequisites (Docker Desktop, Git Bash), secrets (incl. `--san` with the broker host's fixed IP/hostname, `--add-edge`), start/stop, tokens, Windows firewall inbound rule for 8883, troubleshooting, and the trial limits: HTTP API on localhost, dev CA, password grant, secrets visible to `docker inspect`, Docker port publishing bypassing host firewall rules on Linux, trusted network and single-user host only.
- CVE scan: the pinned images are scanned once with a scanner image referenced by digest (e.g. `aquasec/trivy@sha256:...`, digest recorded in the receipt); High/Critical findings are listed in the receipt and reported to the user (not auto-fixed).

## Steps
1. Dockerfile + `.dockerignore`; compose; realm; scripts.
2. Run the smoke test; write the runbook; run the CVE scan.

## Acceptance
- AC-04: `SMOKE-01`..`SMOKE-05` pass in project `iiot-smoke`; a running `iiot` stack makes the smoke test exit 2 without change.
- AC-05: `SMOKE-00`, `SMOKE-06` and `SMOKE-07` pass.
- R-14: `SMOKE-00` proves every image is pinned by digest.

## Dependencies
- task-01-broker-tls-acl.md

## Verification Plan
- Command: `& "$env:ProgramFiles\Git\bin\bash.exe" deployments/scripts/smoke-test.sh`
- Platform note: the Command is PowerShell on Windows; the Linux equivalent is `bash deployments/scripts/smoke-test.sh`.
- Negative control (run once and recorded in the receipt): with project `iiot` started from its own secrets, the same Command exits 2 and the `iiot` containers keep running.
- Named probe: `SMOKE-00` … `SMOKE-07` (lines in the script output)
- Reachability: installed — Docker engine with compose v2, Git Bash, ports 8080/8443/8883 free; `dotnet test IndustrialIot.sln` still exits 0.
- Oracle: exit 0 and eight `PASS SMOKE-0n` lines; any `FAIL` or timeout → non-zero; the negative control exits 2.
- Counterexample: a missing audience mapper (→ 401 at `SMOKE-03`), roles not mapped to `roles` (→ 403 at `SMOKE-03`), the Cloud not trusting the dev CA for Keycloak (→ 401 at `SMOKE-03`), a missing `Cloud__Mqtt__CaCertificatePath` (→ not ready at `SMOKE-01`), `iiot-cli` without the `basic` scope (→ 403 at `SMOKE-05`), a secret or key copied into the image even if deleted in a later layer (→ `SMOKE-06`), an image without a digest or a missing `.dockerignore` entry (→ `SMOKE-00`), a file written into the tracked tree such as a rendered realm (→ `SMOKE-07`) must fail the run.
- Artifacts: ephemeral (`down -v`, temp secrets deleted).

## Failure Protocol
On a failed Step or Verification Plan run: stop; do not widen scope, change the Command, or weaken a test; record observed versus expected; repair only the cited cause; after three failed rounds, stop and ask the user.

## Receipt

Verification: PASS
Command: & "$env:ProgramFiles\Git\bin\bash.exe" deployments/scripts/smoke-test.sh
Exit: 0
Base: 8f23ff1bb1d7b0e5c78cd79ee7ff9fed2bbdcb72
Head: e68c8443e1a3d246d0999bc04b884460ad1bb8e9e114977f22ba124028e79419
```text
$ & "$env:ProgramFiles\Git\bin\bash.exe" deployments/scripts/smoke-test.sh
Smoke test in compose project 'iiot-smoke' with temporary secrets
PASS SMOKE-00 static: per-file secret mounts, no ca.key, digests everywhere, .dockerignore, no password on argv
Starting the stack (first build may take a few minutes)...
PASS SMOKE-01 /health/ready 200
PASS SMOKE-02 no token -> 401
PASS SMOKE-03 viewer token -> 200 with smoke-edge (labelled smoke-edge)
PASS SMOKE-04 viewer POST -> 403
PASS SMOKE-05 operator POST -> 202 published
PASS SMOKE-06 no private key or generated secret in any image layer
PASS SMOKE-07 repository tree unchanged
Smoke test passed.
smoke exit=0
```
- Same final tree: `dotnet test IndustrialIot.sln` exit 0 — Shared 47, Edge 279, Deployment 11, Cloud 98 passed, `Skipped: 0`.
- Pre-change run: `smoke-test.sh: No such file or directory`, exit 127 — the deliverable did not exist yet, as expected.
- Negative control (recorded): with project `iiot` started from its own secrets (4 containers running), the Command exited 2 ("port 8080 is in use"), the 4 `iiot` containers kept running and no `iiot-smoke` project was created; the stack was then removed with `down -v`.
- Counterexamples (mutations on scratch copies, full stack each), every one caught at the expected step: no audience mapper, no roles mapper, no `SSL_CERT_FILE` (SMOKE-03); no `Cloud__Mqtt__CaCertificatePath` (SMOKE-01); no `basic` scope (SMOKE-05); image without digest, `.dockerignore` without `**/*.env`, secrets folder mounted, unpinned image on a continuation line in get-token, `--env-file` in get-token, `docker run -d`/`docker create` with a literal image, `"postgres:17-alpine"`/`"alpine:3.20"`/`"postgres:17"` in tests (SMOKE-00; `"localhost:8883"` correctly not flagged); private key COPY'd then `RUN rm`, the same with CRLF line endings, one broker password in a published `appsettings.Production.json` (SMOKE-06); a file written into the tracked tree and into the untracked `deployments/` folder (SMOKE-07).
- CVE scan (`aquasec/trivy@sha256:ab70a022…`, HIGH/CRITICAL, reported not fixed): eclipse-mosquitto 2.0.20 16/2, postgres 17-alpine 24/1, keycloak 26.0.7 89/7, dotnet aspnet 10.0 0/0, curlimages/curl 8.11.1 28/2 — almost all have fixed versions; bumping the pins is left to the user.
- Repair rounds: implementation — Windows paths for git/docker/tar (`git -C /c/...` had made SMOKE-07 pass vacuously; a git failure is now fatal), comment lines ignored, `tar --force-local`, Debian's libgnutls sample key in the Microsoft base layer (generic PEM check skips base-image layers by diff id; exact-match check covers every layer). R1 review FAIL — Critical: passwords concatenated into one needle (a single leaked password never matched); Mediums: SMOKE-07 blind to files inside untracked folders, PEM check missed CRLF/OpenSSH keys, SMOKE-00 holes, Edge 0 kept the "Line 1" label (user decision: dev-secrets writes `DisplayName`) → fixed; full-suite flakiness ("Database migration failed: The operation has timed out" with Deployment.Tests loading Docker in parallel on a 4-CPU/4 GB Docker Desktop; user decision: test connections `Timeout=60`) → 3 consecutive green runs. R2 PASS_WITH_WARNINGS — the repaired test-image filter missed official images without "/" → three-source check. R3 PASS_WITH_WARNINGS → PASS: removing double-quoted strings hid `"$(docker run …)"` → command-position detection. Final re-review: code-auditor PASS (0 Critical/High/Medium).
- Known limitations: rare docker invocations (`if ! docker run`, `sudo docker run`, `docker --context x run`) and reordered `docker run -e … --rm` are not recognised by SMOKE-00; PostgreSQL is not published at all (stricter than D-06's 127.0.0.1); skipping base-image layers needs `docker image inspect` of the pinned base (an image store without it would fail SMOKE-06 falsely, never pass it); the pinned images carry the CVEs above; `docker inspect` shows env-file secrets; the API is HTTP on localhost; temporary negative-control secrets were left in the session scratchpad (`negctl-secrets`).
