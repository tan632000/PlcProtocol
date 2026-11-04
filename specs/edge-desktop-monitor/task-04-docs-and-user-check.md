# Task 04 — Docs describe the app; the user checks it live and installed

Status: done

## Outcome
`docs/deployment.md` explains the desktop monitor. The user has seen it work against a running Edge and from an
installed package, as a non-admin user.

## Scope
- In, `docs/deployment.md`: a new subsection "Ứng dụng IIoT Edge Monitor (Windows)" next to the monitor page section,
  covering:
  - the Start Menu shortcut and the folder `Edge.Desktop`;
  - `--port`;
  - read-only, with configuration still on the web page for now;
  - that it shows the same data as `http://localhost:5080/`;
  - the SmartScreen note (D-07);
  - that uninstall refuses while the app is open;
  - troubleshooting: the banner and its reasons, a wrong port, an invalid argument (exit 2).
- In, a user check.
  - User decision 2026-11-04 (during edge-desktop-settings GATE-SCOPE): items 1–4 are dropped here, because the window
    they check is replaced by the classic tree/grid/log window of `specs/edge-desktop-settings/`; that feature checks the
    same behaviour on the new window. Only items 5–7 are required for this task.
  - Items 1–4 (dropped): the Edge runs from source with the lab/multi profile and an absolute `--contentRoot`. The app runs with
    `dotnet run --project src/EdgeDesktop`.
    1. The window shows the Edge id, the 4 cards, every device and every tag, the same as the web monitor at the same
       moment.
    2. Stop the Edge. Within about 3 s the banner shows "không kết nối được" and the last data stays. Start the Edge.
       The banner goes away.
    3. In the web settings page, delete a device and save. Within a few seconds the app shows one device fewer.
    4. "Mở trang cấu hình" opens the settings page in the default browser.
  - Items 5–7: on a test machine with no installed Edge, in an elevated PowerShell:
    - `deployments\edge\package-edge.ps1`;
    - then `install-edge.ps1` with throw-away values: `-EdgeId desktop-check`, `-BrokerHost no-broker.invalid`, any
      PEM file as `-CaCertificate`, `-PlcHost 192.0.2.1`, and a made-up MQTT password typed at the prompt.

    5. Sign in as a non-admin user. The Start Menu shortcut "IIoT Edge Monitor" starts the app, and it shows the
       installed Edge.
    6. `"%ProgramFiles%\IndustrialIot\Edge.Desktop\IndustrialIot.EdgeDesktop.exe" --port abc` shows a message and
       exits.
    7. With the app still open, run `uninstall-edge.ps1 -RemoveData` as admin. It refuses and the service still
       exists. Close the app and run it again. The Edge, `Edge.Desktop`, the shortcut and the data are gone.
- Out: tests with real PLCs.

## Coverage
- CP-01, CP-02 and CP-04 (live and installed), CP-05

## Ownership
- Modify: `docs/deployment.md`
- Read: `src/EdgeDesktop/*`, `src/EdgeDesktop.Core/*`, `deployments/edge/*.ps1`

## Steps
1. Write the docs.
2. Run the Command.
3. The user does items 5–7 and reports. Record the report in the Receipt.

## Acceptance
- AC-07 (non-admin start, refusal while open): the user reports items 5–7 as described. AC-01, AC-02 and AC-05 rest
  on the Core tests of task-01 and the smoke start of task-02 here; their live check moves to edge-desktop-settings.
- AC-08:
  - the Command's grep finds the subsection heading, `--port`, `SmartScreen` and `Edge.Desktop` in
    `docs/deployment.md`;
  - a code review confirms the docs match the code and scripts.

## Dependencies
- task-03-package-install.md

