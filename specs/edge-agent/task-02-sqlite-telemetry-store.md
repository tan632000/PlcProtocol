# Task 02 — Telemetry samples persist in SQLite with stream identity, gap-free sequence at append and bounded retention

Status: done

## Outcome
`EdgeDatabase` opens one durable SQLite file at host start, and `TelemetryStore` appends samples (a whole poll cycle per transaction) as fully formed `TelemetryPayload` rows, hands them out in `sequence` order, deletes them only when acknowledged, and enforces the 7-day / 1,000,000-row limit by dropping the oldest rows — never while the clock watermark shows a rollback.

## Scope
- In: `BufferOptions` + validator, `EdgeDatabase` (connection factory, pragmas, schema, open-at-start, file permissions, clock watermark), `TelemetryStore` (append batch, read batch, acknowledge, purge, count, append signal), registration, `EdgeTestHost` defaults, test package.
- Out: what produces samples (task-03), what publishes them (task-04), the `command_log` table (task-05 creates it through `EdgeDatabase`).

## Coverage
- CP-02, CP-01 (buffer config validation, open at start)

## Ownership
- Create: `src/Edge/Buffering/{BufferOptions.cs,EdgeDatabase.cs,TelemetryStore.cs}`
- Modify: `src/Edge/EdgeServiceCollectionExtensions.cs`, `src/Edge/appsettings.json` (add `Edge:Buffer`), `src/Edge/IndustrialIot.Edge.csproj` (add `Microsoft.Data.Sqlite` 10.0.13)
- Modify: `tests/Edge.Tests/Edge.Tests.csproj` (add `Microsoft.Extensions.TimeProvider.Testing` 10.10.0, the version used by `tests/Shared.Tests/Shared.Tests.csproj:9`), `tests/Edge.Tests/Fakes/EdgeTestHost.cs`
- Create: `tests/Edge.Tests/TelemetryStoreTests.cs`
- Read: `src/Shared/Contracts/TelemetryPayload.cs`, `src/Shared/Common/ContractJson.cs`, `src/Edge/EdgeOptions.cs`

## Contracts
**Options** `Edge:Buffer`: `Path` = `data/edge.db` (relative → resolved against `IHostEnvironment.ContentRootPath`), `MaxAgeHours` = 168 (1..8760), `MaxRows` = 1_000_000 (1_000..100_000_000). Invalid → `OptionsValidationException` at start.

**`EdgeDatabase`** (singleton; the only place that opens SQLite connections)
- `OpenConnectionAsync(ct)` returns a new open connection with `PRAGMA journal_mode=WAL`, `PRAGMA synchronous=FULL`, `PRAGMA busy_timeout=5000` applied.
- Opened by a hosted service registered **before** every other Edge hosted service; its `StartAsync` creates the directory, opens the file, checks the schema and creates tables. Failure (not a SQLite file, `user_version` > 1, unwritable path) → `StartAsync` throws `InvalidOperationException` with the path (no automatic delete/recreate).
- On non-Windows: a missing directory is created with mode 0700; the database file is created 0600 before SQLite first writes it, and an existing database, `-wal` or `-shm` file with a broader mode is tightened and logged. An existing directory with a broader mode is only logged, never chmodded (user decision 2026-10-18: it may be a shared directory such as `/tmp` or `/var/lib`).
- `EnsureTableAsync(string createSql, ct)` lets task-05 add its own table.
- **Clock watermark:** `meta.last_seen_utc_ms` = highest `TimeProvider.GetUtcNow()` observed (updated at most once per minute, at shutdown, and **always committed before any purge** — telemetry or command log — so a purge can never run ahead of the persisted watermark). `bool IsClockRolledBack` = `now < last_seen − 2 min`; when it first becomes true a warning is logged once.

**Schema** (`CREATE TABLE IF NOT EXISTS`, `PRAGMA user_version = 1`):
| Table | Columns |
|---|---|
| `meta` | `key TEXT PRIMARY KEY`, `value TEXT NOT NULL` — keys `stream_id` (GUID "D"), `next_sequence` (int64), `last_seen_utc_ms` (int64) |
| `telemetry` | `sequence INTEGER PRIMARY KEY`, `created_utc_ms INTEGER NOT NULL`, `payload TEXT NOT NULL` (exact `ContractJson.Serialize` output) |
- First open of a new file: generate `stream_id`, `next_sequence = 0`.

