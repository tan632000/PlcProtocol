# Task 01 — The `EdgeInventory` contract validates and round-trips bounded, network-free parts

Status: done

## Outcome
`IndustrialIot.Shared.Contracts.EdgeInventory` exists with the D-01 fields, serializes through `ContractJson` like the
other contracts, and `Validate()` refuses every out-of-bound part, so both the Edge and the Cloud share one rule set.

## Scope
- In: the record types (part, status entry, tag item), `Validate()` with the D-01 bounds, `state` and `lastError` as
  strict enums (wire values `disabled`/`waiting`/`connected`/`unavailable` and `unavailable`/`tagErrors`), round-trip and
  rejection tests.
- Out: packing into parts and description normalization (task-02); round-level consistency (task-04).

## Coverage
- CP-01

## Ownership
- Create: `src/Shared/Contracts/EdgeInventory.cs`, `tests/Shared.Tests/EdgeInventoryContractTests.cs`
- Modify: `src/Shared/Common/ContractJson.cs` (only to add shared rules to `ContractRules`)
- Read: `src/Shared/Contracts/TelemetryPayload.cs`, `src/Shared/IndustrialIot.Shared.csproj`, `src/Edge/Configuration/EdgeChannelsOptions.cs`

## Steps
1. Write `EdgeInventoryContractTests` first: a valid multi-part sample round-trips byte-for-byte through `ContractJson`;
   each bound below is rejected with the field name → red build.
2. Implement the records and `Validate()` → green.

## Acceptance
- AC-01: rejected: `schemaVersion` ≠ 1; bad `edgeId` (`ContractRules.EdgeId`); empty `bootId`; `round` < 0; `part` < 0
  or ≥ `partCount`; `partCount` outside 1..200; non-UTC `sentUtc`; a part with neither `devices` nor `tags`; a device,
  `tags.device` or `model` not matching `^[A-Za-z0-9_-]{1,32}$` (so `S7\u0000` and `10.0.0.5:102` fail); duplicate
  device names within one part (ordinal ignore case); an undefined `state` or `lastError`; `tagCount` < 0; a `tagId`
  breaking `ContractRules.TagId`; a tag `name` not matching `^[A-Za-z0-9_.-]{1,64}$`; a `dataType` not matching
  `^[A-Za-z][A-Za-z0-9]{0,15}$`; `length` outside 1..256; `min` > `max`; `description` over 256 chars or failing
  `ContractRules.PlainText`.
- The serialized JSON of a sample part has no property named `host`, `port`, `rack`, `slot`, `address` or `error`
  (asserted by walking the JSON tree); `lastError` `TagErrors` serializes as `"tagErrors"`.
- `SharedProject_ReferencesNoOtherProject`: `src/Shared/IndustrialIot.Shared.csproj` has no `ProjectReference`.

## Dependencies
- none

## Verification Plan
- Command: `dotnet test tests/Shared.Tests --filter "FullyQualifiedName~EdgeInventoryContractTests" -- RunConfiguration.TreatNoTestsAsError=true`
- Named probe: `EdgeInventoryContractTests` (round trip, one case per rejected bound, no-network-field walk, enum wire values, `SharedProject_ReferencesNoOtherProject`).
- Reachability: source — the Shared assembly referenced by Edge and Cloud.
- Oracle: all cases pass; a filter matching no test fails the command.
- Counterexample: a contract that accepts a 300-char description, a free-text `model`, or carries an `address` field fails a named case.
- Artifacts: ephemeral test output.

## Failure Protocol
On a failed Step or Verification Plan run: stop; do not widen scope, change the Command, or weaken a test; record observed versus expected; repair only the cited cause; after three failed rounds, stop and ask the user.

## Receipt

Verification: PASS
Command: dotnet test tests/Shared.Tests --filter "FullyQualifiedName~EdgeInventoryContractTests" -- RunConfiguration.TreatNoTestsAsError=true
Exit: 0
Base: 3e970eadd264a8548f3bdcabfab097ab95c8a6a0
Head: d45bd808ab520f3cc02097ddb66d3bd7df1d3c9ef60ac5818c755f5b037e13af
Process: the exact Command failed before any change (exit 1, no test matched with TreatNoTestsAsError); tests were written first and the build was red for the missing contract; discrimination shown by mutants that each failed tests (model unchecked: 2; duplicate device names allowed: 1; MaxParts 1000: 1; description PlainText removed: 4); source restored and rebuilt after the mutants. Full Shared.Tests 101/101; solution Release build clean.
Review: fresh-context code review, PASS (no Critical/High/Medium).
Limitations (review Lows, accepted): no deserialize-path test for JSON missing the nullable required members (lastGoodUtc, lastError, length, min, max, description) or carrying [null] list elements (behaviour correct by reading: C# `required` members are required in System.Text.Json and null elements are refused in Validate); description allows Unicode format characters such as U+202E (same PlainText rule as issuedBy); a part with "devices": [] and no tags is valid (an Edge with no PLC still reports); device and tag name errors both report field "name"; InventoryRules.Match repeats ContractRules.Match. For task-04: unknown JSON members are skipped, so the Cloud must store the re-serialized typed model, never the raw MQTT payload; record equality compares lists by reference.
```text
$ dotnet test tests/Shared.Tests --filter "FullyQualifiedName~EdgeInventoryContractTests" -- RunConfiguration.TreatNoTestsAsError=true
  Determining projects to restore...
  All projects are up-to-date for restore.
  IndustrialIot.Shared -> C:\Users\case 046\Documents\Project\PlcProtocol\src\Shared\bin\Debug\net10.0\IndustrialIot.Shared.dll
  Shared.Tests -> C:\Users\case 046\Documents\Project\PlcProtocol\tests\Shared.Tests\bin\Debug\net10.0\Shared.Tests.dll
Test run for C:\Users\case 046\Documents\Project\PlcProtocol\tests\Shared.Tests\bin\Debug\net10.0\Shared.Tests.dll (.NETCoreApp,Version=v10.0)
A total of 1 test files matched the specified pattern.

Passed!  - Failed:     0, Passed:    40, Skipped:     0, Total:    40, Duration: 116 ms - Shared.Tests.dll (net10.0)
```
