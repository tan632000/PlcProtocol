# Task 07 — Test gaps left by tasks 02–06 are closed with probes

Status: done

## Outcome
Behaviour that tasks 02–06 implemented but proved only partly, or not at all, is covered by named probes; no production code changes unless a probe exposes a defect (then stop and ask).

## Scope
- In (user decision at GATE-DONE, 2026-10-19: "bổ sung đủ test trước"):
  - `publish_unknown` after a PUBACK timeout and after the connection drops mid-publish (task-05 limitation);
  - token expiry and query-token use over a WebSocket hub connection (task-06 limitation);
  - signing keys and issuer fetched from real OIDC metadata through the JwtBearer configuration manager over HTTPS (task-03 limitation);
  - command-result ingestion during a database outage: retried in place (task-05 reuses the task-02 helper, unproven there);
  - retention across more than one 10,000-row batch (task-02);
  - `LeaveEdge` stops pushes (task-06); history route 401/403 and a malformed `to` (task-04).
- Out: production changes; limitations that are design trade-offs rather than missing tests.

## Coverage
- CP-02, CP-03, CP-04, CP-05, CP-06 (proof only)

## Ownership
- Create: `tests/Cloud.Tests/TestGapTests.cs`
- Modify: `tests/Cloud.Tests/Infrastructure/CloudTestHost.cs` (opt out of static metadata for the OIDC probe)
- Read: everything under `src/Cloud/`, the existing test files
- Scope amendment (user decision 2026-10-19, after `PubackTimeout_PublishUnknown` exposed it): Modify `src/Cloud/Messaging/CloudMqttConnection.cs` — `PublishAsync` must stop waiting for the PUBACK after its own timeout and answer `Unconfirmed` (the MQTTnet communication timeout does not bound it: a PUBACK held 12 s was still awaited and reported `Confirmed`).

## Acceptance
- PUBACK held longer than the publish timeout → 503 `publish_unconfirmed`, status `publish_unknown`; broker drops the Cloud while a command is being published → `publish_unknown`.
- A WebSocket hub connection authenticates with the query token and is closed when its token expires.
- With only an HTTPS backchannel serving `/.well-known/openid-configuration` and JWKS: a token signed by the published key → 200; by another key → 401; wrong issuer → 401.
- Command result during a 2 s database outage (fake clock) → stored without a reconnect.
- 25,000 expired rows → all deleted by one run, newer rows kept.
- After `LeaveEdge` no telemetry push arrives; history without token → 401, without role → 403; malformed `to` → 400 naming `to`.

## Dependencies
- task-06-signalr-live.md

## Verification Plan
- Command: `dotnet test IndustrialIot.sln`
- Named probe: `TestGapTests.PubackTimeout_PublishUnknown`, `TestGapTests.DisconnectDuringPublish_PublishUnknown`, `TestGapTests.WebSocket_QueryToken_AndExpiry`, `TestGapTests.OidcMetadata_FetchedOverHttps`, `TestGapTests.ResultDuringDatabaseOutage_RetriedInPlace`, `TestGapTests.Retention_MultipleBatches`, `TestGapTests.LeaveEdge_StopsPushes`, `TestGapTests.History_AuthAndMalformedTo`
- Reachability: source — .NET 10 SDK + Docker engine + loopback TLS broker.
- Oracle: exit 0, 0 failed and `Skipped: 0` across the solution; the eight probes listed by `--list-tests` and passing.
- Counterexample: recording a PUBACK timeout or a mid-publish drop as `not_sent`, refusing the query token over WebSockets, keeping an expired WebSocket connection, skipping signature validation with fetched keys, a single retention batch only, ignoring `LeaveEdge`, or acknowledging a result before it is stored must fail the named probes.
- Artifacts: ephemeral.

## Failure Protocol
On a failed Step or Verification Plan run: stop; do not widen scope, change the Command, or weaken a test; record observed versus expected; a probe that exposes a production defect is reported to the user before any production change; after three failed rounds, stop and ask the user.

## Receipt

Verification: PASS
Command: dotnet test IndustrialIot.sln
Exit: 0
Base: 8f23ff1bb1d7b0e5c78cd79ee7ff9fed2bbdcb72
Head: 323d21e3ff5908098d9de2ebb9df24bae0c25e3276f1cd66b3432c0e56f883f5
```text
$ dotnet test IndustrialIot.sln
Exit: 0
Passed!  - Failed:     0, Passed:    47, Skipped:     0, Total:    47, Duration: 279 ms - Shared.Tests.dll (net10.0)
Passed!  - Failed:     0, Passed:   279, Skipped:     0, Total:   279, Duration: 14 s - Edge.Tests.dll (net10.0)
Passed!  - Failed:     0, Passed:    98, Skipped:     0, Total:    98, Duration: 1 m 27 s - Cloud.Tests.dll (net10.0)
```
- Named probes (`dotnet test tests/Cloud.Tests --list-tests`): `TestGapTests.PubackTimeout_PublishUnknown`, `DisconnectDuringPublish_PublishUnknown`, `WebSocket_QueryToken_AndExpiry`, `OidcMetadata_FetchedOverHttps`, `ResultDuringDatabaseOutage_RetriedInPlace`, `Retention_MultipleBatches`, `LeaveEdge_StopsPushes`, `History_AuthAndMalformedTo`; `Skipped: 0` in every assembly. Output above is the summary lines of the second of two consecutive green runs after the last change.
- Pre-change run: exit 0 with 0 `TestGapTests` listed → oracle not met, as expected.
- First run of the probes: 2 failures. `WebSocket_QueryToken_AndExpiry` — test defect (the .NET SignalR client sends a header, not `?access_token=`; the probe now builds the URL like a browser). `PubackTimeout_PublishUnknown` — production defect: a PUBACK held 12 s was awaited and reported `Confirmed` (MQTTnet's communication timeout does not bound QoS 1 publishes; confirmed against MQTTnet 5.2.0.1603 source by the reviewer). Reported to the user, who chose to fix it (scope amendment above): `PublishAsync` now cancels the wait after `OperationTimeout` → `Unconfirmed` → `publish_unknown`.
- Counterexamples (mutations in a scratch copy), each caught: publish timeout removed → `PubackTimeout_PublishUnknown`; unconfirmed recorded as `not_sent` → `DisconnectDuringPublish_PublishUnknown`; query token refused on the hub, `CloseOnAuthenticationExpiration` false → `WebSocket_QueryToken_AndExpiry`; signature validation bypassed, issuer validation off → `OidcMetadata_FetchedOverHttps`; single retention batch → `Retention_MultipleBatches`; `LeaveEdge` no-op → `LeaveEdge_StopsPushes`; command result acknowledged on a database failure → `ResultDuringDatabaseOutage_RetriedInPlace`.
- Review: code-auditor PASS on the first round (0 Critical/High/Medium); a late PUBACK after the timeout is only logged by MQTTnet, the packet identifier is not leaked.
- Known limitations: the timeout can cancel a write under TCP backpressure mid-packet, after which the broker drops the connection and the Cloud reconnects (outcome still `Unconfirmed`; before the fix the write hung); the outage probe's "first attempt" wait matches any database command; the WebSocket probe does not assert when or why the connection closed; `DisconnectDuringPublish_PublishUnknown` would also pass through the 10 s timeout path; the `SigningKey` XML doc in `CloudTestHost` still says "static metadata".
