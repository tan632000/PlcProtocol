# Task 03 — The Edge package installs the desktop app safely for every user

Status: done

## Outcome
The Edge zip carries the app. Install puts it in a folder that normal users can run, with a Start Menu shortcut, and
never overwrites anything that exists already. Uninstall refuses while the app is running; otherwise it removes both.

## Scope
- In (D-06). Below, `$AppDir = "$InstallDir.Desktop"` and `$Shortcut` is
  `%ProgramData%\Microsoft\Windows\Start Menu\Programs\IIoT {leaf of $InstallDir} Monitor.lnk`.
  - `package-edge.ps1`: publishes `src/EdgeDesktop` with `-c Release -r win-x64 --self-contained true` into
    `EdgeDesktop\` inside the staging folder.
  - `install-edge.ps1`:
    - Preconditions, checked with the existing ones (`install-edge.ps1:175-189`) before anything is created:
      - `$AppDir` and `$Shortcut` must not exist;
      - their parent must not be a reparse point.
    - After expanding the zip, moves `$InstallDir\EdgeDesktop` to `$AppDir`.
    - Sets the ACL of `$AppDir`: SYSTEM and Administrators full control, Users read and execute, inheritance
      protected.
    - Creates `$Shortcut` with `WScript.Shell`.
    - Records `$created` flags, so the rollback removes only the folder and shortcut that this run created.
  - `uninstall-edge.ps1`:
    - Before stopping or deleting the service, refuses while any process `IndustrialIot.EdgeDesktop` has its
      executable under `$AppDir`. The message: "Hãy đóng IIoT Edge Monitor rồi chạy lại.", exit code 1, nothing
      removed.
    - Otherwise removes `$Shortcut` and `$AppDir`, with the same junction-safe delete as the Edge folder, and then
      the `IndustrialIot` parent only when it is empty, as today.
    - `Test-EdgeFolder` keeps rejecting `Edge.Desktop` as an Edge folder. Because of the dot it does not match
      `Edge[A-Za-z]*`.
  - `verify-edge.ps1`:
    - VERIFY-PACKAGE also expects `EdgeDesktop\IndustrialIot.EdgeDesktop.exe`.
    - `-Live` also refuses, before doing anything, when `EdgeVerify.Desktop` or its shortcut exists.
    - New `-Live` checks:
      - LIVE-DESKTOP-ACL;
      - LIVE-DESKTOP-SHORTCUT;
      - LIVE-DESKTOP-INUSE: start the app from `EdgeVerify.Desktop`, run uninstall, which must refuse and leave the
        service; then stop the app;
      - LIVE-UNINSTALL also covering the app folder and the shortcut.
- Out: per-user install, MSI, signing, auto-start of the app, stopping the app automatically.

## Coverage
- CP-04

## Ownership
- Modify:
  - `deployments/edge/package-edge.ps1`
  - `deployments/edge/install-edge.ps1`
  - `deployments/edge/uninstall-edge.ps1`
  - `deployments/edge/verify-edge.ps1`
- Read: `src/EdgeDesktop/IndustrialIot.EdgeDesktop.csproj`

## Steps
1. Extend `verify-edge.ps1` first. Run the Command: VERIFY-PACKAGE must fail.
2. Change package, install and uninstall. Run the Command.
3. The user runs `verify-edge.ps1 -Live` in an elevated PowerShell, on a test machine without `EdgeVerify`,
   `EdgeVerify.Desktop` or its shortcut, and reports PASS/FAIL per check.

## Acceptance
- AC-07, static: the Command prints PASS for every default check, including VERIFY-PACKAGE with the app's exe.
- AC-07, installed: the user reports these `-Live` checks as PASS:
  - LIVE-DESKTOP-ACL;
  - LIVE-DESKTOP-SHORTCUT;
  - LIVE-DESKTOP-INUSE;
  - LIVE-UNINSTALL;
  - the existing Live checks.
- The non-admin start is item 5 of task-04.

## Dependencies
- task-02-wpf-window.md

## Verification Plan
- Command: `powershell -NoProfile -ExecutionPolicy Bypass -File deployments/edge/verify-edge.ps1`
- Named probe: VERIFY-PACKAGE, plus the user's `-Live` run of LIVE-DESKTOP-ACL, LIVE-DESKTOP-SHORTCUT,
  LIVE-DESKTOP-INUSE and LIVE-UNINSTALL.
- Reachability:
  - source, on the dev machine;
  - installed, on the user's test machine (elevated).
- Oracle: exit 0 and no `FAIL` line, plus the user's report of PASS for every `-Live` check.
- Counterexample: each of these must fail a check:
  - a package without `EdgeDesktop\` (VERIFY-PACKAGE);
  - Users given Modify (LIVE-DESKTOP-ACL);
  - uninstall removing the service while the app runs (LIVE-DESKTOP-INUSE).
- Artifacts: the temp work folder of `verify-edge.ps1` is removed by the script. `-Live` removes everything it
  installed.

## Failure Protocol
On a failed Step or Verification Plan run: stop; do not widen scope, change the Command, or weaken a test; record observed versus expected; repair only the cited cause; after three failed rounds, stop and ask the user.

## Receipt

Verification: PASS
Command: powershell -NoProfile -ExecutionPolicy Bypass -File deployments/edge/verify-edge.ps1
Exit: 0
Base: 11cb08526546d6bdaa5ed2ce295ae60736c4f6f3
Head: 82d45523e29a0edbf121f0bb59d2fcd560496f25599ae3fe419187b4e61ab8b4
Executed by: the user, on the dev machine in an elevated Windows PowerShell 5.1 (the auto-mode classifier denied Claude running this script); output pasted back verbatim; `$LASTEXITCODE` printed afterwards as exit=0.
Process: before package/install/uninstall changed, the same Command (verify-edge.ps1 already extended) exited 1 with "missing in package: EdgeDesktop\IndustrialIot.EdgeDesktop.exe" and "...EdgeDesktop\hostfxr.dll" (FAIL VERIFY-PACKAGE; VERIFY-FAILFAST failed with it because it only runs on a valid package).
Live (installed reachability, user, elevated, same Head): `verify-edge.ps1 -Live` exit=0 with PASS LIVE-SERVICE, LIVE-ACL, LIVE-REGACL, LIVE-RUNNING, LIVE-DESKTOP-ACL, LIVE-DESKTOP-SHORTCUT, LIVE-DESKTOP-INUSE (refusal "Hãy đóng IIoT Edge Monitor rồi chạy lại.", service still Running), LIVE-UNINSTALL; the shortcut and EdgeVerify.Desktop were removed before the service.
Counterexamples: a package without EdgeDesktop\ was observed failing VERIFY-PACKAGE (pre-change run). Users-with-Modify and uninstall-removing-the-service-while-the-app-runs are covered by the exact-rights Test-Acl and the Running/exit/message oracle of LIVE-DESKTOP-INUSE, read in review, not mutated on a live install.
Review: fresh-context code review PASS_WITH_WARNINGS (Medium: uninstall removed the service before the app, so an app started between check and delete left a half-removed, unrecoverable install), repaired by removing shortcut and app before the service; LIVE-DESKTOP-INUSE now also requires the monitor named in the refusal and the service Running; install rollback warns when the app folder cannot be removed. Re-review PASS. The Command run and the -Live run recorded here are after the repair.
Discarded runs: one default run failed VERIFY-BUILD only (build output suppressed; a later `dotnet build` was clean; suspected transient file lock, unconfirmed); two -Live runs got an empty MQTT password from a buffered Enter and stopped before installing anything.
Limitation: uninstall-edge.ps1 carries a UTF-8 BOM for its Vietnamese message (the other scripts are ASCII). A process named IndustrialIot.EdgeDesktop.exe whose path cannot be read blocks uninstall (deliberate fail-safe). The shortcut's existence check and Save() are not atomic (all-users Start Menu is not user-writable; inferred, not checked). If a future uninstall stopped refusing, LIVE-DESKTOP-INUSE would end the run with an error instead of a FAIL line (still exit 1). verify-edge.ps1 -Live throws at its LIVE-ACL Get-Acl when install fails (pre-existing; still exit 1).
```text
$ powershell -NoProfile -ExecutionPolicy Bypass -File deployments/edge/verify-edge.ps1
PASS VERIFY-BUILD the solution builds
PASS VERIFY-STATIC Program.cs calls AddWindowsService; install-edge.ps1 never runs dotnet
PASS VERIFY-PACKAGE self-contained package with exe, runtime, appsettings.json, the scripts and the desktop monitor, no data folder
PASS VERIFY-FAILFAST packaged exe exits non-zero naming Edge:EdgeId for an invalid id
PASS VERIFY-PARSE all four scripts parse with PowerShell 5.1
PASS VERIFY-SECRETS no password parameter; no output of a password
Edge verification passed.
```
