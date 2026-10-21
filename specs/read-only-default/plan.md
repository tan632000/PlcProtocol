# Read-only by default: the web SCADA and the Cloud never write to the PLC
Specs-Contract: process-first-ready-v1

## Scope decision (GATE-SCOPE — 2026-10-20)
- Request: the Cloud and the future web SCADA only display data. Writing to the PLC is off by default and locked at three
  independent layers; the write code stays so it can be switched on for a customer who needs remote setpoints.
- Existing:
  - Edge always registers the command path: `CommandLog`, `CommandExecutor`, `CommandListener`
    (`src/Edge/EdgeServiceCollectionExtensions.cs:57-59`); the listener subscribes `iiot/{edgeId}/commands` in its
    `StartAsync` (`src/Edge/Commands/CommandListener.cs:28-33`). Per-tag `Writable` (all false in
    `src/Edge/appsettings.json`) is today the only lock on the Edge.
  - Cloud always registers `CommandService`, `CommandResultIngestor` (subscribes `iiot/+/command-results` in `StartAsync`,
    `src/Cloud/Commands/CommandResultIngestor.cs:31-33`), `CommandTimeoutSweeper` and `CommandEndpoints`
    (`src/Cloud/CloudServiceCollectionExtensions.cs:73-76`): POST `/api/v1/edges/{edgeId}/commands` (Operator), GET
    `/api/v1/commands/{commandId}` and GET `/api/v1/edges/{edgeId}/commands` (Viewer) (`src/Cloud/Api/CommandEndpoints.cs:44-46`).
    `CommandOptions` exists (`src/Cloud/CloudOptions.cs:33-41`). Nothing else in `src` resolves the command services.
  - Both MQTT clients keep a persistent 24 h session (`src/Edge/Messaging/MqttConnection.cs:195-196`,
    `src/Cloud/Messaging/CloudMqttConnection.cs:252-253`) and never unsubscribe; a message without a handler is logged
    and **not acknowledged** (`MqttConnection.cs:288-293`, `CloudMqttConnection.cs:362-366`), so it is redelivered on
    every reconnect.
  - Broker ACL grants `iiot-cloud` `topic write iiot/+/commands` and every Edge `pattern read iiot/%u/commands`
    (`deployments/mosquitto/acl`); compose mounts that one file (`deployments/compose.yaml:29`), as does `BrokerFixture`
    (`tests/Deployment.Tests/BrokerTests.cs:558`). `EdgeAcl_OwnTopicsOnly`, `PersistentSession_RedeliversUnacked` and
    `EdgeAcl_WildcardSharedSysAndWillDenied` rely on the Cloud publishing commands.
  - Smoke `SMOKE-04`/`SMOKE-05` expect 403/202 for viewer/operator command POSTs (`deployments/scripts/smoke-test.sh:157-171`);
    an unmapped route behind the fallback policy (authenticated user, `src/Cloud/Security/Policies.cs:25`) answers 404.
  - An Edge whose subscription is refused cannot connect at all (`src/Edge/Messaging/MqttConnection.cs:225-237`).
  - `docker compose config` renders volumes in long syntax with Windows paths (`source: C:\…\deployments\mosquitto\acl`),
    observed by a reviewer on this machine.
- Minimum change: one switch per layer, all off by default, read through options at run time; stale command
  subscriptions removed after every connect; test hosts switch commands on explicitly; docs say how to enable writes.
- Expansion signals: ~22 touched files across Edge, Cloud, broker and deploy files, but one outcome (no write reaches the
  PLC by default); five tasks with one outcome each.
- User decisions: **KEEP**; with Cloud commands off every command route returns **404** (not mapped); the read routes
  for command history are **off too** (the whole command module).

## Out of scope
- Showing the read-only/write mode on the PLC Monitor or exposing `commandsEnabled` through the API (EXPAND, not chosen).
- Removing the command code, the `commands`/`command_log` tables or their migration.
- Running `smoke-test.sh` on the dev machine: it needs host ports 8080/8443/8883 free and port 8080 is held by Windows
  `http.sys` here (limitation; the script change is checked statically).
- Changing the switches at run time: they are read at start; a change needs a restart.

