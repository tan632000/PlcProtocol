# Task 04 — SiemensS7Driver talks S7comm with one serialized connection, classified failures, and auto-reconnect

Status: done

## Outcome
`SiemensS7Driver : IPlcDriver` (NuGet `S7netplus`) reads/writes by `PlcAddress`, never runs two PLC operations at once, classifies every failure into a defined exception (distinguishing "nothing sent" from "write outcome unknown"), and opens a fresh connection after any connection-level failure. `docs/plc-setup.md` tells operators how to prepare the PLC and Edge host.

## Scope
- In: options + validation, driver, failure classification, DI registration extension, tests against local fake TCP endpoints including a canned-frame fake S7 server, PLC setup doc.
- Out: live S7-1200 handshake proof (no hardware — known limitation), connection pooling, multiple PLCs, polling and retry policy (wave 2).

## Coverage
- CP-04

## Ownership
- Create: `src/Edge/Drivers/{SiemensS7Options.cs,SiemensS7Driver.cs,DriverServiceCollectionExtensions.cs}`
- Modify: `src/Edge/IndustrialIot.Edge.csproj` (add `S7netplus` pinned to latest stable — 0.20.0 at review time — version recorded in Receipt; `InternalsVisibleTo Edge.Tests`)
- Create: `tests/Edge.Tests/SiemensS7DriverTests.cs`, `tests/Edge.Tests/Fakes/FakeS7Server.cs`
- Create: `docs/plc-setup.md`
- Read: `src/Edge/Drivers/IPlcDriver.cs`, `src/Edge/Drivers/PlcExceptions.cs`, `src/Edge/Drivers/PlcValueConverter.cs`

## Contracts
**Options** (`Edge:Plc`): `Host` (required, non-empty), `Port` = 102 (1..65535), `Rack` = 0 (0..7), `Slot` = 1 (0..15; narrowed from 0..31 by user decision 2026-10-14 because S7netplus `TsapPair.GetDefaultTsapPair` rejects slot > 15), `TimeoutMs` = 3000 (100..60000). Invalid → `OptionsValidationException` at host start (`ValidateOnStart`). Rack/Slot cast to `Int16` for `new Plc(CpuType.S71200, host, port, rack, slot)`. Use `using S7Net = S7.Net;` alias to avoid the `PlcException` name clash.

**Serialization and lock**
- One `SemaphoreSlim(1,1)`; lock wait uses the caller token and counts toward `TimeoutMs`.
- Lock wait times out → `PlcBusyException`; the connection is **not** touched. Only the lock holder may open, close, or discard the `Plc` instance.
- `internal int MaxObservedConcurrency` is incremented/decremented inside the lock (test oracle).

**Phases and failure classification** (evaluated in this order; token state decides, never exception type alone)
| Phase | Condition | Result | Connection |
|---|---|---|---|
| lock wait | caller token cancelled | `OperationCanceledException` | untouched |
| lock wait | `TimeoutMs` elapsed | `PlcBusyException` | untouched |
| connect (`OpenAsync`) | caller cancelled | `OperationCanceledException` | discarded |
| connect | timeout, `SocketException`, `IOException`, `InvalidDataException`, S7.Net TPKT/COTP/byte-count errors | `PlcUnavailableException` | discarded |
| read I/O | caller cancelled | `OperationCanceledException` | **discarded** (stale response may remain on socket) |
| read I/O | exception chain contains a PLC error response (`"Received error from PLC"` / `"Invalid response"`, e.g. header 0x8104 or item code 0x05) | `PlcAccessDeniedException` (message hints: enable PUT/GET, disable Optimized block access) | discarded |
| read I/O | any other failure incl. timeout | `PlcUnavailableException` | discarded |
| write I/O | caller token | **ignored** once write I/O starts; only internal timeout applies | — |
| write I/O | exception chain contains a PLC error response | `PlcAccessDeniedException` (PLC refused; not applied) | discarded |
| write I/O | any other failure incl. timeout after the request was handed to S7netplus | `PlcWriteOutcomeUnknownException` | discarded |

- `[UNVERIFIED]` exact S7netplus messages/inner types — implementer confirms against S7netplus source for the pinned version and records the matched strings in the Receipt.
- Every operation first calls `PlcValueConverter.EnsureCompatible` (no I/O on mismatch). I/O uses `ReadBytesAsync`/`WriteBytesAsync` with the converter; Bool read = read 1 byte and mask bit; Bool write = `WriteBitAsync`.
- `IsReachableAsync` returns `false` instead of throwing on unavailability/busy.
- Registered as singleton via `AddSiemensS7Driver(IConfiguration)`; implements `IAsyncDisposable` (acquires the lock, then closes).

