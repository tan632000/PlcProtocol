using System.Collections.Concurrent;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using IndustrialIot.Edge.Settings;
using IndustrialIot.Edge.Tests.Fakes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace IndustrialIot.Edge.Tests;

/// <summary>
/// The settings password is stored only as a PBKDF2 hash beside the settings file the Edge uses, set from the
/// command line without echo, and verified with a lockout after repeated failures.
/// </summary>
public sealed class SettingsPasswordTests : IDisposable
{
    private const string Good = "correct horse battery";
    private readonly string _directory = Directory.CreateTempSubdirectory("settings-password-").FullName;
    private readonly LogCapture _logs = new();

    public void Dispose() => EdgeTestHost.DeleteDirectory(_directory);

    [Fact]
    public void Verify_AcceptsOnlyTheStoredPassword()
    {
        SettingsPassword.Store(PasswordPath(), Good);
        var verifier = Verifier(new FakeTimeProvider());

        Assert.Equal(PasswordVerdict.Ok, verifier.Verify(PasswordPath(), Good));
        Assert.Equal(PasswordVerdict.Wrong, verifier.Verify(PasswordPath(), Good + "x"));
        Assert.Equal(PasswordVerdict.Wrong, verifier.Verify(PasswordPath(), null));
    }

    [Fact]
    public void StoredFile_HoldsAPbkdf2HashOnly()
    {
        SettingsPassword.Store(PasswordPath(), Good);

        var text = File.ReadAllText(PasswordPath());
        using var json = JsonDocument.Parse(text);
        var root = json.RootElement;

        Assert.DoesNotContain(Good, text);
        Assert.Equal("PBKDF2-SHA256", root.GetProperty("Algorithm").GetString());
        Assert.Equal(600_000, root.GetProperty("Iterations").GetInt32());
        Assert.Equal(16, Convert.FromBase64String(root.GetProperty("Salt").GetString()!).Length);
        var hash = Convert.FromBase64String(root.GetProperty("Hash").GetString()!);
        Assert.NotEqual(SHA256.HashData(Encoding.UTF8.GetBytes(Good)), hash);
        Assert.Equal(
            Rfc2898DeriveBytes.Pbkdf2(Good, Convert.FromBase64String(root.GetProperty("Salt").GetString()!), 600_000, HashAlgorithmName.SHA256, 32),
            hash);
    }

    [Fact]
    public void MissingFile_IsNotSet()
    {
        Assert.Equal(PasswordVerdict.NotSet, Verifier(new FakeTimeProvider()).Verify(PasswordPath(), Good));
    }

    [Fact]
    public void FiveFailures_LockForSixtySeconds_AndAttemptsDuringTheLockDoNotExtendIt()
    {
        SettingsPassword.Store(PasswordPath(), Good);
        var clock = new FakeTimeProvider();
        var verifier = Verifier(clock);

        for (var i = 0; i < 5; i++)
        {
            Assert.Equal(PasswordVerdict.Wrong, verifier.Verify(PasswordPath(), "wrong-" + i));
        }

        Assert.Equal(PasswordVerdict.LockedOut, verifier.Verify(PasswordPath(), Good));
        clock.Advance(TimeSpan.FromSeconds(59));
        Assert.Equal(PasswordVerdict.LockedOut, verifier.Verify(PasswordPath(), "wrong-again"));
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(PasswordVerdict.Ok, verifier.Verify(PasswordPath(), Good));
        Assert.Equal(1, _logs.Count("locked"));
    }

