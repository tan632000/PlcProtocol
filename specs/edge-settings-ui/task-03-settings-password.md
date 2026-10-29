# Task 03 — The settings password is set from the command line, in the directory the service uses, and verified with a lockout

Status: done

## Outcome
`IndustrialIot.Edge.exe set-settings-password` stores a PBKDF2 hash in `<db>.settings-password.json`, in the same
directory the service uses (D-01, D-04). A verifier accepts only that password, with the D-05 lockout and logging.

## Scope
- In:
  - File format: `{"Algorithm":"PBKDF2-SHA256","Iterations":600000,"Salt":"<base64>","Hash":"<base64>"}`, written atomically.
  - CLI:
    - Prompts twice without echo; the password must be at least 12 characters.
    - Mismatched prompts → exit 1, and no file is written.
    - A password on argv → exit 2 and usage text.
    - Success → exit 0. The password is never printed.
  - Command line: `set-settings-password [--service] [--contentRoot <dir>]`. Any other argument → exit 2.
  - Directory resolution: with `--service`, read `Edge__Buffer__Path` from the registry Environment of `IndustrialIot.Edge`. Without it, use the effective configuration of that content root, even when a service is installed. The registry goes through an injectable reader.
  - Register the verifier (with its lockout) in the supervisor's shared-state list, so it survives inner-host rebuilds (N-3).
  - Refuse a target directory that is a reparse point, or whose ACL grants write to Users, Authenticated Users or Everyone (exit 1, naming the reason).
  - Verifier:
    - Compares with `CryptographicOperations.FixedTimeEquals`.
    - Returns `NotSet` when the file is missing.
    - Lockout: 5 failures → locked for 60 s; attempts during the lockout are not counted.
    - Logs each failure and each lockout, without the password.
- Out: using the verifier over HTTP (task-04 and task-05).

## Coverage
- CP-03

## Ownership
- Modify: `src/Edge/Program.cs` (dispatches the `set-settings-password` command before any host is built), `src/Edge/Settings/EdgeSupervisor.cs` (shared-state list entry only), `src/Edge/EdgeServiceCollectionExtensions.cs` (default registration)
- Create: `src/Edge/Settings/SettingsPassword.cs`, `tests/Edge.Tests/SettingsPasswordTests.cs`
- Read: `src/Edge/Settings/EdgeSettingsFile.cs`, `deployments/edge/install-edge.ps1:200-240`

## Steps
1. Write `SettingsPasswordTests`. Cases:
   - (a) Set a password, then verify: the right one passes, a wrong one fails.
   - (b) The file has no plaintext, the iteration count is 600000, and `Hash` ≠ SHA-256(password).
   - (c) A missing file → `NotSet`.
   - (d) With a fake `TimeProvider`: 5 failures → locked; a 6th attempt during the lockout does not extend it; unlocked at 60 s.
   - (e) An 11-character password is refused.
   - (f) A password on argv → exit 2.
   - (g) Mismatched prompts → exit 1 and no file.
   - (h) With `--service` and the registry reader returning `Edge__Buffer__Path=C:\X\edge.db` → the target is `C:\X\edge.settings-password.json`. Without `--service`, with the same reader and `--contentRoot lab/fx5u`, the target is `data/fx5u-lab.settings-password.json` resolved as the lab profile does.
   - (i) A target directory with Users:Write → refused.
   - (j) Captured log output never contains the password.

   Before the change, expect compile failure.
2. Implement it, and dispatch the command in `Program.cs`.

## Acceptance
- AC-03: (a)–(j) pass, and the full Edge suite passes.

## Dependencies
- task-02-supervisor-and-restart.md

## Verification Plan
- Command: `dotnet test tests/Edge.Tests`
- Named probe: `SettingsPasswordTests` (a)–(j).
- Reachability: source level, on the dev machine and in CI. (i) uses a temp directory whose ACL the test sets itself.
- Oracle: exit 0, `Skipped: 0`, `SettingsPasswordTests` ≥ 10.
- Counterexample:
  - Storing a plain SHA-256 makes (b) fail.
  - Ignoring `--service` makes (h) point to `data/`.
  - Always preferring the registry makes the `--contentRoot` half of (h) point to `C:\X`.
- Artifacts: ephemeral temp directories.

