# Device inventory API (roadmap GĐ5)
Specs-Contract: process-first-ready-v1

## Scope decision (GATE-SCOPE — 2026-11-02)
- Request: roadmap `plans/plc-multi-device-roadmap.md` GĐ5 (5.1–5.3): the Edge reports its PLCs and tags to the Cloud;
  the Cloud lists devices, the tags of a device and realtime state; integration docs; acceptance M2 (20/11) is "a sample
  app, using only the docs and the API, lists the PLCs, tags and realtime values of an Edge".
- Existing:
  - `GET /api/v1/edges` (`src/Cloud/Api/EdgesEndpoints.cs:15`), latest values and history per tag
    (`src/Cloud/Api/TelemetryEndpoints.cs:32-34`), SignalR `/hubs/live` pushing `telemetry` per Edge group
    (`src/Cloud/Live/LiveHub.cs:12-28`, `src/Cloud/Live/LiveNotifier.cs:27-38`); every read path uses `Policies.Viewer`
    (`src/Cloud/Security/Policies.cs:9`); endpoint modules are registered by hand in DI
    (`src/Cloud/EndpointModules.cs:17`, `src/Cloud/CloudServiceCollectionExtensions.cs:65-71`).
  - MQTT ingest pattern: topic edge id checked against the allow-list (`src/Cloud/Ingestion/TelemetryIngestor.cs:114-115`),
    subscriptions registered before start (`src/Cloud/Messaging/CloudMqttConnection.cs:89`); the Cloud handles one
    message at a time and a refused subscription fails its connection (`src/Cloud/Messaging/CloudMqttConnection.cs:309-313`).
  - The Edge knows each device's state and last good time (`src/Edge/Monitoring/MonitorServer.cs:197-216`,
    `src/Edge/Monitoring/LiveTagSnapshot.cs:30,58`); tag `Name`/`Description` exist only in the options
    (`src/Edge/Configuration/EdgeChannelsOptions.cs:64-86`), not in the resolved `PlcTagDefinition`
    (`src/Edge/Configuration/EdgeDeviceResolver.cs:78-86`); the agent runs in an inner host rebuilt on every settings
    apply (`src/Edge/Settings/EdgeSupervisor.cs:9-19`); `MqttConnection.ConnectionGeneration` signals reconnects
    (`src/Edge/Messaging/MqttConnection.cs:65-69`).
  - Topics are fixed per Edge (`src/Edge/Messaging/MqttTopics.cs:9-19`); the broker ACL grants four topics
    (`deployments/mosquitto/acl:7-19`); CD refuses a commit that changes the ACL (`deployments/scripts/remote-deploy.sh:53`).
  - Harnesses: real broker + Postgres + Edge + Cloud (`tests/Deployment.Tests/EndToEndTelemetryTests.cs:34`, Cloud on
    `WebApplicationFactory` without a real socket or signing key, Edge on v1 keys, `:206-290`); in-process broker for Edge
    tests (`tests/Edge.Tests/Fakes/TestMqttBroker.cs:18`); Postgres fixture and static signing key for Cloud tests
    (`tests/Cloud.Tests/Infrastructure/CloudTestHost.cs:138-147`).
- Constraint: every MQTT packet is at most 16 KiB — broker `max_packet_size 16384`
  (`deployments/mosquitto/mosquitto.conf`), Edge and Cloud clients (`src/Edge/Messaging/MqttConnection.cs:22`,
  `src/Cloud/Messaging/CloudMqttConnection.cs:39`); Shared must stay free of PLC types (`src/Shared/IndustrialIot.Shared.csproj:3`).
- Minimum change: Shared contract `EdgeInventory`; Edge inventory publisher on `iiot/{edgeId}/inventory`; broker ACL;
  Cloud ingest + one stored document per Edge; two read endpoints; SignalR `devicesUpdated`; integration doc; .NET
  sample client.
- Expansion signals: more than eight touched files across Edge, broker and Cloud; one data path that cannot be delivered
  in parts (nothing is observable until Edge → broker → Cloud → API all exist), so one packet of six tasks.
- User decision: **KEEP** (2026-11-02), with: status sent on change + every 60 s, stale after 150 s; Viewer never sees
  PLC Host/Port, tag Address or raw driver errors (fixed codes only); sample app = .NET console.

