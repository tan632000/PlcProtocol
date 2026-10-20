using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Configurations;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Images;
using MQTTnet;
using MQTTnet.Formatter;
using MQTTnet.Protocol;

namespace IndustrialIot.Deployment.Tests;

/// <summary>
/// The trial broker configuration (deployments/mosquitto) on the real Mosquitto image, with secrets made by the
/// real deployments/scripts/dev-secrets.sh. Each identity logs in as itself, like the Edge and the Cloud do.
/// </summary>
public class BrokerTests(BrokerFixture broker) : IClassFixture<BrokerFixture>
{
    private const string Cloud = "iiot-cloud";
    private const string Line1 = "line1-edge01";
    private const string Line2 = "line2-edge01";

    [Fact]
    public async Task TlsAndCredentialsRequired()
    {
        // Static: anonymous access stays off even though use_username_as_clientid already refuses clients without a
        // username (so allowing it would not change behaviour today — it is the second lock).
        var config = File.ReadAllLines(Path.Combine(Repo.Root, "deployments", "mosquitto", "mosquitto.conf")).Select(l => l.Trim()).ToList();
        Assert.Contains("allow_anonymous false", config);
        Assert.DoesNotContain(config, l => l.StartsWith("allow_anonymous true", StringComparison.Ordinal));

        // No other listener exists: in particular no plain-text 1883 (opened implicitly by misplaced listener options).
        Assert.DoesNotContain("port 1883", await broker.LogsAsync(), StringComparison.Ordinal);
        Assert.Contains("listen socket on port 8883", await broker.LogsAsync(), StringComparison.Ordinal);

        // Plain MQTT on the TLS port never gets a session.
        await Assert.ThrowsAnyAsync<Exception>(() => MqttClientHelper.ConnectAsync(broker, Line1, broker.Password(Line1), tls: false));

        Assert.NotEqual(MqttClientConnectResultCode.Success, await MqttClientHelper.TryConnectAsync(broker, Line1, "wrong-password"));
        Assert.NotEqual(MqttClientConnectResultCode.Success, await MqttClientHelper.TryConnectAsync(broker, username: null, password: null));
        Assert.Equal(MqttClientConnectResultCode.Success, await MqttClientHelper.TryConnectAsync(broker, Line1, broker.Password(Line1)));
    }

    [Fact]
    public async Task ClientIdBoundToUsername()
    {
        await using var cloud = await MqttClientHelper.ConnectAsync(broker, Cloud, broker.Password(Cloud));
        await cloud.SubscribeAsync("iiot/+/telemetry");

        await using var first = await MqttClientHelper.ConnectAsync(broker, Line1, broker.Password(Line1));

        // line2 claiming line1's client id gets line2's identity: it neither takes over line1's session (which an
        // unbound client id would allow) nor speaks as line1.
        await using var impostor = await MqttClientHelper.ConnectAsync(broker, Line2, broker.Password(Line2), clientId: Line1);
        await impostor.PublishAsync($"iiot/{Line1}/telemetry", "spoofed");
        await impostor.PublishAsync($"iiot/{Line2}/telemetry", "marker");
        await cloud.WaitForAsync("marker");
        Assert.True(first.IsConnected);
        Assert.DoesNotContain(cloud.Payloads, p => p == "spoofed");

        // A second line1 login under a made-up client id is still line1's session, so it replaces the first.
        await using var second = await MqttClientHelper.ConnectAsync(broker, Line1, broker.Password(Line1), clientId: "yet-another");
        await MqttClientHelper.WaitUntilAsync(() => !first.IsConnected, "first line1 connection taken over");
    }

