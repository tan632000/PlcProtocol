# Task 01 — The settings file is the only source of PLC, poll and tags

Status: done

## Outcome
When `<db>.settings.json` exists beside the buffer database, the Edge takes `Edge:Plc`, `Edge:Poll` and `Edge:Tags` only
from it, both when it binds options and when it reads raw configuration (D-01..D-03). When the file does not exist,
nothing changes.

## Scope
- In:
  - Resolve the path from `Edge:Buffer:Path`, relative to the content root the same way the buffer resolves it.
  - Load the file and check that `SchemaVersion` is 1.
  - Reject any key outside `Edge:Plc`, `Edge:Poll` and `Edge:Tags`. The error names the file and the key.
  - Compose the configuration so those three sections come only from the file.
  - Expose an `EdgeSettingsFile` API for later tasks: path, bytes, revision, and the parsed `Plc`/`Poll`/`Tags`.
  - Register defaults with `TryAdd` in `EdgeServiceCollectionExtensions`, so plain `AddEdgeAgent` hosts still build.
- Out: restart and apply (task-02), HTTP (task-04).

## Coverage
- CP-01

## Ownership
- Modify: `src/Edge/EdgeServiceCollectionExtensions.cs`
- Create: `src/Edge/Settings/EdgeSettingsFile.cs`, `tests/Edge.Tests/EdgeSettingsFileTests.cs`
- Read: `src/Edge/Drivers/DriverServiceCollectionExtensions.cs`, `src/Edge/Buffering/BufferOptions.cs`, `src/Edge/Buffering/EdgeDatabase.cs`, `src/Edge/lab/fx5u/appsettings.json`, `tests/Edge.Tests/Fakes/EdgeTestHost.cs`, `tests/Edge.Tests/LabSettingsTests.cs`

## Steps
1. Write `EdgeSettingsFileTests`. Inject the environment as a configuration source; never call `Environment.SetEnvironmentVariable`. Cases:
   - (a) No file: the catalog equals the base tags.
   - (b) 21 base tags plus a file with 2: exactly 2 tags.
   - (c) An environment `Edge:Plc:Host` plus the file's Host: the file's Host wins.
   - (d) Base S7, file Slmp: the SLMP driver and address syntax are registered (raw-indexer path).
   - (e) `SchemaVersion` 2, malformed JSON, or a key such as `Edge.Commands.Enabled` / `Edge.Mqtt.Host` / `Edge.Buffer.Path`: the build fails and names the file and the key.
   - (f) Two buffer paths in one directory (`data/edge.db`, `data/fx5u-lab.db`) give distinct settings files.

   Before the change, expect compile failure on the missing type.
2. Implement it and register it.

## Acceptance
- AC-01: (a)–(f) pass, and the full Edge suite passes, including `LabSettingsTests`, `PlcProtocolSwitchTests`, `MonitorTests` and `EdgeHostTests`.

## Dependencies
- none

## Verification Plan
- Command: `dotnet test tests/Edge.Tests`
- Named probe: `EdgeSettingsFileTests` (a)–(f), with the rest of the suite as regression.
- Reachability: source level, on the dev machine and in CI `test`.
- Oracle: exit 0, `Skipped: 0`, and the `EdgeSettingsFileTests` count is at least 6.
- Counterexample: layering the file instead of replacing the sections makes (b) report 21. Binding only through `GetSection` makes (d) keep S7.
- Artifacts: ephemeral temp directories.

## Failure Protocol
On a failed Step or Verification Plan run: stop; do not widen scope, change the Command, or weaken a test; record observed versus expected; repair only the cited cause; after three failed rounds, stop and ask the user.

## Receipt

Verification: PASS
Command: dotnet test tests/Edge.Tests
Exit: 0
Base: 927442f6c144893b2810cf30b14ac572eee996e8
Head: 35c293981544d0e53c4cb7767f904a07ea6f7d7097618a4a369a4f53dd925a51
```text
$ dotnet test tests/Edge.Tests
Passed!  - Failed:     0, Passed:   457, Skipped:     0, Total:   457, Duration: 28 s - Edge.Tests.dll (net10.0)
$ dotnet test tests/Edge.Tests --no-build --filter "FullyQualifiedName~EdgeSettingsFileTests"
Passed!  - Failed:     0, Passed:    24, Skipped:     0, Total:    24, Duration: 155 ms - Edge.Tests.dll (net10.0)
```
- Pre-change run: compile failure `CS0234: The type or namespace name 'Settings' does not exist in the namespace 'IndustrialIot.Edge'` (the type this task creates).
- Probes (a)–(f) map to: `NoFile_TagsComeFromBaseConfiguration`, `File_ReplacesBaseTagArray_InsteadOfMergingByIndex` (21 → 2), `File_HostWinsOverEnvironmentOverride` (environment injected as a later in-memory source, plus base Poll 2500 → default), `File_SwitchesProtocol_ThroughTheRawConfigurationRead`, the `File_WithWrongVersionShapeOrForeignKey_FailsNamingFileAndKey` theory (14 rows), `TwoBufferDatabasesInOneDirectory_GetDistinctSettingsFiles`. Added: revision (`none` / SHA-256), duplicate key refused by `Read`, BOM + lower-case keys accepted, settings file beside `EdgeDatabase.FilePath` for a relative `--contentRoot` and for no `contentRoot` key.
- Counterexamples on a scratch copy (`scratchpad/mut-esui01`): layering the file over the base → 3 failures, including 21 → 2; giving `AddPlcProtocol` the original configuration → 2 failures, including the Slmp switch; the old `?? Directory.GetCurrentDirectory()` content root → the relative-`--contentRoot` case fails.
- Limit: the no-`contentRoot`-key (SCM, cwd System32) case also passes under the old code, because the test process's cwd is the base directory. A process-wide cwd change would disturb parallel tests. The live level of the service runs in task-07.
- Review (code-auditor, fresh context): FAIL first. High: content root resolved unlike the host (System32 fallback; relative `--contentRoot` threw). Medium: UTF-8 BOM refused; `Read` missed duplicate keys. Repair round 1 fixed both (`ResolveContentRoot`, BOM strip, `Read` also flattens) and added the case-insensitive `SchemaVersion` fix. Re-review: PASS, 0 Critical/High/Medium. Lows left as they are: comments and trailing commas stay refused (fail-closed); validation errors do not say a value came from the settings file; `Flatten` runs twice; the test cleanup folder name is shared.
