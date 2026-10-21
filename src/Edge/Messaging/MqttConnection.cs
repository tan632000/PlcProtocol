using System.Collections.Concurrent;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Options;
using MQTTnet;
using MQTTnet.Protocol;

namespace IndustrialIot.Edge.Messaging;

/// <summary>
/// The Edge's single MQTT 5 session (client id = edgeId, persistent for 24 h) over TLS.
/// It reconnects with exponential back-off, re-subscribes after every connect, and acknowledges an
/// incoming message only after its handler finished — so a command that arrives before its handler
/// exists, or whose handling fails, is redelivered by the broker instead of being lost.
/// </summary>
public sealed class MqttConnection : BackgroundService
{
    private static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(10);
    private const uint SessionExpirySeconds = 24 * 60 * 60;
    private const uint MaximumPacketSize = 16 * 1024;

    private readonly MqttOptions _options;
    private readonly string _clientId;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly TimeProvider _clock;
    private readonly ILogger<MqttConnection> _logger;
    private readonly X509Certificate2? _trustedCa;
    private readonly IMqttClient _client;
    private readonly ConcurrentDictionary<string, Func<MqttApplicationMessage, CancellationToken, Task>> _handlers = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _retired = new(StringComparer.Ordinal);
    private readonly Lock _registration = new();

    private TaskCompletionSource _connected = NewSignal();
    private TaskCompletionSource _disconnected = NewSignal();
    private CancellationToken _stopping;
    private volatile bool _started;
    private long _generation;

    public MqttConnection(
        IOptions<MqttOptions> options,
        IOptions<EdgeOptions> edge,
        IHostApplicationLifetime lifetime,
        TimeProvider clock,
        ILogger<MqttConnection> logger)
    {
        _options = options.Value;
        _clientId = edge.Value.EdgeId;
        _lifetime = lifetime;
        _clock = clock;
        _logger = logger;
        _trustedCa = string.IsNullOrEmpty(_options.CaCertificatePath)
            ? null
            : X509CertificateLoader.LoadCertificateFromFile(_options.CaCertificatePath);

        // No MQTTnet logger is attached: its verbose output could include packet contents.
        _client = new MqttClientFactory().CreateMqttClient();
        _client.ApplicationMessageReceivedAsync += OnMessageReceivedAsync;
        _client.DisconnectedAsync += OnDisconnectedAsync;
    }

    public bool IsConnected => _client.IsConnected;

    /// <summary>
    /// Incremented by every successful connect. A changed value tells a caller that the broker session was
    /// re-established, e.g. so it can resend what the previous connection may have lost.
    /// </summary>
    public long ConnectionGeneration => Interlocked.Read(ref _generation);

    /// <summary>
    /// Registers the handler for one exact topic. Must happen before the host starts, so the first
    /// connection already has every subscription and no queued message arrives without a handler.
    /// </summary>
    public void RegisterSubscription(string topic, Func<MqttApplicationMessage, CancellationToken, Task> handler)
    {
        ArgumentException.ThrowIfNullOrEmpty(topic);
        ArgumentNullException.ThrowIfNull(handler);
        lock (_registration)
        {
            // Closed only when ApplicationStarted fires, i.e. after every hosted service's StartAsync.
            if (_started)
            {
                throw new InvalidOperationException("Subscriptions must be registered before the host has started.");
            }

            if (_retired.ContainsKey(topic) || !_handlers.TryAdd(topic, handler))
            {
                throw new InvalidOperationException($"A handler for topic '{topic}' is already registered.");
            }
        }
    }

    /// <summary>
    /// Marks a topic this client no longer serves. The persistent session may still hold its subscription and queued
    /// messages from an earlier configuration: anything delivered on it is acknowledged and discarded (never left
    /// unacknowledged, where it would be redelivered forever and occupy in-flight slots), and the topic is unsubscribed
    /// after every connect. Same registration window as <see cref="RegisterSubscription"/>.
    /// </summary>
    public void RetireSubscription(string topic)
    {
        ArgumentException.ThrowIfNullOrEmpty(topic);
        lock (_registration)
        {
            if (_started)
            {
                throw new InvalidOperationException("Subscriptions must be retired before the host has started.");
            }

            if (_handlers.ContainsKey(topic) || !_retired.TryAdd(topic, 0))
            {
                throw new InvalidOperationException($"Topic '{topic}' is already registered or retired.");
            }
        }
    }

    /// <summary>Completes once a connection is established (immediately if it already is).</summary>
    public Task WaitForConnectionAsync(CancellationToken ct) => Volatile.Read(ref _connected).Task.WaitAsync(ct);

