# Task 02 — The WPF window shows the monitor, and Linux CI still builds and tests

Status: done

## Outcome
`IndustrialIot.EdgeDesktop.exe` opens one window bound to `MonitorViewModel`. It shows the live status and the banner,
and opens the web settings page in the default browser. The CI steps still pass on Linux with the WPF project in the
solution.

## Scope
- In:
  - `IndustrialIot.EdgeDesktop.csproj`: `net10.0-windows`, `UseWPF`, `EnableWindowsTargeting`, referencing Core.
  - `App.xaml`:
    - parses `LaunchOptions`;
    - on an invalid argument shows a `MessageBox` and exits with code 2.
  - `MainWindow.xaml`:
    - title "IIoT Edge Monitor — {edgeId}";
    - 4 cards, the device `DataGrid` and the tag `DataGrid`, all read-only;
    - the banner;
    - a footer saying the app is read-only and updates about every second;
    - the button "Mở trang cấu hình", which calls `Process.Start` with `UseShellExecute=true` on the Core URL.
  - The poll loop starts on load. Its "post to UI" callback is the window's `Dispatcher` (D-09). It is cancelled when
    the window closes.
  - The solution gains the project.
- Out: settings screens; any write.

## Coverage
- CP-01, CP-02 (live), CP-03

## Ownership
- Create:
  - `src/EdgeDesktop/IndustrialIot.EdgeDesktop.csproj`
  - `src/EdgeDesktop/App.xaml`
  - `src/EdgeDesktop/App.xaml.cs`
  - `src/EdgeDesktop/MainWindow.xaml`
  - `src/EdgeDesktop/MainWindow.xaml.cs`
- Modify: `IndustrialIot.sln`
- Read: `src/EdgeDesktop.Core/*`, `.github/workflows/ci.yml`

## Steps
1. Run the Command before any change. It must fail at `test -f …IndustrialIot.EdgeDesktop.dll`.
2. Create the project and the window.
3. On Windows, run `dotnet build IndustrialIot.sln -c Release` and `dotnet test IndustrialIot.sln`.
4. Run the Command.
5. Mutant: remove `EnableWindowsTargeting` on a disposable copy, or restore it right after. The Command must fail.
   Touch the file after restoring it so MSBuild does not reuse the old build.
6. The user's live check is in task-04 (items 1–4).

## Acceptance
- AC-06:
  - The Command exits 0. It runs CI's steps in the Linux SDK container:
    - restore;
    - Release build of the whole solution, with no warnings (`TreatWarningsAsErrors`);
    - `dotnet test` of the solution, with TRX, for the Edge and Shared tests;
    - no `NotExecuted` result;
    - the WPF dll exists.
  - `dotnet test IndustrialIot.sln` on Windows stays green.
- AC-01, AC-02 and AC-05 (live): the user checks these in task-04.

## Dependencies
- task-01-desktop-core.md

## Verification Plan
- Command: `docker run --rm -v "C:/Users/case 046/Documents/Project/PlcProtocol:/src:ro" mcr.microsoft.com/dotnet/sdk:10.0 sh -c "cp -r /src /w && cd /w && find . -type d \( -name bin -o -name obj -o -name TestResults \) -prune -exec rm -rf {} + && dotnet restore && dotnet build --no-restore -c Release && dotnet test IndustrialIot.sln --no-build -c Release --filter 'FullyQualifiedName~IndustrialIot.Edge.Tests|FullyQualifiedName~IndustrialIot.Shared.Tests' --logger trx --results-directory TestResults && ! grep -q 'outcome=\"NotExecuted\"' TestResults/*.trx && test -f src/EdgeDesktop/bin/Release/net10.0-windows/IndustrialIot.EdgeDesktop.dll"`
- Named probe: CI's steps (`ci.yml:30-46`) in the Linux SDK image, plus the WPF dll check.
- Reachability:
  - source, in the Linux container that stands in for CI;
  - live, the window on the dev machine (task-04).
- Oracle: exit 0. The output shows the Edge and Shared test results passed.
- Counterexample: the Command must fail if any of these holds:
  - there is no WPF project (Step 1);
  - `EnableWindowsTargeting` is removed (Step 5);
  - a test result is `NotExecuted`.
