# Task 01 — Core library reads /api/status and drives the monitor view model

Status: done

## Outcome
`IndustrialIot.EdgeDesktop.Core` (net10.0, namespace `IndustrialIot.EdgeDesktop.Core`, public types) can:
- read the Edge's status;
- turn it into what the window shows;
- handle an unreachable Edge and a closed window;
- parse `--port`.

Tests prove all of this, plus the JSON contract against the real Edge.

## Scope
- In:
  - Status models matching `MonitorServer.cs:297-349`. The app reads them leniently, with web JSON defaults (D-02).
  - `StatusClient`:
    - base address `http://127.0.0.1:{port}/`;
    - sends `GET /api/status` only;
    - 2 s timeout;
    - returns a result (status, or a failure reason from D-03) and never throws to the UI. Cancellation is reported as
      cancelled, not as a failure.
  - `MonitorViewModel`, with the texts and badge kinds of `monitor.html:126-210`:
    - Header: the Edge id.
    - PLC card:
      - single PLC (`plc` present): the state word, or "Dữ liệu cũ" when
        `nowUtc - lastCycleUtc > 3 × pollIntervalMs + plc.timeoutMs`, using the server's `nowUtc`;
      - multi-PLC (`plc` null): "N PLC mất kết nối" or "Đọc tốt", and never stale (D-05).
    - Last cycle card, with the "giây trước" detail of each case (`monitor.html:178-184`).
    - MQTT and buffer cards.
    - Device rows and tag rows as in D-05. Bool values show as `TRUE`/`FALSE`.
    - The D-03 banner, and the time of the last update.
  - The poll loop (D-09):
    - waits 1 s after each poll finishes, so polls never overlap;
    - makes every view-model change through an injected "post to UI" callback;
    - keeps the last data on a failure;
    - clears the banner on the next success;
    - after cancellation, makes no change and shows no banner.
  - `LaunchOptions.Parse(args)` for `--port` (D-04).
  - The settings URL `http://localhost:{port}/settings`.
- Out: WPF and XAML (task-02); packaging (task-03).

## Coverage
- CP-01, CP-02 (source level)

## Ownership
- Create:
  - `src/EdgeDesktop.Core/IndustrialIot.EdgeDesktop.Core.csproj`
  - `src/EdgeDesktop.Core/StatusModels.cs`
  - `src/EdgeDesktop.Core/StatusClient.cs`
  - `src/EdgeDesktop.Core/MonitorViewModel.cs`
  - `src/EdgeDesktop.Core/LaunchOptions.cs`
  - `tests/Edge.Tests/Desktop/*.cs`
- Modify:
  - `tests/Edge.Tests/Edge.Tests.csproj` (reference Core)
  - `IndustrialIot.sln` (add Core; task-02 edits it after this task)
- Read: `src/Edge/Monitoring/MonitorServer.cs`, `src/Edge/Monitoring/monitor.html`, `tests/Edge.Tests/MonitorTests.cs`

## Steps
1. Write the tests in `tests/Edge.Tests/Desktop/`, in namespace `IndustrialIot.Edge.Tests.Desktop`. Where they also
   use the Edge records, use a `using` alias for the Edge types.
   - Contract:
     - get real JSON from `GET /api/status` of `EdgeTestHost` for a single-PLC Edge and for a multi-PLC Edge, as
       `MonitorTests` does;
     - read it strictly (D-02);
     - add a negative test: the same JSON with one property removed, and with one renamed, must fail the strict read.
   - Client: a fake `HttpMessageHandler` records method, host and path. Also cover a timeout, HTTP 500, a refused
     connection, unreadable JSON and cancellation.
   - View model: every state word and quality word, Bool format, single-PLC stale and not stale, multi-PLC with a
     very old cycle (no stale), banner reasons, failure then recovery, and that every change goes through the
     callback.
   - Launch options.
2. Run the Command. It must fail, because Core does not exist yet.
3. Implement Core and run the Command. Then run mutants on a disposable copy, each of which must fail a test:
   - stale `>` changed to `>=`;
   - stale applied to multi-PLC;
   - the client's clock used instead of `nowUtc`;
   - Bool formatted as `True`;
   - the banner kept after a success;
   - cancellation reported as a failure;
   - a change made without the callback.

## Acceptance
- AC-01 (source): every text above matches `monitor.html`, for single-PLC and multi-PLC Edges.
- AC-02 (source):
  - failure: the banner shows the D-03 text with the right reason, and the rows stay;
  - success: the banner clears;
  - over 2 s: counts as a failure;
  - cancel: no banner and no change.
- AC-03: the strict read of both real JSON documents passes. The removed-property and renamed-property variants fail.
- AC-04: no args → 5080; `--port 5081` → 5081; `--port 0`, `--port x`, `--port` with no value and an unknown
  argument are each refused.
- AC-05: the client's only request is `GET http://127.0.0.1:{port}/api/status`.

