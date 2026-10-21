# Server deploy: CI on pull requests, CD from main to the AWS Linux server
Specs-Contract: process-first-ready-v1

## Scope decision (GATE-SCOPE — 2026-10-21)
- Request: automatic CI/CD modelled on `vn0047-haiphuong-erp` (`.github/workflows/ci.yml` on pull requests,
  `.github/workflows/cd.yml` builds images to GHCR then deploys over SSH). The Edge stays installed on the customer's
  Windows PC (not deployed by CD); the Cloud stack runs on an AWS Linux server; the domain is not obtained yet.
- Existing:
  - Server stack in one compose file: PostgreSQL, Mosquitto TLS + ACL, Keycloak (`start-dev`, no data volume), Cloud
    built locally (`deployments/compose.yaml:72-75`), API published on `127.0.0.1:8080` only (`deployments/compose.yaml:86-87`);
    the read-only ACL default and the Cloud `environment:` block live in that tracked file (`deployments/compose.yaml:31`, `:77-85`).
  - Cloud image: multi-stage, base images pinned by digest, non-root, no secrets (`deployments/cloud/Dockerfile:4-29`); the
    Cloud migrates forward at start (`src/Cloud/Data/DatabaseMigrator.cs:17`, migrations in `src/Cloud/Data/Migrations/`)
    and answers `/health/ready` (`src/Cloud/Health/HealthEndpoints.cs:10`).
  - Secrets: `deployments/scripts/dev-secrets.sh` refuses to overwrite a set (`:186`); `ca.key` and every password stay in
    `deployments/.secrets/` (gitignored, `.gitignore:24`); Postgres user/db `iiot` (`dev-secrets.sh:239`).
    `deployments/.env` is not gitignored.
  - Smoke SMOKE-00 requires `@sha256:` on every compose `image:` and on every `^[A-Z_]*IMAGE=` script line
    (`deployments/scripts/smoke-test.sh:85-90`); the smoke export/cleanup use the image name `${PROJECT}-cloud` (`:57`, `:172`).
  - Bash-script tests start Git Bash on Windows, whose own `/mingw64/bin` precedes a caller's PATH (reviewer run,
    2026-10-21; `tests/Deployment.Tests/BrokerTests.cs:457-460`).
  - A CI plan exists but never ran: `specs/deployments-ci/task-04-ci-github-actions.md`. No `.github/` directory.
  - GitHub (checked 2026-10-21 with `gh api`): `tan632000/PlcProtocol` public, default branch `main`, **`main` unprotected**,
    no environments; the local `gh` account `hapo-tanhv` has `push: false` and no `read:packages`.
- Minimum change: a server deploy script, a compose image switch, a runbook, a CI workflow, a CD workflow.
- Expansion signals: HTTPS, Keycloak production mode, scheduled backups, monitoring — deferred (need the domain or are independent).
- User decisions: **KEEP**; server = **AWS** (Edges reach it over the Internet on 8883); Cloud API stays on **127.0.0.1**
  until the domain exists (SSH tunnel); CI/CD branch = **`main`**; GHCR package **public**; automatic `pg_dump` before a
  deploy that changes migrations; the live steps (push, pull request, GitHub settings) are done by the owner account
  **tan632000**. `specs/deployments-ci` task-04 is superseded by this packet.

## Out of scope
- HTTPS reverse proxy, certbot, Keycloak `start` mode, a public API/SCADA endpoint — a later spec once the domain exists.
  Scheduled backups, monitoring, staging, Edge auto-update, applying broker/Keycloak/compose changes automatically.
- Creating the AWS instance and the GitHub settings themselves: user-owned, described in the runbook.

