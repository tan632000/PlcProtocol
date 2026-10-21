# Task 05 — Compose ACL selection, smoke checks and docs

Status: done

## Outcome
The compose stack mounts the read-only ACL unless `IIOT_MQTT_ACL` selects another file; the smoke test expects the
command API to be absent (404 for viewer and operator); the docs explain the read-only default, the four switches a write
needs, how to enable them on an installed Edge, what mismatched layers look like, and the upgrade consequences.

## Scope
- In: compose bind mount `${IIOT_MQTT_ACL:-./mosquitto/acl}` (recreate mosquitto after a change); SMOKE-04 and SMOKE-05
  expect 404; `docs/deployment.md`: section "Chế độ read-only và cách bật ghi" — Edge `Edge:Commands:Enabled` in
  `C:\Program Files\IndustrialIot\Edge\appsettings.json` then `Restart-Service` (a reinstall resets it: uninstall keeps
  the old file only as `C:\ProgramData\IndustrialIot\Edge\appsettings.backup.json` and the installer writes a fixed
  environment, `deployments/edge/install-edge.ps1:232-240`, `deployments/edge/uninstall-edge.ps1:71,78-81` — so after a
  reinstall copy the backup back or set the switch again; `uninstall-edge.ps1 -RemoveData` deletes that backup too),
  Cloud `Cloud__Commands__Enabled=true` in `deployments/.secrets/cloud.env`, `IIOT_MQTT_ACL=./mosquitto/acl-commands` in
  the shell or in `deployments/.env` (compose interpolation; **not** in `cloud.env`, an `env_file` does not feed it),
  per-tag `Writable`; any one off blocks writes; restart needed; a table
  "mismatch → symptom → fix" (Cloud on + read-only ACL → `publish_unknown`; Edge off + Cloud on → `timed_out`; Edge on
  + read-only ACL → no command arrives); upgrade note (write-mode installations lose writes until all four are set;
  switch off only when no command is in flight); the section 4 `curl -X POST …/commands` example marked write-mode only;
  `docs/setup-checklist.md` stage 7 and the Writable step (`:125`) list the switches; `docs/plc-setup.md:203` points to
  that section instead of "try a Cloud write".
- Out: running `smoke-test.sh` here (plan Out of scope).

## Coverage
- CP-05

## Ownership
- Modify: `deployments/compose.yaml`
- Modify: `deployments/scripts/smoke-test.sh`
- Modify: `docs/deployment.md`
- Modify: `docs/setup-checklist.md`
- Modify: `docs/plc-setup.md`

## Steps
1. Compose mount with a comment naming `acl-commands`.
2. Smoke: SMOKE-04 and SMOKE-05 each become one check line of the form
   `[ "$code" = "404" ] && pass SMOKE-0N "<role> POST -> 404 (read-only default)" || fail SMOKE-0N "<role> POST -> $code (expected 404)"`
   (the Verification Plan greps that form, so a comment line cannot satisfy it).
3. Docs as in Scope.

## Acceptance
- AC-08: the compose render shows the default and the override source; both smoke lines expect 404; each of the two
  docs contains all four switch names; `plc-setup.md` no longer says to try a Cloud write without them.

## Dependencies
- task-01-edge-commands-switch.md
- task-02-cloud-commands-switch.md
- task-03-retire-stale-subscriptions.md
- task-04-read-only-acl.md

## Verification Plan
- Command: `bash -n deployments/scripts/smoke-test.sh && docker compose -f deployments/compose.yaml config | grep -E 'source: .*mosquitto[\\/]acl$' && IIOT_MQTT_ACL=./mosquitto/acl-commands docker compose -f deployments/compose.yaml config | grep -E 'source: .*mosquitto[\\/]acl-commands$' && grep -E '"404" \] && pass SMOKE-04' deployments/scripts/smoke-test.sh && grep -E '"404" \] && pass SMOKE-05' deployments/scripts/smoke-test.sh && grep -qF 'Chế độ read-only và cách bật ghi' docs/deployment.md && for f in docs/deployment.md docs/setup-checklist.md; do for s in 'Edge:Commands:Enabled' 'Cloud__Commands__Enabled' 'IIOT_MQTT_ACL'; do grep -qF "$s" "$f" || { echo "missing $s in $f"; exit 1; }; done; done && grep -qF 'Chế độ read-only và cách bật ghi' docs/plc-setup.md && ! grep -qF 'thử lệnh ghi từ Cloud' docs/plc-setup.md && dotnet test tests/Deployment.Tests --filter "FullyQualifiedName~EndToEndTelemetryTests"`
- Named probe: the two compose renders, the SMOKE-04/05 check lines (not their comments), the section heading and the three new switch names in both docs, the `plc-setup.md` pointer, and `EndToEndTelemetryTests`
  (the real hosts now run with commands off by default; telemetry must still be exactly-once).