## Dependencies
- none

## Verification Plan
- Command: `dotnet test tests/Edge.Tests --filter "FullyQualifiedName~IndustrialIot.Edge.Tests.Desktop" -- RunConfiguration.TreatNoTestsAsError=true`
- Named probe: the test classes in `tests/Edge.Tests/Desktop/`.
- Reachability: source, on the dev machine and in Linux CI. Edge.Tests is net10.0.
- Oracle: exit 0, failed 0, skipped 0, at least one test for each of AC-01 to AC-05.
- Counterexample: each mutant in Step 3 must fail a test, and so must a Core model property renamed.
- Artifacts: none. Mutants run on a disposable copy.

## Failure Protocol
On a failed Step or Verification Plan run: stop; do not widen scope, change the Command, or weaken a test; record observed versus expected; repair only the cited cause; after three failed rounds, stop and ask the user.

## Receipt

Verification: PASS
Command: dotnet test tests/Edge.Tests --filter "FullyQualifiedName~IndustrialIot.Edge.Tests.Desktop" -- RunConfiguration.TreatNoTestsAsError=true
Exit: 0
Base: 11cb08526546d6bdaa5ed2ce295ae60736c4f6f3
Head: d0629fe18c085d0afb77653843c1c6857b7831c6396fcf2a060a6587005b5d6c
Process: the tests in tests/Edge.Tests/Desktop were written first; the Command then failed to compile (CS0234, namespace IndustrialIot.EdgeDesktop missing). An earlier pre-change run was blocked by a running IndustrialIot.Edge (PID 23980, not started by this session) locking src/Edge/bin; the user stopped it and the task resumed.
Contract (AC-03): real /api/status JSON from EdgeTestHost (single-PLC and multi-PLC) is read strictly (UnmappedMemberHandling.Disallow, RespectRequiredConstructorParameters, RespectNullableAnnotations); removing edgeId, lastCycleUtc, devices[0].lastGoodUtc, tags[0].timestampUtc, plc.timeoutMs or settingsApply.state, or renaming edgeId, bufferPending, devices[0].lastGoodUtc, tags[0].writable or mqtt.connected fails the read. The app itself reads leniently (an unknown property is accepted).
Mutants, each on a disposable copy of src/Shared, src/Edge, src/EdgeDesktop.Core and tests/Edge.Tests in the scratchpad (removed afterwards), all KILLED: stale > to >=; stale applied to multi-PLC; client clock instead of nowUtc; Bool as True/False; banner kept after a success; cancellation reported as failure; change made without the post callback; system proxy used (UseProxy=true); catch-all narrowed; Core model property renamed.
Review: fresh-context code review FAIL (High: the default HttpClient used the system proxy, so a manual proxy without the local bypass would send 127.0.0.1 to the proxy; Medium: a FakeTimeProvider race in the poll-loop test; Low: an unexpected exception could stop polling silently). Repaired: SocketsHttpHandler { UseProxy = false } with a test through the real handler against a real Edge while HttpClient.DefaultProxy is a never-bypassing dead proxy (non-parallel collection); the test advances the fake clock stepwise; a catch-all maps to "không kết nối được". Re-review PASS.
Limitations: LaunchOptions exposes TryParse(args, out options, out error) rather than the task's Parse(args); a Bool tag shows TRUE/FALSE only for JSON true/false (the Edge always sends booleans for Bool tags); "Cập nhật" uses the app's clock, as the web page does.
```text
$ dotnet test tests/Edge.Tests --filter "FullyQualifiedName~IndustrialIot.Edge.Tests.Desktop" -- RunConfiguration.TreatNoTestsAsError=true
  Determining projects to restore...
  All projects are up-to-date for restore.
  IndustrialIot.EdgeDesktop.Core -> C:\Users\case 046\Documents\Project\PlcProtocol\src\EdgeDesktop.Core\bin\Debug\net10.0\IndustrialIot.EdgeDesktop.Core.dll
  IndustrialIot.Shared -> C:\Users\case 046\Documents\Project\PlcProtocol\src\Shared\bin\Debug\net10.0\IndustrialIot.Shared.dll
  IndustrialIot.Edge -> C:\Users\case 046\Documents\Project\PlcProtocol\src\Edge\bin\Debug\net10.0\IndustrialIot.Edge.dll
  Edge.Tests -> C:\Users\case 046\Documents\Project\PlcProtocol\tests\Edge.Tests\bin\Debug\net10.0\Edge.Tests.dll
Test run for C:\Users\case 046\Documents\Project\PlcProtocol\tests\Edge.Tests\bin\Debug\net10.0\Edge.Tests.dll (.NETCoreApp,Version=v10.0)
A total of 1 test files matched the specified pattern.

Passed!  - Failed:     0, Passed:    53, Skipped:     0, Total:    53, Duration: 903 ms - Edge.Tests.dll (net10.0)
```
