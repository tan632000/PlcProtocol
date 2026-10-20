using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Options;
using MQTTnet;
using MQTTnet.Protocol;

namespace IndustrialIot.Cloud.Messaging;

/// <summary>Result of <see cref="CloudMqttConnection.PublishAsync"/>.</summary>
public enum PublishOutcome
{
    /// <summary>There was no connection; nothing was sent.</summary>
    NotConnected,

    /// <summary>Sent or possibly sent, but no success PUBACK arrived (timeout, drop or failed PUBACK).</summary>
    Unconfirmed,

    /// <summary>The broker acknowledged the message.</summary>
    Confirmed,
}

/// <summary>
/// Thrown by a message handler to leave the message unacknowledged (the broker redelivers it on the next
/// session). Logged without a stack trace: it is an expected outcome, not a bug.
/// </summary>
public sealed class MessageNotAcknowledgedException(string reason) : Exception(reason);

/// <summary>
/// The Cloud's single MQTT 5 session (persistent for 24 h) over TLS. It reconnects with exponential
/// back-off, renews its subscriptions after every connect, processes one message at a time, and
/// acknowledges a message only after its handler completed.
/// </summary>
public sealed class CloudMqttConnection : BackgroundService
{
    private static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(10);
    private const uint SessionExpirySeconds = 24 * 60 * 60;
    private const uint MaximumPacketSize = 16 * 1024;

    private readonly CloudMqttOptions _options;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly TimeProvider _clock;
    private readonly ILogger<CloudMqttConnection> _logger;
    private readonly X509Certificate2? _trustedCa;
    private readonly IMqttClient _client;
    private readonly List<(string Filter, Func<MqttApplicationMessage, CancellationToken, Task> Handler)> _handlers = [];
    private readonly Lock _registration = new();

    private TaskCompletionSource _disconnected = NewSignal();
    private TaskCompletionSource _reconnectRequested = NewSignal();
    private readonly TaskCompletionSource _firstAttempt = NewSignal();

    // Cancelled when its connection ends, so a handler never keeps working (or asks for a reconnect) for a dead connection.
    private CancellationTokenSource _connectionScope = new();
    private CancellationToken _stopping;
    private bool _started;

    public CloudMqttConnection(
        IOptions<CloudMqttOptions> options,
        IHostApplicationLifetime lifetime,
        TimeProvider clock,
        ILogger<CloudMqttConnection> logger)
    {
        _options = options.Value;
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

    /// <summary>Completes once the first connection attempt has finished, whether it succeeded or not.</summary>
    internal Task FirstAttemptCompleted => _firstAttempt.Task;

    /// <summary>
    /// Registers the handler for a topic filter (<c>+</c> allowed). Must happen before the host has started,
    /// so the first connection already holds every subscription and no queued message arrives without a handler.
    /// </summary>
    public void RegisterSubscription(string topicFilter, Func<MqttApplicationMessage, CancellationToken, Task> handler)
    {
        ArgumentException.ThrowIfNullOrEmpty(topicFilter);
        ArgumentNullException.ThrowIfNull(handler);
        lock (_registration)
        {
            // Closed only when ApplicationStarted fires, i.e. after every hosted service's StartAsync.
            if (_started)
            {
                throw new InvalidOperationException("Subscriptions must be registered before the host has started.");
            }

            if (_handlers.Exists(h => h.Filter == topicFilter))
            {
                throw new InvalidOperationException($"A handler for topic filter '{topicFilter}' is already registered.");
            }

            _handlers.Add((topicFilter, handler));
        }
    }

    /// <summary>QoS 1, not retained. Never throws for broker or connection problems; the outcome says what happened.</summary>
    public async Task<PublishOutcome> PublishAsync(string topic, string payload, CancellationToken ct)
    {
        if (!_client.IsConnected)
        {
            return PublishOutcome.NotConnected;
        }

        var message = new MqttApplicationMessageBuilder()
            .WithTopic(topic)
            .WithPayload(payload)
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
            .WithRetainFlag(false)
            .Build();

        // MQTTnet's communication timeout does not bound the wait for a PUBACK (a held PUBACK was awaited past
        // it), so the wait is bounded here: after OperationTimeout the outcome is "unconfirmed", never a hang.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(OperationTimeout);
        try
        {
            var result = await _client.PublishAsync(message, timeout.Token).ConfigureAwait(false);
            if (result.IsSuccess)
            {
                return PublishOutcome.Confirmed;
            }

            _logger.LogWarning("Broker answered a publish on '{Topic}' with {Reason}.", topic, result.ReasonCode);
            return PublishOutcome.Unconfirmed;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // The connection may have dropped after the PUBLISH left: the broker may or may not have it.
            _logger.LogWarning("Publish on '{Topic}' was not confirmed ({Reason}).", topic, ex.GetBaseException().Message);
            return PublishOutcome.Unconfirmed;
        }
    }

    /// <summary>
    /// Drops the current connection so the broker redelivers every unacknowledged message on the next
    /// session; the reconnect back-off applies. Safe to call from inside a message handler: the disconnect
    /// runs on the connection loop, after the call returns, and first cancels every running handler.
    /// </summary>
    public Task ForceReconnectAsync()
    {
        Volatile.Read(ref _reconnectRequested).TrySetResult();
        return Task.CompletedTask;
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
                    try
                    {
                        await ConnectAsync(stoppingToken).ConfigureAwait(false);
                    }
                    finally
                    {
                        _firstAttempt.TrySetResult();
                    }

                    var connectedAt = _clock.GetUtcNow();

                    var disconnected = Volatile.Read(ref _disconnected).Task;
                    var reconnect = Volatile.Read(ref _reconnectRequested).Task;
                    var forced = await Task.WhenAny(disconnected, reconnect).WaitAsync(stoppingToken).ConfigureAwait(false) == reconnect;
                    if (forced)
                    {
                        _logger.LogWarning("Dropping the MQTT connection so the broker redelivers unacknowledged messages.");
                        await Volatile.Read(ref _connectionScope).CancelAsync().ConfigureAwait(false);
                        await DisconnectQuietlyAsync(CancellationToken.None).ConfigureAwait(false);
                        await disconnected.WaitAsync(OperationTimeout, stoppingToken).ConfigureAwait(false);
                    }

                    // Only a connection that held for a while resets the back-off; one that keeps dropping
                    // (session takeover, ACL, quota) must not cause a reconnect storm. A forced drop always waits.
                    if (!forced && _clock.GetUtcNow() - connectedAt >= maxDelay)
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
                        _options.Host, _options.Port, ex.GetBaseException().Message, (int)delay.TotalMilliseconds);
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
        await DisconnectQuietlyAsync(cancellationToken).ConfigureAwait(false);
    }

