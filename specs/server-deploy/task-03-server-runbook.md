# Task 03 — The owner can secure GitHub and set up the AWS server from the runbook

Status: done

## Outcome
`docs/server-deploy.md` (Vietnamese) takes the owner from an unprotected repository and an empty AWS account to a server
that only CD from protected `main` can deploy to, and that customer Edges connect to (plan D-05, D-06).

## Scope
- In, in this order:
  1. GitHub prerequisites, **before the first deploy** (account tan632000): protect `main` (pull request required,
     required checks `test` and `smoke`, no bypass, no force push); environment `production` with deployment branch
     `main` only and a required reviewer; **environment** secrets (not repository secrets) `DEPLOY_HOST`, `DEPLOY_USER`,
     `DEPLOY_SSH_KEY`, `DEPLOY_KNOWN_HOSTS`, optional `DEPLOY_PORT`; after the first CD push, set the package
     `plcprotocol/cloud` public. How to check each with `gh api`.
  2. EC2 Ubuntu LTS + **Elastic IP** (the broker certificate SAN; a changing IP breaks every Edge); disk ≥ 30 GB and
     `df -h` check; security group: 22 and 8883 only, 8883 narrowed to customer IPs once known.
  3. Docker Engine + compose plugin; `deploy` user in the `docker` group (root-equivalent — say so).
  4. Keys: the CI key in `~deploy/.ssh/authorized_keys` with
     `restrict,command="/bin/bash /srv/plcprotocol/deployments/scripts/remote-deploy.sh"` (path = the clone; through
     `/bin/bash` because scripts are stored without the executable bit); a **separate** operator
     key for shell and tunnels; `DEPLOY_KNOWN_HOSTS` from `ssh-keyscan` checked against
     `ssh-keygen -lf /etc/ssh/ssh_host_ed25519_key.pub` on the server.
  5. Clone `https://github.com/tan632000/PlcProtocol.git`; `bash deployments/scripts/dev-secrets.sh --san IP:<elastic IP>
     --edge <edgeId>…` once (never by CD); move `ca.key` off the server (it only signs new server certificates; `--add-edge` does not use it — `deployments/scripts/dev-secrets.sh:121,144`; corrected in implementation).
  6. First deploy: merge the pull request; while run 1's `deploy` job waits for the `production` approval, check out
     run 1's sha on the server, run `docker compose -f deployments/compose.yaml up -d --build --wait` once, set the
     package public, then approve — the script then sees no compose/broker/Keycloak change and swaps only the Cloud; what
     `remote-deploy.sh` refuses (exit 2) and why; where failure logs (`deployments/.deploy-logs/`) and backups
     (`deployments/.backups/`) are.
  7. Manual server commands under CD (replace the build commands of `docs/deployment.md`): add an Edge, enable writes,
     upgrade the broker/Keycloak/compose (the changes CD refuses), always without `--build` (the last deployed image is
     `iiot-cloud:local`); after such a manual upgrade: `git checkout --detach <sha>` of the commit applied, then dispatch
     `cd.yml` with that sha so CD resumes from it.
  8. Edge on the customer PC: `install-edge.ps1` with broker host = Elastic IP and the server's `ca.pem`.
  9. API: `ssh -L 8080:127.0.0.1:8080 <operator>@<elastic IP>`; tokens with `get-token.sh` on the server; the API stays on
     127.0.0.1 until the domain + HTTPS spec.
  10. Rollback: dispatch `cd.yml` with the `sha` of an earlier **CD-built** commit (not older than the first CD deploy);
     migration rule (D-06) and restoring a `pg_dump` file.
  11. Limits: Keycloak `start-dev` (recreating it loses manual realm changes), no HTTPS, no scheduled backups.
- Out: AWS/Terraform automation, HTTPS, scheduled backups.

## Coverage
- CP-03

## Ownership
- Create: `docs/server-deploy.md`
- Read: `deployments/scripts/dev-secrets.sh`, `deployments/edge/install-edge.ps1`, `deployments/scripts/get-token.sh`,
  `docs/deployment.md`, `specs/server-deploy/plan.md`