    [Fact]
    public async Task EdgeAcl_OwnTopicsOnly()
    {
        await using var cloud = await MqttClientHelper.ConnectAsync(broker, Cloud, broker.Password(Cloud));
        await cloud.SubscribeAsync("iiot/+/telemetry", "iiot/+/command-results");
        await using var line2 = await MqttClientHelper.ConnectAsync(broker, Line2, broker.Password(Line2));
        await line2.SubscribeAsync($"iiot/{Line2}/commands");
        await using var line1 = await MqttClientHelper.ConnectAsync(broker, Line1, broker.Password(Line1));
        await line1.SubscribeAsync($"iiot/{Line1}/commands", $"iiot/{Line2}/commands");

        await line1.PublishAsync($"iiot/{Line1}/telemetry", "own-telemetry");
        await line1.PublishAsync($"iiot/{Line1}/command-results", "own-result");
        await line1.PublishAsync($"iiot/{Line2}/telemetry", "foreign-telemetry");
        await line1.PublishAsync($"iiot/{Line1}/commands", "self-command");
        await cloud.PublishAsync($"iiot/{Line2}/commands", "command-for-line2");

        await line2.WaitForAsync("command-for-line2");
        await line1.PublishAsync($"iiot/{Line1}/telemetry", "marker");
        await cloud.WaitForAsync("marker");
        await Task.Delay(300);

        Assert.Contains("own-telemetry", cloud.Payloads);
        Assert.Contains("own-result", cloud.Payloads);
        Assert.DoesNotContain("foreign-telemetry", cloud.Payloads);
        Assert.Empty(line1.Payloads); // neither its own "command" nor line2's command
    }

    [Fact]
    public async Task EdgeAcl_WildcardSharedSysAndWillDenied()
    {
        await using var cloud = await MqttClientHelper.ConnectAsync(broker, Cloud, broker.Password(Cloud));
        await cloud.SubscribeAsync("iiot/+/telemetry");

        // A retained message line1 must not be able to read, stored before it subscribes.
        await using (var line2 = await MqttClientHelper.ConnectAsync(broker, Line2, broker.Password(Line2)))
        {
            await line2.PublishAsync($"iiot/{Line2}/telemetry", "retained-line2", retain: true);
            await cloud.WaitForAsync("retained-line2");
        }

        try
        {
            // Control: a will on the identity's own topic is delivered, so a missing forged will means "denied".
            await using (var ownWill = await MqttClientHelper.ConnectAsync(broker, Line2, broker.Password(Line2),
                             configure: o => o.WithWillTopic($"iiot/{Line2}/telemetry").WithWillPayload("own-will")
                                 .WithWillQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)))
            {
                await ownWill.DisconnectWithWillAsync();
            }

            await cloud.WaitForAsync("own-will");

            await using var line1 = await MqttClientHelper.ConnectAsync(broker, Line1, broker.Password(Line1));
            await line1.SubscribeAsync("#", "iiot/+/commands", "$share/g/iiot/+/commands", "$SYS/#");
            await cloud.PublishAsync($"iiot/{Line2}/commands", "command-for-line2");

            // A will aimed at another identity's topic: the broker refuses the connection as not authorized,
            // or accepts it and never delivers the will. Any other failure is not evidence and fails the probe.
            try
            {
                await using var forged = await MqttClientHelper.ConnectAsync(broker, Line2, broker.Password(Line2),
                    configure: o => o.WithWillTopic($"iiot/{Line1}/telemetry").WithWillPayload("forged-will")
                        .WithWillQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce));
                await forged.DisconnectWithWillAsync();
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("NotAuthorized", StringComparison.Ordinal))
            {
                // Refused at CONNECT because of the will topic.
            }

            await line1.PublishAsync($"iiot/{Line1}/telemetry", "marker");
            await cloud.WaitForAsync("marker");
            await Task.Delay(1000);

