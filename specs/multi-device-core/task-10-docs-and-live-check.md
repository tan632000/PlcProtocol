# Task 10 — Setup is documented, and the real PLCs read 100 tags each with a median ≤ 500 ms and values matching the tools

Status: blocked
Blocker: waiting for the user's live run (Step 2) with the lab S7-1200 and the FX5U; the Verification Plan command ends with measure-read-ms.ps1 against the running Edge. Step 1 is implemented and its fresh-context review passed (2026-11-02): docs, lab/multi, measure-read-ms.ps1, two launch profiles, LabSettingsTests 5/5, publish without lab files, both superseded Blocker lines.

## Outcome
`docs/plc-setup.md` explains the v2 configuration and the setup of each model; a lab v2 configuration with the real
S7-1200 and FX5U (and FX3U reads if available) runs from Visual Studio; on each real PLC 100 tags are read with a median
`readMs` ≤ 500 over ≥ 30 cycles and the values equal TIA Portal / GX Works; one allowed FX5U write and one refused write
are observed. Supersedes `plc-multi-protocol` task-07 and `s7-batch-read` task-02.

## Scope
- In:
  - Docs: v2 keys, models, String/LReal/DTL rules (S7 `Length` ≤ 218, byte address), PUT/GET and Optimized block access
    for S7-1200, SLMP connection in GX Works3, MC 1E port in GX Works2 for FX3U-ENET-ADP, the switch rules for writes, a
    warning that a Y written while the program runs is overwritten by its `OUT` on the next scan, and that FX3U writes are
    disabled until verified on a real FX3U.
  - A lab file `src/Edge/lab/multi/appsettings.json` with 100 tags per PLC including the new types and discriminating
    values, laid out as a typical project (contiguous blocks plus scattered tags); a launch profile; `LabSettingsTests`
    cover it and the publish exclusion.
  - `src/Edge/lab/measure-read-ms.ps1`: samples `/api/status` until ≥ 30 distinct cycles per device, prints median
    `readMs` and `requests` per device, exit 1 when a median exceeds 500 or a device named in `-Devices` is missing or
    has fewer than 30 cycles. `<…>` are the device names of the lab file.
  - Superseded files: the Blocker line of `specs/plc-multi-protocol/task-07-live-check.md` and of
    `specs/s7-batch-read/task-02-measure-on-lab-plc.md` names this task; both stay `blocked`.
- Out: 72 h run and release (release packet); FX3U live writes (guarded).

## Coverage
- CP-01 (live), CP-02 (live), CP-03 (live), CP-05 (live read if FX3U), CP-06 (live), CP-07

## Ownership
- Modify: `docs/plc-setup.md`, `docs/deployment.md`, `src/Edge/Properties/launchSettings.json`, `tests/Edge.Tests/LabSettingsTests.cs`
- Create: `src/Edge/lab/multi/appsettings.json`, `src/Edge/lab/measure-read-ms.ps1`
- Modify: `specs/plc-multi-protocol/task-07-live-check.md`, `specs/s7-batch-read/task-02-measure-on-lab-plc.md` (Blocker line only)

## Steps
1. Write the docs, the lab file and the script; extend `LabSettingsTests`; publish check; update the two Blocker lines.
2. User runs the lab profile and the script; records per device: median `readMs` and `requests`, values against the
   engineering tool, one cable pull (the other device keeps reading), one allowed write on FX5U, one refused write.

## Acceptance
- AC-10: each real PLC's median `readMs` ≤ 500 with 100 tags; values match; isolation and write results as stated.

## Dependencies
- task-08-two-tier-writes.md
- task-09-mc1e-writes.md

## Verification Plan
- Command: `dotnet build IndustrialIot.sln -c Release && dotnet test tests/Edge.Tests --filter "FullyQualifiedName~LabSettingsTests" && P=$(mktemp -d) && dotnet publish src/Edge -c Release -o "$P" && ! find "$P" -path "*lab*" | grep -q . && powershell -NoProfile -File src/Edge/lab/measure-read-ms.ps1 -Url http://localhost:5080 -Cycles 30 -Devices <S7-1200 device>,<FX5U device>`
- Live: the user's Edge from Visual Studio with the lab profile, lab S7-1200 (192.168.3.20), the user's FX5U, FX3U if present; the user runs the script.
- Named probe: the script output per device; the engineering tool readings; `LabSettingsTests`.
- Reachability: live — real PLCs; FX3U live read only if hardware is available, otherwise recorded as a limitation.
- Oracle: script exit 0 (every median ≤ 500); every value equals the tool's; the other device keeps reading during the
  cable pull; the allowed write is seen in the tool; the refused write returns its reason and the tool shows no change.
- Counterexample: a median over 500 ms fails AC-10; a DTL shifted by hours shows a time-zone bug; a contiguous-only lab
  layout hides one-request-per-tag FX reads.
- Artifacts: the script output and recorded JSON (no secrets) in the receipt.

## Failure Protocol
On a failed Step or Verification Plan run: stop; do not widen scope, change the Command, or weaken a test; record observed versus expected; repair only the cited cause; after three failed rounds, stop and ask the user.

## Receipt
