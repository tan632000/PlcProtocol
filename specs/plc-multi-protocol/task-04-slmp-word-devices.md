# Task 04 — FX5U SLMP driver reads D registers in blocks, and can only ever read

Status: done

## Outcome
With `Edge:Plc:Protocol=Slmp`, the Edge reads D-register tags from an FX5U over SLMP 3E binary in block reads; no code
path can build any frame other than Device Read (plan D-03, D-05).

## Scope
- In:
  - `SlmpOptions` (`Host` required; `Port` required 1..65535 with no default and 102 rejected with "102 is the S7 port;
    use the SLMP port set in GX Works3"; `TimeoutMs` 1000..60000, default 3000) and validator.
  - `SlmpAddressSyntax` for `D<n>` (case-insensitive) within the FX5U default D range 0..7999 [UNVERIFIED — GX Works3
    CPU parameter "Device/Label Memory Area Setting"; settled in task-07] with Int/Word/DInt/DWord/Real; `Bool`/`Byte`
    on D rejected naming the tag; every SLMP address has `ReadOnlyReason` "SLMP is read-only", so a `Writable` tag
    fails the start with that text.
  - `MitsubishiSlmpDriver` ported from `vn0043-afchem-wastewater-treatment/gateway/AfChem.PlcGateway/Drivers/MitsubishiSlmpDriver.cs`:
    3E binary own-station header, one connection, one request at a time, monitoring timer, strict reply parsing;
    the frame builder is `BuildDeviceReadWords(SlmpDevice device, int head, int count)` with the closed enum
    `SlmpDevice` and command/subcommand as private constants (no parameter carries a command).
  - Batch read per plan D-03: blocks of touching/overlapping D tags only (no gap words), at most 960 words, a tag never
    split; values returned as short/ushort/int/uint/float (little-endian, low word first); an error end code →
    `PlcAccessDeniedException("SLMP end code 0x…. reading D…–D…" + a hint for 0xC056/0xC059/0xC05C)` for that block's
    tags; for an address/range end code (0xC056 [UNVERIFIED list]) the block is split once to find the bad tags, which are
    remembered — later cycles read the good tags merged into blocks and each bad tag alone, and a recovered tag rejoins its
    neighbours; other end codes (busy/timer) give the block's tags the block error with no fan-out (plan D-03 as amended
    by the user, option c); the driver does not log tag errors (the poller logs); a lost connection → `PlcUnavailableException` and no further
    blocks (production short-circuit); `WriteAsync` → `PlcAccessDeniedException` before I/O.
  - `FakeSlmpServer` ported for tests and extended (new work, not in the reference): device-range checking with an error
    end code, a short reply, an ASCII-looking reply, a request log, and `UnexpectedFrames` for any frame that is not
    0401/0000 with one of the four device codes.
  - The task-03 switch registers SLMP (replacing "not available yet").
- Out: bit devices (task-05), writes.

## Coverage
- CP-04

## Ownership
- Create: `src/Edge/Drivers/Slmp/SlmpOptions.cs`, `src/Edge/Drivers/Slmp/SlmpAddressSyntax.cs`,
  `src/Edge/Drivers/Slmp/MitsubishiSlmpDriver.cs`, `src/Edge/Drivers/Slmp/SlmpBlockPlanner.cs`
- Modify: `src/Edge/Drivers/DriverServiceCollectionExtensions.cs`
- Create: `tests/Edge.Tests/Fakes/FakeSlmpServer.cs`, `tests/Edge.Tests/SlmpDriverTests.cs`
- Modify: `tests/Edge.Tests/PlcProtocolSwitchTests.cs` (the placeholder case becomes "Slmp starts")
- Read: the reference driver, catalog and tests in `vn0043-afchem-wastewater-treatment/gateway`

## Steps
1. Tests first:
   - frame bytes for a D read (golden bytes from the iQ-F SLMP manual SH-081951 Device Read example
     [UNVERIFIED until checked against the manual]);
   - decoding: Int −1, DInt 123456 and −2, Real 12.5 across two words;
   - blocks: D0+D1 → one request; D0 and D2 → two requests; 961 words → two requests;
   - end code on one block → its tags carry the hex code and range; with 0xC056 the bad tag is isolated and the next
     cycles cost at most 3 requests for a 100-tag block with one bad tag (good tags merged, bad tag alone); a recovered tag
     rejoins; a busy/timer end code causes no fan-out;
   - stopped server → exactly one connection attempt per batch;
   - read-only and options: `Writable` rejected with "SLMP is read-only", `Bool` on D rejected, Port missing / 102
     rejected, host starts with `Protocol=Slmp`;
   - every test asserts `UnexpectedFrames` is empty;
   - host tests through `EdgeTestHost` override its defaults (`tests/Edge.Tests/Fakes/EdgeTestHost.cs:140-165`):
     `Edge:Plc:TimeoutMs` ≥ 1000, `Edge:Commands:Enabled=false`, SLMP tags instead of `DB1.DBW2`.
   Comments in `src/Edge/Drivers/Slmp` never spell write or remote-operation command codes (the grep below forbids them).
