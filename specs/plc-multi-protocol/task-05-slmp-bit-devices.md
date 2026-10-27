# Task 05 — FX5U SLMP reads M, X and Y bits with a fixed word alignment

Status: done

## Outcome
SLMP tags on `M<n>`, `X<n>` and `Y<n>` read as `Bool` through word-unit Device Reads aligned to 16-device boundaries
(plan D-05).

## Scope
- In:
  - Syntax: `M` decimal (0..7679), `X`/`Y` octal digits only (0..1777 octal) as GX Works3 shows them on the FX5U
    [ranges UNVERIFIED — GX Works3 device settings; settled in task-07]; type `Bool` only; `X8`, `Y19` and any non-Bool
    type rejected at start naming the tag.
  - Device number = decimal value for M, octal value for X/Y (X17 → 15, X20 → 16); word head = number & ~0xF; bit =
    number & 0xF; within the 2-byte word, byte 0 holds bits 0-7 (bit 0 = head) and byte 1 bits 8-15.
  - `SlmpDevice` gains M=0x90, X=0x9C, Y=0x9D (codes [UNVERIFIED] until checked against SH-081951 and task-07);
    `BuildDeviceReadWords` stays the only builder (same command/subcommand constants).
  - Blocks per device on aligned words: bits sharing or touching a word share a request; D and M tags in one cycle use
    separate requests; the same isolation and outage rules as task-04.
- Out: writes; other devices (L, B, F, SM, SD, W, R).

## Coverage
- CP-04

## Ownership
- Modify: `src/Edge/Drivers/Slmp/SlmpAddressSyntax.cs`, `src/Edge/Drivers/Slmp/MitsubishiSlmpDriver.cs`, `src/Edge/Drivers/Slmp/SlmpBlockPlanner.cs` (word mapping; added in implementation)
- Modify: `tests/Edge.Tests/Fakes/FakeSlmpServer.cs`, `tests/Edge.Tests/SlmpDriverTests.cs`

## Steps
1. Tests first:
   - parsing: M100, M7679; X17 → device 15; X20 → 16; Y0; X8 / Y19 / `Int` on M rejected;
   - frame bytes for a word-unit read of M96 count 1 (head 96 = 0x60, device code 0x90), golden bytes from the manual
     where available [UNVERIFIED until checked];
   - decoding: M96 → bit 0 of byte 0; M103 → bit 7 of byte 0; M104 → bit 0 of byte 1; M111 → bit 7 of byte 1;
     M101 (unaligned tag) reads head 96 and bit 5;
   - X10 (octal) reads head 0, bit 8; X20 reads head 16, bit 0;
   - one request for M0..M31; D and M in one cycle → two requests;
   - `UnexpectedFrames` empty in every test.
2. Implement; extend the fake with bit-device memory.

## Acceptance
- AC-04 (bit part): the tests pass; all suites stay green; the negative grep still prints nothing.

## Dependencies
- task-04-slmp-word-devices.md

## Verification Plan
- Command: `dotnet build IndustrialIot.sln -c Release && dotnet test tests/Edge.Tests && ! grep -rniE "0x14[0-9a-f]{2}|0x16[0-9a-f]{2}|0x10[0-9a-f]{2}" src/Edge/Drivers/Slmp`
- Named probe: the bit-device cases of `SlmpDriverTests`.
- Reachability: source — fake server; device codes, octal mapping and alignment are confirmed live in task-07.
- Oracle: build exit 0; `Failed: 0`, `Skipped: 0`; the grep prints nothing.
- Counterexample: decimal X numbering sends 10 for X10 and fails the octal cases; an unaligned head for M101 fails the
  head assertion; swapped bytes fail the M104/M111 cases.
- Artifacts: ephemeral.

## Failure Protocol
On a failed Step or Verification Plan run: stop; do not widen scope, change the Command, or weaken a test; record observed versus expected; repair only the cited cause; after three failed rounds, stop and ask the user.

## Receipt

Verification: PASS
Command: dotnet build IndustrialIot.sln -c Release && dotnet test tests/Edge.Tests && ! grep -rniE "0x14[0-9a-f]{2}|0x16[0-9a-f]{2}|0x10[0-9a-f]{2}" src/Edge/Drivers/Slmp
Exit: 0
Base: f985f23f58461cb5364cf96e0815c54fa444d6ec
Head: 8a222a802a5b488f1f483804bfade190d231994154a1fa1c4ec4cb6d98e90fc8
```text
$ dotnet build IndustrialIot.sln -c Release && dotnet test tests/Edge.Tests && ! grep -rniE "0x14[0-9a-f]{2}|0x16[0-9a-f]{2}|0x10[0-9a-f]{2}" src/Edge/Drivers/Slmp
Test run for C:\Users\case 046\Documents\Project\PlcProtocol\tests\Edge.Tests\bin\Debug\net10.0\Edge.Tests.dll (.NETCoreApp,Version=v10.0)
Passed!  - Failed:     0, Passed:   399, Skipped:     0, Total:   399, Duration: 28 s - Edge.Tests.dll (net10.0)
```
- Pre-change run passed (Edge 377): the unmet probes were the absent bit-device cases. After: Edge 399, build 0 warnings, negative grep empty.
- Named probes (SlmpDriverTests): parsing M100/m7679/X17->15/X20->16/Y0/Y1777, rejecting X8/Y19/X2000/M7680 and non-Bool on M/X/Y; golden frame for M96x1 (50 00 00 FF FF 03 00 0C 00 0A 00 01 04 00 00 60 00 00 90 01 00, UNVERIFIED); M96/M103/M104/M111/M101 decode the right bit of head 96; X10 -> head 0 bit 8, X20 -> head 16 bit 0; M0..M31 one request; D+M two requests; per-device end-code and split rules; UnexpectedFrames empty in every test, and the fake now flags any bit-device read not aligned to 16.
- Two task-04 InlineData rows (M0/Bool, X0/Bool asserted 'not yet supported') were removed because this task makes them valid; replaced by positive parsing tests (reviewer confirmed a legitimate scope change). SlmpBlockPlanner.cs added to Ownership.
- Review: code-auditor PASS (0 Critical/High/Medium). Open/UNVERIFIED until task-07: device codes M/X/Y, octal mapping, 16-device alignment, M/X/Y ranges; a bad word with several bit tags is re-read per tag (bounded, <=16).
