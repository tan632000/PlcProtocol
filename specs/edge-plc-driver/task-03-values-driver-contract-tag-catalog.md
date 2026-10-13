# Task 03 — Typed values convert to S7 bytes, and the tag catalog decides writability without I/O

Status: blocked

## Outcome
Edge Drivers expose `PlcDataType`, a big-endian value converter with range validation, the `IPlcDriver` contract, the PLC exception hierarchy, `PlcTagDefinition`, and a fail-closed `PlcTagCatalog`, all proven by unit tests.

## Scope
- In: type/size compatibility, `JsonElement` → CLR normalization, CLR ↔ big-endian bytes, `IPlcDriver`, exceptions, tag definitions + catalog (overlap and bounds checks) + options.
- Out: network I/O (task-04), command dedupe/freshness/`edgeId` handling and config binding into the host (wave 2), `String`/`DTL`/`LReal`.

## Coverage
- CP-03, CP-04 (contract half)

## Ownership
- Create: `src/Edge/Drivers/{PlcDataType.cs,PlcValueConverter.cs,IPlcDriver.cs,PlcExceptions.cs,PlcTagCatalog.cs}`
- Create: `src/Edge/Drivers/Models/PlcTagDefinition.cs`
- Create: `tests/Edge.Tests/{PlcValueConverterTests.cs,PlcTagCatalogTests.cs}`
- Modify: `src/Edge/IndustrialIot.Edge.csproj` (add `ProjectReference` to `src/Shared`)
- Read: `src/Edge/Drivers/Models/PlcAddress*.cs`, `src/Shared/Common/CommandReasons.cs`

## Contracts
**Types and sizes**
| `PlcDataType` | Address size | CLR type | Accepted JSON | Range |
|---|---|---|---|---|
| Bool | Bit | `bool` | `true`/`false` | — |
| Byte | 1 | `byte` | integer token | 0..255 |
| Word | 2 | `ushort` | integer token | 0..65535 |
| Int | 2 | `short` | integer token | -32768..32767 |
| DWord | 4 | `uint` | integer token | 0..4294967295 |
| DInt | 4 | `int` | integer token | Int32 range |
| Real | 4 | `float` | number | finite IEEE754 single; NaN/±Inf rejected |

- `PlcValueConverter.EnsureCompatible(PlcAddress, PlcDataType)` throws `PlcValueException` on size mismatch (e.g. `DB1.DBW2` as `Real`); the catalog calls it at construction (wrapping into `InvalidOperationException`) and the driver (task-04) calls it before any I/O.
- `PlcValueConverter.Normalize(PlcDataType, JsonElement)` → CLR value. "Integer token" = JSON number whose raw text has no `.`, `e`, or `E`; so `1.0`, `1e2`, `1.5`, strings (`"12"`) and `null` are rejected for integer types.
- Wrong JSON kind or out-of-type-range → `PlcValueException`.
- Byte order is big-endian (S7 native); `Real` uses IEEE754 single. Example: `Int -2` ↔ `FF FE`; `Real 1.5` ↔ `3F C0 00 00`.

**`IPlcDriver`**
```csharp
Task<object> ReadAsync(PlcAddress address, PlcDataType type, CancellationToken ct);
Task WriteAsync(PlcAddress address, PlcDataType type, object value, CancellationToken ct); // ct honored only before I/O starts (task-04)
Task<bool> IsReachableAsync(CancellationToken ct);
```
**Exceptions** (all derive `PlcException`, created with `PlcAddressException` in task-01; this task adds the rest in `PlcExceptions.cs`): `PlcValueException`, `PlcUnavailableException` (safe to retry: nothing was sent), `PlcBusyException : PlcUnavailableException` (lock wait timed out), `PlcAccessDeniedException` (PLC explicitly refused), `PlcWriteOutcomeUnknownException` (write request may have reached the PLC; never auto-retry).

**`PlcTagDefinition`** (record): `TagId` (same rule as contract `tagId`), `Address` (string, parsed at load), `DataType`, `Writable` (default `false`), `Min` / `Max` (optional `decimal?`, numeric types only).