    /// <summary>QoS 1, not retained. Completes only when the broker acknowledged the message.</summary>
    public async Task PublishAsync(string topic, string payload, CancellationToken ct)
    {
        var message = new MqttApplicationMessageBuilder()
            .WithTopic(topic)
            .WithPayload(payload)
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
            .WithRetainFlag(false)
            .Build();

        var result = await _client.PublishAsync(message, ct).ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            throw new InvalidOperationException($"Broker did not accept the message on '{topic}': {result.ReasonCode}.");
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _stopping = stoppingToken;

        try
        {
            // Every hosted service has started (and registered its handlers) once ApplicationStarted fires.
            await WaitForApplicationStartedAsync(stoppingToken).ConfigureAwait(false);
            lock (_registration)
            {
                _started = true;
            }

            var minDelay = TimeSpan.FromMilliseconds(_options.ReconnectMinDelayMs);
            var maxDelay = TimeSpan.FromMilliseconds(_options.ReconnectMaxDelayMs);
            var delay = minDelay;
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await ConnectAsync(stoppingToken).ConfigureAwait(false);
                    var connectedAt = _clock.GetUtcNow();
                    await Volatile.Read(ref _disconnected).Task.WaitAsync(stoppingToken).ConfigureAwait(false);

                    // Only a connection that held for a while resets the back-off; one the broker keeps
                    // dropping (duplicate edgeId taking the session over, ACL, quota) must not cause a reconnect storm.
                    if (_clock.GetUtcNow() - connectedAt >= maxDelay)
                    {
                        delay = minDelay;
                        continue;
                    }

                    _logger.LogWarning("MQTT connection dropped shortly after connecting; retrying in {Delay} ms.", (int)delay.TotalMilliseconds);
                }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                {
                    // Never include the options here: the message must not carry the password.
                    _logger.LogWarning("MQTT connection to {Host}:{Port} failed ({Reason}); retrying in {Delay} ms.",
                        _options.Host, _options.Port, Describe(ex), (int)delay.TotalMilliseconds);
                }

                await Task.Delay(delay, _clock, stoppingToken).ConfigureAwait(false);
                delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, maxDelay.Ticks));
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Host is stopping.
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
        if (_client.IsConnected)
        {
            try
            {
                await _client.DisconnectAsync(new MqttClientDisconnectOptionsBuilder().Build(), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _logger.LogDebug(ex, "MQTT disconnect during shutdown failed.");
            }
        }
    }

    public override void Dispose()
    {
        _client.Dispose();
        base.Dispose();
    }

    private async Task ConnectAsync(CancellationToken ct)
    {
        Volatile.Write(ref _disconnected, NewSignal());

        var options = new MqttClientOptionsBuilder()
            .WithTcpServer(_options.Host, _options.Port)
            .WithProtocolVersion(MQTTnet.Formatter.MqttProtocolVersion.V500)
            .WithClientId(_clientId)
            .WithCredentials(_options.Username, _options.Password)
            .WithCleanStart(false)
            .WithSessionExpiryInterval(SessionExpirySeconds)
            .WithMaximumPacketSize(MaximumPacketSize)
            .WithKeepAlivePeriod(TimeSpan.FromSeconds(_options.KeepAliveSeconds))
            .WithTimeout(OperationTimeout)
            .WithTlsOptions(tls => tls
                .UseTls(true)
                .WithSslProtocols(SslProtocols.Tls12 | SslProtocols.Tls13)
                .WithCertificateValidationHandler(args => ValidateServerCertificate(args.Certificate, args.Chain, args.SslPolicyErrors)))
            .Build();

        var result = await _client.ConnectAsync(options, ct).ConfigureAwait(false);
        if (result.ResultCode != MqttClientConnectResultCode.Success)
        {
            throw new InvalidOperationException($"Broker refused the connection: {result.ResultCode}.");
        }

        try
        {
            if (!_handlers.IsEmpty)
            {
                var subscribe = new MqttClientSubscribeOptionsBuilder();
                foreach (var topic in _handlers.Keys)
                {
                    subscribe.WithTopicFilter(new MqttTopicFilterBuilder()
                        .WithTopic(topic)
                        .WithAtLeastOnceQoS()
                        .WithRetainHandling(MqttRetainHandling.DoNotSendOnSubscribe));
                }

                var granted = await _client.SubscribeAsync(subscribe.Build(), ct).ConfigureAwait(false);
                var refused = granted.Items.Where(i => i.ResultCode != MqttClientSubscribeResultCode.GrantedQoS1).ToList();
                if (refused.Count > 0)
                {
                    throw new InvalidOperationException(
                        $"Broker refused subscription(s): {string.Join(", ", refused.Select(r => $"{r.TopicFilter.Topic}={r.ResultCode}"))}.");
                }
            }
        }
        catch
        {
            await _client.DisconnectAsync(new MqttClientDisconnectOptionsBuilder().Build(), CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        await UnsubscribeRetiredAsync(ct).ConfigureAwait(false);

        _logger.LogInformation("MQTT connected to {Host}:{Port} (session present: {SessionPresent}).",
            _options.Host, _options.Port, result.IsSessionPresent);
        Interlocked.Increment(ref _generation);
        Volatile.Read(ref _connected).TrySetResult();
    }

    /// <summary>
    /// Without a configured CA the OS trust store decides (no errors allowed). With one, the chain must
    /// end at that CA alone, and every other check (host name, certificate present) must still pass.
    /// </summary>
    private bool ValidateServerCertificate(X509Certificate? certificate, X509Chain? presented, SslPolicyErrors errors)
    {
        if (_trustedCa is null)
        {
            return errors == SslPolicyErrors.None;
        }

        if (certificate is null || (errors & ~SslPolicyErrors.RemoteCertificateChainErrors) != SslPolicyErrors.None)
        {
            return false;
        }

        // The chain is rebuilt from scratch, so every check SslStream folded into RemoteCertificateChainErrors
        // must be restated: trust only the configured CA, and require the server-authentication EKU.
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(_trustedCa);
        chain.ChainPolicy.ApplicationPolicy.Add(new Oid("1.3.6.1.5.5.7.3.1")); // serverAuth
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck; // private CAs rarely publish a CRL

        // Intermediates the broker sent may help build the path; with CustomRootTrust they add no trust.
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

    /// <summary>Best effort: a failure only means the next connect tries again; retired messages are discarded anyway.</summary>
    private async Task UnsubscribeRetiredAsync(CancellationToken ct)
    {
        if (_retired.IsEmpty)
        {
            return;
        }

        try
        {
            var unsubscribe = new MqttClientUnsubscribeOptionsBuilder();
            foreach (var topic in _retired.Keys)
            {
                unsubscribe.WithTopicFilter(topic);
            }

            var result = await _client.UnsubscribeAsync(unsubscribe.Build(), ct).ConfigureAwait(false);
            _logger.LogDebug("Unsubscribed retired topic(s): {Results}.",
                string.Join(", ", result.Items.Select(i => $"{i.TopicFilter}={i.ResultCode}")));
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _logger.LogDebug("Unsubscribing retired topic(s) failed ({Reason}); retried on the next connect.", ex.GetBaseException().Message);
        }
    }

    private async Task AcknowledgeQuietlyAsync(MqttApplicationMessageReceivedEventArgs e)
    {
        try
        {
            await e.AcknowledgeAsync(_stopping).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // The connection dropped: the broker delivers it again and it is discarded again.
            _logger.LogDebug(ex, "Acknowledging a discarded message failed.");
        }
    }

    private async Task OnMessageReceivedAsync(MqttApplicationMessageReceivedEventArgs e)
    {
        e.AutoAcknowledge = false;
        var message = e.ApplicationMessage;

        // Left over from an earlier configuration (e.g. commands before they were switched off): acknowledged, not handled.
        if (_retired.ContainsKey(message.Topic))
        {
            _logger.LogInformation("Discarded a message on retired topic '{Topic}'.", message.Topic);
            await AcknowledgeQuietlyAsync(e).ConfigureAwait(false);
            return;
        }

        // Exact topic match only; retained messages are stale state, not commands.
        if (message.Retain || !_handlers.TryGetValue(message.Topic, out var handler))
        {
            _logger.LogWarning("Ignoring a message on '{Topic}' (retained: {Retained}); it is not acknowledged.", message.Topic, message.Retain);
            return;
        }

        try
        {
            await handler(message, _stopping).ConfigureAwait(false);
            await e.AcknowledgeAsync(_stopping).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Not acknowledged: the broker delivers it again on the next session.
            _logger.LogError(ex, "Handling a message on '{Topic}' failed; it is not acknowledged.", message.Topic);
        }
    }

    private Task OnDisconnectedAsync(MqttClientDisconnectedEventArgs e)
    {
        if (e.ClientWasConnected)
        {
            Volatile.Write(ref _connected, NewSignal());
            _logger.LogWarning("MQTT connection lost ({Reason}).", e.Reason);
        }

        Volatile.Read(ref _disconnected).TrySetResult();
        return Task.CompletedTask;
    }

    private Task WaitForApplicationStartedAsync(CancellationToken ct)
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _lifetime.ApplicationStarted.Register(() => started.TrySetResult());
        return started.Task.WaitAsync(ct);
    }

    // MQTTnet's messages carry the CONNACK reason (e.g. BadUserNameOrPassword) but never the credentials.
    private static string Describe(Exception ex) => ex.GetBaseException().Message;

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