## Decisions
| ID | Chosen | Rejected | Why | Load-bearing assumption | Break signal → response |
|---|---|---|---|---|---|
| D-01 | `ci.yml` reusable: `pull_request` → `main`, `workflow_dispatch`, `workflow_call`; job `test` (restore/build/test Release, TRX, fail on any `NotExecuted`, upload TRX) and job `smoke` (`bash deployments/scripts/smoke-test.sh`); actions pinned by 40-hex SHA; `permissions: contents: read` | tests on pull requests only | a direct push would deploy untested code; `cd.yml` calls the same jobs | GitHub-hosted Ubuntu runs Docker/Testcontainers and has port 8080 free | a Linux-only assertion fails (never run before) → fix it inside task-04's conditional ownership, or block naming the file |
| D-02 | `cd.yml`: `push` → `main` and `workflow_dispatch` (input `sha` = rollback). Job `guard` validates the ref is `main` and `sha` matches `^[0-9a-f]{40}$` from `env:` (never `${{ }}` in a shell line). Push: `test` (calls `ci.yml`) → `image` (build, push `cloud:<sha>`, output digest). Dispatch: `resolve` reads the digest of `cloud:<sha>` with `docker buildx imagetools inspect`. `deploy`: `environment: production`, explicit `if` on job results, `concurrency: deploy-production` (`cancel-in-progress: false`), plain `ssh` with `DEPLOY_KNOWN_HOSTS`, sending only `<sha> <image@digest> tip|rollback` | `appleboy/ssh-action`; a registry token on the server; deploy by tag | no third-party action holding the key; injection-proof inputs; immutable bytes | the package is public, so neither the server nor `resolve` logs in | pull or inspect denied → the owner makes the package public (runbook) |
| D-03 | `deployments/scripts/remote-deploy.sh`, run as the CI key's forced command (`command=` in `authorized_keys`, arguments from `SSH_ORIGINAL_COMMAND`) or with arguments: validate (`sha`, `ghcr.io/tan632000/plcprotocol/cloud@sha256:<64hex>`, mode, nothing extra) → require `deployments/.secrets/`, a clean tracked tree, `sha` an ancestor of `origin/main` after `git fetch`, `tip` mode exits 0 "skipped" when `sha` is older than the deployed `HEAD` (never go backwards), the target's compose has `IIOT_CLOUD_IMAGE`, and no change between `HEAD` and `sha` under `deployments/mosquitto/`, `deployments/keycloak/` or `deployments/compose.yaml` (exit 2, manual upgrade) → `docker pull` → `pg_dump` (inside the postgres container, `umask 077`, `deployments/.backups/`) when `src/Cloud/Data/Migrations/` changed → `git checkout --detach` → `up -d --no-deps --no-build --wait cloud` → poll `/health/ready` → on failure write the Cloud log tail to `deployments/.deploy-logs/<sha>.log` (600) and print only its path → on success `docker tag <image> iiot-cloud:local`, prune images older than 240 h, print the running image. Never reads or prints secrets | `git reset --hard`; `up -d` for all services | an operator edit, a broker/realm/compose change or a PLC write switch can never arrive through CD; failures stay diagnosable without public logs | Linux `git checkout` replaces the running script's file without changing the open copy | — |
| D-04 | Compose: Cloud `image: ${IIOT_CLOUD_IMAGE:-iiot-cloud:local}` beside `build:`; smoke exports `IIOT_CLOUD_IMAGE="${PROJECT}-cloud"` and SMOKE-00 exempts exactly that line | a production compose file | one stack definition; manual `up` on the server uses the last deployed image (D-03 tag) | Compose uses `image` as the build tag | — |
| D-05 | Runbook `docs/server-deploy.md`: GitHub prerequisites first (protect `main`: pull request + required checks `test`, `smoke`, no bypass; environment `production` limited to `main` with a required reviewer; **environment** secrets `DEPLOY_HOST`, `DEPLOY_USER`, `DEPLOY_SSH_KEY`, `DEPLOY_KNOWN_HOSTS`, optional `DEPLOY_PORT`; package public during the first run's approval window), EC2 + Elastic IP (broker SAN), security group 22 + 8883, `deploy` user, forced-command CI key (`command="/bin/bash …/remote-deploy.sh"`) and a separate operator key, `ca.key` offline, disk size, first manual deploy inside run 1's approval window, resuming CD after a manual upgrade (checkout + dispatch), server-side manual commands, Edge connection, SSH tunnel, rollback limits, migration rule | — | the owner operates GitHub and AWS | — | — |
| D-06 | Migrations stay compatible with the previous release (expand, then contract in a later release); a rollback across a destructive migration needs the `pg_dump` restore described in the runbook | down-migrations | EF migrates forward only | — | — |

## Coverage profile
| ID | Outcome | Change kinds | Material surfaces | Ambiguity/action | Risk/evidence | Required proof |
|---|---|---|---|---|---|---|
| CP-01 | The server deploy script swaps only the Cloud, to an immutable image of a `main` commit, refuses every unsafe state before changing anything, backs up before migrations, and fails diagnosably | add | Runtime/deploy, Security (forced command input, secrets untouched, write switches), Data (backup) | none | critical — production state | source (script tests in a self-contained temporary repository) + live (task-05) |
| CP-02 | Compose runs a published Cloud image; the trial stack and the smoke test still work | modify | Runtime/deploy | none | elevated | installed (compose render) + live (CI `smoke` job, task-04) |
| CP-03 | The owner can secure GitHub, set up the AWS server, connect Edges, reach the API and roll back from the runbook alone | add | Docs/operations, Security | none | elevated | source (doc checks) + live (task-05 settings check) |
| CP-04 | Every pull request to `main`, and every deploy, runs the whole suite and the smoke test on Linux with nothing skipped | add | Integration/proof, supply chain | none | elevated | installed (actionlint + pin check) + live (GitHub run) |
| CP-05 | A push to `main` is tested, published and deployed; a dispatch redeploys an earlier CD-built commit; nothing else can deploy | add | Runtime/deploy, Security (SSH, injection, branch scope) | none | critical — production deploy credentials | installed (actionlint + pin/injection checks) + live (GitHub runs reaching `/health/ready`) |

## Acceptance criteria
| ID | EARS criterion | Proof |
|---|---|---|
| AC-01 | If an argument is malformed, the secrets directory is missing, a tracked file is modified, the commit is not on `main`, the target lacks the image switch, or broker/Keycloak/compose files differ, the deploy script shall exit 2 before pulling, backing up, checking out or touching a container. | task-01 command |
| AC-02 | When the image cannot be pulled, the Cloud fails to start, or `/health/ready` does not answer within the timeout, the deploy script shall exit 1 and write the Cloud log tail only to a mode-600 file whose path it prints. | task-01 command |
| AC-03 | When a deploy succeeds, the script shall have backed up the database if migrations changed, recreated only the Cloud, tagged the image `iiot-cloud:local` and printed the running image; in `tip` mode a commit older than the deployed one shall exit 0 as skipped without changes. | task-01 command |
| AC-04 | The compose file shall run `IIOT_CLOUD_IMAGE` when set and keep the trial stack and the smoke test working. | task-02 command + task-04 live `smoke` |
| AC-05 | The runbook shall cover the GitHub prerequisites, AWS setup, forced-command key, first deploy, manual server commands, Edges, SSH tunnel, rollback and migration limits. | task-03 command |
| AC-06 | When a pull request targets `main`, CI shall run the whole suite and the smoke test on Linux and fail on any failed or skipped test. | task-04 command + live run |
| AC-07 | When a commit is pushed to `main`, CD shall test, publish and deploy it, ending green only after `/health/ready`; a dispatch with a CD-built `sha` shall redeploy it; a dispatch from another ref or with a malformed `sha` shall fail before SSH. | task-05 command + live runs |

## Tasks
| # | Task | Priority | Criteria | Primary ownership | Dependencies | Status |
|---|---|---|---|---|---|---|
| 01 | Server deploy script | P1 | AC-01, AC-02, AC-03 | `deployments/scripts/remote-deploy.sh`, `tests/Deployment.Tests/RemoteDeployTests.cs`, `.gitignore` | - | done |
| 02 | Compose image switch and smoke compatibility | P1 | AC-04 | `deployments/compose.yaml`, `deployments/scripts/smoke-test.sh`, `docs/deployment.md` | - | done |
| 03 | AWS server and GitHub runbook | P1 | AC-05 | `docs/server-deploy.md` | - | done |
| 04 | CI workflow on pull requests to main | P1 | AC-06 | `.github/workflows/ci.yml`, `specs/deployments-ci/task-04-ci-github-actions.md`, conditional Linux-only test fixes | task-01-remote-deploy-script.md, task-02-compose-image-smoke.md | in_progress |
| 05 | CD workflow from main to the AWS server | P1 | AC-07 | `.github/workflows/cd.yml` | task-01-remote-deploy-script.md, task-02-compose-image-smoke.md, task-03-server-runbook.md, task-04-ci-workflow.md | pending |

## Review log
- Round 1 (2026-10-21, three fresh reviewers: facts/assumptions, security, failure modes/scope): 28 raw findings →
  15 deduplicated (R-01 Critical; R-02..R-10 High; R-11..R-15 Medium). User accepted all 15, chose a public GHCR
  package (R-08), automatic `pg_dump` (R-05), and the owner account tan632000 for live steps. Applied: R-01 → D-05,
  task-03, task-05; R-02 → D-03, task-01; R-03 → D-02, task-05; R-04 → D-02, D-03, task-01, task-05; R-05 → D-03, D-06,
  task-01, task-03; R-06 → D-03, task-01, task-05; R-07 → D-03, D-04, task-03; R-08 → D-02, task-05; R-09 → D-02, D-03,
  D-05, task-03, task-05; R-10 → task-01 fixture; R-11 → D-03, task-01; R-12 → D-02, D-03; R-13 → D-03, AC-02;
  R-14 → D-03, task-03; R-15 → D-01, task-02, task-04, task-05. Old task-02 split into task-01 (script) and task-02
  (compose/smoke); CI renumbered 04 and CD 05.
- Round 2 (2026-10-21, fresh closure reviewer): R-01..R-14 PASS (R-09 pending N-1); R-15 FAIL on `-w /repo` path
  conversion. New: N-1 (High, forced command not executable), N-2 (High, first deploy and post-upgrade resume),
  N-3 (Medium, injection check misses `- run:`), N-4 (Medium, tip-only strands the server), N-5 (Medium, CRLF fixture),
  Low ownership column. User accepted all. Applied: R-15b → task-04/05 Command; N-1 → task-03 item 4, D-05; N-2 →
  task-03 items 6–7, task-05 Step 2, D-05; N-3 → task-05 Command/Counterexample; N-4 → D-03, AC-03, task-01;
  N-5 → task-01 fixture/Counterexample; Low → plan Tasks table. Paper review closed (two rounds); further claims need
  runtime evidence.
- Implementation (2026-10-21): task-04 was blocked because its Command used `-color=never`, which actionlint 1.7.7 rejects
  (`invalid boolean value "never" for -color`); user approved replacing it with `-no-color` in task-04 and task-05 only.
- Implementation (2026-10-21): the first CI run on Linux failed (15 Deployment.Tests, 7 smoke checks): the root `ca.pem` was
  mode 600 (umask 077) so the Mosquitto and Cloud containers could not read it — the real server would have failed the
  same way; two test images lacked a digest (SMOKE-00); SMOKE-06 could not see the base image after a BuildKit build. User
  approved fixing all three in task-04 (ownership extended to smoke-test.sh, PostgresFixture.cs, EndToEndTelemetryTests.cs).
