# Task 01 — The server deploy script swaps only the Cloud, safely

Status: done

## Outcome
`deployments/scripts/remote-deploy.sh` deploys an immutable Cloud image of a `main` commit on the server, refuses every
unsafe state before changing anything, backs up the database before a migration, and fails diagnosably without printing
logs (plan D-03, D-06).

## Scope
- In:
  - Input: arguments `<sha> <image> <mode>`, or — with no arguments — the same three words from `SSH_ORIGINAL_COMMAND`
    (forced command), read with `read -r` into exactly three fields (a fourth word or a second line → exit 2). `sha` `^[0-9a-f]{40}$`;
    `image` `^ghcr\.io/tan632000/plcprotocol/cloud@sha256:[0-9a-f]{64}$`; `mode` `tip` or `rollback`.
  - Works from the repository root containing the script (`cd "$(dirname "$0")/../.."`); `set -euo pipefail`; no
    variable named `*IMAGE` at the start of a line (smoke `:88-90`).
  - Refusals, exit 2, in this order and before any pull/backup/checkout/container command: malformed input;
    `deployments/.secrets/` missing; `git status --porcelain --untracked-files=no` not empty (names the files); after
    `git fetch --quiet origin main`, `sha` not an ancestor of `origin/main`; `git show <sha>:deployments/compose.yaml`
    without `IIOT_CLOUD_IMAGE`; `git diff --name-only HEAD <sha> -- deployments/mosquitto deployments/keycloak
    deployments/compose.yaml deployments/.secrets` not empty ("manual upgrade, see docs/server-deploy.md").
  - `tip` mode and `sha` already an ancestor of the deployed `HEAD` (`git merge-base --is-ancestor <sha> HEAD`, `sha` ≠
    `HEAD`) → print "older than the deployed <HEAD>, skipped" and exit 0, nothing changed (never go backwards; a newer
    `main` commit that is not deployed yet does not stop an older tested one from moving the server forward).
  - `docker pull "$image"` (fail → exit 1, nothing changed).
  - When `git diff --name-only HEAD <sha> -- src/Cloud/Data/Migrations` is not empty:
    `docker compose -f deployments/compose.yaml exec -T postgres pg_dump -U iiot iiot` into
    `deployments/.backups/<UTC timestamp>-<old sha 12>.sql` under `umask 077` (fail → exit 1, before checkout).
  - `git checkout --quiet --detach <sha>`; `IIOT_CLOUD_IMAGE=<image> docker compose -f deployments/compose.yaml up -d
    --no-deps --no-build --wait cloud` (fail → log file, exit 1); poll `curl -fsS --max-time 5
    http://<address>/health/ready` every 2 s up to `IIOT_READY_TIMEOUT` (default 120; validated with the input, `^[1-9][0-9]{0,3}$`, exit 2), where `<address>` is
    `docker compose … port cloud 8080` — the port compose published, which `deployments/.env` may change while the forced
    command's environment cannot (amended in implementation review, 2026-10-21).
  - Failure after checkout: `docker compose … logs --tail 200 cloud` into `deployments/.deploy-logs/<sha>.log` (mode 600, an older file removed first);
    print only that path and the cause; exit 1.
  - Success: `docker tag "$image" iiot-cloud:local`; `docker image prune -af --filter until=240h`; print the deployed sha
    and the running image (`docker compose … ps --format '{{.Image}}' cloud`).
  - Never reads, creates or prints anything under `deployments/.secrets/`.
  - `.gitignore`: `deployments/.env`, `deployments/.backups/`, `deployments/.deploy-logs/`.
  - Tests `RemoteDeployTests`: a self-contained temporary repository — `git init --bare` origin, a work clone that commits
    (`git -c user.name=t -c user.email=t@t`) the repository's `.gitattributes` (LF on checkout despite
    `core.autocrlf=true` on Windows), the working-tree script and a minimal `deployments/` (compose with the
    image switch, `mosquitto/`, `keycloak/`) as commit A, then commit B (and variants: a migration file, a mosquitto
    change, a compose without the switch) pushed to `main`; the deploy clone is at A with a `deployments/.secrets/`
    directory. Stub `docker` and `curl` scripts record their arguments and `IIOT_CLOUD_IMAGE`; the test runs the clone's
    script through bash as `bash -c 'PATH="$STUBS:$PATH"; exec bash "$0" "$@"' <script> <args>` so the stubs precede Git
    Bash's own tools. Each case asserts exit code, the clone's `HEAD`, the stub logs (including that the `curl` log is
    not empty in both health cases), and that the repository under test (`Repo.Root`) `HEAD` is unchanged.
- Out: compose and smoke edits (task-02), workflows (task-04/05), applying broker/Keycloak/compose changes.

## Coverage
- CP-01

## Ownership
- Create: `deployments/scripts/remote-deploy.sh`
- Create: `tests/Deployment.Tests/RemoteDeployTests.cs`
- Modify: `.gitignore`
- Read: `tests/Deployment.Tests/BrokerTests.cs` (`Repo`, `SecretsScript` bash runner), `deployments/scripts/smoke-test.sh:85-90`,
  `deployments/scripts/dev-secrets.sh:239`