## Failure Protocol
On a failed Step or Verification Plan run: stop; do not widen scope, change the Command, or weaken a test; record observed versus expected; repair only the cited cause; after three failed rounds, stop and ask the user.

## Receipt

Verification: PASS
Command: dotnet test tests/Edge.Tests
Exit: 0
Base: 927442f6c144893b2810cf30b14ac572eee996e8
Head: 2c700629a377fae38bd38c5cab0896967094c4356d29cb0ab3d0c2fd097754cf
```text
$ dotnet test tests/Edge.Tests
Passed!  - Failed:     0, Passed:   498, Skipped:     0, Total:   498, Duration: 28 s - Edge.Tests.dll (net10.0)
$ dotnet test tests/Edge.Tests --no-build --filter "FullyQualifiedName~SettingsPasswordTests"
Passed!  - Failed:     0, Passed:    26, Skipped:     0, Total:    26, Duration: 1 s - Edge.Tests.dll (net10.0)
```
- Pre-change run: compile failure CS0246, because `SettingsPassword` and `ISettingsPasswordConsole` did not exist yet.
- Probe → acceptance mapping:
  - (a) `Verify_AcceptsOnlyTheStoredPassword`
  - (b) `StoredFile_HoldsAPbkdf2HashOnly` (iterations 600000, 16-byte salt, hash ≠ SHA-256, equals PBKDF2)
  - (c) `MissingFile_IsNotSet`
  - (d) `FiveFailures_LockForSixtySeconds_AndAttemptsDuringTheLockDoNotExtendIt` (`FakeTimeProvider`)
  - (e) `Command_RefusesAPasswordShorterThan12`
  - (f) `Command_RefusesAnyOtherArgument_WithUsage` (3 rows; no prompt)
  - (g) `Command_RefusesMismatchedPrompts`
  - (h) `Command_WithService_UsesTheServiceBufferPath` and `Command_WithoutService_IgnoresTheService_AndFollowsTheContentRoot` (lab layout `../../data/fx5u-lab.db`, absolute `--contentRoot`)
  - (i) `Command_RefusesADirectoryOtherUsersCanWrite` (Users:Write ACE on Windows, o+w on Linux)
  - (j) `Logs_NeverContainThePassword`
  - Added:
    - the shared verifier is the same instance before and after an apply (N-3)
    - a damaged or tampered hash file gives NotSet in under 2 s (7 rows)
    - access failures give exit 1
    - `--service` together with `--contentRoot` gives exit 2
    - a refused attempt leaves no new directory
- Linux: `dotnet test tests/Edge.Tests` in the pinned SDK container passed 498/498, `Skipped: 0`.
- Counterexamples on fresh scratch copies (`scratchpad/mut-esui03`, script `scratchpad/mut-t03.sh`):

  | Mutation | Tests that failed |
  |---|---|
  | Plain SHA-256 instead of PBKDF2 | 4, incl. (b) |
  | `--service` ignored | 3 |
  | Registry always preferred | the `--contentRoot` half of (h) |
  | Verifier not in the shared list | the same-instance test |

  The first versions of the `--service` and shared-list mutations did not compile (warnings are errors). They were rewritten as compilable changes and rerun.
- Review (code-auditor, fresh context):
  - Round 1: PASS_WITH_WARNINGS, with two Mediums:
    - permission and IO exceptions escaped instead of exiting 1;
    - a damaged or tampered hash file could throw, or stall the lock (iteration count and hash length came from the file).
  - Repair round 1 fixed both: a strict `StoredRecord` check (iterations 600000..10000000, salt ≥ 16, hash = 32), and exit 1 with an "elevated prompt" hint. The same round also added the generic write/all bits to the ACL mask, the 0600 temp file on Unix, cleanup of a refused directory, refusal of `--service` with `--contentRoot`, and a Windows-only message.
  - Re-review: PASS, 0 Critical/High/Medium.
- Lows left as they are:
  - Empty intermediate parent directories can remain after a refusal.
  - The "elevated prompt" hint is also shown for non-permission IO errors.
  - `ReadKey` without a console throws.
  - Owner and other SIDs beyond D-04's list are not checked, nor Unix group write or parent-chain junctions.
- Ownership note: `AddEdgeSupervisor` also registers `TimeProvider` and `SettingsPassword`. Both are needed to resolve the shared list; the review accepted them as Low.
