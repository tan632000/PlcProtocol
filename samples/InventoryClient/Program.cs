using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR.Client;

namespace IndustrialIot.Samples.InventoryClient;

/// <summary>
/// Reads <c>IIOT_CLOUD_URL</c>, <c>IIOT_TOKEN</c> and <c>IIOT_EDGE_ID</c>, then runs <see cref="InventoryClient"/> until
/// Ctrl+C or <c>--seconds N</c>. The token is never printed.
/// </summary>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var url = Environment.GetEnvironmentVariable("IIOT_CLOUD_URL");
        var token = Environment.GetEnvironmentVariable("IIOT_TOKEN");
        var edgeId = Environment.GetEnvironmentVariable("IIOT_EDGE_ID");
        if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(token) || string.IsNullOrEmpty(edgeId))
        {
            Console.Error.WriteLine("Set IIOT_CLOUD_URL (e.g. http://127.0.0.1:8080/), IIOT_TOKEN and IIOT_EDGE_ID.");
            return 2;
        }

        TimeSpan? duration = null;
        if (args is ["--seconds", var seconds])
        {
            if (!int.TryParse(seconds, NumberStyles.None, CultureInfo.InvariantCulture, out var n) || n is < 1 or > 86_400)
            {
                Console.Error.WriteLine("--seconds takes a whole number of seconds from 1 to 86400.");
                return 2;
            }

            duration = TimeSpan.FromSeconds(n);
        }
        else if (args.Length > 0)
        {
            Console.Error.WriteLine("Usage: InventoryClient [--seconds N]");
            return 2;
        }

        using var stop = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            stop.Cancel();
        };

        var cloud = new Uri(url.EndsWith('/') ? url : url + "/");
        return await InventoryClient.RunAsync(cloud, token, edgeId, duration, Console.Out, stop.Token);
    }
}

/// <summary>
/// What a SCADA project does with the Cloud API, step by step as docs/scada-integration.md describes it: list the Edge's
/// PLCs, list each PLC's tags with its latest value, then join <c>/hubs/live</c> and print what is pushed.
/// </summary>
public static class InventoryClient
{
    /// <returns>
    /// 0 when it ran to the end; 1 when a request failed (only the HTTP status is printed) or the Cloud closed the hub
    /// connection (it does so when the token expires: a long-running client gets a new token and connects again).
    /// </returns>
    public static async Task<int> RunAsync(Uri cloud, string token, string edgeId, TimeSpan? duration, TextWriter output, CancellationToken ct)
    {
        // SignalR callbacks run on other threads.
        var console = TextWriter.Synchronized(output);
        using var http = new HttpClient { BaseAddress = cloud };

        // The token travels only in the Authorization header (HTTP) and through AccessTokenProvider (SignalR).
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var edge = Uri.EscapeDataString(edgeId);

        // 1. The Edge's PLCs: GET /api/v1/edges/{edgeId}/devices
        var devices = await GetAsync(http, $"api/v1/edges/{edge}/devices", console, ct);
        if (devices is not { } list)
        {
            return 1;
        }

        console.WriteLine($"Edge {edgeId}: reported {Text(list.GetProperty("reportedUtc"))}, stale {list.GetProperty("stale").GetBoolean()}");
        foreach (var device in list.GetProperty("devices").EnumerateArray())
        {
            var name = device.GetProperty("name").GetString()!;
            console.WriteLine($"PLC {name} ({device.GetProperty("model").GetString()}): {device.GetProperty("state").GetString()}");

            // 2. Its tags with the latest values: GET /api/v1/edges/{edgeId}/devices/{device}/tags
            if (await GetAsync(http, $"api/v1/edges/{edge}/devices/{Uri.EscapeDataString(name)}/tags", console, ct) is not { } tags)
            {
                return 1;
            }

            foreach (var tag in tags.EnumerateArray())
            {
                console.WriteLine(
                    $"  {tag.GetProperty("tagId").GetString()} {tag.GetProperty("dataType").GetString()} = {Text(tag.GetProperty("value"))} " +
                    $"({Text(tag.GetProperty("quality"))}){(tag.GetProperty("canWrite").GetBoolean() ? " writable" : "")}");
            }
        }

        // 3. Realtime: join the Edge on /hubs/live and print the pushes.
        await using var hub = new HubConnectionBuilder()
            .WithUrl(new Uri(cloud, "hubs/live"), options => options.AccessTokenProvider = () => Task.FromResult<string?>(token))
            .Build();
        hub.On<JsonElement>("telemetry", sample => console.WriteLine(
            $"telemetry {sample.GetProperty("tagId").GetString()} = {Text(sample.GetProperty("value"))} ({sample.GetProperty("quality").GetString()})"));
        hub.On<JsonElement>("devicesUpdated", update => console.WriteLine(
            $"devicesUpdated: {update.GetProperty("devices").GetArrayLength()} PLC(s), stale {update.GetProperty("stale").GetBoolean()}"));
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        hub.Closed += _ =>
        {
            closed.TrySetResult();
            return Task.CompletedTask;
        };
        await hub.StartAsync(ct);
        await hub.InvokeAsync("JoinEdge", edgeId, ct);

        try
        {
            await Task.WhenAny(Task.Delay(duration ?? Timeout.InfiniteTimeSpan, ct), closed.Task);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Ctrl+C.
        }

        if (closed.Task.IsCompleted && !ct.IsCancellationRequested)
        {
            console.WriteLine("hub closed: get a new token and connect again");
            return 1;
        }

        return 0;
    }

    private static async Task<JsonElement?> GetAsync(HttpClient http, string path, TextWriter console, CancellationToken ct)
    {
        using var response = await http.GetAsync(path, ct);
        if (!response.IsSuccessStatusCode)
        {
            console.WriteLine($"GET {path} failed: {(int)response.StatusCode} {response.StatusCode}");
            return null;
        }

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        return document.RootElement.Clone();
    }

    private static string Text(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Null => "-",
        JsonValueKind.String => value.GetString()!,
        _ => value.GetRawText(),
    };
}
