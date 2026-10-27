# Task 01 — Protocol-owned addresses; the catalog no longer depends on the S7 address type

Status: done

## Outcome
Tag addresses are parsed and type-checked by a protocol-owned syntax and exposed as `IPlcAddress`; the catalog, its
validator, the write guards and the command path work on that interface; every S7 behaviour and message is unchanged
(plan D-02, D-07).

## Scope
- In:
  - `IPlcAddress` { `Canonical`, `IsBit`, abstract `ReadOnlyReason` (string? — null means the area can be written) };
    the S7 `PlcAddress` implements it (`Input` → today's exact text "input area (I) tags cannot be writable" and reason
    `input_area_not_writable`).
  - `IPlcAddressSyntax.Parse(string address, PlcDataType type)` → `IPlcAddress`; `S7AddressSyntax` wraps
    `PlcAddressParser` and the S7 type rules of `PlcValueConverter.EnsureCompatible` (`src/Edge/Drivers/PlcValueConverter.cs:25`)
    with the same messages; `PlcValueConverter` keeps value conversion plus `EnsureCompatible(PlcAddress, PlcDataType)`
    as a thin S7 shim delegating to the syntax, so `PlcValueConverterTests.cs:53` is unchanged.
  - `PlcTagCatalog` takes the syntax (new constructor) and keeps `PlcTagCatalog(PlcTagCatalogOptions)` defaulting to the
    S7 syntax, so `PlcTagCatalogTests.cs:124,142` are unchanged; `PlcTagCatalogOptionsValidator` gets it too
    (`src/Edge/EdgeServiceCollectionExtensions.cs`); guards at `PlcTagCatalog.cs:81` and `:149` use `ReadOnlyReason`
    (S7 texts and reason codes byte-for-byte; a non-null reason maps to command reason `input_area_not_writable` for S7); `EnsureWritableTagsDoNotOverlap`/`Overlaps` (`:189-220`) stay
    S7-specific on `PlcAddress` and throw `InvalidOperationException` on any other address type.
  - `ResolvedTag.Address` is `IPlcAddress`; `CommandExecutor` (`:203`), `MonitorServer` (`:101`, `.Canonical`), the S7
    driver and test fakes adapt; S7 `WriteAsync`/`ReadAsync` accept `IPlcAddress` and throw `PlcAccessDeniedException`
    before any I/O when it is not an S7 `PlcAddress`.
  - The host still registers only S7 (protocol switch is task-03).
- Out: the batch read (task-02), protocol selection (task-03).

## Coverage
- CP-01

## Ownership
- Create: `src/Edge/Drivers/Models/IPlcAddress.cs` (with `IPlcAddressSyntax`), `src/Edge/Drivers/S7AddressSyntax.cs`
- Modify: `src/Edge/Drivers/Models/PlcAddress.cs`, `src/Edge/Drivers/PlcTagCatalog.cs`, `src/Edge/Drivers/PlcValueConverter.cs`,
  `src/Edge/Drivers/IPlcDriver.cs` (address type only), `src/Edge/Drivers/SiemensS7Driver.cs` (address type only),
  `src/Edge/Commands/CommandExecutor.cs`, `src/Edge/Monitoring/MonitorServer.cs` (`.Canonical` only),
  `src/Edge/EdgeServiceCollectionExtensions.cs`
- Modify (signatures only, no assertion change): `tests/Edge.Tests/Fakes/FakePlcDriver.cs`, `tests/Edge.Tests/*.cs`
  that construct the catalog or parse addresses, `tests/Deployment.Tests/EndToEndTelemetryTests.cs` (`SimulatedPlc`)
- Create: `tests/Edge.Tests/S7AddressSyntaxTests.cs`

## Steps
1. Record the baselines (Edge 304, Deployment 32 tests).
2. Interfaces and S7 implementations; move the S7 type rules into the syntax; switch the catalog, validator and guards.
3. Adapt callers and fakes; add tests: the S7 syntax returns today's messages — asserted as literal strings copied from
   HEAD (`src/Edge/Drivers/PlcValueConverter.cs:36` "cannot hold a … value", `src/Edge/Drivers/PlcTagCatalog.cs:151`
   "input area (I) tags cannot be writable") — for every rejected address/type pair, a mixed address type in the overlap check throws, a non-S7 address in S7 `WriteAsync` is denied with no I/O.

