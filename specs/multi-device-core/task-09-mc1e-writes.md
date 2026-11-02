# Task 09 — MC 1E write frames exist and pass the fake, and FX3U writes stay refused at start

Status: done

## Outcome
The MC 1E driver can write D (word) and M/Y (bit) frames as the manual describes, proven against the fake; an FX3U
device with `WritesEnabled=true` or a `Writable` tag fails the start with "FX3U writes are disabled until verified on a
real FX3U" (plan D-05); the MC 1E command set is exactly batch read and batch write.

## Scope
- In: MC 1E batch write in word and bit units (golden bytes from the manual pages recorded in task-07); write encoding
  for the FX3U types including String; the start guard in the model rules; X never writable; one write at a time per
  device, interleaving with reads.
- Out: lifting the guard (a later change that needs a live FX3U write receipt).

## Coverage
- CP-05

## Ownership
- Modify: `src/Edge/Drivers/Mc1e/MitsubishiMc1eDriver.cs`, `src/Edge/Drivers/Mc1e/Mc1eFrame.cs`, `src/Edge/Drivers/PlcModelCatalog.cs` (guard),
  `src/Edge/Drivers/Mc1e/Mc1eAddressSyntax.cs` (read-only reason narrowed to X only)
- Modify: `tests/Edge.Tests/Mc1eDriverTests.cs`, `tests/Edge.Tests/Fakes/FakeMc1eServer.cs`, `tests/Edge.Tests/PlcModelCatalogTests.cs`
- Modify (added by the user on 2026-11-02): `src/Edge/Configuration/EdgeChannelsOptions.cs` (the validator calls the model's
  write guard for each device and tag)
- Read: `src/Edge/Drivers/PlcProtocolOptions.cs`, `src/Edge/Drivers/PlcValueConverter.cs`

## Steps
1. Tests first: golden bytes for MC 1E word and bit writes from the manual; driver-level D100 ← 1234 then read back,
   M5 ← true, Y7 ← true against the fake; X10 write refused with no frame; an FX3U device with `WritesEnabled=true`
   fails the start with the guard text, and so does an FX3U `Writable` tag; the constants of `Mc1eCommands` found by
   reflection equal {batch read bit, batch read word, batch write bit, batch write word} and every command the fake
   received is in that set.
2. Implement the write frames and the guard.

## Acceptance
- AC-07 (write part): every case above holds.

## Dependencies
- task-07-fx3u-mc1e-read.md
- task-08-two-tier-writes.md

## Verification Plan
- Command: `dotnet build IndustrialIot.sln -c Release && dotnet test tests/Edge.Tests && dotnet test tests/Deployment.Tests`
- Named probe: `Mc1eDriverTests` (write frames, refusals, allow-list), `PlcModelCatalogTests` (guard).
- Reachability: source — the real driver over TCP to the fake; start validation as the host runs it.
- Oracle: build exit 0; `Failed: 0`, `Skipped: 0`.
- Counterexample: an FX3U configuration with writes enabled starts; a write frame differs from the manual's example; an X
  write sends a frame.
- Artifacts: ephemeral.

## Failure Protocol
On a failed Step or Verification Plan run: stop; do not widen scope, change the Command, or weaken a test; record observed versus expected; repair only the cited cause; after three failed rounds, stop and ask the user.

## Receipt