    public override void Dispose()
    {
        _client.Dispose();
        base.Dispose();
    }

    private async Task ConnectAsync(CancellationToken ct)
    {
        Volatile.Write(ref _disconnected, NewSignal());
        // A request made for an earlier connection is fulfilled by this new one.
        Volatile.Write(ref _reconnectRequested, NewSignal());
        // Before connecting: queued messages may arrive as soon as the session resumes.
        Volatile.Write(ref _connectionScope, new CancellationTokenSource());

        var options = new MqttClientOptionsBuilder()
            .WithTcpServer(_options.Host, _options.Port)
            .WithProtocolVersion(MQTTnet.Formatter.MqttProtocolVersion.V500)
            .WithClientId(_options.ClientId)
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
            if (_handlers.Count > 0)
            {
                var subscribe = new MqttClientSubscribeOptionsBuilder();
                foreach (var (filter, _) in _handlers)
                {
                    subscribe.WithTopicFilter(new MqttTopicFilterBuilder()
                        .WithTopic(filter)
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
            await DisconnectQuietlyAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        _logger.LogInformation("MQTT connected to {Host}:{Port} (session present: {SessionPresent}).",
            _options.Host, _options.Port, result.IsSessionPresent);
    }

    private async Task DisconnectQuietlyAsync(CancellationToken ct)
    {
        if (!_client.IsConnected)
        {
            return;
        }

        try
        {
            // A normal DISCONNECT keeps the 24 h session, so unacknowledged messages stay queued at the broker.
            await _client.DisconnectAsync(new MqttClientDisconnectOptionsBuilder().Build(), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogDebug(ex, "MQTT disconnect failed.");
        }
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

    private async Task OnMessageReceivedAsync(MqttApplicationMessageReceivedEventArgs e)
    {
        e.AutoAcknowledge = false;
        var message = e.ApplicationMessage;

        // Retained messages are stale state, not fresh telemetry or results.
        var handler = message.Retain ? null : _handlers.Find(h => MqttTopicFilter.Matches(h.Filter, message.Topic)).Handler;
        if (handler is null)
        {
            _logger.LogWarning("Ignoring a message on '{Topic}' (retained: {Retained}); it is not acknowledged.", message.Topic, message.Retain);
            return;
        }

        using var scope = CancellationTokenSource.CreateLinkedTokenSource(_stopping, Volatile.Read(ref _connectionScope).Token);
        try
        {
            await handler(message, scope.Token).ConfigureAwait(false);
            await e.AcknowledgeAsync(scope.Token).ConfigureAwait(false);
        }
        catch (MessageNotAcknowledgedException ex)
        {
            _logger.LogWarning("A message on '{Topic}' is not acknowledged: {Reason}", message.Topic, ex.Message);
        }
        catch (OperationCanceledException) when (scope.IsCancellationRequested)
        {
            _logger.LogInformation("Handling a message on '{Topic}' stopped with its connection; the broker redelivers it.", message.Topic);
        }
        catch (Exception ex)
        {
            // Not acknowledged: the broker delivers it again on the next session.
            _logger.LogError(ex, "Handling a message on '{Topic}' failed; it is not acknowledged.", message.Topic);
        }
    }

    private Task OnDisconnectedAsync(MqttClientDisconnectedEventArgs e)
    {
        Volatile.Read(ref _connectionScope).Cancel();
        if (e.ClientWasConnected)
        {
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

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