## Decisions
| ID | Chosen | Rejected | Why | Load-bearing assumption | Break signal → response |
|---|---|---|---|---|---|
| D-01 | Three independent switches, all default off: `Edge:Commands:Enabled`, `Cloud:Commands:Enabled`, broker ACL variant. A write reaches the PLC only when all three are on **and** the tag is `Writable` | one central switch | each layer must hold on its own if another is misconfigured or compromised | — | — |
| D-02 | Edge off → the services stay registered but are inert: `CommandListener` registers no subscription and runs no replay/purge loop; nothing calls `CommandExecutor`; one Information log at start names the setting. `command_log` rows of an earlier write-mode run stay untouched (never replayed while off) | conditional DI registration | gating through `IOptions<CommandsOptions>` works the same in production and in every test host; one source of truth | the listener is the only entry to the executor | — |
| D-03 | Cloud off → `CommandEndpoints.Map` maps nothing (all three routes 404); `CommandResultIngestor` registers no subscription; `CommandTimeoutSweeper` does not run. Non-final rows of an earlier write-mode run stay as they are until commands are enabled again | conditional registration read from configuration at registration (may not see `WebApplicationFactory.UseSetting`, review R-05); 403 (user chose 404) | gating through `IOptions<CommandOptions>` at map/start time | `IEndpointModule.Map` runs after the host is built | — |
| D-04 | Default `deployments/mosquitto/acl` drops only the Cloud's `topic write iiot/+/commands`; it keeps the Edges' `pattern read iiot/%u/commands` and `pattern write iiot/%u/command-results`. `deployments/mosquitto/acl-commands` = today's ACL. Compose mounts `${IIOT_MQTT_ACL:-./mosquitto/acl}`; changing it needs `--force-recreate mosquitto` | removing the Edge read pattern too | a refused subscription disconnects the Edge entirely (`MqttConnection.cs:225-237`) | Mosquitto grants a readable filter even when nobody can write it | Edge log `Broker refused subscription(s)` → ACL variant mismatch |
| D-05 | With commands off each client **retires** its command filter (Edge `iiot/{id}/commands`, Cloud `iiot/+/command-results`): any message still delivered on it (queued in the persistent session or in flight before the change) is **acknowledged and discarded** without executing or storing it, and the filter is **unsubscribed** after every connect. So nothing left by an earlier write-mode run is redelivered forever or occupies in-flight slots shared with telemetry and acks | UNSUBSCRIBE alone (closure R-03: already-queued messages are still delivered, MQTT 5 §3.10.4 lets the server finish them); documenting a session reset as an operator step | upgrades from write mode must not need manual broker work | UNSUBSCRIBE of a filter the session does not hold is harmless | — |
| D-06 | Options bound with `ValidateOnStart`; a value that is not a boolean fails the host at start naming the key (the configuration binder's `InvalidOperationException` is acceptable — it names the key) | silent false | misconfiguration must be visible | — | — |
| D-07 | Test hosts (`EdgeTestHost`, `CloudTestHost`) set `Commands:Enabled=true` so the existing command tests keep modelling write mode; dedicated tests remove the key and prove the production default | flipping every existing test | the defaults are proven once, explicitly | — | — |

## Coverage profile
| ID | Outcome | Change kinds | Material surfaces | Ambiguity/action | Risk/evidence | Required proof |
|---|---|---|---|---|---|---|
| CP-01 | The Edge neither receives nor executes commands unless `Edge:Commands:Enabled=true` | modify | Runtime (options, start-up), Integration (MQTT subscription) | none | critical — last layer before the PLC | source |
| CP-02 | The Cloud exposes no command route and runs no command service unless `Cloud:Commands:Enabled=true` | modify | API (routes → 404), Runtime (options, hosted services) | none | critical — write entry point | source |
| CP-03 | A command subscription left in a persistent session by write mode is removed when commands are off | modify | Async/state (MQTT session), Integration | none | elevated — telemetry/ack starvation | source |
| CP-04 | The default broker ACL lets no identity write commands; `acl-commands` restores today's rights | modify, add | Filesystem/security (ACL) | none | critical — broker layer | installed (real Mosquitto) |
| CP-05 | Deployments use the read-only ACL by default and document how to enable, upgrade and diagnose write mode | modify | Runtime/deploy (compose, smoke script, docs) | none | elevated | static/source + installed e2e regression (smoke runtime not runnable here) |

## Acceptance criteria
| ID | EARS criterion | Proof |
|---|---|---|
| AC-01 | While `Edge:Commands:Enabled` is absent or false, the Edge shall not subscribe its commands topic, shall not execute or answer a command published to it, and shall log that remote writes are disabled; a non-boolean value shall fail the start naming the key. | task-01 command |
| AC-02 | Where `Edge:Commands:Enabled` is true, the Edge shall execute commands exactly as before. | task-01 command |
| AC-03 | While `Cloud:Commands:Enabled` is absent or false, the Cloud shall answer 404 on POST `/api/v1/edges/{edgeId}/commands`, GET `/api/v1/commands/{commandId}` and GET `/api/v1/edges/{edgeId}/commands` (Viewer and Operator), shall not subscribe `iiot/+/command-results`, and shall leave existing command rows unchanged when a result arrives. | task-02 command |
| AC-04 | Where `Cloud:Commands:Enabled` is true, the command API shall behave exactly as before. | task-02 command |
| AC-05 | When a client with commands off connects with a persistent session that still holds the command subscription or queued command traffic, it shall acknowledge and discard what is delivered on that filter without executing or storing it, unsubscribe the filter, and keep telemetry/acks flowing; nothing on that filter is delivered again after a reconnect. | task-03 command |
| AC-06 | With the default ACL, a command published by `iiot-cloud` shall not reach any Edge, while an Edge's subscription to its commands topic is granted and its telemetry and acks still flow. | task-04 command |
| AC-07 | With `acl-commands`, the Cloud shall send commands as today (existing command-dependent broker tests pass on it). | task-04 command |
| AC-08 | Compose shall mount `${IIOT_MQTT_ACL:-./mosquitto/acl}`; `SMOKE-04` and `SMOKE-05` shall expect 404; `docs/deployment.md` and `docs/setup-checklist.md` shall each name all four write switches, and `docs/plc-setup.md` shall no longer tell the reader to test a Cloud write without them. | task-05 command |

## Tasks
| # | Task | Priority | Criteria | Primary ownership | Dependencies | Status |
|---|---|---|---|---|---|---|
| 01 | Edge commands switch, default off | P1 | AC-01, AC-02 | `src/Edge/Commands/CommandListener.cs` | - | done |
| 02 | Cloud commands switch, default off | P1 | AC-03, AC-04 | `src/Cloud/Api/CommandEndpoints.cs`, `src/Cloud/Commands/` | - | done |
| 03 | Remove stale command subscriptions when off | P1 | AC-05 | `src/Edge/Messaging/MqttConnection.cs`, `src/Cloud/Messaging/CloudMqttConnection.cs` | task-01-edge-commands-switch.md, task-02-cloud-commands-switch.md | done |
| 04 | Read-only default ACL and `acl-commands` variant | P1 | AC-06, AC-07 | `deployments/mosquitto/acl`, `tests/Deployment.Tests/BrokerTests.cs` | - | done |
| 05 | Compose ACL selection, smoke checks and docs | P2 | AC-08 | `deployments/compose.yaml`, `docs/deployment.md` | task-01-edge-commands-switch.md, task-02-cloud-commands-switch.md, task-03-retire-stale-subscriptions.md, task-04-read-only-acl.md | done |

## Review log
- Round 1 (3 fresh reviewers: fact/contract, security/failure-mode, scope/proof; 24 raw findings → 14): user accepted
  R-01…R-13 (R-14 = confirmations: citations correct, no PLC write path with all defaults). Applied: three ACL tests on
  `acl-commands` (R-01); SMOKE-04 → 404 (R-02); new task-03 unsubscribe + D-05 (R-03); task-05 verification via
  `source:` regex and per-switch greps on both docs (R-04); options-based gating, D-02/D-03/D-06 (R-05); real oracles in
  task-01/02/04 (R-06, R-07, R-08); upgrade, mismatch table and residual-state docs (R-09, R-10, R-11); full Edge/Cloud
  suites, e2e and `plc-setup.md` (R-12); fixture mechanism and task-05 dependencies (R-13).
- Closure 1 (fresh reviewer): 11/13 PASS; R-03 FAIL (queued messages still delivered and left unacked after UNSUBSCRIBE)
  and R-09 FAIL (a registry `Environment` value does not survive a reinstall: uninstall deletes the service key). New:
  N-01 (= R-09 detail), N-02 (= R-03 detail), N-03 Cloud restart test infeasible as written (one broker per
  `CloudTestHost`), N-04 `BrokerFixture` is sealed with a private constructor, N-05 weak task-05 greps (`Writable` already
  present; `SMOKE-04.*404` can match the comment line; no `plc-setup.md` check), N-06 binder exception wording. Round 2
  repairs: D-05 ack-and-discard + unsubscribe; task-03 queued-before-start test and Cloud session seeding; task-05 docs
  text for reinstall; task-04 fixture note; task-05 greps on the check lines, the new section heading and `plc-setup.md`;
  D-06/task-02 wording. User accepted round 2 (2026-10-20).
- Closure 2 (fresh reviewer): R-03/N-02, R-09/N-01, N-03…N-06 PASS. Precision follow-ups applied without new user
  semantics: task-03 reconnect keeps the session (Edge host restart on the same broker; Cloud `ForceReconnectAsync`),
  the vacuous Cloud `commands`-row check replaced by a no-redelivery check, the "Discarded a message" log defined per
  message; task-05 depends on task-01 and task-03 (its e2e runs both defaults on real Mosquitto), `IIOT_MQTT_ACL` goes in
  the shell or `deployments/.env` (not `cloud.env`), `-RemoveData` deletes the backup. Status promoted blocked → pending.