**`TelemetryStore` API** (thread-safe)
- `Task<IReadOnlyList<TelemetryPayload>> AppendBatchAsync(IReadOnlyList<TelemetrySample> samples, CancellationToken ct)` — `TelemetrySample(string TagId, JsonElement? Value, TelemetryQuality Quality, DateTimeOffset TimestampUtc)`; in **one** transaction: read `next_sequence`, build and validate each payload (`schemaVersion 1`, `edgeId`, `streamId`, consecutive sequences), insert, advance `next_sequence`. A failed transaction leaves `next_sequence` unchanged and inserts nothing. Signals waiters after commit.
- `Task WaitForAppendAsync(TimeSpan timeout, CancellationToken ct)` — completes on the next commit or after `timeout`.
- `Task<IReadOnlyList<StoredTelemetry>> ReadBatchAsync(int max, CancellationToken ct)` — oldest first; `StoredTelemetry(long Sequence, string Payload)`.
- `Task AcknowledgeAsync(long sequence, CancellationToken ct)` — deletes that row; unknown sequence is a no-op.
- `Task<long> CountAsync(CancellationToken ct)` — `next_sequence − MIN(sequence)` style bound, never `COUNT(*)` over the table (exact when no holes; holes only come from acknowledgements, which purge tolerates).
- `Task<int> PurgeAsync(CancellationToken ct)` — no-op while `IsClockRolledBack`; otherwise deletes rows with `created_utc_ms` older than now − `MaxAgeHours`, then the oldest rows beyond `MaxRows`, in chunks of at most 5,000 rows per transaction; each chunk logs one warning with its dropped count and `first..last` sequence. Called by `AppendBatchAsync` when the bound exceeds `MaxRows`, and hourly by task-04.
- "Gap-free" means at append; purge creates deliberate gaps that the warnings describe.

## Steps
1. Packages; options + validator; `EdgeDatabase` + its startup hosted service; `TelemetryStore`; register in `AddEdgeAgent`; add `Edge:Buffer` to `appsettings.json`; add buffer defaults (temp path) to `EdgeTestHost`.
2. Tests on unique temp directories (deleted per test) with `FakeTimeProvider`.

## Acceptance
- AC-02: a batch of 3 → sequences 0,1,2 with one `streamId`; reopening continues at 3 with the same `streamId`; a new file gets a different `streamId` at 0; payloads round-trip through `ContractJson.Deserialize<TelemetryPayload>`; a batch containing an invalid sample inserts nothing and leaves `next_sequence` unchanged.
- AC-03: with `MaxRows = 1000`, appending 1005 rows removes exactly the 5 oldest and the logged dropped counts sum to 5; rows older than `MaxAgeHours` are purged; after moving the fake clock back 1 h below the watermark, purge removes nothing and logs the rollback warning once; after a purge, reopening the database shows `last_seen_utc_ms` ≥ the purge time (watermark persisted before the purge).
- AC-01 (part): `MaxRows = 10`, `MaxAgeHours = 0`, empty `Path` → `OptionsValidationException`; a non-SQLite file at `Path` → `StartAsync` throws `InvalidOperationException`.
- Pragmas read back as `wal`, `2` (FULL), `5000`; on Linux the file mode is 0600 (probe skipped on Windows).
- Acknowledge removes exactly the given row; read batch stays ascending after out-of-order acks.

## Dependencies
- task-01-host-config-registration.md

## Verification Plan
- Command: `dotnet test IndustrialIot.sln`
- Named probe: `TelemetryStoreTests.AppendBatch_AssignsGapFreeSequence_AndSurvivesReopen`, `TelemetryStoreTests.NewFile_StartsNewStream`, `TelemetryStoreTests.FailedBatch_InsertsNothing`, `TelemetryStoreTests.Purge_DropsOldestBeyondLimits`, `TelemetryStoreTests.Purge_SkippedWhileClockRolledBack`, `TelemetryStoreTests.Purge_PersistsWatermarkFirst`, `TelemetryStoreTests.Acknowledge_RemovesOnlyThatRow`, `TelemetryStoreTests.Pragmas_AndFileMode`, `TelemetryStoreTests.InvalidBufferConfig_FailsAtStart`; all `EdgeHostTests` still pass.
- Reachability: source — .NET 10 SDK + Microsoft.Data.Sqlite native bundle (win-x64 / linux-x64 / linux-arm64).
- Oracle: exit 0, 0 failed across the solution; nine named probes pass (`Pragmas_AndFileMode` checks the file mode only on Linux — on Windows that part is skipped and recorded as a limitation in the Receipt).
- Counterexample: advancing `next_sequence` outside the insert transaction, deleting on read, purging during a rollback, or opening the file lazily must fail the named probes.
- Artifacts: ephemeral temp directories, deleted per test.

## Failure Protocol
On a failed Step or Verification Plan run: stop; do not widen scope, change the Command, or weaken a test; record observed versus expected; repair only the cited cause; after three failed rounds, stop and ask the user.

## Receipt