            Assert.Empty(line1.Topics); // nothing through #, +, $share or $SYS — not even retained messages
            Assert.DoesNotContain("forged-will", cloud.Payloads);
        }
        finally
        {
            // Remove the retained message even when the probe fails, so it cannot leak into other probes.
            await using var cleaner = await MqttClientHelper.ConnectAsync(broker, Line2, broker.Password(Line2));
            await cleaner.PublishAsync($"iiot/{Line2}/telemetry", "", retain: true);
        }
    }

    [Fact]
    public async Task CloudAcl_CommandsOnly()
    {
        await using var line1 = await MqttClientHelper.ConnectAsync(broker, Line1, broker.Password(Line1));
        await line1.SubscribeAsync($"iiot/{Line1}/commands");
        await using var cloud = await MqttClientHelper.ConnectAsync(broker, Cloud, broker.Password(Cloud));
        await cloud.SubscribeAsync("iiot/+/telemetry", "iiot/+/command-results");

        await cloud.PublishAsync($"iiot/{Line1}/commands", "command");
        await line1.WaitForAsync("command");

        // The Cloud cannot pose as an Edge.
        await cloud.PublishAsync($"iiot/{Line1}/telemetry", "cloud-telemetry");
        await cloud.PublishAsync($"iiot/{Line1}/command-results", "cloud-result");
        await line1.PublishAsync($"iiot/{Line1}/telemetry", "marker");
        await cloud.WaitForAsync("marker");
        await Task.Delay(300);

        Assert.Equal(["marker"], cloud.Payloads);
    }

    [Fact]
    public async Task TelemetryAcks_OnlyCloudWrites_OnlyOwnerReads()
    {
        await using var line1 = await MqttClientHelper.ConnectAsync(broker, Line1, broker.Password(Line1));
        await line1.SubscribeAsync($"iiot/{Line1}/telemetry-acks");
        await using var line2 = await MqttClientHelper.ConnectAsync(broker, Line2, broker.Password(Line2));
        await line2.SubscribeAsync($"iiot/{Line1}/telemetry-acks", "iiot/+/telemetry-acks");
        await using var cloud = await MqttClientHelper.ConnectAsync(broker, Cloud, broker.Password(Cloud));

        await cloud.PublishAsync($"iiot/{Line1}/telemetry-acks", "ack-for-line1");
        await line1.WaitForAsync("ack-for-line1");

        // An ack deletes buffered data, so an Edge must not be able to forge one, not even for itself.
        await line1.PublishAsync($"iiot/{Line1}/telemetry-acks", "forged-own-ack");
        await line2.PublishAsync($"iiot/{Line1}/telemetry-acks", "forged-foreign-ack");
        await cloud.PublishAsync($"iiot/{Line1}/telemetry-acks", "marker");
        await line1.WaitForAsync("marker");
        await Task.Delay(300);

        Assert.Equal(["ack-for-line1", "marker"], line1.Payloads);
        Assert.Empty(line2.Payloads); // another Edge's acks are unreadable, directly or through +
    }

    [Fact]
    public async Task PersistentSession_RedeliversUnacked()
    {
        await MqttClientHelper.ResetSessionAsync(broker, Line1);

        // Received but not acknowledged: the broker sends it again on the next session.
        await using (var edge = await MqttClientHelper.ConnectAsync(broker, Line1, broker.Password(Line1), cleanStart: false, acknowledge: false))
        {
            await edge.SubscribeAsync($"iiot/{Line1}/commands");
            await using var cloud = await MqttClientHelper.ConnectAsync(broker, Cloud, broker.Password(Cloud));
            await cloud.PublishAsync($"iiot/{Line1}/commands", "unacked");
            await edge.WaitForAsync("unacked");
        }

        await using (var edge = await MqttClientHelper.ConnectAsync(broker, Line1, broker.Password(Line1), cleanStart: false))
        {
            await edge.WaitForAsync("unacked");
        }

        // Queued while the Edge is offline, across a broker restart (persistence).
        await using (var cloud = await MqttClientHelper.ConnectAsync(broker, Cloud, broker.Password(Cloud)))
        {
            await cloud.PublishAsync($"iiot/{Line1}/commands", "queued-across-restart");
        }

        await broker.RestartAsync();
        await using (var edge = await MqttClientHelper.ConnectAsync(broker, Line1, broker.Password(Line1), cleanStart: false))
        {
            await edge.WaitForAsync("queued-across-restart");
        }

        await MqttClientHelper.ResetSessionAsync(broker, Line1);
    }

    [Fact]
    public async Task OversizedPacket_Refused()
    {
        await using var cloud = await MqttClientHelper.ConnectAsync(broker, Cloud, broker.Password(Cloud));
        await cloud.SubscribeAsync("iiot/+/telemetry");

        // MQTT 3.1.1 has no CONNACK packet-size property, so the client really sends it and the broker must refuse it.
        try
        {
            await using var edge = await MqttClientHelper.ConnectAsync(broker, Line1, broker.Password(Line1), protocol: MqttProtocolVersion.V311);
            await edge.PublishAsync($"iiot/{Line1}/telemetry", "big:" + new string('x', 17 * 1024));
        }
        catch (Exception ex) when (ex is not Xunit.Sdk.XunitException)
        {
            // Connection closed by the broker: refused.
        }

        await using (var edge = await MqttClientHelper.ConnectAsync(broker, Line1, broker.Password(Line1)))
        {
            await edge.PublishAsync($"iiot/{Line1}/telemetry", "marker");
        }

        await cloud.WaitForAsync("marker");
        await Task.Delay(300);
        Assert.DoesNotContain(cloud.Payloads, p => p.StartsWith("big:", StringComparison.Ordinal));
    }

    [Fact]
    public void Secrets_ValidatedNotOverwrittenAndIgnored()
    {
        foreach (var invalid in new[] { new[] { "--edge", "iiot-cloud" }, ["--edge", "a+b"], ["--edge", "x/y"], ["--edge", "e1", "--edge", "e1"] })
        {
            using var rejected = SecretsDirectory.Empty();
            var result = SecretsScript.Run(["--out", rejected.Path, .. invalid]);
            Assert.Equal(2, result.ExitCode);
            Assert.False(File.Exists(System.IO.Path.Combine(rejected.Path, "ca.pem")));
        }

        using var secrets = SecretsDirectory.Create("--edge", Line1);
        var before = secrets.Fingerprint();
        Assert.Equal(2, SecretsScript.Run("--out", secrets.Path).ExitCode);
        Assert.Equal(before, secrets.Fingerprint());

        // The script names files, never values.
        foreach (var password in Directory.GetFiles(System.IO.Path.Combine(secrets.Path, "passwords")).Select(File.ReadAllText))
        {
            Assert.DoesNotContain(password, secrets.CreationOutput, StringComparison.Ordinal);
        }

        using var empty = SecretsDirectory.Empty();
        Assert.Equal(2, SecretsScript.Run("--add-edge", Line2, "--out", empty.Path).ExitCode);

        // A folder that already holds anything else is refused untouched (no keys mixed in, nothing deleted).
        using var occupied = SecretsDirectory.Empty();
        Directory.CreateDirectory(occupied.Path);
        File.WriteAllText(occupied.File("keep.txt"), "unrelated");
        Assert.Equal(2, SecretsScript.Run("--out", occupied.Path).ExitCode);
        Assert.Equal(["keep.txt"], Directory.GetFileSystemEntries(occupied.Path).Select(System.IO.Path.GetFileName));

        // A relative --out (as typed in a terminal) works, from any working directory.
        using var parent = SecretsDirectory.Empty();
        Directory.CreateDirectory(parent.Path);
        Assert.Equal(0, SecretsScript.RunIn(parent.Path, "--out", "relative/set", "--edge", Line1).ExitCode);
        Assert.True(Directory.Exists(parent.File("relative/set/mosquitto")));
        Assert.True(File.Exists(parent.File("relative/set/cloud.env")));

        var ignored = Process.Start(new ProcessStartInfo("git", "check-ignore -q deployments/.secrets/ca.key") { WorkingDirectory = Repo.Root })!;
        ignored.WaitForExit();
        Assert.Equal(0, ignored.ExitCode);
    }

    [Fact]
    public void Secrets_CertificateExtensions()
    {
        using var ca = X509CertificateLoader.LoadCertificateFromFile(broker.Secrets.File("ca.pem"));
        var caConstraints = Extension<X509BasicConstraintsExtension>(ca);
        Assert.True(caConstraints.Critical && caConstraints.CertificateAuthority && caConstraints.HasPathLengthConstraint);
        Assert.Equal(0, caConstraints.PathLengthConstraint);
        var caUsage = Extension<X509KeyUsageExtension>(ca);
        Assert.True(caUsage.Critical);
        Assert.Equal(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, caUsage.KeyUsages);

        foreach (var (file, dns, ips) in new[]
                 {
                     ("mosquitto/server.pem", new[] { "mosquitto", "localhost" }, new[] { "127.0.0.1", "192.168.1.50" }),
                     ("keycloak/server.pem", new[] { "keycloak", "localhost" }, Array.Empty<string>()), // --san never reaches Keycloak
                 })
        {
            using var leaf = X509CertificateLoader.LoadCertificateFromFile(broker.Secrets.File(file));
            var constraints = Extension<X509BasicConstraintsExtension>(leaf);
            Assert.True(constraints.Critical && !constraints.CertificateAuthority);
            Assert.Equal(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, Extension<X509KeyUsageExtension>(leaf).KeyUsages);
            Assert.Equal(["1.3.6.1.5.5.7.3.1"], Extension<X509EnhancedKeyUsageExtension>(leaf).EnhancedKeyUsages.Cast<Oid>().Select(o => o.Value));
            var san = Extension<X509SubjectAlternativeNameExtension>(leaf);
            Assert.Equal(dns, san.EnumerateDnsNames());
            Assert.Equal(ips, san.EnumerateIPAddresses().Select(ip => ip.ToString()));

            using var chain = new X509Chain();
            chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            chain.ChainPolicy.CustomTrustStore.Add(ca);
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            Assert.True(chain.Build(leaf), $"{file} is not issued by the dev CA");
        }
    }

    [Fact]
    public async Task Secrets_AddEdge()
    {
        using var secrets = SecretsDirectory.Create("--edge", Line1);
        var caBefore = File.ReadAllBytes(secrets.File("ca.pem"));

        Assert.Equal(0, SecretsScript.Run("--add-edge", "line3-edge01", "--out", secrets.Path).ExitCode);
        Assert.Equal(2, SecretsScript.Run("--add-edge", "line3-edge01", "--out", secrets.Path).ExitCode);
        Assert.Equal(2, SecretsScript.Run("--add-edge", "iiot-cloud", "--out", secrets.Path).ExitCode);

        Assert.Equal(caBefore, File.ReadAllBytes(secrets.File("ca.pem")));
        Assert.Contains("Cloud__Edges__1__EdgeId=line3-edge01", File.ReadAllLines(secrets.File("cloud.env")));

        // The new identity works on a broker started from this set, under the same ACL.
        await using var addedBroker = await BrokerFixture.StartAsync(secrets);
        await using var cloud = await MqttClientHelper.ConnectAsync(addedBroker, Cloud, addedBroker.Password(Cloud));
        await cloud.SubscribeAsync("iiot/+/telemetry");
        await using var line3 = await MqttClientHelper.ConnectAsync(addedBroker, "line3-edge01", addedBroker.Password("line3-edge01"));
        await line3.PublishAsync("iiot/line3-edge01/telemetry", "hello");
        await cloud.WaitForAsync("hello");
    }

    [Fact]
    public void Secrets_FileModeAndOwner()
    {
        string[] keys = ["mosquitto/server.key", "mosquitto/passwd", "keycloak/server.key"];
        if (OperatingSystem.IsWindows())
        {
            // Docker Desktop shows bind-mounted files to every uid; ownership cannot be set or checked here.
            Assert.All(keys, key => Assert.NotEmpty(File.ReadAllBytes(broker.Secrets.File(key))));
            return;
        }

        // Linux: each service's private files belong to the uid its container runs as, readable by nobody else.
        Assert.Equal("1883 600", Stat(broker.Secrets.File("mosquitto/server.key")));
        Assert.Equal("1883 600", Stat(broker.Secrets.File("mosquitto/passwd")));
        Assert.Equal("1000 600", Stat(broker.Secrets.File("keycloak/server.key")));
        Assert.EndsWith(" 600", Stat(broker.Secrets.File("ca.key")), StringComparison.Ordinal);

        // Folders and certificates stay usable by the operator (and --add-edge); only keys and passwd are private.
        Assert.Equal("1883 755", Stat(broker.Secrets.File("mosquitto")));
        Assert.Equal("1000 755", Stat(broker.Secrets.File("keycloak")));
        Assert.Equal("1883 644", Stat(broker.Secrets.File("mosquitto/server.pem")));
    }

    private static string Stat(string path)
    {
        var stat = Process.Start(new ProcessStartInfo("stat", ["-c", "%u %a", path]) { RedirectStandardOutput = true })!;
        var output = stat.StandardOutput.ReadToEnd().Trim();
        stat.WaitForExit();
        return output;
    }

    private static T Extension<T>(X509Certificate2 certificate)
        where T : X509Extension =>
        certificate.Extensions.OfType<T>().Single();
}