2. Port and adapt the driver to the contract; extend the fake.

## Acceptance
- AC-04 (D part): the tests pass; all suites stay green; no write or remote-operation command code exists in `src/Edge/Drivers/Slmp`.

## Dependencies
- task-03-protocol-switch-s7-cpu.md

## Verification Plan
- Command: `dotnet build IndustrialIot.sln -c Release && dotnet test tests/Edge.Tests && ! grep -rniE "0x14[0-9a-f]{2}|0x16[0-9a-f]{2}|0x10[0-9a-f]{2}" src/Edge/Drivers/Slmp`
- Named probe: `SlmpDriverTests` (frame, decoding, blocks, isolation, outage, read-only, options) and the `Slmp` case of
  `PlcProtocolSwitchTests`; the negative grep for write/remote-operation command codes.
- Reachability: source — the real driver over TCP to the in-process `FakeSlmpServer`; live is task-07.
- Oracle: build exit 0; `Failed: 0`, `Skipped: 0`; the grep prints nothing.
- Counterexample: big-endian word order fails DInt/Real; one request per tag shows extra requests; reading gap words
  shows one request for D0/D2; a frame builder with a command parameter, or any write code, fails the grep or
  `UnexpectedFrames`.
- Artifacts: ephemeral.

## Failure Protocol
On a failed Step or Verification Plan run: stop; do not widen scope, change the Command, or weaken a test; record observed versus expected; repair only the cited cause; after three failed rounds, stop and ask the user.

## Receipt

Verification: PASS
Command: dotnet build IndustrialIot.sln -c Release && dotnet test tests/Edge.Tests && ! grep -rniE "0x14[0-9a-f]{2}|0x16[0-9a-f]{2}|0x10[0-9a-f]{2}" src/Edge/Drivers/Slmp
Exit: 0
Base: f985f23f58461cb5364cf96e0815c54fa444d6ec
Head: 00ac7981f727d85e1bd1fe9a8299a724243a8d613e2f1923f1e7637d38716557
```text
$ dotnet build IndustrialIot.sln -c Release && dotnet test tests/Edge.Tests && ! grep -rniE "0x14[0-9a-f]{2}|0x16[0-9a-f]{2}|0x10[0-9a-f]{2}" src/Edge/Drivers/Slmp
Test run for C:\Users\case 046\Documents\Project\PlcProtocol\tests\Edge.Tests\bin\Debug\net10.0\Edge.Tests.dll (.NETCoreApp,Version=v10.0)
Passed!  - Failed:     0, Passed:   377, Skipped:     0, Total:   377, Duration: 28 s - Edge.Tests.dll (net10.0)
```
- Pre-change run passed vacuously (Edge 337; the grep failed on the missing src/Edge/Drivers/Slmp and was negated). After: Edge 377, build 0 warnings; the grep scans the 4 files there (a disposable copy with 0x1401 appended was caught, exit 1).
- Named probes (SlmpDriverTests, PlcProtocolSwitchTests): golden 21-byte frame for D100x2 (50 00 00 FF FF 03 00 0C 00 0A 00 01 04 00 00 64 00 00 A8 02 00, UNVERIFIED vs SH-081951); exact CLR types for Int -1, DInt 123456/-2, Real 12.5; blocks D0+D1 = 1 request, D0/D2 = 2, 961 words = 2; address end code 0xC056 in a 100-tag block: cycle 1 isolates D50, cycles 2-4 use 3 requests, recovery 1 request; other end code: 1 request, no fan-out; outage: one connection attempt; read-only, Bool on D, missing Port / 102 rejected; Slmp starts on the real startup path; UnexpectedFrames empty in every SLMP test.
- Plan D-03 amended by the user (option c, 2026-10-26) after review: bounded split for address end codes only; per-cycle poller warnings for Bad tags stay a documented limitation (pre-existing, S7 too).
- Review: code-auditor FAIL (D-03 drift, unbounded fan-out) -> user decision + repair round 1 -> PASS (0 Critical/High/Medium); Low 2 (UnexpectedFrames in Slmp_Starts) applied before this run. Open/UNVERIFIED until task-07: device codes, D range 0..7999, the address end-code list and hint texts, reply header echo (no fake mode); a flapping tag re-splits once per state change; _badTags assumes the single poller caller.
