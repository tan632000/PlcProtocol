# Task 03 — S7PlcClient talks S7comm with one serialized connection, bounded timeout, and auto-reconnect

Status: blocked

## Outcome
`S7PlcClient : IPlcClient` (NuGet `S7netplus`) reads/writes by `PlcAddress`, never runs two PLC operations at once, fails within the configured timeout with `PlcUnavailableException`, and opens a fresh connection on the next call after a failure.

## Scope
- In: options + validation, adapter, exception mapping, DI registration extension, tests against local fake TCP endpoints.
- Out: live S7-1200 handshake proof (no hardware — known limitation), connection pooling, multiple PLCs.

## Coverage
- CP-02

## Ownership
- Create: `src/PlcProtocol.S7/PlcProtocol.S7.csproj` (refs Core + `S7netplus` pinned to latest stable at implementation time, version recorded in Receipt)
- Create: `src/PlcProtocol.S7/{S7PlcOptions.cs,S7PlcClient.cs,S7ServiceCollectionExtensions.cs}`
- Create: `tests/PlcProtocol.S7.Tests/PlcProtocol.S7.Tests.csproj`, `tests/PlcProtocol.S7.Tests/S7PlcClientTests.cs`
- Modify: `PlcProtocol.sln` (add 2 projects)
- Read: `src/PlcProtocol.Core/IPlcClient.cs`, `src/PlcProtocol.Core/PlcValueConverter.cs`

## Contracts
- `S7PlcOptions`: `Host` (required, non-empty), `Port` = 102 (1..65535), `Rack` = 0 (0..7), `Slot` = 1 (0..31), `TimeoutMs` = 3000 (100..60000). Invalid → `OptionsValidationException` at first resolve (`ValidateOnStart`).
- CPU type fixed to `S7.Net.CpuType.S71200`.
- All public operations acquire one `SemaphoreSlim(1,1)`; lock wait counts toward the timeout.
- Each operation runs under a linked CTS: caller token + `CancelAfter(TimeoutMs)`. Caller cancellation → `OperationCanceledException` (not mapped). Internal timeout, `SocketException`, `IOException`, S7.Net connection errors → `PlcUnavailableException`, and the underlying `Plc` is closed and discarded so the next call reconnects.
- S7.Net read/write errors that indicate access refusal → `PlcAccessDeniedException` with a message hinting "enable PUT/GET and disable Optimized block access" `[UNVERIFIED: exact S7.Net error code — confirm against S7netplus source at implementation]`.
- I/O uses `ReadBytesAsync`/`WriteBytesAsync` with Core's converter; Bool read = read 1 byte and mask bit; Bool write = `WriteBitAsync`.
- Name clash: `S7.Net.PlcException` vs Core `PlcException` — use a `using S7Net = S7.Net;` alias.
- `IsReachableAsync` returns `false` instead of throwing on unavailability.
- Registered as singleton; implements `IAsyncDisposable` (closes connection).

## Steps
1. Create project, options, validation, DI extension.
2. Implement adapter per contracts.
3. Tests using in-process `TcpListener` fakes:
   - closed port (listener started then stopped) → `PlcUnavailableException`;
   - black-hole listener (accepts, never replies), `TimeoutMs=500` → throws within 1500 ms;
   - after a failure, second call produces a **new** accepted socket (accept count = 2);
   - 5 concurrent `ReadAsync` calls against black-hole → max simultaneously open accepted sockets = 1;
   - caller-cancelled token → `OperationCanceledException`;
   - invalid options (empty Host, Slot 32) fail validation.

## Acceptance
- AC-05: unreachable/black-hole PLC fails within `TimeoutMs + 1000` ms as `PlcUnavailableException`, and the next call reconnects.

## Dependencies
- task-02-values-client-contract-write-policy.md

## Verification Plan
- Command: `dotnet test tests/PlcProtocol.S7.Tests`
- Named probe: `S7PlcClientTests.ClosedPort_ThrowsUnavailable`, `S7PlcClientTests.BlackHole_TimesOutWithinBound`, `S7PlcClientTests.AfterFailure_Reconnects`, `S7PlcClientTests.ConcurrentCalls_AreSerialized`, `S7PlcClientTests.CallerCancel_PropagatesOce`, `S7PlcClientTests.InvalidOptions_FailValidation`
- Reachability: source — .NET 10 SDK + loopback TCP; no PLC needed.
- Oracle: exit 0, 0 failed, six named probes pass.
- Counterexample: removing the semaphore must fail `ConcurrentCalls_AreSerialized`; keeping a broken `Plc` instance must fail `AfterFailure_Reconnects`.
- Artifacts: ephemeral.

## Failure Protocol
On a failed Step or Verification Plan run: stop; do not widen scope, change the Command, or weaken a test; record observed versus expected; repair only the cited cause; after three failed rounds, stop and ask the user.

## Receipt
<!-- Fill only after execution; see canonical form in templates. -->
