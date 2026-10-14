# Task 02 — Edge⇄Cloud message contracts serialize deterministically and expose freshness checks

Status: done

## Outcome
`src/Shared` defines `TelemetryPayload`, `DeviceCommand`, `CommandResult`, their enums, the shared reason-code constants, and one shared JSON configuration, so Edge (wave 2) and Cloud (wave 3) exchange identical wire formats proven by round-trip tests.

## Scope
- In: three contract records, enums, `CommandReasons` constants, `ContractJson` serializer options + `Serialize`/`Deserialize` helpers, validation, `DeviceCommand.CheckFreshness(TimeProvider)`.
- Out: MQTT topics, QoS, transport, persistence, deduplication logic and `edgeId` matching (wave 2/3); PLC types (task-03).

## Coverage
- CP-02

## Ownership
- Create: `src/Shared/Contracts/{TelemetryPayload.cs,DeviceCommand.cs,CommandResult.cs}`
- Create: `src/Shared/Common/{TelemetryQuality.cs,CommandOutcome.cs,CommandFreshness.cs,CommandReasons.cs,ContractJson.cs,ContractValidationException.cs}`
- Create: `tests/Shared.Tests/{ContractRoundTripTests.cs,DeviceCommandFreshnessTests.cs,CommandReasonsTests.cs}`
- Modify: `tests/Shared.Tests/Shared.Tests.csproj` (add `Microsoft.Extensions.TimeProvider.Testing`, version recorded in Receipt)
- Read: `src/Shared/IndustrialIot.Shared.csproj`

## Contracts (wire format, `schemaVersion = 1`)
**Versioning:** additive changes (new optional fields) keep `schemaVersion = 1`; it is bumped only for breaking changes. Receivers accept `1`, ignore unknown fields, and reject other versions.

**Timestamps:** ISO-8601 with mandatory `Z` suffix, ms precision. Missing offset (`2026-10-13T00:00:00`) or non-`Z` offset (`+07:00`) → `ContractValidationException`.

**Values:** `value` / `targetValue` are typed `JsonElement?` in C#; records override equality to compare `GetRawText()` so round-trips are equal. Only JSON `true`/`false`/number (and `null` where allowed) are valid kinds. Non-finite PLC values (NaN/±Inf) are never serialized: producer sends `value = null`, `quality = bad`.

**`TelemetryPayload`**
| JSON field | Type | Required | Rule |
|---|---|---|---|
| `schemaVersion` | int | yes | `1` |
| `edgeId` | string | yes | 1..64 chars `[A-Za-z0-9_-]` |
| `streamId` | string (GUID) | yes | generated once per Edge local store; changes after store loss/reinstall |
| `sequence` | int64 | yes | ≥ 0; monotonic within (`edgeId`, `streamId`); dedupe key = (`edgeId`, `streamId`, `sequence`) |
| `tagId` | string | yes | 1..64 chars `[A-Za-z0-9_.-]` |
| `value` | bool \| number \| null | yes | `null` iff `quality` ≠ `good` |
| `quality` | enum string | yes | `good` \| `bad` \| `uncertain` |
| `timestampUtc` | string | yes | timestamp rule above |

**`DeviceCommand`**
| JSON field | Type | Required | Rule |
|---|---|---|---|
| `schemaVersion` | int | yes | `1` |
| `commandId` | string (GUID) | yes | non-empty GUID |
| `edgeId` | string | yes | as above |
| `tagId` | string | yes | as above |
| `targetValue` | bool \| number | yes | not null |
| `issuedAtUtc` | string | yes | timestamp rule |
| `expiresAtUtc` | string | yes | > `issuedAtUtc`, ≤ `issuedAtUtc` + 24 h |
| `issuedBy` | string | yes | 1..128 chars (cloud user/service id, no secrets) |

`CheckFreshness(TimeProvider clock)` with `now = clock.GetUtcNow()` and fixed `MaxClockSkew = 2 min` returns, in this order:
1. `issuedAtUtc > now + MaxClockSkew` → `CommandFreshness.ClockSkew`
2. `now >= expiresAtUtc` → `CommandFreshness.Expired` (inclusive boundary)
3. otherwise `CommandFreshness.Fresh`

**`CommandResult`**
| JSON field | Type | Required | Rule |
|---|---|---|---|
| `schemaVersion` | int | yes | `1` |
| `commandId` | string (GUID) | yes | echoes command |
| `edgeId` | string | yes | |
| `outcome` | enum string | yes | `applied` \| `rejected` \| `expired` \| `duplicate` \| `failed` \| `unknown` |
| `reason` | string \| null | no | must match `^[a-z][a-z0-9_]{0,63}$` (else invalid); producers emit only `CommandReasons` constants; receivers tolerate unknown codes (forward compatibility); never exception text |
| `completedAtUtc` | string | yes | timestamp rule |

**`CommandReasons`** (public `const string`, single source for Edge and Cloud): `unknown_tag`, `tag_not_writable`, `input_area_not_writable`, `invalid_value`, `value_out_of_range`, `edge_mismatch`, `clock_skew`, `plc_unavailable`, `plc_busy`, `plc_access_denied`, `write_outcome_unknown`. `CommandReasons.All` lists them.