## Steps
1. Write the runbook.

## Acceptance
- AC-05: the doc contains every item above; each name the workflows and the script use appears verbatim.

## Dependencies
- none

## Verification Plan
- Command: `f=docs/server-deploy.md; for s in 'Elastic IP' '--san IP:' '8883' 'DEPLOY_HOST' 'DEPLOY_USER' 'DEPLOY_SSH_KEY' 'DEPLOY_KNOWN_HOSTS' 'production' 'restrict,command="/bin/bash ' 'remote-deploy.sh' 'checkout --detach' 'ca.key' '.deploy-logs' '.backups' 'iiot-cloud:local' 'install-edge.ps1' 'ssh -L 8080:127.0.0.1:8080' 'cd.yml' 'smoke' 'gh api'; do grep -qF -- "$s" "$f" || { echo "missing $s"; exit 1; }; done && [ "$(wc -l < "$f")" -le 800 ] && echo RUNBOOK-OK`
- Named probe: the required strings and the 800-line limit.
- Reachability: source — documentation; live use is part of task-05.
- Oracle: prints `RUNBOOK-OK`, exit 0.
- Counterexample: a runbook missing the forced command or a secret name fails its grep.
- Artifacts: none.

## Failure Protocol
On a failed Step or Verification Plan run: stop; do not widen scope, change the Command, or weaken a test; record observed versus expected; repair only the cited cause; after three failed rounds, stop and ask the user.

## Receipt

Verification: PASS
Command: f=docs/server-deploy.md; for s in 'Elastic IP' '--san IP:' '8883' 'DEPLOY_HOST' 'DEPLOY_USER' 'DEPLOY_SSH_KEY' 'DEPLOY_KNOWN_HOSTS' 'production' 'restrict,command="/bin/bash ' 'remote-deploy.sh' 'checkout --detach' 'ca.key' '.deploy-logs' '.backups' 'iiot-cloud:local' 'install-edge.ps1' 'ssh -L 8080:127.0.0.1:8080' 'cd.yml' 'smoke' 'gh api'; do grep -qF -- "$s" "$f" || { echo "missing $s"; exit 1; }; done && [ "$(wc -l < "$f")" -le 800 ] && echo RUNBOOK-OK
Exit: 0
Base: 13ef2330374922d40d369e6ed3938bc425e61a68
Head: 3ec9642fdcd09ed93fbb9920073f1611f8097eddab1bf9d6ec03a9e6a7b0971e
```text
$ f=docs/server-deploy.md; for s in 'Elastic IP' '--san IP:' '8883' 'DEPLOY_HOST' 'DEPLOY_USER' 'DEPLOY_SSH_KEY' 'DEPLOY_KNOWN_HOSTS' 'production' 'restrict,command="/bin/bash ' 'remote-deploy.sh' 'checkout --detach' 'ca.key' '.deploy-logs' '.backups' 'iiot-cloud:local' 'install-edge.ps1' 'ssh -L 8080:127.0.0.1:8080' 'cd.yml' 'smoke' 'gh api'; do grep -qF -- "$s" "$f" || { echo "missing $s"; exit 1; }; done && [ "$(wc -l < "$f")" -le 800 ] && echo RUNBOOK-OK
RUNBOOK-OK
```
- Runbook docs/server-deploy.md, 358 lines; all required names present (output above).
- Review: code-auditor round 1 FAIL (High: scp of ca.pem/ca.key as ubuntu denied by the 700 secrets folder; Medium: no backup on the manual-upgrade path, no certificate-expiry limit, overstated CD guarantee) -> repaired; round 2 PASS (0 Critical/High/Medium); its last Low (stop on a failed manual pg_dump) applied before this run.
- Implementation correction recorded in Scope: ca.key is not used by --add-edge (dev-secrets.sh:121,144), so it leaves the server after creation.
- Open for GATE-DONE: whether deployments/scripts/remote-deploy.sh itself should join the manual-upgrade refusal list (outside task-03).
