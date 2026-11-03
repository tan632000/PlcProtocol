# Task 01 — The settings API reads and saves several PLCs

Status: done

## Outcome
`GET /api/settings`, `POST /api/settings/validate` and `PUT /api/settings` work on the schema-2 tree (D-01). Proposed
trees are judged alone and every error is pinned to its node (D-02). A save writes `SchemaVersion 2`, and the
supervisor runs every device of it. A schema-1 Edge saved unchanged keeps its TagIds. Limits and models follow D-04,
D-07 and D-08.

## Scope
- In, GET returns `{revision, source, settings: {Channels}, models, warnings, commandsEnabled, passwordSet}`:
  - The tree comes from the composed configuration, as D-01 says.
  - `models` follows D-07; `warnings` follows D-08.
  - `writableTags` is removed.
- In, validate and PUT:
  - Body `{revision, password, settings: {Channels}}`.
  - Checks run in this order:
    1. The guard, with a 4 MB body (D-04; `ReadGuardedAsync` takes the limit, default 1 MB).
    2. The D-02 steps 1–5.
    3. Revision.
    4. Password.
    5. Whether an apply is already running.
  - Error paths are full node keys with a fixed text per field, and never a submitted value.
  - The 409 `multi-device-file` branch and the schema-1 writable rule (`ApplyWritableRule`, `Unchanged`,
    `ChangedPlcKeys`) are removed. Write fields are saved as sent.
- In, `PlcDeviceRegistry`: the per-device driver-option and tag-catalog checks are exposed as a list of (device, TagId?,
  field, message), used by the API. `ThrowIfInvalid` keeps its current behaviour on top of it.
- In, `PlcTagCatalog`: its error also carries the field (`Address`, `DataType`, `Length`, `Min`, `Max`). Its rules
  and its exception text do not change.
- In: D-02 step 5 runs only when steps 1–4 found nothing.
- In: keep `SettingsService.PathOf` and `FixedMessage` working for the schema-1 paths that `PlcConnectionTester` still
  uses until task-03.
- In, `EdgeSettingsFile`: the ownership test for schema 2 is exposed (`IsOwnedBy(MultiDeviceSchemaVersion, key)`).
- Out: the write-change log (task-02), the connection test (task-03), CSV (task-04), the page (task-05).

## Coverage
- CP-01

## Ownership
- Modify: `src/Edge/Settings/SettingsService.cs`, `src/Edge/Drivers/PlcDeviceRegistry.cs`,
  `src/Edge/Drivers/PlcTagCatalog.cs` (the error field only),
  `src/Edge/Settings/EdgeSettingsFile.cs` (expose the schema-2 ownership test only), `tests/Edge.Tests/SettingsApiTests.cs`
- Create: `src/Edge/Settings/SettingsTree.cs` (tree ↔ keys, error path mapping)
- Read: `src/Edge/Settings/EdgeSupervisor.cs`, `src/Edge/Configuration/EdgeChannelsOptions.cs`,
  `src/Edge/Configuration/EdgeDeviceResolver.cs`, `src/Edge/Drivers/PlcModelCatalog.cs`,
  `tests/Edge.Tests/EdgeSupervisorTests.cs`

## Steps
1. Rewrite the `SettingsApiTests` that use schema-1 bodies so they use the tree.
   - Keep every guard, password, revision and lockout test.
   - Add the acceptance tests below.
   - For the apply tests, use `EdgeSupervisorOptions.ConfigureInner` with `services.Replace` for `IPlcDriverFactory`.
     It is registered with `TryAdd`; see `tests/Edge.Tests/EdgeSupervisorTests.cs` for the pattern.
   - Run the Command. It fails.
2. Change the code. The Command passes.

## Acceptance
- AC-01:
  - A schema-1 Edge: GET returns one device `Plc1` with `LegacyTagIds: true` and the old TagIds as tag names.
  - A schema-1 Edge with `CpuType: S71500`: GET also returns `warnings: ["cpuType-not-kept"]`.
  - A schema-2 file while appsettings holds a different `Edge:Channels`: GET returns the file's tree.
  - `models` lists S7-1200, FX5U and FX3U with their defaults, `portHint` and `writesAllowed` (false for FX3U).
- AC-02:
  - PUT of 2 channels × 2 devices (S7 and Slmp3E) → 202.
  - The file holds `SchemaVersion 2`.
  - After the supervisor rebuild, the registry has 4 devices (fake driver factory).
  - Schema-1 Edges (S7, and SLMP): GET, then PUT the same `settings` → the rebuilt Edge's tag catalog has exactly the
    old TagIds.
- AC-03:
  - Each case below is refused with 400, the file is unchanged, and the error path is the stated node:
    - duplicate device name → `…:Devices:d:Name`;
    - model of the wrong driver → `…:Devices:d:Model`;
    - bad S7 address at channel index 1, device index 0, tag index 5 (0-based) → `Edge:Channels:1:Devices:0:Tags:5:Address`;
    - FX3U `WritesEnabled` → `…:Devices:d:WritesEnabled`;
    - an empty `{}` tag in the middle of a list → that tag's own index;
    - 1001 tags on one device;
    - 5001 tags in total;
    - an unknown key.
  - Two errors on two devices at once: both are listed.
  - The Edge's appsettings holds a larger `Edge:Channels`, and the proposal is an FX5U without a Port → 400 on that
    device's `Port`. A leftover appsettings value is not inherited.
  - A body over 4 MB → 413.
  - No error text contains a submitted host or address.

## Dependencies
- none

