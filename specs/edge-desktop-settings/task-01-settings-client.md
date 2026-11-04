# Task 01 — The desktop Core reads, checks, saves and tests settings through the real API

Status: pending

## Outcome
`IndustrialIot.EdgeDesktop.Core` can load the Edge's settings into an editable draft, send the draft to `validate`,
save it with a password and a revision, and test one PLC's connection. Each reply of the API becomes a typed result,
proven against the real settings API.

## Scope
- In (D-01, D-04 client part, D-05 path mapping, D-06):
  - `SettingsModels.cs`:
    - the `GET /api/settings` reply (`revision`, `source`, `settings.channels`, `models`, `warnings`, `commandsEnabled`,
      `passwordSet`);
    - mutable draft nodes Channel → Device → Tag with every field the web page sends (`src/Edge/Monitoring/settings.html:309-319`).
  - `ConfigDraft.cs`:
    - built from a GET reply, keeping its revision;
    - deep copy, add / replace / remove by node, dirty and stale flags;
    - the list of channels without PLC (D-04 step 1);
    - the JSON body: top-level keys `revision`, `password`, `settings` exactly in lower case (the server compares them
      ordinally, `src/Edge/Settings/SettingsService.cs:35,197`), and settings keys as the web page sends;
    - mapping of an error path with the grammar `Edge:Channels:c[:Devices:d[:Tags[:t]]][:Field]` to a node and field;
      any other path is returned unmapped.
  - `SettingsClient.cs` on `http://127.0.0.1:{port}`, with `SocketsHttpHandler { UseProxy = false }` as `StatusClient`.
    - Timeouts: 30 s for GET, validate and PUT; `TimeoutMs` + 5 s for a test.
    - `GetAsync`.
    - `ValidateAsync(draft)` → errors.
    - `SaveAsync(draft, password)` → Accepted(applyId), or a refusal classified by the reply's `error` key:
      - WrongPassword;
      - PasswordNotSet;
      - Locked(retryAfterSeconds);
      - StaleRevision;
      - ApplyInProgress;
      - Invalid(errors);
      - TooLarge;
      - Failed(HTTP code or "không kết nối được").
    - `TestConnectionAsync(driver, device, password)` → the tester's reply (`src/Edge/Settings/PlcConnectionTester.cs:90-150`),
      or a refusal:
      - EndpointInUse, TestRunning, RateLimited;
      - Invalid with `proposed:{key}` paths;
      - the password refusals above.
    - An empty password is refused locally; no request is sent.
    - The client never throws for an HTTP or network failure, and never puts the password into a result, exception text
      or `ToString()`.
- Out: view models, WPF, CSV, Edge changes.

## Coverage
- CP-03, CP-04

## Ownership
- Create:
  - `src/EdgeDesktop.Core/SettingsModels.cs`
  - `src/EdgeDesktop.Core/ConfigDraft.cs`
  - `src/EdgeDesktop.Core/SettingsClient.cs`
  - `tests/Edge.Tests/Desktop/SettingsClientTests.cs`
  - `tests/Edge.Tests/Desktop/ConfigDraftTests.cs`
- Read:
  - `src/Edge/Settings/SettingsService.cs`
  - `src/Edge/Settings/PlcConnectionTester.cs`
  - `src/Edge/Settings/SettingsTree.cs`
  - `src/Edge/Settings/SettingsPassword.cs`
  - `src/Edge/Monitoring/settings.html`
  - `tests/Edge.Tests/SettingsApiTests.cs` (host with the supervisor and password)

## Steps
1. Write the tests.
   - `SettingsClientTests` starts the real Edge through `AddEdgeSupervisor` with a fake driver factory and a set
     settings password (as `SettingsApiTests.cs:545-571`), then checks:
     - GET read strictly;
     - validate errors with paths, including a channel-level path;
     - save Accepted, after which `/api/status` shows `settingsApply` applied with that applyId;
     - wrong password 401, no password file 403, lockout 429 `locked`, stale revision 409;
     - test-connection for a closed port, `endpoint-in-use` (a running PLC with another rack), `test-running` and
       `rate-limited` (429 without `retryAfterSeconds`);
     - the timeouts, with a scripted handler that never answers: GET/validate/PUT end as Failed at 30 s and a test at
       `TimeoutMs` + 5 s, measured with an injected `TimeProvider` or timeout;
     - an empty password sends nothing.
   - `ConfigDraftTests` checks:
     - GET → draft → JSON equal to the GET settings when keys are compared case-insensitively;
     - top-level keys exactly lower case;
     - dirty and stale tracking;
     - the path grammar, including `Edge:Channels:1:Name`, `…:Devices:0:Tags` and an unmapped path;
     - channels without PLC listed.
2. Run the Command; it must fail (types missing).
3. Implement; run the Command.

## Acceptance
- AC-04: the client returns `validate`'s errors with their paths. The draft maps each one to its node and field, or
  returns it unmapped.
- AC-05: every refusal listed in Scope comes back from the real API as its own result. A save is accepted and applied.
- AC-07:
  - after a save or a test, no result, exception text or draft field contains the password;
  - an empty password sends no request.
- AC-09: `TestConnectionAsync` returns the real tester's reply or refusal, with 429 `rate-limited` distinct from 429 `locked`.

## Dependencies
- none

## Verification Plan
- Command: `dotnet test tests/Edge.Tests/Edge.Tests.csproj --filter "FullyQualifiedName~IndustrialIot.Edge.Tests.Desktop" -- RunConfiguration.TreatNoTestsAsError=true`
- Named probe: `SettingsClientTests`, `ConfigDraftTests`.
- Reachability: source; the real `SettingsService` and `PlcConnectionTester` behind the supervisor host.
- Oracle: exit 0, failed 0, skipped 0, the new tests present.
- Counterexample: each of these must fail a test, shown with a mutant on a disposable copy:
  - the client sends `Origin: http://evil.example`;
  - the top-level key is written `Revision`;
  - 409 `stale-revision` is read as Failed;
  - 429 `rate-limited` is read as Locked;
  - the password is kept in the save result;
  - an empty password is sent;
  - a test uses the 30 s timeout instead of `TimeoutMs` + 5 s.
- Artifacts: the host's temp content root is deleted by the tests.

## Failure Protocol
On a failed Step or Verification Plan run: stop; do not widen scope, change the Command, or weaken a test; record observed versus expected; repair only the cited cause; after three failed rounds, stop and ask the user.

## Receipt