Verification: PASS
Command: dotnet build IndustrialIot.sln -c Release && dotnet test tests/Edge.Tests && dotnet test tests/Deployment.Tests
Exit: 0
Base: d8d498a1bdb6249b1b7d46c12d0092edd9cc3f65
Head: 790c747c0e231f1d0c2a294ac937b6afe7f2f5d5b27afb1feb613682303bf2b3
Manual evidence: FX3U-ENET-ADP user's manual JY997D45801 rev C, batch write in bit units (7.5.3, p.68: M50-M61) and in word units (7.5.6, p.76: D100-D102); `WriteFrames_AreTheManualsExamples` asserts both request frames byte for byte.
Changed existing assertions: `Mc1eDriverTests` "Writes_AreRefusedBeforeAnyIo" (asserted a D100 write is refused) became `WritesThatCannotFit_AreRefusedBeforeAnyIo` (wrong type on M5; DInt at D7999 built directly so the driver's own fit check runs), because D, M and Y are now writable by design; `DisposeAsync` now allows commands {00, 01, 02, 03} instead of reads only.
Scope change by the user (2026-11-02): `src/Edge/Configuration/EdgeChannelsOptions.cs` added to Ownership so the validator calls the model's write guard.
Process: tests were written first and the build was red for the missing write builders; discrimination was also shown by mutants that each failed tests (FX3U guard removed: 2; X writable: 2; bit nibbles low first: 2; write after send reported unavailable: 1; refused write drops the connection: 1; D0-D7999 fit check removed: 1).
Review: fresh-context code review, PASS after one repair round (the D7999 case reached the syntax refusal, not the driver's fit check).
Limitations: FX3U writes stay refused at start until verified on a real FX3U (task-10 live check); a write whose budget runs out just before the send is reported write_outcome_unknown (fails safe, as SLMP); `BatchWriteBits` does not bound head + points itself (the only caller writes one point that passed the syntax); the guard test calls the validator directly, not through the resolver the host uses; the fake adapter accepts bit writes to X (the driver refuses X before any I/O); a Y written while the PLC program runs is overwritten by its OUT instruction.
```text
$ dotnet build IndustrialIot.sln -c Release && dotnet test tests/Edge.Tests && dotnet test tests/Deployment.Tests
  Determining projects to restore...
  All projects are up-to-date for restore.
  IndustrialIot.Shared -> C:\Users\case 046\Documents\Project\PlcProtocol\src\Shared\bin\Release\net10.0\IndustrialIot.Shared.dll
  Shared.Tests -> C:\Users\case 046\Documents\Project\PlcProtocol\tests\Shared.Tests\bin\Release\net10.0\Shared.Tests.dll
  IndustrialIot.Cloud -> C:\Users\case 046\Documents\Project\PlcProtocol\src\Cloud\bin\Release\net10.0\IndustrialIot.Cloud.dll
  IndustrialIot.Edge -> C:\Users\case 046\Documents\Project\PlcProtocol\src\Edge\bin\Release\net10.0\IndustrialIot.Edge.dll
  Cloud.Tests -> C:\Users\case 046\Documents\Project\PlcProtocol\tests\Cloud.Tests\bin\Release\net10.0\Cloud.Tests.dll
  Edge.Tests -> C:\Users\case 046\Documents\Project\PlcProtocol\tests\Edge.Tests\bin\Release\net10.0\Edge.Tests.dll
  Deployment.Tests -> C:\Users\case 046\Documents\Project\PlcProtocol\tests\Deployment.Tests\bin\Release\net10.0\Deployment.Tests.dll

Build succeeded.
    0 Warning(s)
    0 Error(s)

Time Elapsed 00:00:01.69
  Determining projects to restore...
  All projects are up-to-date for restore.
  IndustrialIot.Shared -> C:\Users\case 046\Documents\Project\PlcProtocol\src\Shared\bin\Debug\net10.0\IndustrialIot.Shared.dll
  IndustrialIot.Edge -> C:\Users\case 046\Documents\Project\PlcProtocol\src\Edge\bin\Debug\net10.0\IndustrialIot.Edge.dll
  Edge.Tests -> C:\Users\case 046\Documents\Project\PlcProtocol\tests\Edge.Tests\bin\Debug\net10.0\Edge.Tests.dll
Test run for C:\Users\case 046\Documents\Project\PlcProtocol\tests\Edge.Tests\bin\Debug\net10.0\Edge.Tests.dll (.NETCoreApp,Version=v10.0)
A total of 1 test files matched the specified pattern.

Passed!  - Failed:     0, Passed:   765, Skipped:     0, Total:   765, Duration: 30 s - Edge.Tests.dll (net10.0)
  Determining projects to restore...
  All projects are up-to-date for restore.
  IndustrialIot.Shared -> C:\Users\case 046\Documents\Project\PlcProtocol\src\Shared\bin\Debug\net10.0\IndustrialIot.Shared.dll
  IndustrialIot.Edge -> C:\Users\case 046\Documents\Project\PlcProtocol\src\Edge\bin\Debug\net10.0\IndustrialIot.Edge.dll
  IndustrialIot.Cloud -> C:\Users\case 046\Documents\Project\PlcProtocol\src\Cloud\bin\Debug\net10.0\IndustrialIot.Cloud.dll
  Deployment.Tests -> C:\Users\case 046\Documents\Project\PlcProtocol\tests\Deployment.Tests\bin\Debug\net10.0\Deployment.Tests.dll
Test run for C:\Users\case 046\Documents\Project\PlcProtocol\tests\Deployment.Tests\bin\Debug\net10.0\Deployment.Tests.dll (.NETCoreApp,Version=v10.0)
A total of 1 test files matched the specified pattern.

Passed!  - Failed:     0, Passed:    37, Skipped:     0, Total:    37, Duration: 3 m 14 s - Deployment.Tests.dll (net10.0)
```