## Acceptance
- AC-01 (addresses and write guards): all pre-existing tests pass unchanged; new tests pass; the solution builds.

## Dependencies
- none

## Verification Plan
- Command: `dotnet build IndustrialIot.sln -c Release && dotnet test tests/Edge.Tests && dotnet test tests/Deployment.Tests --filter "FullyQualifiedName~EndToEndTelemetryTests" && ! git diff -U0 HEAD -- tests/Edge.Tests tests/Deployment.Tests | grep -E '^-[^-].*(Assert\.|InlineData|MemberData|\{ ")'`
- Named probe: existing `PlcAddressParserTests`, `PlcValueConverterTests`, `PlcTagCatalogTests`, `CommandExecutorTests`,
  `CommandFlowTests`, `SiemensS7DriverTests`, `EndToEndTelemetryTests`; new `S7AddressSyntaxTests`; the removed-assertion grep.
- Reachability: source — real host, real S7 driver against the in-process fake S7 server.
- Oracle: build exit 0; `Failed: 0`, `Skipped: 0`, Edge ≥ 304 and Deployment end-to-end tests passing; the grep prints nothing.
- Counterexample: a changed S7 message fails `PlcAddressParserTests` or the literal-string cases of `S7AddressSyntaxTests`; editing an assertion to make
  it pass shows a removed `Assert.` line; a forgotten `SimulatedPlc` breaks the solution build.
- Artifacts: ephemeral.

## Failure Protocol
On a failed Step or Verification Plan run: stop; do not widen scope, change the Command, or weaken a test; record observed versus expected; repair only the cited cause; after three failed rounds, stop and ask the user.

## Receipt

Verification: PASS
Command: dotnet build IndustrialIot.sln -c Release && dotnet test tests/Edge.Tests && dotnet test tests/Deployment.Tests --filter "FullyQualifiedName~EndToEndTelemetryTests" && ! git diff -U0 HEAD -- tests/Edge.Tests tests/Deployment.Tests | grep -E '^-[^-].*(Assert\.|InlineData|MemberData|\{ ")'
Exit: 0
Base: f985f23f58461cb5364cf96e0815c54fa444d6ec
Head: 4cd13cf61aeaacd85a8d72af188c4ce068ddecfecd688fa9a244efdec3d86065
```text
$ dotnet build IndustrialIot.sln -c Release && dotnet test tests/Edge.Tests && dotnet test tests/Deployment.Tests --filter "FullyQualifiedName~EndToEndTelemetryTests" && ! git diff -U0 HEAD -- tests/Edge.Tests tests/Deployment.Tests | grep -E '^-[^-].*(Assert\.|InlineData|MemberData|\{ ")'
Test run for C:\Users\case 046\Documents\Project\PlcProtocol\tests\Edge.Tests\bin\Debug\net10.0\Edge.Tests.dll (.NETCoreApp,Version=v10.0)
Passed!  - Failed:     0, Passed:   316, Skipped:     0, Total:   316, Duration: 28 s - Edge.Tests.dll (net10.0)
Test run for C:\Users\case 046\Documents\Project\PlcProtocol\tests\Deployment.Tests\bin\Debug\net10.0\Deployment.Tests.dll (.NETCoreApp,Version=v10.0)
Passed!  - Failed:     0, Passed:     2, Skipped:     0, Total:     2, Duration: 3 m 13 s - Deployment.Tests.dll (net10.0)
```
- Baselines before the change (same command, exit 0): Edge 304, Deployment end-to-end 2 — a refactor task, so the pre-change run passed; the unmet probe was the absent S7AddressSyntaxTests.
- After: Edge 316 (304 unchanged + 12 new S7AddressSyntaxTests cases), Deployment end-to-end 2, build 0 warnings; the removed-assertion/test-data grep printed nothing (no pre-existing test line changed).
- New tests: literal S7 messages copied from HEAD (cannot hold a … value; input area (I) tags cannot be writable), overlap check rejects non-S7 writable tags (lone, pair, mixed S7/foreign), SiemensS7Driver denies a non-S7 address on read and write with no connection (AcceptCount 0).
- Review: code-auditor PASS_WITH_WARNINGS (missing mixed-type test) -> repair round 1 -> PASS (0 Critical/High/Medium). Carried to task-03: every non-null ReadOnlyReason maps to input_area_not_writable.