/// <summary>Finds the repository root (the folder holding IndustrialIot.sln).</summary>
internal static class Repo
{
    public static string Root { get; } = Find();

    private static string Find()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "IndustrialIot.sln")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("Repository root not found.");
    }
}

/// <summary>Runs deployments/scripts/dev-secrets.sh with Git Bash on Windows (never the WSL launcher) or bash elsewhere.</summary>
internal static class SecretsScript
{
    public static (int ExitCode, string Output) Run(params string[] arguments) => RunIn(Repo.Root, arguments);

    public static (int ExitCode, string Output) RunIn(string workingDirectory, params string[] arguments)
    {
        var bash = Environment.GetEnvironmentVariable("IIOT_BASH")
                   ?? (OperatingSystem.IsWindows()
                       ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Git", "bin", "bash.exe")
                       : "bash");
        var start = new ProcessStartInfo(bash)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add(Path.Combine(Repo.Root, "deployments", "scripts", "dev-secrets.sh").Replace('\\', '/'));
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        Assert.True(process.WaitForExit(TimeSpan.FromMinutes(3)), "dev-secrets.sh did not finish");
        return (process.ExitCode, stdout.Result + stderr.Result);
    }
}

/// <summary>A temp directory of secrets; deleted afterwards, through Docker when the files belong to container uids.</summary>
public sealed class SecretsDirectory : IDisposable
{
    private SecretsDirectory(string path) => Path = path;