**`ContractJson`**: camelCase, `JsonStringEnumConverter` (camelCase), unknown fields ignored. Any `JsonException` (incl. missing `required` member) or rule violation is wrapped/raised as `ContractValidationException` naming the field when known.

## Steps
1. Add enums, `CommandReasons`, `ContractJson` with options and validation.
2. Add the three records with `required` members, `JsonElement?` values with raw-text equality, and `Validate()` called by `ContractJson.Deserialize<T>`.
3. Tests: round-trip equality per record; exact JSON field names via snapshot string; invalid cases (missing field, `schemaVersion: 2`, `expiresAtUtc ≤ issuedAtUtc`, `+07:00` offset, no offset, string `targetValue`, non-GUID `streamId`, `reason` = `"Tag Not Writable"` (not snake_case)) → `ContractValidationException`; unknown extra field and unknown snake_case `reason` accepted; freshness boundaries with `FakeTimeProvider`; `CommandReasons.All` equals exactly the eleven codes listed above, each unique snake_case.

## Acceptance
- AC-02: each record round-trips to an equal object, emits exactly the field names above, and every invalid case raises `ContractValidationException`.
- AC-03: `CheckFreshness` is `Fresh` at `expiresAtUtc − 1 ms`, `Expired` at `expiresAtUtc`, `ClockSkew` when `issuedAtUtc = now + 2 min + 1 ms`, and `Fresh` when `issuedAtUtc = now + 2 min`.

## Dependencies
- task-01-solution-and-address-parser.md

## Verification Plan
- Command: `dotnet test tests/Shared.Tests`
- Named probe: `ContractRoundTripTests.RoundTrip_Equal`, `ContractRoundTripTests.FieldNames_MatchSchema`, `ContractRoundTripTests.Invalid_ThrowsValidation`, `DeviceCommandFreshnessTests.Boundaries`, `CommandReasonsTests.All_ExactSetUniqueSnakeCase`
- Reachability: source — .NET 10 SDK.
- Oracle: exit 0, 0 failed, five named probes pass.
- Counterexample: PascalCase names, accepting a timestamp without `Z`, an exclusive expiry comparison, or a leaked `JsonException` must fail the named probes.
- Artifacts: ephemeral.

## Failure Protocol
On a failed Step or Verification Plan run: stop; do not widen scope, change the Command, or weaken a test; record observed versus expected; repair only the cited cause; after three failed rounds, stop and ask the user.

## Receipt

Verification: PASS
Command: dotnet test tests/Shared.Tests
Exit: 0
Base: bccfdfd166a07c585ae473e37fba1f71a20e6e81
Head: 71c866176e0becd223b8ad6b81245d4bc19b43de71d5bdace928c76362b002e4
```text
$ dotnet test tests/Shared.Tests
  Determining projects to restore...
  All projects are up-to-date for restore.
  IndustrialIot.Shared -> C:\Users\HOME\source\repos\PLCProtocol\src\Shared\bin\Debug\net10.0\IndustrialIot.Shared.dll
  Shared.Tests -> C:\Users\HOME\source\repos\PLCProtocol\tests\Shared.Tests\bin\Debug\net10.0\Shared.Tests.dll
Test run for C:\Users\HOME\source\repos\PLCProtocol\tests\Shared.Tests\bin\Debug\net10.0\Shared.Tests.dll (.NETCoreApp,Version=v10.0)
A total of 1 test files matched the specified pattern.

Passed!  - Failed:     0, Passed:    47, Skipped:     0, Total:    47, Duration: 489 ms - Shared.Tests.dll (net10.0)
```
- Pre-change run: exit 0 with `No test is available` (0 tests) → oracle (five named probes) not met, as expected.
- Named probes discovered via `--list-tests`: `RoundTrip_Equal`, `FieldNames_MatchSchema`, `Invalid_ThrowsValidation` (34 rows), `Boundaries`, `All_ExactSetUniqueSnakeCase`.
- Build: `dotnet build IndustrialIot.sln` → 0 warnings, 0 errors.
- Package: Microsoft.Extensions.TimeProvider.Testing 10.10.0.
- Repair history: R1 missing `using` (test); R2 `MakeReadOnly` needs resolver + FakeTimeProvider forward-only ordering; R3 review FAIL (lenient enum converter, Serialize leaked JsonException, sub-ms timestamps, 1e400, TZ-dependent test) → fixed; extra round authorized by user at re-review FAIL (lone-surrogate ArgumentException, `.Z`, issuedBy text rules, default(JsonElement)) → fixed. One added test row (lone surrogate inside an unknown field) was corrected to an accepted case to match the contract "unknown fields ignored".
- Final review: code-auditor PASS (0 Critical/High/Medium); remaining Low (doc wording on caller-argument exceptions) fixed before the final run.
- Known limitation: NaN→`value=null`/`quality=bad` is a producer rule exercised in wave 2; this task rejects non-finite numbers on both Serialize and Deserialize.