**`PlcTagCatalog`**
- `PlcTagCatalogOptions { PlcTagDefinition[] Tags = []; }` — bound from config section `Edge:Tags` in wave 2.
- All `TagId` lookups and duplicate checks use `StringComparer.OrdinalIgnoreCase`.
- Constructor throws `InvalidOperationException` (startup fails closed) on: invalid address; size/type mismatch; duplicate `TagId`; `Writable=true` on area `I`; `Min`/`Max` on `Bool`, `Min > Max`, or bounds outside the type range; **overlap** — a writable tag whose byte range (area + DB + `[ByteOffset, ByteOffset + max(SizeBytes,1))`) intersects any other tag's range, except when both are `Bool` with different bits.
- `TryGet(tagId, out ResolvedTag)` for reads.
- `EvaluateWrite(tagId, JsonElement targetValue)` returns `WriteDecision { Allowed, Reason, ResolvedTag?, NormalizedValue? }`, checking in order; reasons are `CommandReasons` constants:
  1. unknown tag → `unknown_tag`
  2. area `I` → `input_area_not_writable`
  3. `Writable=false` → `tag_not_writable`
  4. `Normalize` throws → `invalid_value`
  5. outside `Min`/`Max` (inclusive) → `value_out_of_range`
  6. else Allowed with the normalized CLR value.

## Steps
1. Add `PlcDataType` + converter; unit-test `EnsureCompatible` for every type/size pair, every range edge (min, max, min−1, max+1), integer-token rule (`1` ok, `1.0`/`1e2` rejected), and the byte examples above.
2. Add `IPlcDriver` and exceptions (no implementation here).
3. Add `PlcTagDefinition` + `PlcTagCatalog`; test empty catalog denies everything; each reason in order; case-insensitive lookup; invalid catalogs (incl. `DB1.DBW0` writable + `DB1.DBX0.3` read-only overlap, and `Min > Max`) throw; two `Bool` tags on different bits of the same byte are accepted.

## Acceptance
- AC-04: out-of-range / wrong-kind / non-integer-token / size-mismatch values throw `PlcValueException`.
- AC-05: `EvaluateWrite` returns `unknown_tag`, `input_area_not_writable`, `tag_not_writable`, `invalid_value`, `value_out_of_range` for the respective cases and `Allowed` only for a writable, in-bounds value; overlapping writable tags fail catalog construction.

## Dependencies
- task-01-solution-and-address-parser.md
- task-02-shared-contracts.md

## Verification Plan
- Command: `dotnet test tests/Edge.Tests --filter "FullyQualifiedName~PlcValueConverterTests|FullyQualifiedName~PlcTagCatalogTests"`
- Named probe: `PlcValueConverterTests.EnsureCompatible_SizeMismatch_Throws`, `PlcValueConverterTests.Normalize_OutOfRange_Throws`, `PlcValueConverterTests.Normalize_IntegerToken_Rule`, `PlcValueConverterTests.ToBytes_BigEndianExamples`, `PlcTagCatalogTests.Empty_DeniesAll`, `PlcTagCatalogTests.EvaluateWrite_ReasonCodes`, `PlcTagCatalogTests.InvalidCatalog_ThrowsAtConstruction`, `PlcTagCatalogTests.Overlap_WritableTag_Throws`
- Reachability: source — .NET 10 SDK.
- Oracle: exit 0, 0 failed, eight named probes pass.
- Counterexample: a converter that accepts `Int 32768` or `1.0`, writes little-endian bytes, a catalog that allows a `Writable=false` tag, or accepts the overlap example must fail the named probes.
- Artifacts: ephemeral.

## Failure Protocol
On a failed Step or Verification Plan run: stop; do not widen scope, change the Command, or weaken a test; record observed versus expected; repair only the cited cause; after three failed rounds, stop and ask the user.

## Receipt
<!-- Fill only after execution; see canonical form in templates. -->