    /// <summary>Forward slashes: the path is passed to Git Bash and Docker as well.</summary>
    public string Path { get; }

    public string CreationOutput { get; private set; } = "";

    public static SecretsDirectory Empty() =>
        new(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "iiot-secrets-" + Guid.NewGuid().ToString("N")).Replace('\\', '/'));

    public static SecretsDirectory Create(params string[] arguments)
    {
        var secrets = Empty();
        var (exitCode, output) = SecretsScript.Run(["--out", secrets.Path, .. arguments]);
        if (exitCode != 0)
        {
            secrets.Dispose();
            Assert.Fail("dev-secrets.sh failed: " + output);
        }

        secrets.CreationOutput = output;
        return secrets;
    }

    public string File(string relative) => System.IO.Path.Combine(Path, relative);

    /// <summary>
    /// Name, size and write time of every file, to show nothing changed. Metadata, not content: on Linux the keys
    /// belong to the container uids and the test process may not read them.
    /// </summary>
    public string Fingerprint() =>
        string.Join("\n", Directory.GetFiles(Path, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
            .Select(f => new FileInfo(f))
            .Select(f => $"{f.FullName}={f.Length}@{f.LastWriteTimeUtc.Ticks}"));

    public void Dispose()
    {
        if (!Directory.Exists(Path))
        {
            return;
        }

        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            // Linux: the service folders belong to container uids — empty them as root in a throw-away container.
            // Only those folders are mounted (ca.key never enters a container); the rest belongs to the test user.
            foreach (var service in Directory.GetDirectories(Path, "*", SearchOption.AllDirectories))
            {
                using var cleanup = Process.Start("docker", ["run", "--rm", "--user", "0", "--entrypoint", "sh", "-v", $"{service}:/work",
                    BrokerFixture.Image, "-c", "rm -rf /work/* /work/.[!.]* 2>/dev/null; true"])!;
                cleanup.WaitForExit();
            }

            Directory.Delete(Path, recursive: true);
        }
    }
}

