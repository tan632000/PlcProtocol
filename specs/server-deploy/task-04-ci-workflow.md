# Task 04 — Every pull request to main runs the whole suite and the smoke test on Linux

Status: done

## Outcome
`.github/workflows/ci.yml` builds and tests the solution and runs the smoke test on `ubuntu-latest` for pull requests to
`main` (and when called by `cd.yml` or dispatched), failing on any failed or skipped test; a real run proves it (plan
D-01). Replaces `specs/deployments-ci/task-04-ci-github-actions.md`.

## Scope
- In: triggers `pull_request` → `main`, `workflow_dispatch`, `workflow_call`; `permissions: contents: read`; `concurrency`
  per ref cancelling superseded pull-request runs; job `test` (timeout 30 min): `actions/checkout`, `actions/setup-dotnet`
  (`global-json-file: global.json`), `dotnet restore`, `dotnet build --no-restore -c Release`, `dotnet test --no-build -c
  Release --logger trx --results-directory TestResults`, a step failing when any TRX has `outcome="NotExecuted"`,
  `actions/upload-artifact` of `TestResults` with `if: always()`; job `smoke` (timeout 30 min): checkout,
  `bash deployments/scripts/smoke-test.sh`. Every `uses:` pinned by 40-hex SHA with the tag in a comment. Mark the
  superseded deployments-ci task `blocked` (Status and Blocker lines only).
- Conditional: a Linux-only assertion that fails on its first Linux run may be fixed here, in
  `tests/Deployment.Tests/BrokerTests.cs`, `tests/Edge.Tests/TelemetryStoreTests.cs` or `deployments/scripts/dev-secrets.sh`
  only, with the observed failure recorded in the receipt; no assertion is removed or weakened.
- Out: branch protection and environments (task-03 runbook, done by the owner), path filters.

## Coverage
- CP-04

## Ownership
- Create: `.github/workflows/ci.yml`
- Modify: `specs/deployments-ci/task-04-ci-github-actions.md` (Status → `blocked`, Blocker: superseded by
  `specs/server-deploy/task-04-ci-workflow.md`)
- Conditional modify: see Scope.
- Modify (user-approved after the first Linux run, 2026-10-21): `tests/Cloud.Tests/Infrastructure/PostgresFixture.cs`,
  `tests/Deployment.Tests/EndToEndTelemetryTests.cs` (pin the postgres test image by digest, SMOKE-00),
  `deployments/scripts/smoke-test.sh` (pull the pinned base image before SMOKE-06 skips its layers).
- Read: `global.json`, `IndustrialIot.sln`, `deployments/scripts/smoke-test.sh`

## Steps
1. Write the workflow (resolve action SHAs with `gh api repos/<owner>/<repo>/git/ref/tags/<tag>`); run the local command.
2. Commit (conventional commits, on the current branch, after the user authorizes). The owner account tan632000 pushes the
   branch and opens a pull request into `main`; observe with `gh run list/view` (read access suffices) and record the
   run URL and conclusion.

## Acceptance
- AC-06: actionlint clean; every `uses:` pinned; the pull request's run green — job `test` with all four test projects,
  0 not executed, `TelemetryStoreTests.Pragmas_AndFileMode` and `BrokerTests.*` passed; job `smoke` reports `Smoke test
  passed.`

## Dependencies
- task-01-remote-deploy-script.md
- task-02-compose-image-smoke.md

## Verification Plan
- Command: `MSYS_NO_PATHCONV=1 docker run --rm -v "$(pwd -W):/repo" -w /repo rhysd/actionlint:1.7.7 -no-color .github/workflows/ci.yml && grep -qE '^\s*(- )?uses:' .github/workflows/ci.yml && ! grep -nE '^\s*(- )?uses:' .github/workflows/ci.yml | grep -vE '@[0-9a-f]{40}( |$)'`, then the GitHub Actions run of the pull request.
- Named probe: actionlint (0 findings); the pin check (at least one `uses:`, none unpinned); run jobs `test` and `smoke`
  conclusion `success`; the skip-check output; TRX entries named above; the smoke log's final line.
- Reachability: installed — Docker Desktop for actionlint (native path via `pwd -W`); live — repository
  `tan632000/PlcProtocol`, after the owner's push and pull request.
