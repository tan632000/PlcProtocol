using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace IndustrialIot.EdgeDesktop.Core;

/// <summary>Why the settings API, or the client itself, did not do what was asked.</summary>
public enum RefusalKind
{
    /// <summary>Refused by the client: an empty password is never sent (it would count against the shared lockout).</summary>
    EmptyPassword,
    WrongPassword,
    PasswordNotSet,
    Locked,
    StaleRevision,
    ApplyInProgress,
    Invalid,
    TooLarge,
    EndpointInUse,
    TestRunning,
    RateLimited,

    /// <summary>Anything else: no connection, a timeout, an unreadable reply or another HTTP status (see Detail).</summary>
    Failed,
}

/// <param name="Errors">The pinned errors of an Invalid refusal; empty otherwise.</param>
/// <param name="RetryAfterSeconds">Only for Locked.</param>
/// <param name="Detail">For Failed, the reason shown to the operator; otherwise the Edge's message, if it sent one.</param>
public sealed record ApiRefusal(RefusalKind Kind, IReadOnlyList<ApiError> Errors, int? RetryAfterSeconds, string? Detail);

public sealed record SettingsLoad(SettingsSnapshot? Snapshot, ApiRefusal? Refusal);

/// <summary>The draft's errors (none means valid), or why the check could not be made.</summary>
public sealed record ValidateResult(IReadOnlyList<ApiError> Errors, ApiRefusal? Refusal);

public sealed record SaveResult(string? ApplyId, ApiRefusal? Refusal);

public sealed record TestResult(bool Reachable, double? ElapsedMs, ApiRefusal? Refusal);

/// <summary>An unsaved PLC to test, as the web page sends it (<c>proposed</c>); null fields take the model's defaults.</summary>
public sealed record PlcConnection(string Driver, string Model, string Host, int? Port, int? Rack, int? Slot, int TimeoutMs);

/// <summary>
/// The settings API of the Edge on this machine (<c>/api/settings</c>, validate, save, test-connection), with the web
/// page's JSON. Never throws for an HTTP or network failure: each reply becomes a result, classified by its
/// <c>error</c> key rather than its status code (429 is a lockout or a test rate limit). The password goes into the one
/// request body it is given for and into nothing else; an empty one is not sent at all. Unlike StatusClient, the
/// caller's own cancellation is not a result: it throws <see cref="OperationCanceledException"/>.
/// </summary>
public sealed class SettingsClient : IDisposable
{
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Added to a PLC's own timeout for a connection test, as the web page does.</summary>
    public static readonly TimeSpan TestMargin = TimeSpan.FromSeconds(5);

    // Lenient on purpose, as StatusClient: a newer Edge with an added property must keep working.
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly TimeProvider _time;

    public SettingsClient(int port, HttpMessageHandler? handler = null, TimeProvider? time = null)
    {
        // No proxy, for the reason given in StatusClient.
        _http = new HttpClient(handler ?? new SocketsHttpHandler { UseProxy = false });
        _http.BaseAddress = new Uri($"http://127.0.0.1:{port}/");
        _http.Timeout = Timeout.InfiniteTimeSpan; // per-request timeouts below
        _time = time ?? TimeProvider.System;
    }

    public void Dispose() => _http.Dispose();

    public async Task<SettingsLoad> GetAsync(CancellationToken ct = default)
    {
        var reply = await SendAsync(HttpMethod.Get, "api/settings", null, RequestTimeout, ct).ConfigureAwait(false);
        if (reply.Refusal is not null)
        {
            return new SettingsLoad(null, reply.Refusal);
        }

        // Read leniently, so a missing part comes back null rather than failing: such a reply is not usable.
        var snapshot = Read<SettingsSnapshot>(reply.Body);
        return snapshot?.Settings?.Channels is null || snapshot.Models is null
            ? new SettingsLoad(null, Failed("dữ liệu không đọc được"))
            : new SettingsLoad(snapshot, null);
    }

    public async Task<ValidateResult> ValidateAsync(ConfigDraft draft, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var body = new JsonObject { ["revision"] = draft.Revision, ["settings"] = draft.ToSettingsJson() };
        var reply = await SendAsync(HttpMethod.Post, "api/settings/validate", body, RequestTimeout, ct).ConfigureAwait(false);
        return reply.Refusal is not null
            ? new ValidateResult([], reply.Refusal)
            : new ValidateResult(Errors(reply.Body), null);
    }

    public async Task<SaveResult> SaveAsync(ConfigDraft draft, string password, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        if (string.IsNullOrEmpty(password))
        {
            return new SaveResult(null, new ApiRefusal(RefusalKind.EmptyPassword, [], null, null));
        }

        // Exactly these lower-case top-level keys: the Edge compares them ordinally.
        var body = new JsonObject { ["revision"] = draft.Revision, ["password"] = password, ["settings"] = draft.ToSettingsJson() };
        var reply = await SendAsync(HttpMethod.Put, "api/settings", body, RequestTimeout, ct).ConfigureAwait(false);
        if (reply.Refusal is not null)
        {
            return new SaveResult(null, reply.Refusal);
        }

        return Text(reply.Body, "applyId") is { } applyId
            ? new SaveResult(applyId, null)
            : new SaveResult(null, Failed("dữ liệu không đọc được"));
    }