/// <summary>Mosquitto with the repository's configuration, each secret mounted read-only file by file (as compose does).</summary>
public sealed class BrokerFixture : IAsyncLifetime, IAsyncDisposable
{
    public const string Image = "eclipse-mosquitto@sha256:21421af7b32bf9ce508e9090c8eb13bb81f410ca778dc205506180a6f862d0eb";

    // Testcontainers also starts its resource reaper; pin that image by digest too.
    static BrokerFixture() =>
        TestcontainersSettings.ResourceReaperImage =
            new DockerImage("testcontainers/ryuk@sha256:7c1a8a9a47c780ed0f983770a662f80deb115d95cce3e2daa3d12115b8cd28f0");

    private readonly bool _ownsSecrets;
    private IContainer _container = null!;

    public BrokerFixture()
        : this(SecretsDirectory.Create("--edge", "line1-edge01", "--edge", "line2-edge01", "--san", "IP:192.168.1.50"), ownsSecrets: true)
    {
    }

    private BrokerFixture(SecretsDirectory secrets, bool ownsSecrets)
    {
        Secrets = secrets;
        _ownsSecrets = ownsSecrets;
    }

    public SecretsDirectory Secrets { get; }

    public int Port => _container.GetMappedPublicPort(8883);

    public string CaPath => Secrets.File("ca.pem");

