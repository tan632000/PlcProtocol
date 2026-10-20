# Telemetry delivery: Cloud-confirmed delete, 100 tags/s without loss
Specs-Contract: process-first-ready-v1

## Scope decision (GATE-SCOPE — 2026-10-20)
- Request: raise Edge telemetry throughput from the observed ~23 samples/s to "a few hundred tags at 1 s" while keeping no loss / no duplicates.
- Existing (measured 2026-10-20 on the dev machine):
  - Edge drains strictly in order, deleting each row after its PUBACK (`src/Edge/Messaging/TelemetryPublisher.cs:83-106`); QoS 1 publish awaits the PUBACK (`src/Edge/Messaging/MqttConnection.cs:90-104`).
  - The 23/s comes from the dev profile host `localhost` → `::1` → Docker Desktop IPv6 port proxy: QoS 1 over TLS measured **44.2 ms/publish (23/s) on `::1`, 0.5 ms (~2 060/s) on `127.0.0.1`** (scratch MQTTnet 5.2 benchmark against the pinned Mosquitto image). The publisher algorithm is not the bottleneck.
  - SQLite delete with `synchronous=FULL` + WAL (`src/Edge/Buffering/EdgeDatabase.cs:73,100`): 0.36 ms/row → not the bottleneck.
  - **Existing data-loss defect:** the broker keeps at most 1 000 queued QoS 1 messages per offline persistent client (Mosquitto default `max_queued_messages 1000`; not overridden in `deployments/mosquitto/mosquitto.conf:1-30`). Reproduced: publisher got PUBACK 1500/1500, an offline persistent subscriber received **1000/1500**, broker logged `Outgoing messages are being dropped`. The Edge has already deleted those rows → silent permanent loss whenever the Cloud is offline for > 1 000 samples.
  - Cloud stores one message per PostgreSQL transaction, dedupes on (`edge_id`,`stream_id`,`sequence`) (`src/Cloud/Ingestion/TelemetryIngestor.cs:116-170`), acknowledges after commit, keeps a persistent 24 h session (`src/Cloud/Messaging/CloudMqttConnection.cs:252-253`); capacity not yet measured.
  - Real broker fixture with the repo's `mosquitto.conf` + ACL (`tests/Deployment.Tests/BrokerTests.cs:491-545`); Cloud host is test-hostable (`src/Cloud/Program.cs:24`).
  - PLC reads cost 17.5 ms/tag (one S7 request per tag, `src/Edge/Drivers/SiemensS7Driver.cs:62`) → ~55 tags per 1 s cycle. Out of scope here (next packet).
- Minimum change: Edge deletes a sample only after the Cloud confirms it is stored; bounded unconfirmed window with backed-off resend; broker queue bounded in messages and bytes; end-to-end proof at the target load.
- Expansion signals: 4 components (Shared contract, broker config, Cloud, Edge) but one deliverable: none of them alone closes the loss defect.
- User decisions: **KEEP**; target **100 tags / 1 s** (100 samples/s sustained, backlog drain ≥ 200 samples/s); **no loss for as long as the Edge buffer holds** (the durable buffer is the Edge, not the broker).

## Out of scope
- Dev launch profile `127.0.0.1` fix — done directly outside this packet (GATE-REVIEW R-11).
- Batched PLC reads (S7 multi-variable / block reads) — the real limit for > ~55 tags at 1 s; separate packet.
- Cloud batch inserts and ack batching — only if AC-07 measures a drain < 200/s (break signal of D-05).
- Showing "waiting for Cloud" on the PLC Monitor page; changing the telemetry payload or its dedupe key.