- Oracle: local exit 0; run conclusion `success` for both jobs.
- Counterexample: a misspelled key fails actionlint; a tag-pinned action fails the pin check; a skipped test fails the
  skip-check step; a broken SMOKE-00 exemption fails `smoke`.
- Artifacts: run URL and TRX artifact name in the receipt.

## Failure Protocol
On a failed Step or Verification Plan run: stop; do not widen scope, change the Command, or weaken a test; record observed versus expected; repair only the cited cause; after three failed rounds, stop and ask the user. Without the owner's push and pull request the task stays `blocked` with that cause.

## Receipt

Verification: PASS
Command: MSYS_NO_PATHCONV=1 docker run --rm -v "$(pwd -W):/repo" -w /repo rhysd/actionlint:1.7.7 -no-color .github/workflows/ci.yml && grep -qE '^\s*(- )?uses:' .github/workflows/ci.yml && ! grep -nE '^\s*(- )?uses:' .github/workflows/ci.yml | grep -vE '@[0-9a-f]{40}( |$)'`, then the GitHub Actions run of the pull request.
Exit: 0
Base: 135fe6f73e8381155f6330842a3ab77e0f7c3137
Head: b4f0780cceeae4cae30caa4116de11cbc4af7e43af6c33a183a4540fedb6a813
```text
$ MSYS_NO_PATHCONV=1 docker run --rm -v "$(pwd -W):/repo" -w /repo rhysd/actionlint:1.7.7 -no-color .github/workflows/ci.yml && grep -qE '^\s*(- )?uses:' .github/workflows/ci.yml && ! grep -nE '^\s*(- )?uses:' .github/workflows/ci.yml | grep -vE '@[0-9a-f]{40}( |$)'`, then the GitHub Actions run of the pull request.
(local command: no findings, exit 0)
GitHub Actions run 39564072368 (pull request #3, head 135fe6f): https://github.com/tan632000/PlcProtocol/actions/runs/39564072368
conclusion: success
job test: success
job smoke: success
Passed!  - Failed:     0, Passed:    55, Skipped:     0, Total:    55, Duration: 487 ms - Shared.Tests.dll (net10.0)
Passed!  - Failed:     0, Passed:   304, Skipped:     0, Total:   304, Duration: 43 s - Edge.Tests.dll (net10.0)
Passed!  - Failed:     0, Passed:   118, Skipped:     0, Total:   118, Duration: 2 m 43 s - Cloud.Tests.dll (net10.0)
Passed!  - Failed:     0, Passed:    32, Skipped:     0, Total:    32, Duration: 3 m 12 s - Deployment.Tests.dll (net10.0)
4 TRX file(s), 0 not executed
PASS SMOKE-00 static: per-file secret mounts, no ca.key, digests everywhere, .dockerignore, no password on argv
PASS SMOKE-01 /health/ready 200
PASS SMOKE-02 no token -> 401
PASS SMOKE-03 viewer token -> 200 with smoke-edge (labelled smoke-edge)
PASS SMOKE-04 viewer POST -> 404 (read-only default)
PASS SMOKE-05 operator POST -> 404 (read-only default)
PASS SMOKE-06 no private key or generated secret in any image layer
PASS SMOKE-07 repository tree unchanged
Smoke test passed.
TRX: TelemetryStoreTests.Pragmas_AndFileMode outcome=Passed; BrokerTests 13 Passed, 0 other
```
- Live: run 39564072368 on pull request #3 green; artifact test-results downloaded and checked (4 TRX, Pragmas_AndFileMode Passed, 13 BrokerTests Passed).
- Counterexamples on disposable copies: a misspelled timeout-minuts failed actionlint; actions/checkout@v7 failed the pin check; the skip-check step exited 1 with no TRX and with one NotExecuted result (also under pipefail), 0 on a clean TRX.
- Repair rounds: (1) the Command's -color=never flag was invalid (user-approved correction to -no-color); (2) first Linux run: root ca.pem mode 600 unreadable by the Mosquitto/Cloud containers (real server defect), undigested postgres test images, SMOKE-06 base image absent - fixed in cc71431 with user-approved ownership extension; (3) second run: SecretsDirectory cleanup recreated removed folders as root - fixed in 135fe6f.
- Review: code-auditor PASS for ci.yml and for both repair commits (0 Critical/High/Medium). Open Low: a Linux secret set created before cc71431 needs chmod 644 deployments/.secrets/ca.pem (not documented).