Verification: PASS
Command: dotnet test IndustrialIot.sln
Exit: 0
Base: d50cbfdf5a122f42e9cba23409b986e85969ba2e
Head: 7b3c0ece87a279c46a79b5cdfafc851d98654364338cadfe0b8708bf79f8fe34
```text
$ dotnet test IndustrialIot.sln
  Determining projects to restore...
  All projects are up-to-date for restore.
  IndustrialIot.Shared -> C:\Users\HOME\source\repos\PLCProtocol\src\Shared\bin\Debug\net10.0\IndustrialIot.Shared.dll
  IndustrialIot.Edge -> C:\Users\HOME\source\repos\PLCProtocol\src\Edge\bin\Debug\net10.0\IndustrialIot.Edge.dll
  Shared.Tests -> C:\Users\HOME\source\repos\PLCProtocol\tests\Shared.Tests\bin\Debug\net10.0\Shared.Tests.dll
Test run for C:\Users\HOME\source\repos\PLCProtocol\tests\Shared.Tests\bin\Debug\net10.0\Shared.Tests.dll (.NETCoreApp,Version=v10.0)
A total of 1 test files matched the specified pattern.
  Edge.Tests -> C:\Users\HOME\source\repos\PLCProtocol\tests\Edge.Tests\bin\Debug\net10.0\Edge.Tests.dll
Test run for C:\Users\HOME\source\repos\PLCProtocol\tests\Edge.Tests\bin\Debug\net10.0\Edge.Tests.dll (.NETCoreApp,Version=v10.0)
A total of 1 test files matched the specified pattern.

Passed!  - Failed:     0, Passed:    47, Skipped:     0, Total:    47, Duration: 207 ms - Shared.Tests.dll (net10.0)

Passed!  - Failed:     0, Passed:   241, Skipped:     0, Total:   241, Duration: 14 s - Edge.Tests.dll (net10.0)
```
- Pre-change run: exit 0 with 0 `TelemetryStoreTests` listed → oracle (named probes) not met, as expected.
- Named probes present and passing: `AppendBatch_AssignsGapFreeSequence_AndSurvivesReopen`, `NewFile_StartsNewStream`, `FailedBatch_InsertsNothing`, `Purge_DropsOldestBeyondLimits`, `Purge_SkippedWhileClockRolledBack`, `Purge_PersistsWatermarkFirst`, `Acknowledge_RemovesOnlyThatRow`, `Pragmas_AndFileMode`, `InvalidBufferConfig_FailsAtStart` (3 rows), plus `NotASqliteFile_FailsAtStart` and `AgePurge_UsesCreatedIndex`; all `EdgeHostTests` still pass.
- Counterexamples (mutations in a scratch copy), each caught: `next_sequence` advanced outside the insert transaction → `FailedBatch_InsertsNothing`; delete on read → `Acknowledge_RemovesOnlyThatRow`; purge ignoring the rollback → `Purge_SkippedWhileClockRolledBack`; no open at start → `NotASqliteFile_FailsAtStart`; age purge ordered by `sequence` → `AgePurge_UsesCreatedIndex` (plan checked on the bundled e_sqlite3).
- Packages: Microsoft.Data.Sqlite 10.0.13 (Edge), Microsoft.Extensions.TimeProvider.Testing 10.10.0 (Edge.Tests). Tests use a hand-written `MutableClock` because `FakeTimeProvider` cannot move backwards.
- `EdgeTestHost` also fixes task-01 review Lows: case-insensitive overrides, SQLite pool clear and broader cleanup on dispose/build failure.
- Repair rounds: R1 review PASS_WITH_WARNINGS (-wal/-shm created 0644; append could throw after commit; age purge full scan) → files pre-created 0600, post-commit trim never throws, index added (+ Lows: created time ≥ watermark, single-statement count, shutdown persist logged); R2 re-review PASS_WITH_WARNINGS (index unused because of `ORDER BY sequence`) → age purge ordered by `created_utc_ms, sequence` + EXPLAIN probe; existing directories only warned. Final re-review: code-auditor PASS (0 Critical/High/Medium).
- User decision 2026-10-18: an existing broader directory is only logged, never chmodded (Contracts updated).
- Known limitations: the Unix-permission code (0700/0600) does not execute on this Windows machine — `Pragmas_AndFileMode` checks the file mode only on Linux (wave-4 CI); `Purge_PersistsWatermarkFirst` cannot distinguish persisting before vs. after the delete; a commit between an empty read and `WaitForAppendAsync` delays the publisher by up to its timeout; with a non-monotonic `created_utc_ms` (clock stepped back within the 1-minute watermark window) one age-purge transaction may delete more than 5,000 rows (all expired, counts still exact).
