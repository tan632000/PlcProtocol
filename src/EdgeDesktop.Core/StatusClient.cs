using System.Text.Json;

namespace IndustrialIot.EdgeDesktop.Core;

/// <summary>A poll's outcome: a status, a fixed failure reason for the banner, or cancelled (the window closed).</summary>
public sealed record StatusResult(EdgeStatus? Status, string? FailureReason, bool Cancelled);

/// <summary>
/// Reads <c>GET /api/status</c> of the Edge on this machine. Only that request is ever sent. It goes to 127.0.0.1
/// rather than localhost, so a machine without IPv6 on loopback does not first try ::1 and run into the timeout.
/// </summary>
public sealed class StatusClient : IDisposable
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(2);

    // Lenient on purpose: a newer Edge with an added property must not blank the operator's screen.
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly TimeSpan _timeout;

    public StatusClient(int port, HttpMessageHandler? handler = null, TimeSpan? timeout = null)
    {
        // No proxy: a manual system proxy without "bypass for local addresses" would send 127.0.0.1 to the proxy
        // machine, and the banner would stay while the Edge runs (browsers skip proxies for loopback themselves).
        _http = new HttpClient(handler ?? new SocketsHttpHandler { UseProxy = false });
        _http.BaseAddress = new Uri($"http://127.0.0.1:{port}/");
        _http.Timeout = Timeout.InfiniteTimeSpan; // the per-request timeout below tells a timeout from a cancel
        _timeout = timeout ?? DefaultTimeout;
    }

    /// <summary>Never throws: every failure becomes a reason, and the caller's cancellation becomes Cancelled.</summary>
    public async Task<StatusResult> GetAsync(CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_timeout);
        try
        {
            using var response = await _http.GetAsync(new Uri("api/status", UriKind.Relative), timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return Failed($"HTTP {(int)response.StatusCode}");
            }

            var body = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            return Parse(body) is { } status ? new StatusResult(status, null, false) : Failed("dữ liệu không đọc được");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return new StatusResult(null, null, true);
        }
        catch (OperationCanceledException)
        {
            return Failed("quá 2 giây không trả lời");
        }
        catch (HttpRequestException)
        {
            return Failed("không kết nối được");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Anything else still becomes a banner: the poll loop must never stop silently on stale data.
            return Failed("không kết nối được");
        }
    }

    /// <summary>The status in <paramref name="json"/>, or null when it is not a readable status object.</summary>
    public static EdgeStatus? Parse(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<EdgeStatus>(json, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public void Dispose() => _http.Dispose();

    private static StatusResult Failed(string reason) => new(null, reason, false);
}
