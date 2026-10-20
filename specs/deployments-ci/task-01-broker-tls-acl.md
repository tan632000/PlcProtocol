# Task 01 — The broker admits only TLS clients with their own identity and enforces the topic ACL

Status: done

## Outcome
`deployments/mosquitto/` holds a TLS-only, credential-checked, identity-bound Mosquitto configuration with the D-02 ACL; `deployments/scripts/dev-secrets.sh` generates every dev secret safely (D-03); tests start the real broker with the script's output and prove AC-01..AC-03 and the secret-handling part of AC-05.

## Scope
- In: `mosquitto.conf`, `acl`, the secrets script (incl. `--add-edge`, id validation, certificate extensions, file ownership), `.gitignore` entry for `deployments/.secrets/`, a new `tests/Deployment.Tests` project in the solution.
- Out: compose and Keycloak realm (task-02), Edge service (task-03).

## Coverage
- CP-01, CP-02 (secret generation part)

## Ownership
- Create: `deployments/mosquitto/{mosquitto.conf,acl}`, `deployments/scripts/dev-secrets.sh`, `tests/Deployment.Tests/{Deployment.Tests.csproj,BrokerTests.cs}`
- Modify: `IndustrialIot.sln` (add the test project), `.gitignore` (`deployments/.secrets/`)
- Read: `src/Edge/Messaging/{MqttConnection.cs,MqttTopics.cs}`, `src/Cloud/Messaging/CloudMqttConnection.cs` (client settings to mirror), `tests/Edge.Tests/Fakes/TestMqttBroker.cs`, `.gitattributes`

## Contracts
- Script: `dev-secrets.sh [--out DIR] [--edge ID]... [--san DNS:name|IP:addr]...` or `dev-secrets.sh --add-edge ID [--out DIR]`. Defaults `--out deployments/.secrets`, `--edge line1-edge01`. Sets `MSYS_NO_PATHCONV=1`. Exit 2 when `DIR` already holds secrets (create mode) or lacks them (`--add-edge`), when an id fails `^[A-Za-z0-9_-]{1,64}$`, is duplicated or is `iiot-cloud`; non-zero naming a missing `openssl`/`docker`. Prints file names only, never secret values.
- Outputs: `ca.pem`, `ca.key` (never mounted anywhere), `mosquitto/{server.pem,server.key,passwd}`, `keycloak/{server.pem,server.key}`, `passwords/<name>.txt` for every broker identity (`iiot-cloud`, each Edge) and for the Keycloak users `viewer` and `operator`, `cloud.env` (`Cloud__Mqtt__Password`, `Cloud__Database__ConnectionString`, and the Cloud allow-list as `Cloud__Edges__{i}__EdgeId` for every Edge, `i` from 0; `--add-edge` appends the next index), `postgres.env`, `keycloak.env` (admin + `VIEWER_PASSWORD`, `OPERATOR_PASSWORD`). Key files and `passwd` mode `0600`; Mosquitto files owned by uid 1883 and Keycloak files by uid 1000 (chown through a pinned helper container) so the containers can read them on Linux.
- Certificates: CA 2 years, `basicConstraints=critical,CA:TRUE,pathlen:0`, `keyUsage=critical,keyCertSign,cRLSign`; leaves 1 year, `basicConstraints=critical,CA:FALSE`, `keyUsage=critical,digitalSignature,keyEncipherment`, `extendedKeyUsage=serverAuth`; SANs Mosquitto `DNS:mosquitto,DNS:localhost,IP:127.0.0.1` + `--san` values; Keycloak `DNS:keycloak,DNS:localhost` only.
- Broker config as D-01/D-02; certificate and password paths under `/mosquitto/config/secrets/`.
- Tests invoke the script through `IIOT_BASH` (default `%ProgramFiles%\Git\bin\bash.exe` on Windows, `bash` elsewhere) and bind-mount config and each secret file read-only, file by file, as compose does — the ownership set by the script is what lets the container read them on Linux (user decision 2026-10-19 during develop: resource mapping replaced, because the test process cannot read files owned by uids 1883/1000 to copy them); images pinned by digest.

