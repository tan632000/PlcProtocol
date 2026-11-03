using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using IndustrialIot.Edge.Drivers;
using IndustrialIot.Edge.Tests.Fakes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace IndustrialIot.Edge.Tests;

/// <summary>
/// The settings page is served by the loopback monitor like the monitor page itself, writes text only through
/// textContent/value, offers exactly the DataTypes the Edge knows, and never lets the browser store the password.
/// </summary>
public sealed partial class SettingsPageTests : IAsyncDisposable
{
    private readonly int _port = FreePort();
    private EdgeTestHost? _edge;
    private HttpClient? _http;

    public async ValueTask DisposeAsync()
    {
        _http?.Dispose();
        if (_edge is not null)
        {
            await _edge.Host.StopAsync();
            await _edge.DisposeAsync();
        }
    }

    [Fact]
    public async Task SettingsPage_IsServed_WithTheMonitorsHeaders()
    {
        var http = await StartAsync();

        using var page = await http.GetAsync("/settings");
        using var monitor = await http.GetAsync("/");

        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Equal("text/html", page.Content.Headers.ContentType?.MediaType);
        Assert.Equal(Single(monitor, "Content-Security-Policy"), Single(page, "Content-Security-Policy"));
        Assert.Contains("no-store", Single(page, "Cache-Control"));
        Assert.Equal("nosniff", Single(page, "X-Content-Type-Options"));
        Assert.Contains("/api/settings", await page.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task SettingsPage_NeverWritesMarkupFromStrings()
    {
        var html = await PageAsync();

        foreach (var sink in new[] { "innerHTML", "outerHTML", "insertAdjacentHTML", "document.write", "eval(", "new Function" })
        {
            Assert.DoesNotContain(sink, html);
        }
    }

    [Fact]
    public async Task SettingsPage_HasNoInlineEventHandler()
    {
        var html = await PageAsync();

        Assert.DoesNotMatch(@"<[^>]+\son[a-z]+\s*=", html);
        Assert.DoesNotMatch(@"\.on[a-z]+\s*=", html);
    }

    [Fact]
    public async Task SettingsPage_CallsEverySettingsRoute()
    {
        var html = await PageAsync();

        foreach (var route in new[] { "\"/api/settings\"", "\"/api/settings/validate\"", "\"/api/settings/test-connection\"", "\"/api/settings/csv/parse\"", "\"/api/settings/csv/format\"", "\"/api/status\"" })
        {
            Assert.Contains(route, html);
        }
    }

    [Fact]
    public async Task ModelsAndDataTypes_ComeFromTheEdge_NotFromThePage()
    {
        var html = await PageAsync();

        foreach (var field in new[] { ".models", ".dataTypes", ".portHint", ".writesAllowed", ".defaultPort", ".warnings", ".commandsEnabled" })
        {
            Assert.Contains(field, html);
        }

        // No list of its own: no model name, and no data type other than String (whose Length the page shows).
        foreach (var model in PlcModelCatalog.Models)
        {
            Assert.DoesNotContain(model.Name, html);
        }

        foreach (var name in Enum.GetNames<PlcDataType>().Where(name => name != nameof(PlcDataType.String)))
        {
            Assert.DoesNotMatch($"[\"'`]{name}[\"'`]", html);
            Assert.DoesNotContain($"<option value=\"{name}\"", html);
        }

        foreach (var driver in Enum.GetNames<Configuration.PlcDriverKind>())
        {
            Assert.DoesNotMatch($"[\"'`]{driver}[\"'`]", html);
        }
    }

    [Fact]
    public async Task UnreadableNumbers_AreRefused_AndTheCsvDownloadStartsWithABom()
    {
        var html = await PageAsync();

        // A number field reports "" for text it cannot read; the page must not send that as "not set".
        Assert.Contains("validity.badInput", html);
        // Excel opens a UTF-8 CSV without a BOM as ANSI.
        Assert.Contains("new Blob([\"\\uFEFF\", json.csv]", html);
    }

    [Fact]
    public async Task ThePassword_IsNeverStoredByThePage()
    {
        var html = await PageAsync();

        Assert.DoesNotContain("localStorage", html);
        Assert.DoesNotContain("sessionStorage", html);
        Assert.DoesNotContain("indexedDB", html);
        Assert.DoesNotContain("document.cookie", html);
    }

    [Fact]
    public async Task MonitorPage_LinksToTheSettingsPage()
    {
        var http = await StartAsync();

        var monitor = await http.GetStringAsync("/");

        Assert.Matches(@"href=""/settings""", monitor);
    }

    [Fact]
    public async Task SettingsPage_ForAnotherHost_Is400()
    {
        var http = await StartAsync();

        using var request = new HttpRequestMessage(HttpMethod.Get, "/settings");
        request.Headers.Host = "evil.example";
        using var response = await http.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task PasswordField_IsNeverRemembered()
    {
        var html = await PageAsync();

        var field = PasswordInput().Match(html);
        Assert.True(field.Success, "no password input on the page");
        Assert.Contains("autocomplete=\"new-password\"", field.Value);
    }

    private async Task<string> PageAsync() => await (await StartAsync()).GetStringAsync("/settings");

    private async Task<HttpClient> StartAsync()
    {
        _edge = EdgeTestHost.Create(
            new Dictionary<string, string?>
            {
                ["Edge:Monitor:Enabled"] = "true",
                ["Edge:Monitor:Port"] = _port.ToString(CultureInfo.InvariantCulture),
            },
            services => EdgeTestHost.WithoutHostedService<Polling.TagPoller>(services),
            runAgentServices: true);
        await _edge.Host.StartAsync();
        _http = new HttpClient { BaseAddress = new Uri($"http://localhost:{_port}") };
        return _http;
    }

    private static string Single(HttpResponseMessage response, string header) =>
        response.Headers.TryGetValues(header, out var values) || response.Content.Headers.TryGetValues(header, out values)
            ? string.Join(",", values)
            : "";

    private static int FreePort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        return ((IPEndPoint)probe.LocalEndpoint).Port;
    }

    [GeneratedRegex(@"<input[^>]*type=""password""[^>]*>")]
    private static partial Regex PasswordInput();
}
