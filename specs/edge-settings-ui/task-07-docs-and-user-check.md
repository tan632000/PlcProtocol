# Task 07 — Docs and installer text match the new precedence; the user confirms the page and the installed service

Status: done

## Outcome
`docs/deployment.md` explains the settings page, and the installer's closing message points to it. The user has used the
page on the dev machine, and an installed service is still Running after a Save (the live level of AC-02).

## Scope
- In, `docs/deployment.md`:
  - The command `set-settings-password --service`, run as Administrator for an installed Edge; `set-settings-password` (optionally `--contentRoot <dir>`) for a console run.
  - `http://localhost:5080/settings`.
  - The file name and location (D-01).
  - While the file exists, `appsettings.json` `Plc`/`Poll`/`Tags` and `-PlcHost` are ignored (D-02). The file survives uninstall without `-RemoveData` and a reinstall.
  - `Writable`/`Min`/`Max` are edited in the settings file as Administrator, followed by `Restart-Service`.
  - Renaming a TagId starts a new series in the Cloud.
  - The lockout and how to recover from it. The accepted local-DoS risk.
  - Recovery: delete the settings file, then restart.
  - Troubleshooting rows for 401/403/409/415/429 and `rolledBack`.
  - Rewrite the existing lines that send users to `appsettings.json` / `-PlcHost`. The R-15 review found them at about lines 146, 149, 155-160, 276, 308, 344-346 and 390; re-find them before editing.
- In, `deployments/edge/install-edge.ps1`: only the final `Write-Host` (line 255) changes, to point to `/settings`.
- In, `deployments/edge/uninstall-edge.ps1`: only the "Kept the tag list" message (line 86) changes, to also name the kept `edge.settings.json`.
- Out: any other installer behaviour.

## Coverage
- CP-06, plus the live level of CP-02

## Ownership
- Modify: `docs/deployment.md`, `deployments/edge/install-edge.ps1` (line 255 message only), `deployments/edge/uninstall-edge.ps1` (line 86 message only)
- Read: `src/Edge/Settings/*.cs`, `src/Edge/Monitoring/settings.html`, `deployments/edge/verify-edge.ps1`, `deployments/edge/uninstall-edge.ps1`

## Steps
1. Write the docs and the message.
2. User check on the dev machine, with no PLC. The user does each item and reports what happened (without the password):
   - Set the password.
   - Open `/settings`, change the poll interval, add a tag, save.
   - See the change applied on the monitor.
   - Press Test kết nối and get a clear failure.
3. User live check, from an elevated PowerShell:
   - Install with `verify-edge.ps1`'s install path, or `install-edge.ps1` with a throwaway EdgeId.
   - Run `set-settings-password --service`.
   - Save once on `/settings`.
   - `Get-Service IndustrialIot.Edge` still shows Running, and the monitor shows the new settings.
   - Uninstall with `-RemoveData`.

## Acceptance
- AC-02 (live): Step 3 shows Running after the save.
- AC-08: code review confirms the docs match the code, and the Step 2 observations are recorded.

## Dependencies
- task-06-settings-page.md

## Verification Plan
- Command: `dotnet test IndustrialIot.sln`
- Named probe: the whole solution on the final tree, plus `powershell -NoProfile -ExecutionPolicy Bypass -File deployments/edge/verify-edge.ps1` (VERIFY-PARSE and VERIFY-STATIC after the message change), plus the user's Step 2 and Step 3 reports.
- Reachability: source on the dev machine. Steps 2 and 3 are done by the user; Step 3 needs an elevated PowerShell.
- Oracle: exit 0 with `Skipped: 0`; verify-edge default level all PASS; the user reports Running after the save.
- Counterexample: a doc saying the page edits `Writable` contradicts D-06 and fails review. A service that stops after the save fails AC-02 live.
- Artifacts: none kept. The test install is removed with `-RemoveData`.

## Failure Protocol
On a failed Step or Verification Plan run: stop; do not widen scope, change the Command, or weaken a test; record observed versus expected; repair only the cited cause; after three failed rounds, stop and ask the user.

## Receipt

Verification: PASS
Command: dotnet test IndustrialIot.sln
Exit: 0
Base: 927442f6c144893b2810cf30b14ac572eee996e8
Head: 1d1bbd51c1552a2f73f7afba1e8b01f7241280511bd4ffa3a58e108d62081039
```text
$ dotnet test IndustrialIot.sln
Passed!  - Failed:     0, Passed:    55, Skipped:     0, Total:    55, Duration: 168 ms - Shared.Tests.dll (net10.0)
Passed!  - Failed:     0, Passed:   549, Skipped:     0, Total:   549, Duration: 33 s - Edge.Tests.dll (net10.0)
Passed!  - Failed:     0, Passed:   143, Skipped:     0, Total:   143, Duration: 1 m 50 s - Cloud.Tests.dll (net10.0)
Passed!  - Failed:     0, Passed:    37, Skipped:     0, Total:    37, Duration: 3 m 30 s - Deployment.Tests.dll (net10.0)
```
- Pre-change run: the same command passed (55/549/143/37). This was expected: for this docs task the command only guards against regressions; the review and the user's Step 2 and Step 3 reports judge the work.
- verify-edge default level: the user ran `powershell -NoProfile -ExecutionPolicy Bypass -File deployments\edge\verify-edge.ps1` (the session's own run of it was refused by the permission classifier). The pasted run showed PASS for VERIFY-BUILD, VERIFY-STATIC, VERIFY-PACKAGE, VERIFY-FAILFAST, VERIFY-PARSE and VERIFY-SECRETS, then "Edge verification passed." That run came before the last `install-edge.ps1` message edit (adding `& '...'`). The user reported the rerun after it as passing, without pasting its output.
- Review (code-auditor, fresh context): the first round was FAIL, with 1 High, 2 Medium and 4 Low:
  - High: the FX5U example used a relative `--contentRoot`, which resolves under `bin\`.
  - Medium: the wrong fatal log string.
  - Medium: 429 `rate-limited` was folded into `locked`.
  - Low: the button names, the claim "trình duyệt không lưu", inexact page strings, and `&` missing in the installer message.

  After one repair round: PASS, with no findings. The reviewer ran the built exe with an absolute `--contentRoot`; it resolved `lab\fx5u` and wrote no file.
- Live (user, 2026-10-29):
  - Step 2 on the dev machine: the poll interval change and the added tag were applied. Test kết nối gave `unreachable`. The task-06 observations cover the rest.
  - Step 3: the package was built with `package-edge.ps1`, then installed with `install-edge.ps1` and the throwaway EdgeId `verify-settings`. `set-settings-password --service` was run, then one Save on `/settings`. After it, `Get-Service IndustrialIot.Edge` showed Running and the monitor showed the new settings. The service was then uninstalled with `-RemoveData`.

  The user reported all of these as "tất cả đều ổn"; no raw output was pasted.
- Limitations found during this task, outside its scope:
  - The package contains the dev `data\edge.settings.json` and `data\edge.settings-password.json`. This was confirmed by listing the zip. The build copies `src\Edge\data\*.json` to the output. The service does not read them, because its buffer path is DataDir, but a release package should not carry them.
  - The VS profile "FX5U test (no broker)" passes a relative `--contentRoot lab/fx5u`. It may resolve under `bin\`. This is an inference; it has not been run.
  - The uninstall "Kept…" message prints even with `-RemoveData`, which then deletes the folder. This behaviour existed before this task.