## Decisions
| ID | Chosen | Rejected | Why | Load-bearing assumption | Break signal → response |
|---|---|---|---|---|---|
| D-01 | The Cloud confirms stored telemetry to the owning Edge on `iiot/{edgeId}/telemetry-acks` with a `TelemetryAck` (`schemaVersion`, `edgeId`, `streamId`, `fromSequence`, `toSequence`: every sequence in the inclusive range is handled); the Edge deletes rows only on such an ack | bigger broker queue only (an outage longer than the queue still loses — violates GATE-SCOPE); Edge querying the Cloud over HTTP (the Edge has no Cloud URL or credentials; new auth surface) | the only Edge↔Cloud channel is MQTT; the Cloud already dedupes, so resends are safe | the Cloud commits before acknowledging | acks lost or late → Edge resends (D-03); duplicates absorbed by the PK |
| D-02 | The Cloud acks a message only when the payload parsed, `payload.edgeId == topic edgeId`, the topic Edge is allow-listed, and the sample was inserted, was a duplicate, or failed a **clock-independent** contract rule; the ack goes to the **topic** Edge. Never acked: unparseable payload, non-allow-listed Edge, `edgeId` mismatch (R-03), timestamp > 2 min in the future (R-07a: the Edge keeps it and resends; the Cloud logs the drop), a payload whose `schemaVersion` is not the Cloud's (a newer Edge: valid after the Cloud upgrade), a payload with duplicated properties, store failure | acking every validation drop; addressing acks by payload `edgeId` | an ack must never delete another Edge's data, nor data that becomes valid once the clock is fixed | — | Edge window stuck with the Cloud logging "timestamp more than 2 minutes in the future" → fix NTP on the Edge; logging "invalid payload" after an Edge upgrade → upgrade the Cloud |
| D-03 | Edge publishes in sequence order from an in-memory cursor without deleting on PUBACK. Window = rows still in the table with `sequence ≤ cursor` (re-read after every ack and purge); publishing stops while it is ≥ `Edge:Mqtt:MaxUnacknowledged` (default 500, valid 1..5000). Progress = the oldest queued row (head of the window) was confirmed, i.e. the smallest queued sequence advanced (amended at develop review H-1: "any ack that deleted a row" let a single unconfirmed sample sit forever behind steadily acked later ones until the MaxRows purge deleted it). Without progress for the current ack timeout the cursor resets to the oldest queued row; the timeout starts at `Edge:Mqtt:AckTimeoutSeconds` (default 30, valid 5..600), doubles on each reset up to 600 s, and returns to the start value on progress. A reconnect (new connection generation) also resets the cursor. Acks with another `edgeId` or `streamId` are ignored; ack-handler store errors are logged and the MQTT message is still acknowledged | sent/confirmed counters (miscount on duplicate acks, purges, ack-before-PUBACK); fixed-interval resend (floods the broker queue during long Cloud outages) | simplest correct at-least-once; the queue a Cloud outage builds is bounded by window × (1 + number of resets) | backoff keeps resets ≤ ~log2(600/start) + outage/600 | broker logs `dropped` with the repo config → lower the window or raise D-04 (no data is lost, only delayed) |
| D-04 | `max_queued_messages 20000` and `max_queued_bytes 134217728` (128 MiB) in `mosquitto.conf`; sizing rule documented: messages ≥ 2 × (Edges × MaxUnacknowledged), which covers a Cloud outage up to the second resend reset (~90 s at the default 30 s timeout); longer outages may log `dropped` without losing data | 100000 (≈ 1.6 GiB per offline client at `max_packet_size 16384`); `0` (unbounded) | with D-03 a broker drop only delays data, so the broker limit protects the broker, not the data | ≤ ~20 Edges at the default window | `dropped` during long Cloud outages → expected (data stays on the Edges); raise the limit only if broker memory allows |
| D-05 | The Cloud publishes one `TelemetryAck` (from = to) per acked message right after the commit, through `CloudMqttConnection.PublishAsync`; a failed or not-connected publish is logged and dropped (the Edge resend covers it) | batching with timers (R-11: premature, more failure modes) | 100 small QoS 1 publishes/s are cheap (0.5 ms measured) | — | AC-07 drain < 200/s → back to GATE-SCOPE (ack batching or Cloud batch insert) |
| D-06 | Rollout: broker ACL + config, then Cloud, then Edges. Old Edge + new Cloud: acks go to a topic nobody reads (harmless). New Edge + old ACL: the broker refuses the `telemetry-acks` subscription and the Edge cannot connect at all (`src/Edge/Messaging/MqttConnection.cs:218-229`) — no telemetry, no commands; data stays buffered | — | — | operators follow `docs/deployment.md` order | Edge log `Broker refused subscription(s): iiot/<id>/telemetry-acks` → update the broker ACL |