## Steps
1. Tests first (cases below), then the script, then `.gitignore`.
2. Cases: (1) malformed sha, (2) foreign image / tag instead of digest, (3) fourth word in `SSH_ORIGINAL_COMMAND`,
   (4) no secrets directory, (5) modified tracked file, (6) sha not on `main`, (7) target compose without the switch,
   (8) mosquitto change → each exit 2 with no stub call after `git`; (9) `tip` with a sha older than the deployed `HEAD` → exit 0
   "skipped", nothing changed; (10) pull fails → exit 1, `HEAD` unchanged; (11) `up` fails → exit 1, log file written mode 600, its
   path printed, no log content on stdout; (12) never ready (`IIOT_READY_TIMEOUT=4`) → same as 11; (13) ready → exit 0,
   `HEAD` detached at B, `docker` saw `pull <image>`, `up -d --no-deps --no-build --wait cloud` with `IIOT_CLOUD_IMAGE`,
   `tag <image> iiot-cloud:local`, `image prune`, no `pg_dump`; (14) ready with a migration change → `exec -T postgres
   pg_dump` before checkout, backup file mode 600; (15) forced-command input (`SSH_ORIGINAL_COMMAND`, no arguments)
   behaves as (13); (16) `rollback` to a commit older than the deployed one → deployed (no skip); (17) backup fails →
   exit 1, `HEAD` unchanged, no partial backup, no `up`. Case 3 also covers a second line in `SSH_ORIGINAL_COMMAND` and a malformed `IIOT_READY_TIMEOUT`.

## Acceptance
- AC-01: cases 1–8 exit 2 with no pull, backup, checkout or container command.
- AC-02: cases 10–12 and 17 exit 1; 11–12 print no Cloud log line, only the file path.
- AC-03: cases 9, 13, 14, 15, 16 as described.

## Dependencies
- none

## Verification Plan
- Command: `bash -n deployments/scripts/remote-deploy.sh && dotnet test tests/Deployment.Tests --filter "FullyQualifiedName~RemoteDeployTests"`
- Named probe: the 17 `RemoteDeployTests` cases.
- Reachability: source — the real script executed by bash in a temporary repository, `docker`/`curl` stubbed; the real
  server run is task-05's live proof.
- Oracle: exit 0; `Failed: 0`, `Skipped: 0`, 17 tests.
- Counterexample: checking out before validating moves `HEAD` in cases 1–8; `up -d` without `cloud` shows in case 13's
  stub log; printing logs puts log lines on stdout in 11–12; stubs placed only on the caller's PATH leave the `curl` log
  empty on Windows; a fixture without `.gitattributes` fails every case on Windows with CRLF.
- Artifacts: ephemeral; temporary repositories deleted by the tests.

## Failure Protocol
On a failed Step or Verification Plan run: stop; do not widen scope, change the Command, or weaken a test; record observed versus expected; repair only the cited cause; after three failed rounds, stop and ask the user.

## Receipt

Verification: PASS
Command: bash -n deployments/scripts/remote-deploy.sh && dotnet test tests/Deployment.Tests --filter "FullyQualifiedName~RemoteDeployTests"
Exit: 0
Base: 13ef2330374922d40d369e6ed3938bc425e61a68
Head: d3875c029d9154ed22985db4760913cc03b99f8802c7eb55ae62d566a4f6077b
```text
$ bash -n deployments/scripts/remote-deploy.sh && dotnet test tests/Deployment.Tests --filter "FullyQualifiedName~RemoteDeployTests"
Test run for C:\Users\case 046\Documents\Project\PlcProtocol\tests\Deployment.Tests\bin\Debug\net10.0\Deployment.Tests.dll (.NETCoreApp,Version=v10.0)
Passed!  - Failed:     0, Passed:    17, Skipped:     0, Total:    17, Duration: 24 s - Deployment.Tests.dll (net10.0)
```
- Named probe: 17 RemoteDeployTests cases in a self-contained temporary repository (bare origin, work clone, deploy clone; docker/curl stubbed first on PATH inside bash); the repository under test's HEAD asserted unchanged after each case.
- Counterexamples built into the cases: refusals assert HEAD unchanged and no docker call; the up line is matched exactly and is the only up/restart/down; the health URL must be the compose-published port (stub 18080); rollback must not be skipped; a failed backup leaves no partial file; a stale log is replaced.
- Repairs: round 1 test stub pattern; round 2 review (M1 second line, M2 health port from compose port - task Scope amended, M3 backup failure test, M4 rollback test, Lows); round 3 review Lows (timeout validated before any change, distinct second-line message).
- Review: code-auditor PASS (round 3, 0 Critical/High/Medium). Limits: mode-600 assertions run only on Linux (CI, task-04); prune removes any image unused for 240 h host-wide; git/cd errors exit 128 not 1/2; the switch check matches the string anywhere in compose.
