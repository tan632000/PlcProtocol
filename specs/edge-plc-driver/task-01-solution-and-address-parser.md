# Task 01 — Solution skeleton builds and S7 address strings parse deterministically

Status: blocked

## Outcome
`dotnet test` builds the `IndustrialIot` solution (Shared, Edge, two test projects) and proves `PlcAddressParser` accepts the documented grammar, emits a canonical form, and rejects everything else.

## Scope
- In: solution + build props + line-ending policy; empty `Shared` library; `Edge` worker project with minimal `Program.cs`; `PlcArea`, `PlcAddress`, `PlcAddressParser`, `PlcAddressException`; both test projects.
- Out: contracts (task-02), value conversion (task-03), PLC I/O (task-04), German mnemonics (`E`/`A`), `T`/`C` areas, peripheral `PI`/`PQ`.

## Coverage
- CP-01

## Ownership
- Create: `global.json` (SDK `10.0.100`, `rollForward: latestFeature`)
- Create: `Directory.Build.props` (`net10.0`, `Nullable` enable, `TreatWarningsAsErrors` true, `ImplicitUsings` enable, `RootNamespace` prefix `IndustrialIot`)
- Create: `.gitattributes` (`* text=auto eol=lf`, `*.ps1 text eol=crlf`)
- Create: `IndustrialIot.sln` (`dotnet new sln -n IndustrialIot --format sln`)
- Create: `src/Shared/IndustrialIot.Shared.csproj` (class library, no files yet besides csproj)
- Create: `src/Edge/IndustrialIot.Edge.csproj` (`Microsoft.NET.Sdk.Worker`), `src/Edge/Program.cs` (builds Generic Host, no hosted services yet)
- Create: `src/Edge/Drivers/PlcException.cs` (abstract base for all driver exceptions; subclasses added in task-03)
- Create: `src/Edge/Drivers/Models/{PlcArea.cs,PlcAddress.cs,PlcAddressParser.cs,PlcAddressException.cs}` (`PlcAddressException : PlcException`)
- Create: `tests/Shared.Tests/Shared.Tests.csproj` (xUnit, `ProjectReference` → `src/Shared`), `tests/Edge.Tests/Edge.Tests.csproj` (xUnit, `ProjectReference` → `src/Edge`), `tests/Edge.Tests/PlcAddressParserTests.cs`
- Modify: `.gitignore` (add `bin/`, `obj/`, `*.user`, `TestResults/`, `deployments/**/certs/`)

## Address grammar (contract)
Input is trimmed and case-insensitive. Numbers are ASCII decimal digits `[0-9]` only (no sign, no Unicode digits — .NET regex `\d` is NOT acceptable); values exceeding the range or `Int32` throw `PlcAddressException`, never `OverflowException`. `n` = DB number 1..65535, `b` = byte offset 0..65535, `x` = bit 0..7.

| Form | Area | Size | Example |
|---|---|---|---|
| `DB{n}.DBX{b}.{x}` | DataBlock | Bit | `DB1.DBX0.3` |
| `DB{n}.DBB{b}` / `DBW` / `DBD` | DataBlock | 1 / 2 / 4 bytes | `DB10.DBW2` |
| `{M\|I\|Q}{b}.{x}` | Memory / Input / Output | Bit | `M10.0`, `Q0.1` |
| `{M\|I\|Q}B{b}` / `W` / `D` | Memory / Input / Output | 1 / 2 / 4 bytes | `MW20`, `ID4` |

- `PlcAddress` is an immutable record: `Area`, `DbNumber` (0 for non-DB), `ByteOffset`, `BitOffset` (null unless Bit), `SizeBytes` (0 for Bit), `Canonical` (uppercase, no leading zeros, e.g. ` db01.dbw02 ` → `DB1.DBW2`).
- Any other input (empty, `DB0.DBW0`, `DB1.DBX0`, `M10`, `M0.8`, `DB1.DBW-1`, `DB1.DBW2x`, `DB70000.DBW0`, `DB٣.DBW0` (Arabic-Indic digit), `DB99999999999.DBW0`) throws `PlcAddressException` whose message contains the original input.
- Parser is a pure function: no I/O, no regex timeout risk (use `RegexOptions.CultureInvariant` + explicit timeout or hand-written parser).

## Steps
1. Repo is already a git repo (`fec73b1`); work on a feature branch.
2. Create root build files, solution, the four projects; `dotnet sln add` all → `dotnet build IndustrialIot.sln` exits 0.
3. Implement the four address files per contract above.
4. Write `PlcAddressParserTests` with `[Theory]` rows covering every table form, canonicalization, and every rejected example above.

## Acceptance
- AC-01: all valid examples parse to the expected fields; all invalid examples throw `PlcAddressException`.

## Dependencies
- none

## Verification Plan
- Command: `dotnet test tests/Edge.Tests --filter FullyQualifiedName~PlcAddressParserTests`
- Named probe: `PlcAddressParserTests.Parse_ValidForms_ReturnsExpectedFields`, `PlcAddressParserTests.Parse_InvalidForms_Throws`, `PlcAddressParserTests.Canonical_NormalizesCaseAndZeros`
- Reachability: source — requires .NET 10 SDK (prerequisite in plan.md).
- Oracle: exit 0, `Passed!` with ≥ 20 test cases, 0 failed.
- Counterexample: a parser accepting `M0.8` or `DB0.DBW0` must fail `Parse_InvalidForms_Throws`.
- Artifacts: ephemeral (`TestResults/`, git-ignored).

## Failure Protocol
On a failed Step or Verification Plan run: stop; do not widen scope, change the Command, or weaken a test; record observed versus expected; repair only the cited cause; after three failed rounds, stop and ask the user.

## Receipt
<!-- Fill only after execution; see canonical form in templates. -->