    public static async Task<BrokerFixture> StartAsync(SecretsDirectory secrets)
    {
        var broker = new BrokerFixture(secrets, ownsSecrets: false);
        await broker.InitializeAsync();
        return broker;
    }

    public string Password(string identity) => System.IO.File.ReadAllText(Secrets.File($"passwords/{identity}.txt"));

    public async Task InitializeAsync()
    {
        var config = Path.Combine(Repo.Root, "deployments", "mosquitto");
        _container = new ContainerBuilder(Image)
            .WithPortBinding(8883, assignRandomHostPort: true)
            .WithBindMount(Path.Combine(config, "mosquitto.conf"), "/mosquitto/config/mosquitto.conf", AccessMode.ReadOnly)
            .WithBindMount(Path.Combine(config, "acl"), "/mosquitto/config/acl", AccessMode.ReadOnly)
            .WithBindMount(Secrets.File("ca.pem"), "/mosquitto/config/secrets/ca.pem", AccessMode.ReadOnly)
            .WithBindMount(Secrets.File("mosquitto/server.pem"), "/mosquitto/config/secrets/server.pem", AccessMode.ReadOnly)
            .WithBindMount(Secrets.File("mosquitto/server.key"), "/mosquitto/config/secrets/server.key", AccessMode.ReadOnly)
            .WithBindMount(Secrets.File("mosquitto/passwd"), "/mosquitto/config/secrets/passwd", AccessMode.ReadOnly)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("mosquitto version .* running"))
            .Build();
        await _container.StartAsync();
    }

    public async Task<string> LogsAsync()
    {
        var (stdout, stderr) = await _container.GetLogsAsync();
        return stdout + stderr;
    }

    /// <summary>Stops (Mosquitto saves its persistence file on SIGTERM) and starts the same container again.</summary>
    public async Task RestartAsync()
    {
        await _container.StopAsync();
        await _container.StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _container.DisposeAsync();
        if (_ownsSecrets)
        {
            Secrets.Dispose();
        }
    }

    async ValueTask IAsyncDisposable.DisposeAsync() => await DisposeAsync();
}

/// <summary>An MQTT 5 client configured like the Edge and the Cloud: TLS trusting only the dev CA, QoS 1.</summary>
public sealed class MqttClientHelper : IAsyncDisposable
{
    private readonly IMqttClient _client;
    private readonly ConcurrentQueue<(string Topic, string Payload)> _received = new();

    private MqttClientHelper(IMqttClient client, bool acknowledge)
    {
        _client = client;
        _client.ApplicationMessageReceivedAsync += e =>
        {
            e.AutoAcknowledge = acknowledge;
            _received.Enqueue((e.ApplicationMessage.Topic, Encoding.UTF8.GetString(e.ApplicationMessage.Payload.ToArray())));
            return Task.CompletedTask;
        };
    }

    public bool IsConnected => _client.IsConnected;

    public IReadOnlyList<string> Payloads => [.. _received.Select(r => r.Payload)];

    public IReadOnlyList<string> Topics => [.. _received.Select(r => r.Topic)];

    public static async Task<MqttClientHelper> ConnectAsync(
        BrokerFixture broker,
        string? username,
        string? password,
        string? clientId = null,
        bool cleanStart = true,
        bool acknowledge = true,
        bool tls = true,
        MqttProtocolVersion protocol = MqttProtocolVersion.V500,
        Action<MqttClientOptionsBuilder>? configure = null)
    {
        var client = new MqttClientFactory().CreateMqttClient();
        var helper = new MqttClientHelper(client, acknowledge);
        var result = await client.ConnectAsync(Options(broker, username, password, clientId, cleanStart, tls, protocol, configure));
        if (result.ResultCode != MqttClientConnectResultCode.Success)
        {
            client.Dispose();
            throw new InvalidOperationException($"Connect refused: {result.ResultCode}");
        }

        return helper;
    }

