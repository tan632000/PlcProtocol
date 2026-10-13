# Task 02 — Edge⇄Cloud message contracts serialize deterministically and expose freshness checks

Status: blocked

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
<!-- Fill only after execution; see canonical form in templates. -->