## Steps
1. Config + ACL; script.
2. Test project: generate secrets into a temp directory, start Mosquitto with Testcontainers, connect MQTTnet clients configured like the Edge/Cloud (TLS with the dev CA, MQTT 5, persistent session).
3. Probes below.

## Acceptance
- AC-01: plain TCP to 8883 fails the TLS handshake; wrong password and anonymous → CONNACK refused; logging in as `line1-edge01` with client id `other` still gets only `line1-edge01`'s session and topics.
- AC-02: `line1-edge01` publishes its telemetry and results (delivered to `iiot-cloud`); its publishes to `iiot/line2-edge01/telemetry` and `iiot/line1-edge01/commands` are not delivered; its subscriptions to `#`, `iiot/+/commands`, `$share/g/iiot/+/commands` and `$SYS/#` receive nothing of `line2-edge01` or the broker; a will on `iiot/line2-edge01/telemetry` is not delivered when it disconnects abruptly; a retained message published by `line2-edge01` on its own telemetry topic before `line1-edge01` subscribes to `#` is not delivered to `line1-edge01`; `iiot-cloud` delivers commands and cannot publish `iiot/line1-edge01/telemetry`.
- AC-03: an unacknowledged QoS 1 command is redelivered after the Edge reconnects with `CleanStart=false`; a 17 KiB publish is refused or the connection closed, and nothing is delivered.
- AC-05 (secrets): `git check-ignore deployments/.secrets/x` succeeds; a second create run exits 2 without changing files; `--edge iiot-cloud`, `--edge 'a+b'` and a duplicate id exit 2; `--add-edge line2-edge01` adds a working broker identity and the line `Cloud__Edges__1__EdgeId=line2-edge01` to `cloud.env` without changing `ca.pem`; `openssl x509 -text` shows the CA/leaf extensions and SANs above; on Linux key files are `0600` and owned by the container uids.

## Dependencies
- none