    [Fact]
    public void Command_RefusesAPasswordShorterThan12()
    {
        var console = new FakeConsole("eleven-char", "eleven-char");

        Assert.Equal(1, Run([], console));
        Assert.False(File.Exists(DevPasswordPath()));
        Assert.Contains(console.Errors, e => e.Contains("12", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("--password")]
    [InlineData("hunter2-hunter2")]
    [InlineData("--contentRoot")]
    public void Command_RefusesAnyOtherArgument_WithUsage(string argument)
    {
        var console = new FakeConsole(Good, Good);

        Assert.Equal(2, Run([argument], console));
        Assert.Equal(0, console.Prompts);
        Assert.Contains(console.Errors, e => e.Contains("Usage", StringComparison.Ordinal));
    }

    [Fact]
    public void Command_RefusesMismatchedPrompts()
    {
        var console = new FakeConsole(Good, Good + "!");

        Assert.Equal(1, Run([], console));
        Assert.False(File.Exists(DevPasswordPath()));
    }

    [Fact]
    public void Command_StoresTheHash_BesideTheSettingsFileOfThatContentRoot()
    {
        var console = new FakeConsole(Good, Good);

        Assert.Equal(0, Run([], console));

        Assert.Equal(PasswordVerdict.Ok, Verifier(new FakeTimeProvider()).Verify(DevPasswordPath(), Good));
        Assert.DoesNotContain(console.Output.Concat(console.Errors), line => line.Contains(Good, StringComparison.Ordinal));
    }

    [Fact]
    public void Command_WithService_UsesTheServiceBufferPath()
    {
        var serviceData = Path.Combine(_directory, "service-data");
        var console = new FakeConsole(Good, Good);

        Assert.Equal(0, SettingsPasswordCommand.Run(["--service"], console, () => Path.Combine(serviceData, "edge.db")));

        Assert.True(File.Exists(Path.Combine(serviceData, "edge.settings-password.json")));
        Assert.False(File.Exists(DevPasswordPath()));
    }

    [Fact]
    public void Command_WithoutService_IgnoresTheService_AndFollowsTheContentRoot()
    {
        // The FX5U lab profile: content root lab/fx5u, buffer ../../data/fx5u-lab.db.
        var lab = Path.Combine(_directory, "src", "Edge", "lab", "fx5u");
        Directory.CreateDirectory(lab);
        File.WriteAllText(Path.Combine(lab, "appsettings.json"), """{"Edge":{"Buffer":{"Path":"../../data/fx5u-lab.db"}}}""");
        var console = new FakeConsole(Good, Good);

        Assert.Equal(0, SettingsPasswordCommand.Run(["--contentRoot", lab], console, () => Path.Combine(_directory, "service", "edge.db")));

        Assert.True(File.Exists(Path.Combine(_directory, "src", "Edge", "data", "fx5u-lab.settings-password.json")));
        Assert.False(Directory.Exists(Path.Combine(_directory, "service")));
    }

    [Fact]
    public void Command_WithService_WhenTheServiceHasNoBufferPath_Fails()
    {
        var console = new FakeConsole(Good, Good);

        Assert.Equal(1, SettingsPasswordCommand.Run(["--service"], console, () => null));
        Assert.Equal(0, console.Prompts);
    }

    [Fact]
    public void Command_RefusesADirectoryOtherUsersCanWrite()
    {
        var shared = Path.Combine(_directory, "shared");
        Directory.CreateDirectory(shared);
        MakeWritableByOthers(shared);
        var console = new FakeConsole(Good, Good);

        Assert.Equal(1, SettingsPasswordCommand.Run(["--service"], console, () => Path.Combine(shared, "edge.db")));

        Assert.False(File.Exists(Path.Combine(shared, "edge.settings-password.json")));
        Assert.Contains(console.Errors, e => e.Contains("other users", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("""{"Algorithm":"PBKDF2-SHA256","Iterations":600000,"Hash":"AAAA"}""")]
    [InlineData("""{"Algorithm":"PBKDF2-SHA256","Iterations":600000,"Salt":"AAAAAAAAAAAAAAAAAAAAAA==","Hash":""}""")]
    [InlineData("""{"Algorithm":"PBKDF2-SHA256","Iterations":1,"Salt":"AAAAAAAAAAAAAAAAAAAAAA==","Hash":"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA="}""")]
    [InlineData("""{"Algorithm":"PBKDF2-SHA256","Iterations":2147483647,"Salt":"AAAAAAAAAAAAAAAAAAAAAA==","Hash":"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA="}""")]
    [InlineData("""{"Algorithm":"MD5","Iterations":600000,"Salt":"AAAAAAAAAAAAAAAAAAAAAA==","Hash":"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA="}""")]
    [InlineData("""{"Algorithm":"PBKDF2-SHA256","Iterations":600000,"Salt":"not base64!","Hash":"AAAA"}""")]
    [InlineData("""not json""")]
    public void DamagedOrTamperedFile_IsNotSet_WithoutThrowingOrStalling(string content)
    {
        File.WriteAllText(PasswordPath(), content);
        var clock = System.Diagnostics.Stopwatch.StartNew();

        Assert.Equal(PasswordVerdict.NotSet, Verifier(new FakeTimeProvider()).Verify(PasswordPath(), Good));

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(2), $"verify took {clock.Elapsed}");
        Assert.Equal(1, _logs.Count("not valid"));
    }

    [Fact]
    public void Command_TurnsAccessFailuresIntoExit1()
    {
        var console = new FakeConsole(Good, Good);

        Assert.Equal(1, SettingsPasswordCommand.Run(["--service"], console, () => throw new System.Security.SecurityException("Requested registry access is not allowed.")));
        Assert.Contains(console.Errors, e => e.Contains("elevated", StringComparison.Ordinal));
    }

    [Fact]
    public void Command_RefusesServiceTogetherWithContentRoot()
    {
        var console = new FakeConsole(Good, Good);

        Assert.Equal(2, SettingsPasswordCommand.Run(["--service", "--contentRoot", _directory], console, () => Path.Combine(_directory, "edge.db")));
        Assert.Equal(0, console.Prompts);
    }

    [Fact]
    public void RefusedAttempt_LeavesNoNewDirectoryBehind()
    {
        var console = new FakeConsole(Good, Good + "!");
        var target = Path.Combine(_directory, "new-data");

        Assert.Equal(1, SettingsPasswordCommand.Run(["--service"], console, () => Path.Combine(target, "edge.db")));
        Assert.False(Directory.Exists(target));
    }

    [Fact]
    public void Logs_NeverContainThePassword()
    {
        SettingsPassword.Store(PasswordPath(), Good);
        var verifier = Verifier(new FakeTimeProvider());
        const string Attempt = "not-the-password-123";

        for (var i = 0; i < 6; i++)
        {
            verifier.Verify(PasswordPath(), Attempt);
        }

        verifier.Verify(PasswordPath(), Good);

        Assert.NotEmpty(_logs.Entries);
        Assert.DoesNotContain(_logs.Entries, e => e.Contains(Attempt, StringComparison.Ordinal) || e.Contains(Good, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Verifier_IsTheSameInstanceInEveryInnerHost_SoTheLockoutSurvivesApplies()
    {
        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings { ContentRootPath = _directory });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Edge:EdgeId"] = "password-test",
            ["Edge:Plc:Host"] = "127.0.0.1",
            ["Edge:Tags:0:TagId"] = "A.One",
            ["Edge:Tags:0:Address"] = "DB1.DBW0",
            ["Edge:Tags:0:DataType"] = "Int",
            ["Edge:Buffer:Path"] = "data/edge.db",
            ["Edge:Mqtt:Host"] = "no-broker.invalid",
            ["Edge:Mqtt:Username"] = "password-test",
            ["Edge:Mqtt:Password"] = "not-a-secret-test",
            ["Edge:Monitor:Enabled"] = "false",
        });
        builder.Services.AddEdgeSupervisor(options => options.ConfigureInner = services =>
        {
            foreach (var descriptor in services.Where(d => d.ServiceType == typeof(IHostedService)).ToList())
            {
                services.Remove(descriptor);
            }
        });
        using var outer = builder.Build();
        await outer.StartAsync();
        var supervisor = outer.Services.GetRequiredService<EdgeSupervisor>();
        var restarter = outer.Services.GetRequiredService<EdgeRestarter>();
        var before = supervisor.Inner!.Services.GetRequiredService<SettingsPassword>();

        Assert.True(restarter.TryRequest(Encoding.UTF8.GetBytes(
            """{"SchemaVersion":1,"Edge":{"Plc":{"Host":"127.0.0.1"},"Tags":[{"TagId":"B.Two","Address":"DB1.DBW2","DataType":"Int"}]}}"""), out var applyId));
        restarter.Release(applyId);
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (restarter.Status.State == ApplyState.Applying && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }

        Assert.Equal(ApplyState.Applied, restarter.Status.State);
        Assert.Same(before, supervisor.Inner!.Services.GetRequiredService<SettingsPassword>());
        Assert.Same(outer.Services.GetRequiredService<SettingsPassword>(), before);
        await outer.StopAsync();
    }

    private int Run(string[] args, FakeConsole console)
    {
        // A dev content root with its own appsettings.json (buffer data/edge.db), as `dotnet run` uses.
        File.WriteAllText(Path.Combine(_directory, "appsettings.json"), """{"Edge":{"Buffer":{"Path":"data/edge.db"}}}""");
        return SettingsPasswordCommand.Run([.. args.Prepend(_directory).Prepend("--contentRoot")], console, () => null);
    }

    private SettingsPassword Verifier(TimeProvider clock) =>
        new(clock, new Microsoft.Extensions.Logging.Logger<SettingsPassword>(new LoggerFactory([_logs])));

    private string PasswordPath() => Path.Combine(_directory, "edge.settings-password.json");

    private string DevPasswordPath() => Path.Combine(_directory, "data", "edge.settings-password.json");

    private static void MakeWritableByOthers(string directory)
    {
        if (OperatingSystem.IsWindows())
        {
            var info = new DirectoryInfo(directory);
            var security = info.GetAccessControl();
            security.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
                FileSystemRights.Write,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow));
            info.SetAccessControl(security);
        }
        else
        {
            File.SetUnixFileMode(directory, File.GetUnixFileMode(directory) | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute);
        }
    }

    private sealed class FakeConsole(params string[] secrets) : ISettingsPasswordConsole
    {
        private readonly Queue<string> _secrets = new(secrets);

        public int Prompts { get; private set; }

        public List<string> Output { get; } = [];

        public List<string> Errors { get; } = [];

        public string? ReadSecret(string prompt)
        {
            Prompts++;
            Assert.DoesNotContain(Good, prompt);
            return _secrets.Count > 0 ? _secrets.Dequeue() : null;
        }

        public void WriteLine(string line) => Output.Add(line);

        public void WriteError(string line) => Errors.Add(line);
    }

    private sealed class LogCapture : ILoggerProvider
    {
        private readonly ConcurrentQueue<string> _entries = new();

        public IReadOnlyList<string> Entries => [.. _entries];

        public int Count(string fragment) => _entries.Count(e => e.Contains(fragment, StringComparison.OrdinalIgnoreCase));

        public ILogger CreateLogger(string categoryName) => new Logger(_entries);

        public void Dispose()
        {
        }

        private sealed class Logger(ConcurrentQueue<string> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                entries.Enqueue(formatter(state, exception));
        }
    }
}
