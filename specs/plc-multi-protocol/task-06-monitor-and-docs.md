# Task 06 — The Monitor shows the protocol; operators can configure and reinstall an FX5U Edge

Status: done

## Outcome
The PLC Monitor shows the selected protocol, host and port (rack/slot only for S7); the docs explain the FX5U setup,
an FX5U configuration and what to copy back after a reinstall (plan D-06).

## Scope
- In:
  - Monitor status `plc` gains `protocol`; host/port come from the selected protocol's options instead of
    `IOptions<SiemensS7Options>` (`src/Edge/Monitoring/MonitorServer.cs:23,128`); rack/slot are null for SLMP and
    `monitor.html` (`:151`) shows them only when present.
  - `docs/plc-setup.md` section "Mitsubishi FX5U (SLMP)": GX Works3 Ethernet port → External Device Configuration →
    SLMP Connection Module, TCP, port, partner IP = the Edge PC, Alive Check enabled, Communication Data Code = Binary,
    IP Filter to the Edge's IP, Online Change "Disable All (SLMP)" with what each setting blocks marked [UNVERIFIED]
    until checked on a bench PLC (FX5 communication manual), one Edge per SLMP connection; addresses D/M/X/Y with X/Y
    octal (X17), data types and word order; a complete `Edge:Plc` + `Tags` example with `"Protocol": "Slmp"`; S7 section
    notes `CpuType` for S7-1500.
  - `docs/deployment.md` (Edge reinstall paragraph): copy back the whole `Edge:Plc` section as well as `Tags`/`Commands`;
    note that `install-edge.ps1` writes `Edge__Plc__Host` into the service environment, which overrides `appsettings.json`
    (`deployments/edge/install-edge.ps1:240`).
  - `docs/setup-checklist.md`: choose the protocol and set `Edge:Plc` for an FX5U.
- Out: installer parameters (documentation only, user decision).

## Coverage
- CP-05

## Ownership
- Modify: `src/Edge/Monitoring/MonitorServer.cs`, `src/Edge/Monitoring/monitor.html`, `tests/Edge.Tests/MonitorTests.cs`
- Modify: `docs/plc-setup.md`, `docs/deployment.md`, `docs/setup-checklist.md`

## Steps
1. Monitor tests first: S7 → protocol `S7`, port 102, rack/slot present; SLMP with port 5000 → protocol `Slmp`, port
   5000, rack/slot null; implement.
2. Docs.

## Acceptance
- AC-05: Monitor tests pass; doc checks pass.

## Dependencies
- task-03-protocol-switch-s7-cpu.md
- task-05-slmp-bit-devices.md

## Verification Plan
- Command: `dotnet test tests/Edge.Tests --filter "FullyQualifiedName~MonitorTests" && for s in 'SLMP Connection Module' 'Binary' 'IP Filter' 'Alive Check' '"Protocol": "Slmp"' 'CpuType' 'X17'; do grep -qF -- "$s" docs/plc-setup.md || { echo "missing $s"; exit 1; }; done && grep -qF 'Edge:Plc' docs/deployment.md && grep -qF 'Protocol' docs/setup-checklist.md && echo DOCS-OK`
- Named probe: Monitor protocol/port/rack cases; the doc strings.
- Reachability: source.
- Oracle: `Failed: 0`, `Skipped: 0`, then `DOCS-OK`.
- Counterexample: a Monitor still reading `SiemensS7Options` shows port 102 for the SLMP case (configured 5000) and fails it.
- Artifacts: none.

## Failure Protocol
On a failed Step or Verification Plan run: stop; do not widen scope, change the Command, or weaken a test; record observed versus expected; repair only the cited cause; after three failed rounds, stop and ask the user.

## Receipt

Verification: PASS
Command: dotnet test tests/Edge.Tests --filter "FullyQualifiedName~MonitorTests" && for s in 'SLMP Connection Module' 'Binary' 'IP Filter' 'Alive Check' '"Protocol": "Slmp"' 'CpuType' 'X17'; do grep -qF -- "$s" docs/plc-setup.md || { echo "missing $s"; exit 1; }; done && grep -qF 'Edge:Plc' docs/deployment.md && grep -qF 'Protocol' docs/setup-checklist.md && echo DOCS-OK
Exit: 0
Base: f985f23f58461cb5364cf96e0815c54fa444d6ec
Head: 08913b10f4ba1f8c3498520ec3328263d8cc1b6fd1701eacd03d29abeee513a0
```text
$ dotnet test tests/Edge.Tests --filter "FullyQualifiedName~MonitorTests" && for s in 'SLMP Connection Module' 'Binary' 'IP Filter' 'Alive Check' '"Protocol": "Slmp"' 'CpuType' 'X17'; do grep -qF -- "$s" docs/plc-setup.md || { echo "missing $s"; exit 1; }; done && grep -qF 'Edge:Plc' docs/deployment.md && grep -qF 'Protocol' docs/setup-checklist.md && echo DOCS-OK
Test run for C:\Users\case 046\Documents\Project\PlcProtocol\tests\Edge.Tests\bin\Debug\net10.0\Edge.Tests.dll (.NETCoreApp,Version=v10.0)
Passed!  - Failed:     0, Passed:     9, Skipped:     0, Total:     9, Duration: 2 s - Edge.Tests.dll (net10.0)
DOCS-OK
```
- Pre-change run failed as expected (missing SLMP Connection Module in docs/plc-setup.md). After: MonitorTests 9/9, DOCS-OK; full Release build 0 warnings; implementer reported the full Edge suite 401/401.
- Named probes: Status_S7ShowsProtocolPortRackAndSlot (S7, 102, rack 0, slot 1); Status_SlmpShowsProtocolAndPortWithoutRackAndSlot (Slmp, 5000 from SLMP options, rack/slot null — fails if the Monitor still reads SiemensS7Options, which would show 102); doc strings in plc-setup.md, deployment.md, setup-checklist.md.
- Review: code-auditor PASS (0 Critical/High/Medium); Low 1 (GX Works3 menu path not labelled) and Low 2 (test spacing) applied before this run. Docs keep [UNVERIFIED] on GX Works3 menu names, protection-setting effects and S7-1500 until task-07 / hardware.
