using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using MQTTnet;
using MQTTnet.Protocol;
using MQTTnet.Server;

namespace IndustrialIot.Edge.Tests.Fakes;

/// <summary>
/// In-process MQTT 5 broker with TLS only, for tests. Its server certificate (SAN <c>localhost</c>) is
/// signed by a test CA generated per instance; <see cref="CaPath"/> is that CA as PEM. It checks one
/// username/password, records every publish in arrival order, can publish as "the Cloud", and can be
/// stopped and started again on the same port.
/// </summary>
internal sealed class TestMqttBroker : IAsyncDisposable
{
    private readonly string _directory;
    private readonly X509Certificate2 _serverCertificate;
    private readonly ConcurrentQueue<MqttApplicationMessage> _received = new();
    private readonly ConcurrentQueue<DateTimeOffset> _connectAttempts = new();
    private MqttServer? _server;

    private TestMqttBroker(string username, string password)
    {
        Username = username;
        Password = password;
        Port = FreePort();
        _directory = Path.Combine(Path.GetTempPath(), "edge-tests", "mqtt-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);

        using var ca = CreateCa("Edge Test CA");
        CaPath = WritePem(ca, "ca.pem");
        _serverCertificate = CreateServerCertificate(ca);

        using var otherCa = CreateCa("Unrelated Test CA");
        UnrelatedCaPath = WritePem(otherCa, "unrelated-ca.pem");
    }

    public int Port { get; }

    public string Username { get; }

    public string Password { get; }

    /// <summary>PEM of the CA that signed the broker certificate.</summary>
    public string CaPath { get; }

    /// <summary>PEM of a CA that has nothing to do with the broker.</summary>
    public string UnrelatedCaPath { get; }

    /// <summary>CONNECT packets seen (after a successful TLS handshake), accepted or not.</summary>
    public IReadOnlyList<DateTimeOffset> ConnectAttempts => [.. _connectAttempts];

    /// <summary>Messages published by clients, in arrival order.</summary>
    public IReadOnlyList<MqttApplicationMessage> Received => [.. _received];

    public MqttServer Server => _server ?? throw new InvalidOperationException("Broker is stopped.");

    /// <summary>When it returns true for a client publish, the broker answers with a failed PUBACK.</summary>
    public Func<MqttApplicationMessage, bool>? RejectPublish { get; set; }

    public static async Task<TestMqttBroker> StartAsync(string username = "test-edge", string password = "test-only-password")
    {
        var broker = new TestMqttBroker(username, password);
        await broker.StartServerAsync();
        return broker;
    }

    /// <summary>Configuration for the Edge to use this broker.</summary>
    public Dictionary<string, string?> EdgeSettings(bool trustCa = true) => new()
    {
        ["Edge:Mqtt:Host"] = "localhost",
        ["Edge:Mqtt:Port"] = Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
        ["Edge:Mqtt:Username"] = Username,
        ["Edge:Mqtt:Password"] = Password,
        ["Edge:Mqtt:CaCertificatePath"] = trustCa ? CaPath : null,
    };

    public async Task StopServerAsync()
    {
        if (_server is { } server)
        {
            _server = null;
            await server.StopAsync(new MqttServerStopOptionsBuilder().Build());
            server.Dispose();
        }
    }

    public async Task StartServerAsync()
    {
        var options = new MqttServerOptionsBuilder()
            .WithoutDefaultEndpoint()
            .WithEncryptedEndpoint()
            .WithEncryptedEndpointBoundIPAddress(IPAddress.Loopback)
            .WithEncryptedEndpointPort(Port)
            .WithTlsEndpointReuseAddress()
            .WithEncryptionCertificate(_serverCertificate)
            .WithPersistentSessions(true)
            .Build();

        var server = new MqttServerFactory().CreateMqttServer(options);
        server.ValidatingConnectionAsync += e =>
        {
            _connectAttempts.Enqueue(DateTimeOffset.UtcNow);
            if (e.UserName != Username || e.Password != Password)
            {
                e.ReasonCode = MqttConnectReasonCode.BadUserNameOrPassword;
            }

            return Task.CompletedTask;
        };
        server.InterceptingPublishAsync += e =>
        {
            if (e.ClientId == CloudClientId)
            {
                return Task.CompletedTask;
            }

            if (RejectPublish?.Invoke(e.ApplicationMessage) == true)
            {
                // The client gets a failed PUBACK; the message is not recorded or forwarded.
                e.ProcessPublish = false;
                e.Response.ReasonCode = MqttPubAckReasonCode.ImplementationSpecificError;
                return Task.CompletedTask;
            }

            _received.Enqueue(e.ApplicationMessage);
            return Task.CompletedTask;
        };

        await server.StartAsync();
        _server = server;
    }

    /// <summary>Publishes as the Cloud would (QoS 1).</summary>
    public Task PublishAsync(string topic, string payload, bool retain = false)
    {
        var message = new MqttApplicationMessageBuilder()
            .WithTopic(topic)
            .WithPayload(payload)
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
            .WithRetainFlag(retain)
            .Build();
        return Server.InjectApplicationMessage(new InjectedMqttApplicationMessage(message) { SenderClientId = CloudClientId });
    }

    /// <summary>Connected client ids.</summary>
    public async Task<IReadOnlyList<string>> ConnectedClientsAsync() =>
        _server is null ? [] : (await _server.GetClientsAsync()).Select(c => c.Id).ToList();

    public async ValueTask DisposeAsync()
    {
        await StopServerAsync();
        _serverCertificate.Dispose();
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Left for the OS temp cleanup.
        }
    }

    private const string CloudClientId = "test-cloud";

    private static X509Certificate2 CreateCa(string name)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest($"CN={name}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
    }

    private static X509Certificate2 CreateServerCertificate(X509Certificate2 ca)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("localhost");
        request.CertificateExtensions.Add(san.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, false));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false)); // serverAuth

        var serial = new byte[16];
        RandomNumberGenerator.Fill(serial);
        using var signed = request.Create(ca, DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddDays(7), serial);
        using var withKey = signed.CopyWithPrivateKey(key);

        // Windows SChannel cannot use an ephemeral key; a PFX round-trip gives the certificate a usable one.
        return X509CertificateLoader.LoadPkcs12(withKey.Export(X509ContentType.Pfx), password: null);
    }

    private string WritePem(X509Certificate2 certificate, string fileName)
    {
        var path = Path.Combine(_directory, fileName);
        File.WriteAllText(path, certificate.ExportCertificatePem());
        return path;
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