## Verification Plan
- Command: `dotnet test tests/Edge.Tests --filter "FullyQualifiedName~SettingsApiTests|FullyQualifiedName~EdgeSupervisorTests|FullyQualifiedName~EdgeSettingsFileTests|FullyQualifiedName~PlcConnectionTesterTests|FullyQualifiedName~EdgeDeviceResolverTests|FullyQualifiedName~DeviceIsolationTests" -- RunConfiguration.TreatNoTestsAsError=true`
- Named probe: `SettingsApiTests` (the new tree tests plus the kept guard, password and revision tests). The other
  classes run as regression for the shared guard, the registry and the supervisor.
- Reachability: source. The tests start the real monitor routes and the supervisor with fake drivers.
- Oracle: exit 0, failed 0, skipped 0.
- Counterexample: each of these must fail a test:
  - a save that writes `SchemaVersion 1` or drops `LegacyTagIds`;
  - a GET that shows the appsettings tree over a saved file;
  - any error whose path is exactly `Edge:Channels`, asserted in every AC-03 case;
  - an address echoed in an error.
- Artifacts: ephemeral (temp settings files under the test's temp folder).

## Failure Protocol
On a failed Step or Verification Plan run: stop; do not widen scope, change the Command, or weaken a test; record observed versus expected; repair only the cited cause; after three failed rounds, stop and ask the user.

## Receipt

Verification: PASS
Command: dotnet test tests/Edge.Tests --filter "FullyQualifiedName~SettingsApiTests|FullyQualifiedName~EdgeSupervisorTests|FullyQualifiedName~EdgeSettingsFileTests|FullyQualifiedName~PlcConnectionTesterTests|FullyQualifiedName~EdgeDeviceResolverTests|FullyQualifiedName~DeviceIsolationTests" -- RunConfiguration.TreatNoTestsAsError=true
Exit: 0
Base: e4f39e96710b817946ac2099e63e0a9bf7dbb80f
Head: 1392b52ad37dfc0a189455177fd001749f5ff93c306dcb4a4e16a4c1c121a177
Process: the SettingsApiTests were rewritten for the tree first; the Command then failed 31 of 120 (every new tree test, plus the guard tests whose schema-1 body is now refused before the password). Discrimination shown by mutants, each failing tests: empty objects allowed, catalog errors pinned to the device only, no cpuType warning, LegacyTagIds dropped on GET, the per-device checks skipped, repeated keys in another case allowed; sources restored and rebuilt after every mutant run.
Review: fresh-context code review, PASS with 7 Lows. Repaired: the no-echo test now asserts the Address and Host paths it relies on; an object repeating a key in another case (e.g. "Channels" and "channels") is refused, since the binder merged both past the tag limits (two new cases, which failed before the fix).
Ownership notes: src/Edge/Configuration/EdgeDeviceResolver.cs gained ToDevice, extracted from FromChannels without a behaviour change, so the API checks proposed devices exactly as a start maps them (EdgeDeviceResolverTests in the Command). tests/Edge.Tests/PlcConnectionTesterTests.cs (task-03's file): the save in WrongPassword_Is401_WithoutConnecting_AndCountsTowardTheSaveLockout now sends the tree, since a schema-1 body is refused with 400 before the password; the test still proves the shared lockout.
Limitations: GET returns camelCase keys and nulls, so a saved file keeps them (an empty Description binds as "", which the inventory publishes as null); a channel without devices is not shown by GET and is dropped by the next save; every PlcException from address parsing is pinned to Address; the device checks cover disabled devices too, as a start does (D-02 says enabled; stricter, consistent with ThrowIfInvalid); the catalog reports only the first bad tag of a device.
```text
$ dotnet test tests/Edge.Tests --filter "FullyQualifiedName~SettingsApiTests|FullyQualifiedName~EdgeSupervisorTests|FullyQualifiedName~EdgeSettingsFileTests|FullyQualifiedName~PlcConnectionTesterTests|FullyQualifiedName~EdgeDeviceResolverTests|FullyQualifiedName~DeviceIsolationTests" -- RunConfiguration.TreatNoTestsAsError=true
  Determining projects to restore...
  Restored C:\Users\case 046\Documents\Project\PlcProtocol\src\Shared\IndustrialIot.Shared.csproj (in 87 ms).
  Restored C:\Users\case 046\Documents\Project\PlcProtocol\src\Edge\IndustrialIot.Edge.csproj (in 225 ms).
  Restored C:\Users\case 046\Documents\Project\PlcProtocol\tests\Edge.Tests\Edge.Tests.csproj (in 238 ms).
  IndustrialIot.Shared -> C:\Users\case 046\Documents\Project\PlcProtocol\src\Shared\bin\Debug\net10.0\IndustrialIot.Shared.dll
  IndustrialIot.Edge -> C:\Users\case 046\Documents\Project\PlcProtocol\src\Edge\bin\Debug\net10.0\IndustrialIot.Edge.dll
  Edge.Tests -> C:\Users\case 046\Documents\Project\PlcProtocol\tests\Edge.Tests\bin\Debug\net10.0\Edge.Tests.dll
Test run for C:\Users\case 046\Documents\Project\PlcProtocol\tests\Edge.Tests\bin\Debug\net10.0\Edge.Tests.dll (.NETCoreApp,Version=v10.0)
A total of 1 test files matched the specified pattern.

Passed!  - Failed:     0, Passed:   122, Skipped:     0, Total:   122, Duration: 17 s - Edge.Tests.dll (net10.0)
```
