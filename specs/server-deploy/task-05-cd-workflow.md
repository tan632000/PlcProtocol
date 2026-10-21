# Task 05 — Only a tested push to main, or a rollback dispatch, deploys the Cloud

Status: pending

## Outcome
`.github/workflows/cd.yml` tests every push to `main`, publishes `ghcr.io/tan632000/plcprotocol/cloud:<sha>`, and deploys
its digest through the server's forced command; a dispatch on `main` with the `sha` of an earlier CD-built commit
redeploys it; nothing else reaches the server (plan D-02).

## Scope
- In: triggers `push` → `main`, `workflow_dispatch` (input `sha`, optional); top-level `permissions: contents: read`; jobs:
  - `guard`: fails unless `github.ref == 'refs/heads/main'`; for a dispatch, validates `INPUT_SHA` (passed via `env:`)
    against `^[0-9a-f]{40}$` in bash; outputs `sha` (the pushed `github.sha` or the input) and `mode` (`tip`/`rollback`).
  - `test` (push only): `uses: ./.github/workflows/ci.yml`.
  - `image` (push only, needs `guard`, `test`): `packages: write`; checkout, `docker/setup-buildx-action`,
    `docker/login-action` (GHCR, `GITHUB_TOKEN`), `docker/build-push-action` (`deployments/cloud/Dockerfile`, context `.`,
    tag `:<sha>`, `provenance: false`) → output `image` = `ghcr.io/tan632000/plcprotocol/cloud@<digest>`.
  - `resolve` (dispatch only, needs `guard`): `docker buildx imagetools inspect` of `cloud:<sha>` (public, no login) →
    output the digest reference; fails if absent.
  - `deploy`: `needs: [guard, test, image, resolve]`; `if: ${{ !cancelled() && needs.guard.result == 'success' &&
    ((needs.test.result == 'success' && needs.image.result == 'success') || (needs.resolve.result == 'success' &&
    needs.test.result == 'skipped' && needs.image.result == 'skipped')) }}`; `environment: production`;
    `concurrency: { group: deploy-production, cancel-in-progress: false }`; one `run:` step with `set -euo pipefail`
    that writes `DEPLOY_SSH_KEY` and `DEPLOY_KNOWN_HOSTS` to files under `$RUNNER_TEMP` (mode 600), then
    `ssh -i … -o UserKnownHostsFile=… -o StrictHostKeyChecking=yes -p "${DEPLOY_PORT:-22}" "$DEPLOY_USER@$DEPLOY_HOST"
    "$SHA $IMAGE $MODE"`, every value from `env:`; the forced command runs `remote-deploy.sh`.
  - Every external `uses:` pinned by 40-hex SHA; no `${{ … }}` inside any `run:` line; no secret echoed.
- Out: staging, image signing/SBOM, notifications, GitHub settings (runbook, owner).

## Coverage
- CP-05

## Ownership
- Create: `.github/workflows/cd.yml`
- Read: `.github/workflows/ci.yml`, `deployments/scripts/remote-deploy.sh`, `docs/server-deploy.md`

## Steps
1. Write the workflow; run the local command.
2. Live, by the owner tan632000 following `docs/server-deploy.md`: GitHub prerequisites and server prepared (item 1–5;
   the first deploy follows runbook item 6 inside run 1's approval window); check
   `gh api repos/tan632000/PlcProtocol/branches/main/protection` (required checks `test`, `smoke`) and
   `gh api repos/tan632000/PlcProtocol/environments/production` (branch policy, reviewer); merge the pull request of
   task-04 (push run 1); merge a second trivial change (push run 2); dispatch with run 1's sha (rollback), then with run 2's
   sha; dispatch from another branch and with `sha=abc` (both must fail in `guard`).

## Acceptance
- AC-07: actionlint clean, all external `uses:` pinned, no `${{` in `run:` lines; settings checks show protection and the
  environment policy; push runs 1 and 2 green with `deploy` printing the deployed sha and running image;
  `docker manifest inspect ghcr.io/tan632000/plcprotocol/cloud:<sha>` succeeds without login; the rollback dispatch green
  and the server then runs run 1's sha; the two invalid dispatches red in `guard`.

## Dependencies
- task-01-remote-deploy-script.md
- task-02-compose-image-smoke.md
- task-03-server-runbook.md
- task-04-ci-workflow.md

## Verification Plan
- Command: `MSYS_NO_PATHCONV=1 docker run --rm -v "$(pwd -W):/repo" -w /repo rhysd/actionlint:1.7.7 -no-color .github/workflows/cd.yml .github/workflows/ci.yml && grep -qE '^\s*(- )?uses:' .github/workflows/cd.yml && ! grep -nE '^\s*(- )?uses:' .github/workflows/cd.yml | grep -vE '@[0-9a-f]{40}( |$)|uses: \./\.github/workflows/ci\.yml$' && ! awk '/^[[:space:]]*(- )?run:/{r=1} r&&/\$\{\{/{print FILENAME": "NR": "$0} /^[[:space:]]*(- )?[a-z-]+:/&&!/^[[:space:]]*(- )?run:/{r=0}' .github/workflows/cd.yml | grep -q .`, then the GitHub Actions runs.
- Named probe: actionlint; pin check; injection check (no `${{` inside `run:` blocks); `gh api` settings checks; run jobs
  `guard`, `test`, `image`, `resolve`, `deploy` conclusions; the deploy success line; `docker manifest inspect`; the two
  rejected dispatches.
- Reachability: installed — Docker Desktop; live — GitHub Actions, GHCR and the AWS server prepared by the owner. Without
  the server, the settings or the owner's merges the live level is unavailable and the task stays `blocked` naming which.
- Oracle: local exit 0; runs and checks as in Acceptance.
- Counterexample: `${{ inputs.sha }}` in a `run:` or `- run:` line (inline or in its block) fails the injection check; a dispatch from another branch reaching
  `deploy`, or a push run deploying when `test` failed, fails the live checks; a Cloud that never becomes ready turns
  `deploy` red (recovery is the rollback dispatch).
- Artifacts: run URLs, image digests and settings-check output (without secret values) in the receipt.

## Failure Protocol
On a failed Step or Verification Plan run: stop; do not widen scope, change the Command, or weaken a test; record observed versus expected; repair only the cited cause; after three failed rounds, stop and ask the user.

## Receipt