- Reachability: static/source for compose (`docker compose config` needs `deployments/.secrets` env files, present on the
  dev machine) and docs; installed for the e2e regression; the smoke runtime is not run (Out of scope).
- Oracle: exit 0; e2e `Failed: 0`, `Skipped: 0`.
- Counterexample: a hard-coded `./mosquitto/acl` mount fails the override render; a doc missing one switch fails its grep.
- Artifacts: ephemeral.

## Failure Protocol
On a failed Step or Verification Plan run: stop; do not widen scope, change the Command, or weaken a test; record observed versus expected; repair only the cited cause; after three failed rounds, stop and ask the user.

## Receipt

Verification: PASS
Command: bash -n deployments/scripts/smoke-test.sh && docker compose -f deployments/compose.yaml config | grep -E 'source: .*mosquitto[\\/]acl$' && IIOT_MQTT_ACL=./mosquitto/acl-commands docker compose -f deployments/compose.yaml config | grep -E 'source: .*mosquitto[\\/]acl-commands$' && grep -E '"404" \] && pass SMOKE-04' deployments/scripts/smoke-test.sh && grep -E '"404" \] && pass SMOKE-05' deployments/scripts/smoke-test.sh && grep -qF 'Chế độ read-only và cách bật ghi' docs/deployment.md && for f in docs/deployment.md docs/setup-checklist.md; do for s in 'Edge:Commands:Enabled' 'Cloud__Commands__Enabled' 'IIOT_MQTT_ACL'; do grep -qF "$s" "$f" || { echo "missing $s in $f"; exit 1; }; done; done && grep -qF 'Chế độ read-only và cách bật ghi' docs/plc-setup.md && ! grep -qF 'thử lệnh ghi từ Cloud' docs/plc-setup.md && dotnet test tests/Deployment.Tests --filter "FullyQualifiedName~EndToEndTelemetryTests"
Exit: 0
Base: 2dd6c6feaf16dc24a691c8f999b04b84b824f237
Head: b95806a6c56ef32c9dd071322f146345f862a9b792fa6eb65ef085a837f1c5b1
```text
$ bash -n deployments/scripts/smoke-test.sh && docker compose -f deployments/compose.yaml config | grep -E 'source: .*mosquitto[\\/]acl$' && IIOT_MQTT_ACL=./mosquitto/acl-commands docker compose -f deployments/compose.yaml config | grep -E 'source: .*mosquitto[\\/]acl-commands$' && grep -E '"404" \] && pass SMOKE-04' deployments/scripts/smoke-test.sh && grep -E '"404" \] && pass SMOKE-05' deployments/scripts/smoke-test.sh && grep -qF 'Chế độ read-only và cách bật ghi' docs/deployment.md && for f in docs/deployment.md docs/setup-checklist.md; do for s in 'Edge:Commands:Enabled' 'Cloud__Commands__Enabled' 'IIOT_MQTT_ACL'; do grep -qF "$s" "$f" || { echo "missing $s in $f"; exit 1; }; done; done && grep -qF 'Chế độ read-only và cách bật ghi' docs/plc-setup.md && ! grep -qF 'thử lệnh ghi từ Cloud' docs/plc-setup.md && dotnet test tests/Deployment.Tests --filter "FullyQualifiedName~EndToEndTelemetryTests"
Passed!  - Failed:     0, Passed:     2, Skipped:     0, Total:     2, Duration: 3 m 19 s - Deployment.Tests.dll (net10.0)
```
- Compose default render: source: <repo>\deployments\mosquitto\acl; override render (IIOT_MQTT_ACL=./mosquitto/acl-commands): source: <repo>\deployments\mosquitto\acl-commands (other compose output not recorded: it holds env_file values).
- Smoke check lines: SMOKE-04 and SMOKE-05 each '[ "$code" = "404" ] && pass SMOKE-0N ... (read-only default) || fail ... (expected 404)'; smoke runtime not run here (plan Out of scope).
- Docs greps: heading and Edge:Commands:Enabled, Cloud__Commands__Enabled, IIOT_MQTT_ACL present in deployment.md and setup-checklist.md; plc-setup.md points to the section and no longer says 'thử lệnh ghi từ Cloud'.
- Review: code-auditor PASS (0 Critical/High/Medium); Low 1-4 (publish_unknown->timed_out signs, publish_unknown in in-flight list, --force-recreate cloud, prefer deployments/.env) fixed before this run; Low 5 (deployments/.env not gitignored) and Low 6 (older -RemoveData wording elsewhere) left out of scope.
