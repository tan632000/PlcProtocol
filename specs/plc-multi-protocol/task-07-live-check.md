# Task 07 — The Edge reads the real S7-1200 and the real FX5U, proven with discriminating values

Status: blocked
Blocker: waiting for the user's live run with the lab S7-1200 and the FX5U (Step 2); Step 1 is implemented (launch profile, src/Edge/lab/fx5u/appsettings.json, publish exclusion, .gitignore, LabSettingsTests 3/3; publish verified without lab files). Review of Step 1 runs with the live results.

## Outcome
Running the Edge from Visual Studio without a broker, the Monitor shows values that equal TIA Portal for the lab
S7-1200 and GX Works3 for the FX5U, with test values chosen so that a wrong word order, bit position or X/Y numbering
would be visible (settles the [UNVERIFIED] items of plan D-05; S7-1500 stays unverified).

## Scope
- In:
  - Profile "FX5U test (no broker)" with `--contentRoot` pointing at `src/Edge/lab/fx5u`, a complete separate
    `appsettings.json` there (protocol `Slmp`, host/port placeholders, the tag set below); the folder is excluded from
    publish (`CopyToPublishDirectory="Never"`) so it never ships to customers; the lab file sets an explicit
    `Edge:Buffer:Path` outside the source tree and `.gitignore` adds `src/Edge/lab/**/data/`; `LabSettingsTests` starts
    the host with `Host.CreateApplicationBuilder(["--contentRoot", <lab dir>])` (not `EdgeTestHost`, which loads no
    appsettings) and a test MQTT password, proving the file loads with `Protocol=Slmp`.
  - FX5U tag set and values set from GX Works3 (Watch/Device test): D Int = −1; D DInt = 123456 then −2; D Real = 12.5;
    M14 OFF with M15 ON, then reversed; M101 (unaligned) ON then OFF; octal check with X10 ON / X12 OFF then reversed
    if inputs can be switched, otherwise Y10 / Y12 forced by GX Works3; every bit tag seen in both states.
  - S7-1200 regression with the existing "PLC test (no broker)" profile: at least one value changed in TIA Portal and
    seen on the Monitor.
  - Before the FX5U run: stop any installed Edge service that holds the SLMP connection; run from the PC the IP Filter
    allows; make sure the developer user-secrets hold no `Edge:Plc:*` key (they override under Development).
  - Record the FX5U "Device/Label Memory Area Setting" from GX Works3 (D/M/X/Y ranges) in the receipt; if it differs from
    the ranges assumed in tasks 04-05, stop and correct them.
- Out: S7-1500 (no hardware), Cloud/MQTT.

## Coverage
- CP-01, CP-04

## Ownership
- Modify: `src/Edge/Properties/launchSettings.json`, `src/Edge/IndustrialIot.Edge.csproj` (publish exclusion), `.gitignore`
- Create: `src/Edge/lab/fx5u/appsettings.json`, `tests/Edge.Tests/LabSettingsTests.cs`

## Steps
1. Profile, lab settings, publish exclusion, the source test (host starts from the lab content root with
   `Protocol=Slmp`; `package-edge.ps1` output contains no `lab/` file).
2. The user sets IP/port/addresses, runs each profile and sets the prescribed values; after each change the controller
   reads `/api/status`; the user reads the engineering tool at the same moment. The receipt records both verbatim with
   times; only the user's own message counts as their reading.

## Acceptance
- AC-06: every tag Good; each prescribed value and both states of every bit tag equal the user's engineering-tool
  readings; the S7 change is seen; the lab settings test passes.

## Dependencies
- task-05-slmp-bit-devices.md
- task-06-monitor-and-docs.md

## Verification Plan
- Command: `dotnet test tests/Edge.Tests --filter "FullyQualifiedName~LabSettingsTests" && P=$(mktemp -d) && dotnet publish src/Edge -c Release -o "$P" && ! find "$P" -path "*lab*" | grep -q . && curl -s http://localhost:5080/api/status`
- Live: run after each prescribed change, for the S7-1200 profile and the FX5U profile, by the user's Edge started from Visual Studio.
- Named probe: the recorded `/api/status` JSON per change; the user's GX Works3/TIA readings; `LabSettingsTests`.
- Reachability: live — lab S7-1200 (192.168.3.20) and the user's FX5U.
- Oracle: values and bit states equal the engineering tool's readings; all Good.
- Counterexample: wrong word order shows 123456 as another number; decimal X numbering makes X10 follow X12; a bit
  off by one shows M14/M15 swapped.
- Artifacts: the recorded JSON (no secrets) in the receipt.

## Failure Protocol
On a failed Step or Verification Plan run: stop; do not widen scope, change the Command, or weaken a test; record observed versus expected; repair only the cited cause; after three failed rounds, stop and ask the user. Without the PLCs or the user's run the task stays `blocked` naming which.

## Receipt