## Verification Plan
- Command: `grep -q "Ứng dụng IIoT Edge Monitor (Windows)" docs/deployment.md && grep -q -- "--port" docs/deployment.md && grep -q "SmartScreen" docs/deployment.md && grep -q "Edge.Desktop" docs/deployment.md && dotnet test IndustrialIot.sln -- RunConfiguration.TreatNoTestsAsError=true`
- Named probe: the four docs greps, the whole solution on the final tree, and the user's report of items 5–7.
- Reachability: source on the dev machine; live and installed, by the user.
- Oracle:
  - exit 0, failed 0, skipped 0;
  - items 5–7 as expected;
  - review PASS.
- Counterexample: any of these fails this task:
  - item 5 needs administrator rights;
  - item 7 removes the service while the app is open;
  - the docs claim the app can change settings.
- Artifacts: none kept. Item 7 removes the test install.

## Failure Protocol
On a failed Step or Verification Plan run: stop; do not widen scope, change the Command, or weaken a test; record observed versus expected; repair only the cited cause; after three failed rounds, stop and ask the user.

## Receipt

Verification: PASS
Command: grep -q "Ứng dụng IIoT Edge Monitor (Windows)" docs/deployment.md && grep -q -- "--port" docs/deployment.md && grep -q "SmartScreen" docs/deployment.md && grep -q "Edge.Desktop" docs/deployment.md && dotnet test IndustrialIot.sln -- RunConfiguration.TreatNoTestsAsError=true
Exit: 0
Base: 11cb08526546d6bdaa5ed2ce295ae60736c4f6f3
Head: 752b4d8f44610e31ea02511ca92a475588832ec5aad7c9c9c433006a2d6bc98e
Process: before the docs were written, each of the four greps exited 1 (the Command stopped before dotnet test). After the docs: the Command exited 0 (the four greps print nothing; 1205 tests, 0 failed, 0 skipped); rerun on the final tree after the review repairs of the docs, output below.
Review: fresh-context docs review PASS (2 Lows on the troubleshooting table: 4xx vs 5xx split, version mismatch shows no banner), repaired; re-check PASS with one Low wording point, repaired as worded by the reviewer.
User check (scope per the user decision of 2026-11-04 recorded in Scope: items 5–7 only): the user reported on 2026-11-04 that all items met the stated expectations: item 5, a non-admin account opened "IIoT Edge Monitor" from the all-users Start Menu and saw Edge ID desktop-check; item 6, `--port abc` showed the "--port cần một số…" message and the process exit code was 2; item 7, uninstall -RemoveData with the app open in the other user's session printed the refusal with exit=1 and the service still existed, and after closing the app a second run gave exit=0, no service, and Test-Path False for Edge, Edge.Desktop, the data folder and the shortcut. Reported as a summary, without pasted output; whether SmartScreen warned was not stated.
Limitation: items 1–4 (live comparison of this window with the web monitor) were dropped by the user and move to edge-desktop-settings, which replaces this window; AC-01, AC-02 and AC-05 rest on the Core tests of task-01 and the smoke of task-02. The first run of items 1–4 found no Edge running because the instructions given did not start it (Claude's error); not a product failure.
```text
$ grep -q "Ứng dụng IIoT Edge Monitor (Windows)" docs/deployment.md && grep -q -- "--port" docs/deployment.md && grep -q "SmartScreen" docs/deployment.md && grep -q "Edge.Desktop" docs/deployment.md && dotnet test IndustrialIot.sln -- RunConfiguration.TreatNoTestsAsError=true
Passed!  - Failed:     0, Passed:   101, Skipped:     0, Total:   101, Duration: 413 ms - Shared.Tests.dll (net10.0)
Passed!  - Failed:     0, Passed:   882, Skipped:     0, Total:   882, Duration: 1 m 28 s - Edge.Tests.dll (net10.0)
Passed!  - Failed:     0, Passed:   175, Skipped:     0, Total:   175, Duration: 2 m 22 s - Cloud.Tests.dll (net10.0)
Passed!  - Failed:     0, Passed:    47, Skipped:     0, Total:    47, Duration: 3 m 37 s - Deployment.Tests.dll (net10.0)
```