    public async Task<TestResult> TestConnectionAsync(PlcConnection connection, string password, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (string.IsNullOrEmpty(password))
        {
            return new TestResult(false, null, new ApiRefusal(RefusalKind.EmptyPassword, [], null, null));
        }

        var body = new JsonObject
        {
            ["password"] = password,
            ["proposed"] = new JsonObject
            {
                ["driver"] = connection.Driver,
                ["model"] = connection.Model,
                ["host"] = connection.Host,
                ["port"] = connection.Port,
                ["rack"] = connection.Rack,
                ["slot"] = connection.Slot,
                ["timeoutMs"] = connection.TimeoutMs,
            },
        };
        var timeout = TimeSpan.FromMilliseconds(Math.Max(0, connection.TimeoutMs)) + TestMargin;
        var reply = await SendAsync(HttpMethod.Post, "api/settings/test-connection", body, timeout, ct).ConfigureAwait(false);
        if (reply.Refusal is not null)
        {
            return new TestResult(false, null, reply.Refusal);
        }

        var result = Text(reply.Body, "result");
        if (result is not ("ok" or "unreachable"))
        {
            return new TestResult(false, null, Failed("dữ liệu không đọc được"));
        }

        double? elapsed = reply.Body is { ValueKind: JsonValueKind.Object } root && root.TryGetProperty("elapsedMs", out var ms) && ms.ValueKind == JsonValueKind.Number
            ? ms.GetDouble()
            : null;
        return new TestResult(result == "ok", elapsed, null);
    }

    private async Task<(JsonElement? Body, ApiRefusal? Refusal)> SendAsync(
        HttpMethod method, string path, JsonObject? body, TimeSpan timeout, CancellationToken ct)
    {
        using var timer = new CancellationTokenSource(timeout, _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timer.Token);
        try
        {
            using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));
            if (body is not null)
            {
                request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
            }

            using var response = await _http.SendAsync(request, linked.Token).ConfigureAwait(false);
            var text = await response.Content.ReadAsStringAsync(linked.Token).ConfigureAwait(false);
            var json = Parse(text);
            if (!response.IsSuccessStatusCode)
            {
                return (json, Classify(response.StatusCode, json));
            }

            return json is null ? (null, Failed("dữ liệu không đọc được")) : (json, null);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            var seconds = Math.Ceiling(timeout.TotalSeconds).ToString(CultureInfo.InvariantCulture);
            return (null, Failed($"quá {seconds} giây không trả lời"));
        }
        catch (HttpRequestException)
        {
            return (null, Failed("không kết nối được"));
        }
    }

    private static ApiRefusal Classify(HttpStatusCode status, JsonElement? body)
    {
        var error = Text(body, "error");
        var errors = Errors(body);
        var kind = error switch
        {
            "wrong-password" => RefusalKind.WrongPassword,
            "password-not-set" => RefusalKind.PasswordNotSet,
            "locked" => RefusalKind.Locked,
            "stale-revision" => RefusalKind.StaleRevision,
            "apply-in-progress" => RefusalKind.ApplyInProgress,
            "too-large" => RefusalKind.TooLarge,
            "endpoint-in-use" => RefusalKind.EndpointInUse,
            "test-running" => RefusalKind.TestRunning,
            "rate-limited" => RefusalKind.RateLimited,
            null when status == HttpStatusCode.BadRequest && errors.Count > 0 => RefusalKind.Invalid,
            _ => RefusalKind.Failed,
        };
        if (kind == RefusalKind.Failed)
        {
            var code = ((int)status).ToString(CultureInfo.InvariantCulture);
            return Failed(error is null ? $"HTTP {code}" : $"HTTP {code} ({error})");
        }

        int? retry = kind == RefusalKind.Locked && body is { ValueKind: JsonValueKind.Object } root
            && root.TryGetProperty("retryAfterSeconds", out var seconds)
            && seconds.TryGetInt32(out var value)
                ? value
                : null;
        return new ApiRefusal(kind, errors, retry, Text(body, "message"));
    }

    private static ApiRefusal Failed(string detail) => new(RefusalKind.Failed, [], null, detail);

    private static IReadOnlyList<ApiError> Errors(JsonElement? body) =>
        body is { ValueKind: JsonValueKind.Object } root && root.TryGetProperty("errors", out var list) && list.ValueKind == JsonValueKind.Array
            ? Read<List<ApiError>>(list) ?? []
            : [];

    private static string? Text(JsonElement? body, string name) =>
        body is { ValueKind: JsonValueKind.Object } root && root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static T? Read<T>(JsonElement? element)
        where T : class
    {
        try
        {
            return element?.Deserialize<T>(Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static JsonElement? Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(text);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