- Limitation: `Cloud.Tests` and `Deployment.Tests` need Docker inside the container and are filtered out. The PR's
  real CI run covers them.
- Precondition: `edge-settings-multi-device` is committed, so the copied tree holds only this feature's changes.
- Artifacts: none. The container is removed (`--rm`) and the source is mounted read-only.

## Failure Protocol
On a failed Step or Verification Plan run: stop; do not widen scope, change the Command, or weaken a test; record observed versus expected; repair only the cited cause; after three failed rounds, stop and ask the user.

## Receipt

Verification: PASS
Command: docker run --rm -v "C:/Users/case 046/Documents/Project/PlcProtocol:/src:ro" mcr.microsoft.com/dotnet/sdk:10.0 sh -c "cp -r /src /w && cd /w && find . -type d \( -name bin -o -name obj -o -name TestResults \) -prune -exec rm -rf {} + && dotnet restore && dotnet build --no-restore -c Release && dotnet test IndustrialIot.sln --no-build -c Release --filter 'FullyQualifiedName~IndustrialIot.Edge.Tests|FullyQualifiedName~IndustrialIot.Shared.Tests' --logger trx --results-directory TestResults && ! grep -q 'outcome=\"NotExecuted\"' TestResults/*.trx && test -f src/EdgeDesktop/bin/Release/net10.0-windows/IndustrialIot.EdgeDesktop.dll"
Exit: 0
Base: 11cb08526546d6bdaa5ed2ce295ae60736c4f6f3
Head: 58169b24971ec2b3a943f58ce8bfba1ac0d196da3e10f148b4cec585fd2519ac
Process: before the WPF project existed, the same Command exited 1 only at the final `test -f …IndustrialIot.EdgeDesktop.dll` (restore, Release build, and the Edge 882 / Shared 101 tests had passed in the container).
Negative control: removing EnableWindowsTargeting in the container's copy of the tree (source mounted read-only) failed the Linux build with NETSDK1100.
Windows: `dotnet build IndustrialIot.sln -c Release` clean; `dotnet test IndustrialIot.sln -- RunConfiguration.TreatNoTestsAsError=true` exit 0 (Shared 101, Edge 882, Cloud 175, Deployment 47), run before the XAML-only review repair below. Smoke: the Release exe started with `--port 5999` (no Edge there) stayed up 4 s with no XAML runtime error and was closed with taskkill (window close); started again after the repair, same result. No process left running.
Review: fresh-context code review FAIL (High: MaxHeight on the device grid's Auto row clips rows with no scroll bar, so PLCs beyond about ten would be hidden), repaired by moving MaxHeight onto the DataGrid; also the title without a dangling dash before the first status, an "Edge ID:" label as on the web page, and Loaded unsubscribed so one window runs one poll loop. Re-review PASS. The High was found by reading WPF's Grid measuring, not reproduced; the live check in task-04 has 4 PLCs only.
Limitation: Cloud.Tests and Deployment.Tests need Docker inside the container and are filtered out of this Command; the PR's CI covers them. The window has no uppercase headings or dark mode (the web page has both); a selected row whose values change is replaced and loses its selection.
```text
$ docker run --rm -v "C:/Users/case 046/Documents/Project/PlcProtocol:/src:ro" mcr.microsoft.com/dotnet/sdk:10.0 sh -c "cp -r /src /w && cd /w && find . -type d \( -name bin -o -name obj -o -name TestResults \) -prune -exec rm -rf {} + && dotnet restore && dotnet build --no-restore -c Release && dotnet test IndustrialIot.sln --no-build -c Release --filter 'FullyQualifiedName~IndustrialIot.Edge.Tests|FullyQualifiedName~IndustrialIot.Shared.Tests' --logger trx --results-directory TestResults && ! grep -q 'outcome=\"NotExecuted\"' TestResults/*.trx && test -f src/EdgeDesktop/bin/Release/net10.0-windows/IndustrialIot.EdgeDesktop.dll"
  Determining projects to restore...
  Restored /w/src/Shared/IndustrialIot.Shared.csproj (in 109 ms).
  Restored /w/src/EdgeDesktop.Core/IndustrialIot.EdgeDesktop.Core.csproj (in 109 ms).
  Restored /w/samples/InventoryClient/InventoryClient.csproj (in 14.91 sec).
  Restored /w/src/EdgeDesktop/IndustrialIot.EdgeDesktop.csproj (in 15.05 sec).
  Restored /w/tests/Shared.Tests/Shared.Tests.csproj (in 31.84 sec).
  Restored /w/src/Edge/IndustrialIot.Edge.csproj (in 39.56 sec).
  Restored /w/tests/Edge.Tests/Edge.Tests.csproj (in 39.77 sec).
  Restored /w/src/Cloud/IndustrialIot.Cloud.csproj (in 40.2 sec).
  Restored /w/tests/Cloud.Tests/Cloud.Tests.csproj (in 46.13 sec).
  Restored /w/tests/Deployment.Tests/Deployment.Tests.csproj (in 46.13 sec).
  IndustrialIot.EdgeDesktop.Core -> /w/src/EdgeDesktop.Core/bin/Release/net10.0/IndustrialIot.EdgeDesktop.Core.dll
  IndustrialIot.Shared -> /w/src/Shared/bin/Release/net10.0/IndustrialIot.Shared.dll
  InventoryClient -> /w/samples/InventoryClient/bin/Release/net10.0/InventoryClient.dll
  IndustrialIot.EdgeDesktop -> /w/src/EdgeDesktop/bin/Release/net10.0-windows/IndustrialIot.EdgeDesktop.dll
  Shared.Tests -> /w/tests/Shared.Tests/bin/Release/net10.0/Shared.Tests.dll
  IndustrialIot.Cloud -> /w/src/Cloud/bin/Release/net10.0/IndustrialIot.Cloud.dll
  IndustrialIot.Edge -> /w/src/Edge/bin/Release/net10.0/IndustrialIot.Edge.dll
  Deployment.Tests -> /w/tests/Deployment.Tests/bin/Release/net10.0/Deployment.Tests.dll
  Cloud.Tests -> /w/tests/Cloud.Tests/bin/Release/net10.0/Cloud.Tests.dll
  Edge.Tests -> /w/tests/Edge.Tests/bin/Release/net10.0/Edge.Tests.dll

Build succeeded.
    0 Warning(s)
    0 Error(s)

Time Elapsed 00:00:09.63
Test run for /w/tests/Deployment.Tests/bin/Release/net10.0/Deployment.Tests.dll (.NETCoreApp,Version=v10.0)
Test run for /w/tests/Cloud.Tests/bin/Release/net10.0/Cloud.Tests.dll (.NETCoreApp,Version=v10.0)
Test run for /w/tests/Edge.Tests/bin/Release/net10.0/Edge.Tests.dll (.NETCoreApp,Version=v10.0)
Test run for /w/tests/Shared.Tests/bin/Release/net10.0/Shared.Tests.dll (.NETCoreApp,Version=v10.0)
A total of 1 test files matched the specified pattern.A total of 1 test files matched the specified pattern.A total of 1 test files matched the specified pattern.


A total of 1 test files matched the specified pattern.
No test matches the given testcase filter `FullyQualifiedName~IndustrialIot.Edge.Tests|FullyQualifiedName~IndustrialIot.Shared.Tests` in /w/tests/Deployment.Tests/bin/Release/net10.0/Deployment.Tests.dll
No test matches the given testcase filter `FullyQualifiedName~IndustrialIot.Edge.Tests|FullyQualifiedName~IndustrialIot.Shared.Tests` in /w/tests/Cloud.Tests/bin/Release/net10.0/Cloud.Tests.dll
Results File: /w/TestResults/_9c75e096bbf5_2026-11-03_10_57_59_net10.0.trx

Results File: /w/TestResults/_9c75e096bbf5_2026-11-03_10_57_59_net10.0[1].trx

Results File: /w/TestResults/_9c75e096bbf5_2026-11-03_10_57_59_net10.0[2].trx

Passed!  - Failed:     0, Passed:   101, Skipped:     0, Total:   101, Duration: 150 ms - Shared.Tests.dll (net10.0)
Results File: /w/TestResults/_9c75e096bbf5_2026-11-03_10_58_00_net10.0.trx

Passed!  - Failed:     0, Passed:   882, Skipped:     0, Total:   882, Duration: 1 m 11 s - Edge.Tests.dll (net10.0)
```
