# Task 02 — Compose runs a published Cloud image; the trial stack and smoke test keep working

Status: done

## Outcome
The Cloud service runs `IIOT_CLOUD_IMAGE` when set and otherwise builds/uses `iiot-cloud:local`; the smoke test keeps its
image name and its SMOKE-00 digest rule with exactly one exemption (plan D-04).

## Scope
- In: `deployments/compose.yaml` Cloud `image: ${IIOT_CLOUD_IMAGE:-iiot-cloud:local}` beside `build:`, with a comment;
  `deployments/scripts/smoke-test.sh`: `export IIOT_CLOUD_IMAGE="${PROJECT}-cloud"` (keeps `:57` cleanup and `:172` export
  working) and the SMOKE-00 image loop (`:85-87`) skips exactly the line `image: ${IIOT_CLOUD_IMAGE:-iiot-cloud:local}`;
  `docs/deployment.md` one line in the update/upgrade section: on a CD-managed server use `docs/server-deploy.md` instead.
- Out: the deploy script (task-01), the CI job that runs the smoke test (task-04).

## Coverage
- CP-02

## Ownership
- Modify: `deployments/compose.yaml`
- Modify: `deployments/scripts/smoke-test.sh`
- Modify: `docs/deployment.md`

## Steps
1. Compose line; smoke export and exemption; doc line.

## Acceptance
- AC-04: both compose renders show the expected `image:`; the smoke script parses; the exemption matches only that line
  (proved locally by a SMOKE-00-equivalent loop over the compose file, live by task-04's `smoke` job).

## Dependencies
- none

## Verification Plan
- Command: `IIOT_CLOUD_IMAGE=ghcr.io/tan632000/plcprotocol/cloud@sha256:0000000000000000000000000000000000000000000000000000000000000000 docker compose -f deployments/compose.yaml config | grep -E '^\s*image: ghcr.io/tan632000/plcprotocol/cloud@sha256:0{64}$' && docker compose -f deployments/compose.yaml config | grep -E '^\s*image: iiot-cloud:local$' && bash -n deployments/scripts/smoke-test.sh && grep -hE '^\s*image:' deployments/compose.yaml | grep -vF 'image: ${IIOT_CLOUD_IMAGE:-iiot-cloud:local}' | grep -vc '@sha256:' | grep -qx 0 && [ "$(grep -cE '^\s*image:' deployments/compose.yaml | tr -d ' ')" = "$(( $(grep -hE '^\s*image:' deployments/compose.yaml | grep -c '@sha256:') + 1 ))" ] && echo COMPOSE-OK`
- Named probe: the two renders (only the `image:` lines are read; the rest of the render holds env_file values and is
  never recorded); the digest rule over compose `image:` lines with exactly one exemption.
- Reachability: installed — `docker compose config` on the dev machine (needs the `deployments/.secrets` env files,
  present here); live — task-04's CI `smoke` job runs the real SMOKE-00 on Linux.
- Oracle: prints `COMPOSE-OK`, exit 0.
- Counterexample: no `image:` line fails both renders; a second unpinned `image:` breaks the count check.
- Artifacts: none.

## Failure Protocol
On a failed Step or Verification Plan run: stop; do not widen scope, change the Command, or weaken a test; record observed versus expected; repair only the cited cause; after three failed rounds, stop and ask the user.

## Receipt

Verification: PASS
Command: IIOT_CLOUD_IMAGE=ghcr.io/tan632000/plcprotocol/cloud@sha256:0000000000000000000000000000000000000000000000000000000000000000 docker compose -f deployments/compose.yaml config | grep -E '^\s*image: ghcr.io/tan632000/plcprotocol/cloud@sha256:0{64}$' && docker compose -f deployments/compose.yaml config | grep -E '^\s*image: iiot-cloud:local$' && bash -n deployments/scripts/smoke-test.sh && grep -hE '^\s*image:' deployments/compose.yaml | grep -vF 'image: ${IIOT_CLOUD_IMAGE:-iiot-cloud:local}' | grep -vc '@sha256:' | grep -qx 0 && [ "$(grep -cE '^\s*image:' deployments/compose.yaml | tr -d ' ')" = "$(( $(grep -hE '^\s*image:' deployments/compose.yaml | grep -c '@sha256:') + 1 ))" ] && echo COMPOSE-OK
Exit: 0
Base: 13ef2330374922d40d369e6ed3938bc425e61a68
Head: 03658dfc2a7b5634c66c4611d8c2d3ed3f2f8852151e7cd728a30559e6383ae3
```text
$ IIOT_CLOUD_IMAGE=ghcr.io/tan632000/plcprotocol/cloud@sha256:0000000000000000000000000000000000000000000000000000000000000000 docker compose -f deployments/compose.yaml config | grep -E '^\s*image: ghcr.io/tan632000/plcprotocol/cloud@sha256:0{64}$' && docker compose -f deployments/compose.yaml config | grep -E '^\s*image: iiot-cloud:local$' && bash -n deployments/scripts/smoke-test.sh && grep -hE '^\s*image:' deployments/compose.yaml | grep -vF 'image: ${IIOT_CLOUD_IMAGE:-iiot-cloud:local}' | grep -vc '@sha256:' | grep -qx 0 && [ "$(grep -cE '^\s*image:' deployments/compose.yaml | tr -d ' ')" = "$(( $(grep -hE '^\s*image:' deployments/compose.yaml | grep -c '@sha256:') + 1 ))" ] && echo COMPOSE-OK
    image: ghcr.io/tan632000/plcprotocol/cloud@sha256:0000000000000000000000000000000000000000000000000000000000000000
    image: iiot-cloud:local
COMPOSE-OK
```
- Output lines are only the grep-selected image lines of the two renders and the final marker; the rest of the render (env_file values) was never recorded.
- SMOKE-00 replay: the real loop extracted from smoke-test.sh run against the repo compose (no failure), a copy with the Cloud default changed to iiot-cloud:latest (fails: digest + exactly-once) and a copy with the exemption line duplicated (fails: exactly-once).
- Review: code-auditor PASS (0 Critical/High/Medium); Low 1 (exactly-once in SMOKE-00) applied before this run. Open: the link to docs/server-deploy.md resolves once task-03 lands; the live SMOKE-00 runs in the CI smoke job (task-04); an old deployments-cloud image is left on existing trial machines.