## Coverage profile
| ID | Outcome | Change kinds | Material surfaces | Ambiguity/action | Risk/evidence | Required proof |
|---|---|---|---|---|---|---|
| CP-01 | A `TelemetryAck` contract and topic exist that only the Cloud can write and only the owning Edge can read | add | API/contract (JSON shape, validation), Filesystem/security (broker ACL) | none | critical — wrong ACL lets an Edge delete another's data | source + installed (real Mosquitto) |
| CP-02 | The Cloud confirms exactly the stored samples to the right Edge | add, modify | Async/state, Integration (MQTT publish), Security (topic vs payload edgeId) | none | critical — a false ack deletes undelivered data | source (Postgres + test broker) |
| CP-03 | The Edge deletes only confirmed samples, keeps a bounded window and resends with backoff | modify, remove | Async/state (cursor, window, timeout, reconnect), Data (SQLite delete/count), Runtime (new options) | none | critical — durability contract change | source |
| CP-04 | 100 tags/s end to end with every sample stored exactly once, also when the broker drops queued messages; drain ≥ 200/s; broker bounded | integrate, modify | Integration/proof (real broker + Cloud + PostgreSQL + Edge), Runtime (broker config, rollout docs) | none | critical — the defect being closed | installed (real Mosquitto containers, real Cloud and Edge hosts) |

## Acceptance criteria
| ID | EARS criterion | Proof |
|---|---|---|
| AC-01 | The `TelemetryAck` contract shall reject schemaVersion ≠ 1, an invalid edgeId, an empty streamId, a negative or reversed range, and a range wider than 10 000 sequences. | task-01 command |
| AC-02 | The broker shall let only `iiot-cloud` write `iiot/+/telemetry-acks` and let each Edge read only `iiot/<own id>/telemetry-acks`. | task-01 command |
| AC-03 | When the Cloud has inserted, found as duplicate, or rejected by a clock-independent contract rule a telemetry message whose payload edgeId matches its allow-listed topic Edge, the Cloud shall publish a `TelemetryAck` for that sequence to that topic Edge within 1 s. | task-02 command |
| AC-04 | If a message is unparseable, from a non-allow-listed Edge, has a payload edgeId different from the topic, has a timestamp more than 2 min in the future, or could not be stored, the Cloud shall publish no ack on any topic. | task-02 command |
| AC-05 | The Edge shall delete a queued sample only after a `TelemetryAck` for its own edgeId and current streamId covers it; a PUBACK alone shall not delete it, and a failing delete shall not stop later acks from being processed. | task-03 command |
| AC-06 | While queued rows with sequence ≤ cursor ≥ `MaxUnacknowledged`, the Edge shall not publish; without progress of the oldest queued row for the current timeout (start `AckTimeoutSeconds`, doubling to 600 s, reset on progress) or after a reconnect, it shall resend from the oldest queued row — also while later samples keep being acked. | task-03 command |
| AC-07 | With 100 tags at 1 s: (A) for 60 s every produced sequence `0..n-1` is stored exactly once and the Edge buffer returns to 0; (B) with the repo broker config and the Cloud stopped for ≥ 40 s (`AckTimeoutSeconds=5`), the same holds, the broker logs no `dropped`, and the gross drain rate after restart is ≥ 200 samples/s; (C) with a broker capped at `max_queued_messages 1000` and a Cloud outage producing > 1 000 samples, the same exactly-once check holds. | task-04 command |

## Tasks
| # | Task | Priority | Criteria | Primary ownership | Dependencies | Status |
|---|---|---|---|---|---|---|
| 01 | `TelemetryAck` contract, topic and broker ACL | P1 | AC-01, AC-02 | `src/Shared/Contracts/TelemetryAck.cs`, `deployments/mosquitto/acl` | - | done |
| 02 | Cloud acks exactly the stored telemetry | P1 | AC-03, AC-04 | `src/Cloud/Ingestion/TelemetryIngestor.cs` | task-01-telemetry-ack-contract-acl.md | done |
| 03 | Edge deletes only on Cloud ack, bounded window, backed-off resend | P1 | AC-05, AC-06 | `src/Edge/Messaging/TelemetryPublisher.cs`, `src/Edge/Buffering/TelemetryStore.cs` | task-01-telemetry-ack-contract-acl.md | done |
| 04 | Broker limits + end-to-end 100 tags/s exactly-once proof + docs | P1 | AC-07 | `deployments/mosquitto/mosquitto.conf`, `tests/Deployment.Tests/EndToEndTelemetryTests.cs` | task-02-cloud-acks-stored-telemetry.md, task-03-edge-delete-on-ack.md | done |

