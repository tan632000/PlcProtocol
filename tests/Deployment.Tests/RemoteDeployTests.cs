using System.Diagnostics;

namespace IndustrialIot.Deployment.Tests;

/// <summary>
/// deployments/scripts/remote-deploy.sh, run by bash in a self-contained temporary repository (a bare origin, a work
/// clone that commits, a deploy clone that plays the server). <c>docker</c> and <c>curl</c> are stubs that record their
/// calls; nothing touches this repository, Docker or the network.
/// </summary>
public sealed class RemoteDeployTests : IDisposable
{
    private const string Image = "ghcr.io/tan632000/plcprotocol/cloud@sha256:1111111111111111111111111111111111111111111111111111111111111111";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "remote-deploy-" + Guid.NewGuid().ToString("N"));
    private readonly string _repoHeadBefore = Git(Repo.Root, "rev-parse", "HEAD");

    public RemoteDeployTests()
    {
        Directory.CreateDirectory(_root);
        Git(_root, "init", "--quiet", "--bare", "-b", "main", "origin.git");
        Git(_root, "clone", "--quiet", Origin, "work");

        // LF on checkout whatever core.autocrlf says (the repository's own rule).
        File.Copy(Path.Combine(Repo.Root, ".gitattributes"), Path.Combine(Work, ".gitattributes"));
        WriteWork("deployments/scripts/remote-deploy.sh", File.ReadAllText(Path.Combine(Repo.Root, "deployments", "scripts", "remote-deploy.sh")));
        WriteWork("deployments/compose.yaml", "services:\n  cloud:\n    image: ${IIOT_CLOUD_IMAGE:-iiot-cloud:local}\n");
        WriteWork("deployments/mosquitto/acl", "user iiot-cloud\n");
        WriteWork("deployments/keycloak/iiot-realm.json", "{}\n");
        WriteWork("src/Cloud/Data/Migrations/0001_Initial.cs", "// initial\n");
        WriteWork("src/Cloud/Program.cs", "// version A\n");
        ShaA = Commit("A");

        Git(_root, "clone", "--quiet", Origin, "deploy");
        Directory.CreateDirectory(Path.Combine(Deploy, "deployments", ".secrets"));

        Directory.CreateDirectory(Stubs);
        WriteStub("docker", """
            #!/usr/bin/env bash
            echo "$* | IIOT_CLOUD_IMAGE=${IIOT_CLOUD_IMAGE:-}" >> "$STUB_LOG/docker.log"
            case " $* " in
              " pull "*) [ -z "${STUB_PULL_FAIL:-}" ] || exit 1 ;;
              *" up "*) [ -z "${STUB_UP_FAIL:-}" ] || exit 1 ;;
              *" logs "*) echo "CLOUD-LOG-LINE" ;;
              *" pg_dump "*) echo "-- dump"; [ -z "${STUB_DUMP_FAIL:-}" ] || exit 1 ;;
              *" ps "*) echo "$IIOT_CLOUD_IMAGE" ;;
              *" port "*) echo "127.0.0.1:18080" ;;
            esac
            exit 0
            """);
        WriteStub("curl", """
            #!/usr/bin/env bash
            echo "$*" >> "$STUB_LOG/curl.log"
            exit "${STUB_CURL_EXIT:-0}"
            """);
    }

    private string ShaA { get; }

    private string Origin => Path.Combine(_root, "origin.git");

    private string Work => Path.Combine(_root, "work");

    private string Deploy => Path.Combine(_root, "deploy");

    private string Stubs => Path.Combine(_root, "stubs");

    [Fact]
    public void MalformedSha_Refused()
    {
        CommitB();

        AssertRefused(RunScript(["abc; touch pwned", Image, "tip"]), "sha");
    }

    [Fact]
    public void ForeignImageOrTag_Refused()
    {
        var shaB = CommitB();

        AssertRefused(RunScript([shaB, "ghcr.io/attacker/cloud@sha256:" + new string('1', 64), "tip"]), "image");
        AssertRefused(RunScript([shaB, "ghcr.io/tan632000/plcprotocol/cloud:" + shaB, "tip"]), "image");
    }

    [Fact]
    public void ExtraWordInForcedCommand_Refused()
    {
        var shaB = CommitB();

        AssertRefused(RunScript([], forcedCommand: $"{shaB} {Image} tip ; reboot"), "unexpected");
        AssertRefused(RunScript([], forcedCommand: $"{shaB} {Image} tip\nreboot"), "unexpected second line");
        AssertRefused(RunScript([shaB, Image, "tip"], ("IIOT_READY_TIMEOUT", "08")), "IIOT_READY_TIMEOUT");
    }

    [Fact]
    public void MissingSecrets_Refused()
    {
        var shaB = CommitB();
        Directory.Delete(Path.Combine(Deploy, "deployments", ".secrets"));

        AssertRefused(RunScript([shaB, Image, "tip"]), "deployments/.secrets");
    }

    [Fact]
    public void ModifiedTrackedFile_Refused()
    {
        var shaB = CommitB();
        File.AppendAllText(Path.Combine(Deploy, "deployments", "mosquitto", "acl"), "topic write iiot/+/commands\n");

        AssertRefused(RunScript([shaB, Image, "tip"]), "deployments/mosquitto/acl");
    }

    [Fact]
    public void CommitNotOnMain_Refused()
    {
        Git(Work, "checkout", "--quiet", "-b", "feature");
        WriteWork("src/Cloud/Program.cs", "// unreviewed\n");
        Git(Work, "-c", "user.name=t", "-c", "user.email=t@t", "-c", "commit.gpgsign=false", "commit", "--quiet", "-am", "feature");
        Git(Work, "push", "--quiet", "origin", "feature");
        var feature = Git(Work, "rev-parse", "HEAD");

        AssertRefused(RunScript([feature, Image, "rollback"]), "origin/main");
    }

    [Fact]
    public void TargetWithoutImageSwitch_Refused()
    {
        var shaB = CommitB(work => WriteWork("deployments/compose.yaml", "services:\n  cloud:\n    build: .\n"));

        AssertRefused(RunScript([shaB, Image, "tip"]), "IIOT_CLOUD_IMAGE");
    }

    [Fact]
    public void BrokerChange_RefusedAsManualUpgrade()
    {
        var shaB = CommitB(work => WriteWork("deployments/mosquitto/acl", "user iiot-cloud\ntopic write iiot/+/commands\n"));

        AssertRefused(RunScript([shaB, Image, "tip"]), "manual upgrade");
    }

    [Fact]
    public void TipOlderThanDeployed_SkippedWithoutChanges()
    {
        var shaB = CommitB();
        Git(Deploy, "fetch", "--quiet", "origin", "main");
        Git(Deploy, "checkout", "--quiet", "--detach", shaB);

        var run = RunScript([ShaA, Image, "tip"]);

        Assert.Equal(0, run.ExitCode);
        Assert.Contains("skipped", run.Output, StringComparison.Ordinal);
        Assert.Equal(shaB, Git(Deploy, "rev-parse", "HEAD"));
        Assert.False(File.Exists(StubLog("docker")), "no docker call");
    }

    [Fact]
    public void RollbackToOlderCommit_IsDeployed()
    {
        var shaB = CommitB();
        Git(Deploy, "fetch", "--quiet", "origin", "main");
        Git(Deploy, "checkout", "--quiet", "--detach", shaB);

        var run = RunScript([ShaA, Image, "rollback"]);

        Assert.True(run.ExitCode == 0, run.Output);
        Assert.Equal(ShaA, Git(Deploy, "rev-parse", "HEAD"));
        AssertSwapped(run, ShaA, backup: false);
    }

    [Fact]
    public void BackupFails_NothingChanged()
    {
        var shaB = CommitB(work => WriteWork("src/Cloud/Data/Migrations/0002_Next.cs", "// next\n"));

        var run = RunScript([shaB, Image, "tip"], ("STUB_DUMP_FAIL", "1"));

        Assert.Equal(1, run.ExitCode);
        Assert.Contains("backup failed", run.Output, StringComparison.Ordinal);
        Assert.Equal(ShaA, Git(Deploy, "rev-parse", "HEAD"));
        Assert.Empty(Directory.GetFiles(Path.Combine(Deploy, "deployments", ".backups"), "*.sql"));
        Assert.DoesNotContain(DockerCalls(), c => c.Contains(" up ", StringComparison.Ordinal));
    }

    [Fact]
    public void PullFails_NothingChanged()
    {
        var shaB = CommitB();

        var run = RunScript([shaB, Image, "tip"], ("STUB_PULL_FAIL", "1"));

        Assert.Equal(1, run.ExitCode);
        Assert.Equal(ShaA, Git(Deploy, "rev-parse", "HEAD"));
        Assert.Equal([$"pull {Image} | IIOT_CLOUD_IMAGE="], DockerCalls().Select(c => c.Replace(" --quiet", "", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData("up-fails")]
    [InlineData("never-ready")]
    public void StartFailure_LogsOnlyToPrivateFile(string kind)
    {
        var shaB = CommitB();
        (string, string)[] env = kind == "up-fails"
            ? [("STUB_UP_FAIL", "1")]
            : [("STUB_CURL_EXIT", "7"), ("IIOT_READY_TIMEOUT", "4")];

        // A log from an earlier failed run of the same commit is replaced, not appended to.
        var logDir = Directory.CreateDirectory(Path.Combine(Deploy, "deployments", ".deploy-logs")).FullName;
        File.WriteAllText(Path.Combine(logDir, shaB + ".log"), "stale\n");

        var run = RunScript([shaB, Image, "tip"], env);

        Assert.Equal(1, run.ExitCode);
        Assert.DoesNotContain("CLOUD-LOG-LINE", run.Output, StringComparison.Ordinal);
        var logFile = Path.Combine(Deploy, "deployments", ".deploy-logs", shaB + ".log");
        Assert.DoesNotContain("stale", File.ReadAllText(logFile), StringComparison.Ordinal);
        Assert.Contains("CLOUD-LOG-LINE", File.ReadAllText(logFile), StringComparison.Ordinal);
        Assert.Contains($".deploy-logs/{shaB}.log", run.Output, StringComparison.Ordinal);
        AssertPrivate(logFile);
        if (kind == "never-ready")
        {
            Assert.True(File.Exists(StubLog("curl")), "the health check went through the curl stub");
        }
    }

    [Fact]
    public void Ready_SwapsOnlyTheCloud()
    {
        var shaB = CommitB();

        var run = RunScript([shaB, Image, "tip"]);

        Assert.True(run.ExitCode == 0, run.Output);
        Assert.Equal(shaB, Git(Deploy, "rev-parse", "HEAD"));
        AssertSwapped(run, shaB, backup: false);
    }

    [Fact]
    public void ReadyWithMigration_BacksUpBeforeTheSwap()
    {
        var shaB = CommitB(work => WriteWork("src/Cloud/Data/Migrations/0002_Next.cs", "// next\n"));

        var run = RunScript([shaB, Image, "tip"]);

        Assert.True(run.ExitCode == 0, run.Output);
        AssertSwapped(run, shaB, backup: true);
        var backup = Assert.Single(Directory.GetFiles(Path.Combine(Deploy, "deployments", ".backups"), "*.sql"));
        Assert.Contains(ShaA[..12], Path.GetFileName(backup), StringComparison.Ordinal);
        Assert.Contains("-- dump", File.ReadAllText(backup), StringComparison.Ordinal);
        AssertPrivate(backup);
    }

    [Fact]
    public void ForcedCommandInput_DeploysLikeArguments()
    {
        var shaB = CommitB();

        var run = RunScript([], forcedCommand: $"{shaB} {Image} tip");

        Assert.True(run.ExitCode == 0, run.Output);
        Assert.Equal(shaB, Git(Deploy, "rev-parse", "HEAD"));
        AssertSwapped(run, shaB, backup: false);
    }

    public void Dispose()
    {
        try
        {
            // The repository under test is never touched.
            Assert.Equal(_repoHeadBefore, Git(Repo.Root, "rev-parse", "HEAD"));
        }
        finally
        {
            DeleteRoot();
        }
    }

    private void DeleteRoot()
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal); // git object files are read-only on Windows
            }

            Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Left for the OS temp cleanup.
        }
    }

    private void AssertSwapped((int ExitCode, string Output) run, string sha, bool backup)
    {
        var calls = DockerCalls();
        var pull = calls.FindIndex(c => c.StartsWith($"pull ", StringComparison.Ordinal) && c.Contains(Image, StringComparison.Ordinal));
        var dump = calls.FindIndex(c => c.Contains("exec -T postgres pg_dump -U iiot iiot", StringComparison.Ordinal));
        var up = calls.IndexOf($"compose -f deployments/compose.yaml up -d --no-deps --no-build --wait cloud | IIOT_CLOUD_IMAGE={Image}");
        Assert.True(pull >= 0 && up > pull, string.Join('\n', calls));
        Assert.Single(calls, c => c.Contains(" up ", StringComparison.Ordinal) || c.Contains(" restart", StringComparison.Ordinal) || c.Contains(" down", StringComparison.Ordinal));
        Assert.Equal(backup, dump >= 0);
        Assert.True(!backup || (dump > pull && dump < up), "the backup runs after the pull and before the swap");
        Assert.Contains(calls, c => c.StartsWith($"tag {Image} iiot-cloud:local", StringComparison.Ordinal));
        Assert.Contains(calls, c => c.StartsWith("image prune -af --filter until=240h", StringComparison.Ordinal));
        Assert.Contains("http://127.0.0.1:18080/health/ready", File.ReadAllText(StubLog("curl")), StringComparison.Ordinal); // the port compose published
        Assert.Contains($"deployed {sha}", run.Output, StringComparison.Ordinal);
    }

    private void AssertRefused((int ExitCode, string Output) run, string reason)
    {
        Assert.True(run.ExitCode == 2, run.Output);
        Assert.Contains(reason, run.Output, StringComparison.Ordinal);
        Assert.Equal(ShaA, Git(Deploy, "rev-parse", "HEAD"));
        Assert.False(File.Exists(StubLog("docker")), "no pull, backup or container command");
    }

    private static void AssertPrivate(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
        }
    }

    /// <summary>Commit B on main (by default a Cloud source change only), pushed to the origin.</summary>
    private string CommitB(Action<string>? change = null)
    {
        (change ?? (_ => WriteWork("src/Cloud/Program.cs", "// version B\n")))(Work);
        return Commit("B");
    }

    private string Commit(string message)
    {
        Git(Work, "add", "-A");
        Git(Work, "-c", "user.name=t", "-c", "user.email=t@t", "-c", "commit.gpgsign=false", "commit", "--quiet", "-m", message);
        Git(Work, "push", "--quiet", "origin", "HEAD:main");
        return Git(Work, "rev-parse", "HEAD");
    }

    private void WriteWork(string relativePath, string content)
    {
        var path = Path.Combine(Work, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content.Replace("\r\n", "\n", StringComparison.Ordinal));
    }

    private void WriteStub(string name, string content)
    {
        var path = Path.Combine(Stubs, name);
        File.WriteAllText(path, content.Replace("\r\n", "\n", StringComparison.Ordinal) + "\n");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    private string StubLog(string name) => Path.Combine(Stubs, name + ".log");

    private List<string> DockerCalls() =>
        File.Exists(StubLog("docker")) ? [.. File.ReadAllLines(StubLog("docker"))] : [];

    /// <summary>
    /// The deploy clone's script through bash; the stubs are put first on PATH inside bash, because Git Bash puts its own
    /// tools (including curl) ahead of the caller's PATH.
    /// </summary>
    private (int ExitCode, string Output) RunScript(string[] arguments, params (string Name, string Value)[] environment) =>
        RunScript(arguments, forcedCommand: null, environment);

    private (int ExitCode, string Output) RunScript(string[] arguments, string? forcedCommand, params (string Name, string Value)[] environment)
    {
        var bash = Environment.GetEnvironmentVariable("IIOT_BASH")
                   ?? (OperatingSystem.IsWindows()
                       ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Git", "bin", "bash.exe")
                       : "bash");
        var start = new ProcessStartInfo(bash)
        {
            WorkingDirectory = _root, // not the deploy clone: the script must find its repository itself
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add("PATH=\"$STUBS:$PATH\"; exec bash \"$0\" \"$@\"");
        start.ArgumentList.Add(PosixPath(Path.Combine(Deploy, "deployments", "scripts", "remote-deploy.sh")));
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        start.Environment["STUBS"] = PosixPath(Stubs);
        start.Environment["STUB_LOG"] = PosixPath(Stubs);
        if (forcedCommand is not null)
        {
            start.Environment["SSH_ORIGINAL_COMMAND"] = forcedCommand;
        }

        foreach (var (name, value) in environment)
        {
            start.Environment[name] = value;
        }

        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        Assert.True(process.WaitForExit(TimeSpan.FromMinutes(1)), "remote-deploy.sh did not finish");
        return (process.ExitCode, stdout.Result + stderr.Result);
    }

    /// <summary>A path bash can put on PATH (no drive colon on Windows).</summary>
    private static string PosixPath(string path) =>
        OperatingSystem.IsWindows() && path.Length > 2 && path[1] == ':'
            ? "/" + char.ToLowerInvariant(path[0]) + path[2..].Replace('\\', '/')
            : path;

    private static string Git(string workingDirectory, params string[] arguments)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        Assert.True(process.WaitForExit(TimeSpan.FromMinutes(1)), "git did not finish");
        Assert.True(process.ExitCode == 0, $"git {string.Join(' ', arguments)}: {stderr.Result}");
        return stdout.Result.Trim();
    }
}