**`FakeS7Server`** (test-only, loopback `TcpListener`) modes: `ClosedPort`, `BlackHole` (accept, never reply), `HandshakeThenSilent` (answers COTP CR→CC and S7 setup-communication with canned frames, then never replies), `HandshakeThenErrorHeader` (replies to read/write with header error class 0x81 code 0x04), `HandshakeThenItemAccessDenied` (item return code 0x05). Canned frames derived from S7netplus source/tests for the pinned version.

**`docs/plc-setup.md`** (≤ 800 lines): TIA Portal steps — enable "Permit access with PUT/GET", disable "Optimized block access" per DB, rack/slot values, TCP 102 only inside plant LAN (never Internet); Edge host must sync time via NTP (commands are rejected beyond 2 min skew — task-02); S7-1200 G2 server-side PUT/GET marked `[UNVERIFIED]` until tested on hardware.

## Steps
1. Add package, options, validation, DI extension.
2. Implement driver per the classification table.
3. Implement `FakeS7Server`, then tests:
   - `ClosedPort` → `PlcUnavailableException`; `BlackHole` with `TimeoutMs=500` → `PlcUnavailableException` within 1500 ms;
   - after a failure, the next call opens a **new** connection (fake accept count = 2);
   - 5 concurrent reads → `MaxObservedConcurrency == 1`;
   - lock holder hung on `BlackHole`, second caller with short timeout → `PlcBusyException`, and holder's socket still open at that moment;
   - caller cancels during read on `HandshakeThenSilent` → `OperationCanceledException`, next call uses a new socket;
   - write on `HandshakeThenSilent` → `PlcWriteOutcomeUnknownException`; caller cancel during that write does not change the outcome type;
   - read **and** write on `HandshakeThenErrorHeader` and `HandshakeThenItemAccessDenied` → `PlcAccessDeniedException` (never `PlcWriteOutcomeUnknownException`), and the next call opens a new socket;
   - size-mismatched (address, type) → `PlcValueException` with fake accept count 0 (checked via `EnsureCompatible` before I/O);
   - invalid options (empty Host, Slot 16) fail validation.
4. Write `docs/plc-setup.md`.

## Acceptance
- AC-06: unreachable/black-hole PLC fails within `TimeoutMs + 1000` ms as `PlcUnavailableException`, and the next call reconnects.
- AC-07: a write whose request was sent but not acknowledged raises `PlcWriteOutcomeUnknownException`, never `PlcUnavailableException`.
- AC-08: PLC error responses map to `PlcAccessDeniedException`; lock-wait timeout maps to `PlcBusyException` without closing the active connection.

## Dependencies
- task-03-values-driver-contract-tag-catalog.md

## Verification Plan
- Command: `dotnet test tests/Edge.Tests --filter FullyQualifiedName~SiemensS7DriverTests`
- Named probe: `SiemensS7DriverTests.ClosedPort_ThrowsUnavailable`, `SiemensS7DriverTests.BlackHole_TimesOutWithinBound`, `SiemensS7DriverTests.AfterFailure_Reconnects`, `SiemensS7DriverTests.ConcurrentCalls_AreSerialized`, `SiemensS7DriverTests.LockWaitTimeout_DoesNotDropConnection`, `SiemensS7DriverTests.CallerCancelMidRead_NextCallUsesNewSocket`, `SiemensS7DriverTests.WriteNoAck_OutcomeUnknown`, `SiemensS7DriverTests.PlcErrorResponses_AccessDenied_ReadAndWrite`, `SiemensS7DriverTests.SizeMismatch_NoIo`, `SiemensS7DriverTests.InvalidOptions_FailValidation`
- Reachability: source — .NET 10 SDK + loopback TCP; no PLC needed.
- Oracle: exit 0, 0 failed, ten named probes pass.
- Counterexample: removing the semaphore must fail `ConcurrentCalls_AreSerialized`; discarding the connection on lock-wait timeout must fail `LockWaitTimeout_DoesNotDropConnection`; mapping a silent write to `PlcUnavailableException` must fail `WriteNoAck_OutcomeUnknown`.
- Artifacts: ephemeral.

## Failure Protocol
On a failed Step or Verification Plan run: stop; do not widen scope, change the Command, or weaken a test; record observed versus expected; repair only the cited cause; after three failed rounds, stop and ask the user.

## Receipt