## Review log
- Round 1 (3 fresh reviewers: fact/contract, failure-mode, security/scope; 22 raw findings → 13): user accepted R-01…R-06 and R-08…R-13, and chose R-07 (a). Applied: D-02 (R-03, R-07a), D-03 (R-01, R-05, R-08, R-09), D-04 (R-01, R-04), D-05 (R-11), D-06 (R-12); task-01 split out (R-11); task-03 ownership (R-06, R-09); task-04 phases B/C, host 127.0.0.1, drain definition, docs ownership (R-02, R-10, R-13); task-01 command (R-13). Sweep: 5 files reread / 13 deltas / 1 stale counterexample fixed (task-04) / 0 conflicts.
- Closure (fresh reviewer): R-01…R-13 all PASS (table below). Follow-ups applied without new user semantics: D-04 sizing scope and break signal; phase C asserts the broker really dropped; task-03 named probe wording; task-04 in-test configuration (no reliance on output `appsettings.json`). Status promoted blocked → pending.
- Develop task-03 review: H-1 (user decision 2026-10-20) — D-03 progress amended to "head of the window advanced"; M-1 cursor reset before any I/O; M-2 measured `COUNT(*)` on 1 000 000 rows = 12 ms warm on the dev SSD, kept (target eMMC unmeasured, GATE-DONE limitation).
- Develop task-02 review (repair round 1): fixed a poison message (invalid UTF-8 escaped the rejection parser) by re-parsing the decoded text with the contract's duplicate-property rule; D-02 now also lists schema-version and duplicate-property drops as never acked.

| ID | Decision | Original counterexample | Repaired at | Proved at | Replay | Closure |
|---|---|---|---|---|---|---|
| R-01 | accept | fixed 30 s resend floods broker queue; e2e outage < timeout | D-03 backoff; AC-07(B) | task-03 AC-06 schedule 5/15/35 s; task-04 phase B 40 s | queue bounded by window × resets | PASS |
| R-02 | accept | old design passes e2e; dup assert always true | task-04 phase C capped broker | task-04 Counterexample + set check | old design loses in C | PASS |
| R-03 | accept | payload edgeId routes ack to another Edge | D-02, AC-04 | task-02 case (f) | mismatch → no ack | PASS |
| R-04 | accept | 100000 × 16 KiB per client | D-04 | task-04 Step 1 | bytes cap 128 MiB | PASS |
| R-05 | accept | counter window miscounts; CountAsync over-counts gaps | D-03 table window | task-03 Steps 3, 5 | COUNT-based | PASS |
| R-06 | accept | BrokerOutage test breaks; store tests unowned | task-03 Ownership | task-03 Step 5 | owned and rewritten | PASS |
| R-07 | accept (a) | future-timestamp ack deletes recoverable data | D-02 | task-02 case (d) | no ack | PASS |
| R-08 | accept | handler error stalls inflight | D-03 | task-03 Step 5 | logged, still acked | PASS |
| R-09 | accept | no reconnect signal in ownership | task-03 Ownership/Step 2 | task-03 AC-06 restart case | generation counter | PASS |
| R-10 | accept | localhost → 23/s, undefined drain, port 5080 | task-04 Step 3 | task-04 Oracle | 127.0.0.1, gross drain, monitor off | PASS |
| R-11 | accept | premature batcher; task-01 off-goal | D-05; Out of scope | task-02 Step 2 | per-message ack | PASS |
| R-12 | accept | D-06 claims window stall with old ACL | D-06 | `src/Edge/Messaging/MqttConnection.cs:218-225` | refused subscription documented | PASS |
| R-13 | accept | filter may run 0 tests; docs drift | task-01 command; task-04 Ownership | task-01 Oracle grep ≥ 1 | full suite + docs owned | PASS |