## Out of scope
- Settings UI v2 (`multi-device-settings-ui`), 72 h run and release (`multi-device-release`).
- Device tree in the `/scada/` diagnostic viewer; MQTT Last Will / instant Edge-offline detection.
- PLC Host/Port, tag Address and raw error text in any Cloud API or message; Operator-only views.
- Changing `max_packet_size` or any existing topic, contract or endpoint shape.
- Inventory history (only the latest document per Edge is kept); per-Edge Viewer authorization (unchanged).

## Decisions
| ID | Chosen | Rejected | Why | Load-bearing assumption | Break signal → response |
|---|---|---|---|---|---|
| D-01 | Wire contract `EdgeInventory` (Shared, `schemaVersion` 1, `ContractJson` camelCase) on topic `iiot/{edgeId}/inventory`, published at QoS 1 (so the Edge sees a refusal), not retained, never buffered; the Cloud subscribes at QoS 0 (D-04), so the broker never queues inventory for an offline Cloud. Fields every part carries: `edgeId`, `bootId` (Guid, new for every agent start, i.e. every inner host incl. a settings apply), `round` (long ≥ 0, +1 per round, 0 at agent start), `part` (0-based), `partCount` (1..200), `sentUtc`, `commandsEnabled` (bool). Optional per part: `devices` (status entries) and `tags` {`device`, `items`}. Status entry: `name` (`^[A-Za-z0-9_-]{1,32}$`), `model` (`^[A-Za-z0-9_-]{1,32}$`), `enabled`, `writesEnabled`, `state` (`disabled`\|`waiting`\|`connected`\|`unavailable`), `lastGoodUtc` (nullable), `lastError` (null\|`unavailable`\|`tagErrors`), `tagCount`. Tag item: `tagId`, `name`, `dataType` (Shared checks `^[A-Za-z][A-Za-z0-9]{0,15}$`; the Edge only emits `PlcDataType` names), `length` (nullable), `writable`, `min`, `max` (nullable decimals), `description` (nullable, normalized per D-02). No host, port, rack, slot, address or error text anywhere. | one document per message; raise the packet limit; `PlcDataType` in Shared | 16 KiB end to end; a larger packet makes an old broker drop the Edge; Shared stays PLC-free; enum casing follows the existing contracts | 300 tags fit in ≤ 200 parts | a real round exceeds 200 parts → stop, re-plan |
| D-02 | The Edge carries tag `Name` and `Description` into `PlcTagDefinition` (v1 tags: `name` = the TagId, `description` null). Before a round, each description is normalized: control characters → space, trim, empty → null, truncated to 256 UTF-16 units without splitting a surrogate pair. Parts are packed greedily: status entries first, then each device's tags in configuration order, a new part when the serialized part would exceed 12 KiB; a device with no tags gets a `tags` part with no items. Every part is validated before the round is published; a round that still cannot be built, or that needs more than 200 parts, is logged at most once per 10 minutes and skipped. | fixed page sizes; sending config text raw | descriptions are free text (`src/Edge/Configuration/EdgeChannelsOptions.cs:86`) and `PlainText` refuses control characters, blanks and lone surrogates (`src/Shared/Common/ContractJson.cs:318-340`) | 4 KiB headroom covers MQTT headers and topic | a part over 16 KiB is refused by the client → test fails |
| D-03 | Rounds: one when the agent starts, one within 5 s after any device's `state` changes (at most one round per 5 s), one every 60 s, and one right after `ConnectionGeneration` changes. `lastError` changes ride the next heartbeat. The publisher compares the live snapshot every 1 s. A round is skipped while MQTT is not connected. A publish failure (any non-success PUBACK, e.g. an old ACL) is logged at most once per 10 minutes and never affects telemetry. | event-only; 15 s heartbeat; rounds on `lastError` | user decision 2026-11-02 (60 s / 150 s); a flapping tag must not multiply traffic (`src/Edge/Monitoring/LiveTagSnapshot.cs:60-70` has no change event) | — | — |
| D-04 | Cloud `InventoryIngestor` subscribes to `iiot/+/inventory` at QoS 0 (`CloudMqttConnection` takes a QoS per topic filter and accepts a QoS 0 grant for that filter only; telemetry and command results stay QoS 1). It drops unknown Edges, invalid parts and parts whose `edgeId` differs from the topic. One open buffer per Edge, at most 2400 KiB (200 parts × 12 KiB): a part of another (bootId, round) replaces the open buffer unless it is an older round of the same boot (dropped); parts are kept by index (a duplicate index is ignored); a part whose `partCount` differs from the buffer's is dropped. When every index is present the round is checked: device names unique across the round (ordinal ignore case), every `tags.device` matches exactly one status entry, items per device equal its `tagCount`, `tagId` unique across the round, `commandsEnabled` equal in all parts; a failing round is dropped with a fixed reason. A valid round replaces the stored document unless the stored one is the same boot with `round` ≥ this one. A `bootId` whose stored document was replaced by another boot's round is remembered for 10 minutes (at most one per 5 s per Edge, so bounded); its later rounds are dropped with the warning "two Edge processes share edgeId". At most one store and one push per Edge per 5 s (a newer complete round waiting inside that window replaces the older; a late older round of the same boot never replaces a newer waiting one). A part lost in delivery leaves its round incomplete; the next round (≤ 60 s) replaces that buffer, so an Edge turns stale only after two consecutive lost rounds. The MQTT handler only assembles parts and never touches the database; a background loop (woken when a round completes, else every 1 s of the Cloud clock) stores each waiting round as soon as its Edge's 5 s gap allows. If the write fails after `DatabaseRetry`, the round is logged and stays waiting for the loop's next attempt (a newer round replaces it); the ingestor never forces a reconnect. Incomplete buffers expire after 5 minutes. Warnings name topic and reason only. | store parts in the database; order by `sentUtc`; telemetry's reconnect-on-DB-failure; a QoS 1 subscription with message expiry | runtime probe 2026-11-02 on the pinned mosquitto 2.0.20: expired QoS 1 messages still occupy an offline session's queue (3 expired + 5 new in a queue of 5 delivered `new1 new2`), and an offline QoS 1 inventory subscription pushed telemetry out of the queue, while a QoS 0 one was not queued and telemetry arrived; bounded memory per Edge; one message at a time must not block telemetry (`src/Cloud/Ingestion/TelemetryIngestor.cs:66-78,269-305`); duplicates arrive after a forced reconnect (`src/Cloud/Messaging/CloudMqttConnection.cs:173-183`) | the broker delivers one publisher's messages in publish order | — |
| D-05 | Storage: table `edge_inventory` (`edge_id` PK, `boot_id`, `round`, `received_utc` = the Cloud clock when the round completed (its last part arrived), `document` jsonb holding `commandsEnabled`, devices and tags in Edge order), EF migration (`dotnet ef`, tool pinned in `.config/dotnet-tools.json`). Stale = `received_utc` older than 150 s by the Cloud's `TimeProvider`. | Edge `sentUtc` for staleness | the Edge clock may be wrong; arrival time is the Cloud's own fact | — | — |
| D-06 | API (Viewer policy, registered in `CloudServiceCollectionExtensions`; unknown or not allow-listed Edge → 404 `edge_not_found` like `TelemetryEndpoints`): `GET /api/v1/edges/{edgeId}/devices` → 200 `{edgeId, reportedUtc, stale, commandsEnabled, devices: [{name, model, enabled, writesEnabled, state, lastGoodUtc, lastError, tagCount}]}`; no document yet → `reportedUtc`/`commandsEnabled` null, `stale` true, `devices` []; when stale every `state` is `stale`. `GET /api/v1/edges/{edgeId}/devices/{device}/tags` → 200 `[{tagId, name, dataType, length, writable, canWrite, min, max, description, value, quality, timestampUtc}]` in Edge order, value fields from `latest_values` (null when none); device name matched case-insensitively; unknown device or no document → 404 `device_not_found`. `commandsEnabled` in responses = the Cloud's `Cloud:Commands:Enabled` ∧ the Edge's value; `canWrite` = that ∧ device `enabled` ∧ `writesEnabled` ∧ tag `writable`. SignalR: after each stored round, `devicesUpdated` with the `/devices` body to the Edge's group. A stale transition is not pushed; clients read `reportedUtc`. | push on a stale timer; Edge-only `canWrite` | a read-only Cloud answers command routes 404 (`tests/Cloud.Tests/CommandsDisabledTests.cs:22-45`) | — | — |
| D-07 | Sample `samples/InventoryClient` (.NET console, in the solution): reads `IIOT_CLOUD_URL`, `IIOT_TOKEN`, `IIOT_EDGE_ID`; lists devices and each device's tags, then joins `/hubs/live` and prints `telemetry` and `devicesUpdated` until Ctrl+C or `--seconds N`. HTTP uses the `Authorization` header and SignalR the client's `AccessTokenProvider` (never a token in a URL it builds); the token, the environment and request headers are never printed — a 401 prints the status only. The token comes from the documented `get-token.sh viewer`. | PowerShell polling | user decision; integrators copy the sample; the hub also accepts `access_token` in the query (`src/Cloud/Security/AuthenticationSetup.cs:69-77`) | — | — |
| D-08 | Upgrade order: broker ACL first (an old ACL refuses the Cloud's new subscription and the whole Cloud connection fails, `src/Cloud/Messaging/CloudMqttConnection.cs:309-313`), then Cloud, then Edges. On a CD-managed server the ACL change is a manual broker upgrade (`deployments/scripts/remote-deploy.sh:53`, `docs/server-deploy.md`) done before CD deploys this commit. An old Edge sends nothing: its `/devices` answers the "no document" shape. | — | existing order in `docs/deployment.md` "Thứ tự nâng cấp" | — | — |
| D-09 | End-to-end harness for task-06: the Cloud on Kestrel (`WebApplicationFactory.UseKestrel`, random port) with a static RSA signing key the test uses to sign a Viewer token; an Edge with `Edge:Channels` and two simulated PLCs; the simulated PLC driver moved to a shared file in `tests/Deployment.Tests`. | Keycloak in the test | `get-token.sh` needs the compose network (`deployments/scripts/get-token.sh:37`); the existing Cloud host has no socket (`tests/Deployment.Tests/EndToEndTelemetryTests.cs:268-290`) | — | — |

## Coverage profile
| ID | Outcome | Change kinds | Material surfaces | Ambiguity/action | Risk/evidence | Required proof |
|---|---|---|---|---|---|---|
| CP-01 | Inventory parts are valid, bounded and leak no network detail | add | Data/schema (Shared contract) | none | elevated — cross-component contract | source |
| CP-02 | The Edge publishes complete, bounded rounds on start, state change, heartbeat and reconnect, with normalized tag text | add, modify | Async/state, Integration (Edge MQTT, resolver) | none | elevated — runtime behavior | source |
| CP-03 | The broker lets each Edge write only its own inventory topic and the Cloud read all, in both ACL files; an offline Cloud session queues telemetry but no inventory | modify | Runtime/deploy, Security (ACL) | none | elevated — authz | installed (real mosquitto) |
| CP-04 | The Cloud keeps bounded buffers and stores the latest consistent round per Edge, newest wins, without blocking telemetry | add, migrate | Data/schema, Async/state | none | elevated — durable data, availability | source (Postgres) |
| CP-05 | Viewers list devices and tags with state, staleness, values and truthful `canWrite`; realtime `devicesUpdated` | add | API, Time/retention | none | elevated — external API | source |
| CP-06 | A sample client, using only docs and API, lists PLCs, tags and realtime values without exposing its token | add | Integration/proof, docs | none | elevated — acceptance M2 | live (end-to-end stack) |

## Acceptance criteria
| ID | EARS criterion | Proof |
|---|---|---|
| AC-01 | The `EdgeInventory` contract shall reject a part that breaks D-01 bounds and shall carry no host, port, rack, slot, address or error text. | task-01 command |
| AC-02 | When the agent starts, a device state changes, 60 s pass, or MQTT reconnects, the Edge shall publish one complete round of at most 200 parts, each ≤ 12 KiB. | task-02 command |
| AC-03 | The broker shall let an Edge write only `iiot/<its id>/inventory` and the Cloud read `iiot/+/inventory`, in both ACL files, and while the Cloud is offline shall queue its telemetry but no inventory. | task-03 command |
| AC-04 | When all parts of a consistent round arrive, the Cloud shall replace the Edge's stored document unless it holds a newer round of the same boot, with bounded buffers and without forcing a reconnect. | task-04 command |
| AC-05 | When a Viewer calls the device or tag endpoint, the Cloud shall answer per D-06, including `stale` after 150 s, 404s and the Cloud command switch, and shall push `devicesUpdated` after a stored round. | task-05 command |
| AC-06 | The sample client shall list the PLCs, tags and realtime values of an Edge from the end-to-end stack using only the documented API, and shall never print its token. | task-06 command |

## Tasks
| # | Task | Priority | Criteria | Primary ownership | Dependencies | Status |
|---|---|---|---|---|---|---|
| 01 | Inventory contract | P1 | AC-01 | `src/Shared/Contracts/EdgeInventory.cs` | - | done |
| 02 | Edge inventory publisher | P1 | AC-02 | `src/Edge/Messaging/InventoryPublisher.cs` | task-01-inventory-contract.md | done |
| 03 | Broker ACL for the inventory topic | P1 | AC-03 | `deployments/mosquitto/` | - | done |
| 04 | Cloud inventory ingest and storage | P1 | AC-04 | `src/Cloud/Ingestion/InventoryIngestor.cs` | task-01-inventory-contract.md | done |
| 05 | Device and tag API, realtime push | P1 | AC-05 | `src/Cloud/Api/DevicesEndpoints.cs` | task-04-cloud-inventory-ingest.md | done |
| 06 | Sample client, integration doc, end-to-end proof | P2 | AC-06 | `samples/InventoryClient/`, `docs/scada-integration.md` | task-02-edge-inventory-publisher.md, task-03-broker-acl.md, task-05-devices-api.md | done |

## Review log
- Round 1 (2026-11-02): three fresh-context reviewers (fact/contract, security, failure-mode); 23 findings deduplicated to
  R1–R15 (7 High, 8 Medium). User: **accept all**. Applied: R1 → D-02, task-02; R2 → D-01, task-02/04; R3 → D-04,
  task-04; R4 → D-04, task-04; R5 → D-01/D-03, task-02/03; R6 → D-09, task-06; R7 → D-06, task-05; R8 → D-01/D-04,
  task-01/04; R9 → D-06, task-05; R10 → D-01, task-01; R11 → D-04, task-04; R12 → D-03, task-02; R13 → task-03 and
  every Verification command; R14 → D-07, task-06; R15 → D-08, task-06.
- Sweep after round 1: 7 files reread / deltas R1–R15 / stale references fixed: `partCount` 1..1000 → 1..200,
  `tag_errors` → `tagErrors`, "discovered by MapEndpointModules" removed, bootId "per process" → "per agent start",
  "zero tests is a failure" → `TreatNoTestsAsError` / conflicts left: 0.
- Closure of round 1 (fresh context): R1–R4, R6–R15 PASS; R5 UNKNOWN; new contradictions N1–N5. Runtime probe on the
  pinned mosquitto 2.0.20 turned R5 into FAIL (expired messages still fill an offline queue).
- Round 2 (2026-11-02, with runtime evidence): R5′ (Cloud subscribes to inventory at QoS 0; message expiry dropped),
  N1 (buffer 2400 KiB; Edge skips rounds over 200 parts), N3 (probes for task-06 doc criteria; mosquitto.conf comment
  dropped), N4 (new broker tests live in class `BrokerTests`), N5 (superseded = stored document replaced). User:
  **accept all**. Applied to D-01, D-02, D-04, CP-02/03, AC-02/03, tasks 02, 03, 04, 06.
- Sweep after round 2: 7 files reread / deltas R5′, N1, N3–N5 / stale references fixed: message expiry 120 s removed
  (D-01, CP-02, AC-02/03, task-02/03), 2 MiB → 2400 KiB, Blockers → round 2 / conflicts left: 0.
- Closure of round 2 (fresh context): R5′, N1, N3, N4, N5 PASS. Five wording/probe gaps left by the repairs fixed by
  sweep (no new scope or decision): lost-part consequence stated in D-04 with a task-04 bullet; the D-04 order
  assumption no longer says QoS 1; ack wording removed (QoS 0 has no ack); the task-04 Command also runs the tests of the
  shared `CloudMqttConnection`; a task-01 probe for the Shared project reference. Sweep: 7 files reread / conflicts left: 0.
- Task-04 review (2026-11-02, fresh context) found a High: a waiting round was stored only on the Edge's next message and
  `received_utc` was the store time, so a state change could show up to ~60 s late and an old round could look fresh.
  User: **background loop**. D-04 and D-05 now say the handler never touches the database, a loop stores waiting rounds as
  soon as the 5 s gap allows, `received_utc` is the completion time, and a late older round never replaces a newer waiting
  one; task-04 Acceptance (Rate, Database down) changed accordingly.
- GATE-DONE (2026-11-02): tasks 01–06 done with valid Receipts; feature check `dotnet build IndustrialIot.sln -c Release`
  + `dotnet test IndustrialIot.sln -c Release --no-build` exit 0 (Shared 101, Edge 783, Cloud 175, Deployment 47). User:
  **complete**. canWrite on a stale Edge: user keeps D-06 as is (docs tell clients to treat a stale Edge as not writable).