Verification: PASS
Command: dotnet test tests/Edge.Tests --filter FullyQualifiedName~SiemensS7DriverTests
Exit: 0
Base: 2aca9a078ee6d4438b266ef5abc83bca3af7ec9b
Head: 3a0b13911256631f8ebcc7bba3623c519c066a4127ea4814d902014d3a05644c
```text
$ dotnet test tests/Edge.Tests --filter FullyQualifiedName~SiemensS7DriverTests
  Determining projects to restore...
  All projects are up-to-date for restore.
  IndustrialIot.Shared -> C:\Users\HOME\source\repos\PLCProtocol\src\Shared\bin\Debug\net10.0\IndustrialIot.Shared.dll
  IndustrialIot.Edge -> C:\Users\HOME\source\repos\PLCProtocol\src\Edge\bin\Debug\net10.0\IndustrialIot.Edge.dll
  Edge.Tests -> C:\Users\HOME\source\repos\PLCProtocol\tests\Edge.Tests\bin\Debug\net10.0\Edge.Tests.dll
Test run for C:\Users\HOME\source\repos\PLCProtocol\tests\Edge.Tests\bin\Debug\net10.0\Edge.Tests.dll (.NETCoreApp,Version=v10.0)
A total of 1 test files matched the specified pattern.

Passed!  - Failed:     0, Passed:    21, Skipped:     0, Total:    21, Duration: 14 s - Edge.Tests.dll (net10.0)
```
- Pre-change run: exit 0 with `No test matches the given testcase filter` (0 tests) → oracle (ten named probes) not met, as expected.
- Status moved from `blocked` to `in_progress`: SDK blocker closed (`dotnet --list-sdks` → `10.0.401`).
- Package: S7netplus 0.20.0 (source checked at tag `v0.20.0`, commit f1ae0ea).
- Matched S7netplus strings/types: item return codes → `Exception("Received error from PLC: ...")` / `"Invalid response from PLC: statusCode=..."` (`PLC.ValidateResponseCode`); header-only error reply (no item code; S7netplus does not decode the header error class) → read: `S7.Net.PlcException` `ErrorCode.WrongNumberReceivedBytes` (`AssertReadResponse`); write: `S7.Net.PlcException(ErrorCode.WriteData)` wrapping `IndexOutOfRangeException` whose innermost S7netplus stack frame is `<WriteBytesWithASingleRequestAsync>`/`<WriteBitWithASingleRequestAsync>` (an IOORE from `COTP.TPDU` parsing stays OutcomeUnknown). `TargetSite` is unusable on .NET 10 (points to `ThrowHelpers.ThrowIndexOutOfRangeException`).
- Fake frames: COTP CC and setup-communication ack from S7netplus `ConnectionOpenTemplates.cs`; header error `32 03 .. 81 04` with no params/data; item code `0x05`. Extra test-only mode `HandshakeThenRespond` (happy path, concurrency) and `HandshakeThenTruncatedFrame`.
- Named probes: all ten present plus `WriteTruncatedReply_OutcomeUnknown`, `ReadWrite_HappyPath_DecodesBigEndianAndReusesConnection`, `ValidOptions_RegisterSingletonDriver`.
- Counterexamples (mutations in a scratch copy): no semaphore → `ConcurrentCalls_AreSerialized` + `LockWaitTimeout_DoesNotDropConnection` fail; discard on Busy → `LockWaitTimeout_DoesNotDropConnection` fails; silent write mapped to Unavailable → `WriteNoAck_OutcomeUnknown` + `WriteTruncatedReply_OutcomeUnknown` fail; broad IOORE check → `WriteTruncatedReply_OutcomeUnknown` fails.
- Full suite: `dotnet test IndustrialIot.sln` → Shared 47/47, Edge 221/221; build 0 warnings.
- Repair rounds: R1 test compile error (parameter `host` shadowed local); R2 test disposed container synchronously (driver is IAsyncDisposable-only); R3 review PASS_WITH_WARNINGS (Medium: broad IOORE check could label an unknown write "not applied"; Slot 16..31 passed validation but S7netplus rejects slot > 15 → user decided Slot 0..15) → rerun failed (TargetSite-based filter); extra round authorized by user → stack-frame filter.
- Review: code-auditor re-review PASS (0 Critical/High/Medium).
- Known limitations: no live S7-1200 / G2 hardware proof; real 0x8104 frame `[UNVERIFIED]`; detection tied to S7netplus 0.20.0 internals (re-check on upgrade); under NativeAOT/trimming stack frames may be unavailable → genuine write refusals degrade to OutcomeUnknown (safe); a hypothetical empty COTP DT payload after a write would be labelled AccessDenied; read with success code but short data is labelled AccessDenied; S7netplus leaves TcpClient undisposed after failed connect/peer reset (needs retry backoff in wave 2); lock contention shrinks the write budget (more OutcomeUnknown under load); no boundary test for Slot 15 / Rack 7.
