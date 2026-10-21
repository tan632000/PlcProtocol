# Task 04 — Every push and pull request runs the whole suite on Linux

Status: blocked
Blocker: superseded by `specs/server-deploy/task-04-ci-workflow.md` (CI on pull requests to main, run by that packet; decided at its GATE-SCOPE, 2026-10-21).

## Outcome
`.github/workflows/ci.yml` builds and tests the solution on `ubuntu-latest` for pushes and pull requests to `main`, failing on any failed or skipped test; a real GitHub Actions run proves it.

## Scope
- In: workflow, local lint, the first real run (after a user-authorized commit + push).
- Out: image publishing, deployment, branch protection settings (GitHub UI, user-owned), a local CI runner (cut at GATE-REVIEW R-06).

## Coverage
- CP-04

## Ownership
- Create: `.github/workflows/ci.yml`
- Read: `global.json`, `IndustrialIot.sln`, `tests/**/*.csproj`, `tests/Edge.Tests/TelemetryStoreTests.cs` (`Pragmas_AndFileMode`)

## Contracts
- Workflow: `on: push` and `pull_request` for `main`; `permissions: contents: read`; `concurrency` cancels superseded runs of the same ref; job `test` on `ubuntu-latest`, timeout 30 min; actions pinned by full commit SHA with the version tag in a comment: `actions/checkout` (v4), `actions/setup-dotnet` (v4, `global-json-file: global.json`), `actions/upload-artifact` (v4); steps: `dotnet restore`, `dotnet build --no-restore -c Release`, `dotnet test --no-build -c Release --logger trx --results-directory TestResults` (Git Bash is not needed on Linux: `IIOT_BASH` unset → `bash`), a step failing when any TRX reports a not-executed/skipped test, upload `TestResults` (always). No secrets.
- Preconditions for the live run: waves 1–4 committed (the solution references `src/Cloud`, `tests/Cloud.Tests` and `tests/Deployment.Tests`); the user authorizes the commit and push to a branch with a pull request into `main` (or to `main`) at that moment.

## Steps
1. Workflow; lint it locally.
2. Ask the user to authorize commit + push; observe the run (via the GitHub web UI or API) and record its result.

## Acceptance
- AC-07: actionlint clean; every `uses:` in the workflow references a 40-hex-character commit SHA (`PIN-CHECK`); the GitHub run of the pushed commit is green with every test project executed and 0 skipped, including `TelemetryStoreTests.Pragmas_AndFileMode` passing on Linux (its Linux-only file-mode branch) and `Deployment.Tests` passing on Linux (file ownership for the broker containers).

## Dependencies
- task-03-edge-windows-service.md

## Verification Plan
- Command: `docker run --rm -v "<repo>:/repo" -w /repo rhysd/actionlint@sha256:<pinned> -color=never` (local), then the GitHub Actions run for the pushed commit.
- Named probe: `actionlint` (0 findings); `PIN-CHECK` (every `uses:` line matches `@[0-9a-f]{40}`); workflow run conclusion `success` with job `test`; TRX entries `TelemetryStoreTests.Pragmas_AndFileMode` and every `BrokerTests.*` outcome `Passed`.
- Reachability: installed — Docker for actionlint; live — the GitHub repository `tan632000/PlcProtocol` with Actions enabled, after the user-authorized push.
- Oracle: actionlint exit 0; run conclusion `success`; the skip-check step reports 0 skipped.
- Counterexample: a skipped test (the skip check must fail the job), a misspelled workflow key (actionlint), or a failing test must turn the run red.
- Artifacts: the run URL and the uploaded TRX artifact name recorded in the receipt.

## Failure Protocol
On a failed Step or Verification Plan run: stop; do not widen scope, change the Command, or weaken a test; record observed versus expected; repair only the cited cause; after three failed rounds, stop and ask the user. Without the user's push authorization the task stays `blocked` with that cause.

## Receipt
<!-- Fill only after execution; see canonical form in templates. -->
