# Task 03 — Connection test for an unsaved PLC

Status: done

## Outcome
`POST /api/settings/test-connection` tests either a saved PLC by name, as today, or an unsaved one described by
driver, model and endpoint (D-06). The answer, password, lockout and rate limits are unchanged. No test opens a
second connection to a PLC the Edge is reading.

## Scope
- In, the body `{password, proposed: {driver, model, host, port?, rack?, slot?, timeoutMs?}}`:
  - Port, rack and slot left out take the model's defaults.
  - The model must belong to the driver.
  - The port is checked by `PlcModelCatalog.CheckPort`.
- In, reusing a running driver:
  - Hosts are trimmed and compared case-insensitively.
  - An enabled device that matches every connection field answers through its running driver. The fields are driver,
    host and port, plus rack and slot for S7. This includes MC 1E.
  - The same host and port with any other field, the driver included, → 409 `endpoint-in-use`.
  - Otherwise a driver of that kind is made and disposed after the test.
- In, errors and removals:
  - Errors name the key under `proposed`, without the value.
  - The body limit is 1 MB (D-04).
  - The schema-1 `plc` body and the `several-plcs` 409 are removed.
- Out: any change to what the drivers report (`ok`/`unreachable` only).

## Coverage
- CP-03

## Ownership
- Modify: `src/Edge/Settings/PlcConnectionTester.cs`, `tests/Edge.Tests/PlcConnectionTesterTests.cs`
- Read: `src/Edge/Drivers/PlcDeviceRegistry.cs`, `src/Edge/Drivers/IPlcDriverFactory.cs`, `src/Edge/Drivers/PlcModelCatalog.cs`

## Steps
1. Replace the schema-1 tests with `proposed` tests. Keep the guard, password, busy and rate-limit tests. Run the
   Command; it fails.
2. Change the tester. The Command passes.

## Acceptance
- AC-05:
  - Results on a local TCP listener:
    - a proposed FX5U and a proposed FX3U → `ok`;
    - the same on a closed port → `unreachable`.
  - Reuse of a running driver:
    - a proposed device equal to a running S7, Slmp3E or MC 1E device, the host written with surrounding spaces and
      in another case → answered by that device's driver, with no second accept on the listener;
    - a proposed S7 at a running device's endpoint with another slot → 409 `endpoint-in-use`, no connection;
    - a proposed FX3U (MC 1E) at a running FX5U's (Slmp3E) host and port → 409 `endpoint-in-use`, no connection.
  - Password and limits:
    - wrong password → 401;
    - the 11th test in a minute → 429.
  - Bad input:
    - a model of the wrong driver → 400 naming `proposed:model`;
    - FX3U on port 102 → 400 naming `proposed:port`;
    - a body over 1 MB → 413.

## Dependencies
- task-01-settings-api-v2.md

## Verification Plan
- Command: `dotnet test tests/Edge.Tests --filter "FullyQualifiedName~PlcConnectionTesterTests" -- RunConfiguration.TreatNoTestsAsError=true`
- Named probe: `PlcConnectionTesterTests`.
- Reachability: source, through the real monitor route and local TCP listeners.
- Oracle: exit 0, failed 0, skipped 0.
- Counterexample: each of these must fail a test:
  - a tester that opens a second connection to a running PLC;
  - a tester that answers a changed slot with the running driver;
  - a tester that skips the password.
- Artifacts: none.

## Failure Protocol
On a failed Step or Verification Plan run: stop; do not widen scope, change the Command, or weaken a test; record observed versus expected; repair only the cited cause; after three failed rounds, stop and ask the user.

## Receipt

Verification: PASS
Command: dotnet test tests/Edge.Tests --filter "FullyQualifiedName~PlcConnectionTesterTests" -- RunConfiguration.TreatNoTestsAsError=true
Exit: 0
Base: e4f39e96710b817946ac2099e63e0a9bf7dbb80f
Head: 19e0a12cb47e6ea5bba936b94f07587ed854b42ec9c0a6f5d154027e7bd34599
Process: the PlcConnectionTesterTests were rewritten for the proposed body first; the Command then failed 21 of 25 (the guard, 1 MB and the two by-name tests passed, being unchanged behaviour). Discrimination shown by mutants, each failing tests: host compared case-sensitively, rack and slot ignored, rack alone ignored, driver ignored, the running driver never reused, the device checks skipped, JSON null not left out; sources restored and rebuilt after every mutant run. The settings API and supervisor tests also pass on this tree (62/62).
Review: fresh-context code review, PASS with 5 Lows. Repaired: the reuse test types the running host with spaces and in another case; the 400 cases use their own host and assert that neither it, the unknown driver nor the unknown key's value is echoed; a changed rack is refused like a changed slot; a JSON null is left out so the model's default applies (the page sends null for an empty field), with a test that failed before the fix.
Limitations: endpoints are compared as text, so localhost and 127.0.0.1 are different endpoints (the save validator has the same rule); the endpoint-in-use refusal is not counted by the 10-a-minute limiter (it opens no connection); a reused running driver answers with its own timeout.
```text
$ dotnet test tests/Edge.Tests --filter "FullyQualifiedName~PlcConnectionTesterTests" -- RunConfiguration.TreatNoTestsAsError=true
  Determining projects to restore...
  Restored C:\Users\case 046\Documents\Project\PlcProtocol\src\Shared\IndustrialIot.Shared.csproj (in 93 ms).
  Restored C:\Users\case 046\Documents\Project\PlcProtocol\src\Edge\IndustrialIot.Edge.csproj (in 268 ms).
  Restored C:\Users\case 046\Documents\Project\PlcProtocol\tests\Edge.Tests\Edge.Tests.csproj (in 280 ms).
  IndustrialIot.Shared -> C:\Users\case 046\Documents\Project\PlcProtocol\src\Shared\bin\Debug\net10.0\IndustrialIot.Shared.dll
  IndustrialIot.Edge -> C:\Users\case 046\Documents\Project\PlcProtocol\src\Edge\bin\Debug\net10.0\IndustrialIot.Edge.dll
  Edge.Tests -> C:\Users\case 046\Documents\Project\PlcProtocol\tests\Edge.Tests\bin\Debug\net10.0\Edge.Tests.dll
Test run for C:\Users\case 046\Documents\Project\PlcProtocol\tests\Edge.Tests\bin\Debug\net10.0\Edge.Tests.dll (.NETCoreApp,Version=v10.0)
A total of 1 test files matched the specified pattern.

Passed!  - Failed:     0, Passed:    26, Skipped:     0, Total:    26, Duration: 20 s - Edge.Tests.dll (net10.0)
```