    public static async Task<MqttClientConnectResultCode> TryConnectAsync(BrokerFixture broker, string? username, string? password)
    {
        using var client = new MqttClientFactory().CreateMqttClient();
        try
        {
            var result = await client.ConnectAsync(Options(broker, username, password, null, true, true, MqttProtocolVersion.V500, null));
            if (result.ResultCode == MqttClientConnectResultCode.Success)
            {
                await client.DisconnectAsync();
            }

            return result.ResultCode;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Refused before a CONNACK could be read. The probe's successful login with valid credentials shows
            // the transport itself works, so a failure here is the refusal.
            return MqttClientConnectResultCode.UnspecifiedError;
        }
    }

    /// <summary>Connects with a clean start and leaves, discarding the identity's persistent session.</summary>
    public static async Task ResetSessionAsync(BrokerFixture broker, string identity)
    {
        await using var reset = await ConnectAsync(broker, identity, broker.Password(identity), cleanStart: true);
    }

    public async Task SubscribeAsync(params string[] filters)
    {
        var options = new MqttClientSubscribeOptionsBuilder();
        foreach (var filter in filters)
        {
            options.WithTopicFilter(f => f.WithTopic(filter).WithAtLeastOnceQoS());
        }

        // A refused filter (SUBACK error) is a valid outcome for the denial probes.
        await _client.SubscribeAsync(options.Build());
    }

    /// <summary>QoS 1; a denied publish (failed PUBACK or a closed connection) is not an error here.</summary>
    public async Task PublishAsync(string topic, string payload, bool retain = false) =>
        await _client.PublishAsync(new MqttApplicationMessageBuilder()
            .WithTopic(topic)
            .WithPayload(payload)
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
            .WithRetainFlag(retain)
            .Build());

    public Task DisconnectWithWillAsync() =>
        _client.DisconnectAsync(new MqttClientDisconnectOptionsBuilder().WithReason(MqttClientDisconnectOptionsReason.DisconnectWithWillMessage).Build());

    public Task WaitForAsync(string payload) =>
        WaitUntilAsync(() => _received.Any(r => r.Payload == payload), $"message '{payload}'");

    public static async Task WaitUntilAsync(Func<bool> condition, string what)
    {
        var deadline = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(deadline.Elapsed < TimeSpan.FromSeconds(15), $"timed out waiting for {what}");
            await Task.Delay(50);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_client.IsConnected)
        {
            try
            {
                await _client.DisconnectAsync();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Already closed by the broker.
            }
        }

        _client.Dispose();
    }

    private static MqttClientOptions Options(
        BrokerFixture broker,
        string? username,
        string? password,
        string? clientId,
        bool cleanStart,
        bool tls,
        MqttProtocolVersion protocol,
        Action<MqttClientOptionsBuilder>? configure)
    {
        var builder = new MqttClientOptionsBuilder()
            .WithTcpServer("localhost", broker.Port)
            .WithProtocolVersion(protocol)
            .WithClientId(clientId ?? username ?? "anonymous-" + Guid.NewGuid().ToString("N")[..8])
            .WithCleanStart(cleanStart)
            .WithTimeout(TimeSpan.FromSeconds(10));
        if (protocol == MqttProtocolVersion.V500)
        {
            builder.WithSessionExpiryInterval(cleanStart ? 0u : 3600u);
        }

        if (username is not null)
        {
            builder.WithCredentials(username, password);
        }

        if (tls)
        {
            var ca = X509CertificateLoader.LoadCertificateFromFile(broker.CaPath);
            builder.WithTlsOptions(o => o
                .UseTls(true)
                .WithCertificateValidationHandler(args => TrustsDevCa(ca, args.Certificate, args.Chain, args.SslPolicyErrors)));
        }

        configure?.Invoke(builder);
        return builder.Build();
    }

    // The same rule as the Edge and the Cloud: name must match, chain must end at the dev CA.
    private static bool TrustsDevCa(X509Certificate2 ca, X509Certificate? certificate, X509Chain? presented, SslPolicyErrors errors)
    {
        if (certificate is null || (errors & ~SslPolicyErrors.RemoteCertificateChainErrors) != SslPolicyErrors.None)
        {
            return false;
        }

        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(ca);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        if (presented is not null)
        {
            foreach (var element in presented.ChainElements)
            {
                chain.ChainPolicy.ExtraStore.Add(element.Certificate);
            }
        }

        using var leaf = X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());
        return chain.Build(leaf);
    }
}