## Verification Plan
- Command: `dotnet test IndustrialIot.sln`
- Named probe: `BrokerTests.TlsAndCredentialsRequired`, `BrokerTests.ClientIdBoundToUsername`, `BrokerTests.EdgeAcl_OwnTopicsOnly`, `BrokerTests.EdgeAcl_WildcardSharedSysAndWillDenied`, `BrokerTests.CloudAcl_CommandsOnly`, `BrokerTests.PersistentSession_RedeliversUnacked`, `BrokerTests.OversizedPacket_Refused`, `BrokerTests.Secrets_ValidatedNotOverwrittenAndIgnored`, `BrokerTests.Secrets_CertificateExtensions`, `BrokerTests.Secrets_AddEdge`, `BrokerTests.Secrets_FileModeAndOwner` (on Linux: `stat` of the script's output shows `0600` and owner uid 1883 for the Mosquitto files and 1000 for the Keycloak files; on Windows it asserts the files exist and are readable — a runtime branch like `TelemetryStoreTests.Pragmas_AndFileMode`, never a skip); every image the tests start is referenced with `@sha256:`
- Reachability: source — .NET 10 SDK, Docker engine, Git Bash with `openssl` (Windows) or `bash` + `openssl` (Linux, exercised by task-04's CI run).
- Oracle: exit 0, 0 failed and `Skipped: 0` across the solution; the eleven probes listed by `dotnet test tests/Deployment.Tests --list-tests` and passing.
- Counterexample: a 1883 listener, `allow_anonymous true`, removing `use_username_as_clientid`, a `topic readwrite iiot/#` or `pattern read iiot/+/commands` line, `persistence false` with a broker restart, no packet limit, a leaf without `CA:FALSE`, or accepting `--edge iiot-cloud` must fail the named probes (mutations on copies in a temp directory).
- Artifacts: ephemeral (temp secrets directory deleted after the tests).

## Failure Protocol
On a failed Step or Verification Plan run: stop; do not widen scope, change the Command, or weaken a test; record observed versus expected; repair only the cited cause; after three failed rounds, stop and ask the user.

## Receipt

Verification: PASS
Command: dotnet test IndustrialIot.sln
Exit: 0
Base: 8f23ff1bb1d7b0e5c78cd79ee7ff9fed2bbdcb72
Head: 4d5cb3860a51f1a6794bfb3bc37c68ea5128cd662e2661c97cd0a0325ab63b23
```text
$ dotnet test IndustrialIot.sln
Exit: 0
Passed!  - Failed:     0, Passed:    47, Skipped:     0, Total:    47, Duration: 352 ms - Shared.Tests.dll (net10.0)
Passed!  - Failed:     0, Passed:   279, Skipped:     0, Total:   279, Duration: 14 s - Edge.Tests.dll (net10.0)
Passed!  - Failed:     0, Passed:    11, Skipped:     0, Total:    11, Duration: 21 s - Deployment.Tests.dll (net10.0)
Passed!  - Failed:     0, Passed:    98, Skipped:     0, Total:    98, Duration: 1 m 38 s - Cloud.Tests.dll (net10.0)
```
- Named probes (`dotnet test tests/Deployment.Tests --list-tests`): `BrokerTests.TlsAndCredentialsRequired`, `ClientIdBoundToUsername`, `EdgeAcl_OwnTopicsOnly`, `EdgeAcl_WildcardSharedSysAndWillDenied`, `CloudAcl_CommandsOnly`, `PersistentSession_RedeliversUnacked`, `OversizedPacket_Refused`, `Secrets_ValidatedNotOverwrittenAndIgnored`, `Secrets_CertificateExtensions`, `Secrets_AddEdge`, `Secrets_FileModeAndOwner` (11/11, real `eclipse-mosquitto@sha256:21421af7…` via Testcontainers, secrets from the real script run with Git Bash); `Skipped: 0` in every assembly. Output above is the summary lines of the final run.
- Pre-change run: exit 0 with no `tests/Deployment.Tests` project → oracle (11 probes) not met, as expected.
- Defect found by the first run: `use_username_as_clientid` above `listener 8883` was ignored by the TLS listener and made Mosquitto open an implicit plain-text listener on 1883 (container log); moved under the listener; the TLS probe now asserts the log has no port 1883.
- Counterexamples (mutations in a scratch copy), each caught: 1883 listener, `allow_anonymous true` (static assertion; behaviourally equivalent because `use_username_as_clientid` already refuses clients without a username), client id not bound, `pattern readwrite iiot/#`, `pattern read iiot/+/commands`, Cloud allowed to write telemetry, `persistence false` + broker restart, no packet limit, leaf without `CA:FALSE`, `--san` on the Keycloak certificate, `iiot-cloud` accepted as an Edge id, overwrite allowed, non-empty `--out` allowed, relative `--out` not normalized, `--add-edge` twice allowed, `--add-edge` without the allow-list line. The Linux-only ownership branch of `Secrets_FileModeAndOwner` runs in task-04's CI.
- Repair rounds: implementation — script paths for native `openssl`/Docker (`cygpath -m`), cleanup of a half-written set, `--add-edge` on an incomplete set; `ClientIdBoundToUsername` strengthened (first connection uses the real id). R1 review FAIL — High: default relative `--out` became a Docker named-volume name; on Linux 0700 folders chowned to container uids locked the operator and four probes out; Mediums: resource mapping vs bind mounts (user decision: per-file read-only bind mounts, spec amended), `ca.key` mounted into the helper, `allow_anonymous` mutation unprobed, Ryuk image not pinned, no `--add-edge` rollback, destructive cleanup in a non-empty `--out`, will probe accepting any exception → all fixed. Final re-review: code-auditor PASS (0 Critical/High/Medium).
- Known limitations: `--add-edge` rollback after a late failure (after `passwd` was updated) may leave the allow-list line and, on Linux, a root-owned `passwd`; test cleanup mounts every subfolder (not `ca.key`) into a root container on Linux; `mosquitto_passwd` errors are hidden; an unreadable existing `--out` passes the emptiness check (then fails later); the Linux ownership branch is unproven until the CI run (task-04).
