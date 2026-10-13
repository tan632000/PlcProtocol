# Task 02 — Typed values convert to S7 bytes, and write policy decides allow/deny without I/O

Status: blocked

## Outcome
Core exposes `PlcDataType`, a big-endian value converter with range validation, the `IPlcClient` contract, the PLC exception hierarchy, and a fail-closed `WritePolicy`, all proven by unit tests.

## Scope
- In: type/size compatibility, JSON → CLR normalization, CLR ↔ big-endian bytes, `IPlcClient`, exceptions, `WritePolicy` + options.
- Out: any network I/O, HTTP, configuration binding (task-04), `String`/`DTL`/`LReal`.

## Coverage
- CP-02 (contract half), CP-03

## Ownership
- Create: `src/PlcProtocol.Core/PlcDataType.cs`, `src/PlcProtocol.Core/PlcValueConverter.cs`, `src/PlcProtocol.Core/IPlcClient.cs`, `src/PlcProtocol.Core/PlcExceptions.cs`, `src/PlcProtocol.Core/Safety/{WritePolicy.cs,WritePolicyOptions.cs}`
- Create: `tests/PlcProtocol.Core.Tests/{PlcValueConverterTests.cs,WritePolicyTests.cs}`
- Read: `src/PlcProtocol.Core/Addressing/*`

## Contracts
**Types and sizes**
| `PlcDataType` | Address size | CLR type | Accepted JSON | Range |
|---|---|---|---|---|
| Bool | Bit | `bool` | `true`/`false` | — |
| Byte | 1 | `byte` | integer | 0..255 |
| Word | 2 | `ushort` | integer | 0..65535 |
| Int | 2 | `short` | integer | -32768..32767 |
| DWord | 4 | `uint` | integer | 0..4294967295 |
| DInt | 4 | `int` | integer | Int32 range |
| Real | 4 | `float` | number | finite IEEE754 single; NaN/±Inf rejected |

- Size mismatch (e.g. `DB1.DBW2` as `Real`) → `PlcValueException`. Integer types reject JSON with a fractional part (`1.5`) and strings (`"12"`).
- Byte order is big-endian (S7 native); `Real` uses IEEE754 single. Example: `Int -2` ↔ `FF FE`; `Real 1.5` ↔ `3F C0 00 00`.

**`IPlcClient`**
```csharp
Task<object> ReadAsync(PlcAddress address, PlcDataType type, CancellationToken ct);
Task WriteAsync(PlcAddress address, PlcDataType type, object value, CancellationToken ct);
Task<bool> IsReachableAsync(CancellationToken ct);
```
**Exceptions** (all derive `PlcException`): `PlcAddressException` (task-01), `PlcValueException`, `PlcUnavailableException` (connect/IO/timeout), `PlcAccessDeniedException` (PLC rejects access, e.g. PUT/GET disabled or optimized DB).

**`WritePolicy`**
- `WritePolicyOptions { bool ReadOnly = true; string[] WritableTags = []; }` — entry format `"<address>:<PlcDataType>"`, e.g. `"DB1.DBW2:Int"`.
- Constructor parses every entry with `PlcAddressParser` + `Enum.Parse(ignoreCase)`; any invalid entry, size mismatch, or area `I` entry → `InvalidOperationException` (startup fails closed).
- `Evaluate(PlcAddress, PlcDataType)` returns `WriteDecision { Allowed, Reason }` in this order: area `I` → `InputAreaNotWritable`; `ReadOnly` → `ReadOnlyMode`; (Canonical, type) not in set → `NotInAllowList`; else Allowed.

## Steps
1. Add `PlcDataType` + converter; unit-test every range edge (min, max, min−1, max+1) and the byte examples above.
2. Add `IPlcClient` and exceptions (no implementation here).
3. Add `WritePolicy`; test default options deny everything, allow-list exact match by canonical form (`db1.dbw02:int` matches `DB1.DBW2`), type mismatch denied, invalid config throws.

## Acceptance
- AC-02: out-of-range / wrong-shape / size-mismatch values throw `PlcValueException`.
- AC-03 (unit level): `WritePolicy` with defaults returns `ReadOnlyMode`; unlisted tag returns `NotInAllowList`.

## Dependencies
- task-01-solution-and-address-parser.md

## Verification Plan
- Command: `dotnet test tests/PlcProtocol.Core.Tests`
- Named probe: `PlcValueConverterTests.Normalize_OutOfRange_Throws`, `PlcValueConverterTests.ToBytes_BigEndianExamples`, `WritePolicyTests.Default_DeniesAll`, `WritePolicyTests.AllowList_MatchesCanonicalAndType`, `WritePolicyTests.InvalidEntry_ThrowsAtConstruction`
- Reachability: source — .NET 10 SDK.
- Oracle: exit 0, 0 failed, the five named probes appear in `--logger "console;verbosity=normal"` output.
- Counterexample: a converter that accepts `Int 32768` or writes little-endian bytes must fail the named probes.
- Artifacts: ephemeral.

## Failure Protocol
On a failed Step or Verification Plan run: stop; do not widen scope, change the Command, or weaken a test; record observed versus expected; repair only the cited cause; after three failed rounds, stop and ask the user.

## Receipt
<!-- Fill only after execution; see canonical form in templates. -->
