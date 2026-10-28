using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using IndustrialIot.Cloud.Tests.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Options;

namespace IndustrialIot.Cloud.Tests;

/// <summary>
/// The SCADA page is reachable without a token, holds no data, and leaves every data path behind the Viewer policy.
/// </summary>
[Collection(CloudCollection.Name)]
public class ScadaPageTests(PostgresFixture database)
{
    // CloudTestHost.Issuer is https://idp.test/, so the page may talk only to its own origin and that IdP.
    private const string ExpectedCsp =
        "default-src 'none'; script-src 'self'; style-src 'self' 'unsafe-inline'; connect-src 'self' https://idp.test; " +
        "img-src 'self' data:; frame-ancestors 'none'; base-uri 'none'; form-action 'self' https://idp.test";

    // SHA-256 of dist/browser/signalr.min.js in the @microsoft/signalr 8.0.7 npm tarball (registry integrity checked).
    private const string SignalrSha256 = "e28a720a359b37cb015758d543f908730ed5bbe478db09506bb6887f18313538";

    private static readonly Dictionary<string, string?> Disabled = new() { ["Cloud:Scada:Enabled"] = "false" };

    [Theory]
    [InlineData("/scada/", "text/html; charset=utf-8")]
    [InlineData("/scada/app.js", "text/javascript; charset=utf-8")]
    [InlineData("/scada/scada-core.js", "text/javascript; charset=utf-8")]
    [InlineData("/scada/signalr.min.js", "text/javascript; charset=utf-8")]
    [InlineData("/scada/config", "application/json; charset=utf-8")]
    public async Task ServedPath_AnonymousWithSecurityHeaders(string path, string contentType)
    {
        await using var host = new CloudTestHost(database);
        using var client = host.CreateClient();

        var response = await client.GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(contentType, response.Content.Headers.ContentType?.ToString());
        AssertSecurityHeaders(response);
    }

    [Fact]
    public async Task Shell_LoadsSignalrAsClassicScriptBeforeTheAppModule_WithoutInlineScript()
    {
        await using var host = new CloudTestHost(database);
        using var client = host.CreateClient();

        var html = await client.GetStringAsync("/scada/");

        var signalr = html.IndexOf("<script src=\"signalr.min.js\"></script>", StringComparison.Ordinal);
        var app = html.IndexOf("<script type=\"module\" src=\"app.js\"></script>", StringComparison.Ordinal);
        Assert.True(signalr >= 0 && app > signalr, "signalr.min.js must load as a classic script before app.js");

        // Every script tag has a src: CSP script-src 'self' would block an inline one anyway.
        Assert.Equal(2, CountOf(html, "<script"));
        Assert.Equal(2, CountOf(html, "<script src=\"") + CountOf(html, "<script type=\"module\" src=\""));
    }

    [Fact]
    public async Task Signalr_IsTheVendoredBuildByteForByte()
    {
        await using var host = new CloudTestHost(database);
        using var client = host.CreateClient();

        var bytes = await client.GetByteArrayAsync("/scada/signalr.min.js");

        Assert.Equal(SignalrSha256, Convert.ToHexStringLower(SHA256.HashData(bytes)));
    }

    [Fact]
    public async Task Config_IsTheApiAuthorityAndTheConfiguredClient()
    {
        await using var host = new CloudTestHost(database, new Dictionary<string, string?> { ["Cloud:Scada:ClientId"] = "scada-test" });
        using var client = host.CreateClient();

        var config = JsonDocument.Parse(await client.GetStringAsync("/scada/config")).RootElement;

        Assert.Equal(CloudTestHost.Issuer, config.GetProperty("authority").GetString());
        Assert.Equal("scada-test", config.GetProperty("clientId").GetString());
        Assert.Equal(2, config.EnumerateObject().Count()); // nothing else, in particular no secret
    }

    [Fact]
    public async Task Config_DefaultClientIsIiotScada()
    {
        await using var host = new CloudTestHost(database);
        using var client = host.CreateClient();

        var config = JsonDocument.Parse(await client.GetStringAsync("/scada/config")).RootElement;

        Assert.Equal("iiot-scada", config.GetProperty("clientId").GetString());
    }

    [Fact]
    public async Task BarePath_RedirectsToTheSlashedPage()
    {
        await using var host = new CloudTestHost(database);
        using var client = host.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var response = await client.GetAsync("/scada");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/scada/", response.Headers.Location?.OriginalString);
        AssertSecurityHeaders(response);
    }

    [Theory]
    [InlineData("/scada/unknown.js")]
    [InlineData("/scada/index.html")] // served only as /scada/
    [InlineData("/scada/..%2fappsettings.json")] // %2f stays encoded, so the path stays under /scada
    public async Task UnknownPath_Anonymous_Returns404(string path)
    {
        await using var host = new CloudTestHost(database);
        using var client = host.CreateClient();

        var response = await client.GetAsync(path);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.DoesNotContain("ConnectionString", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        AssertSecurityHeaders(response);
    }

    [Fact]
    public async Task DotSegmentPath_NeverServesTheSettingsFile()
    {
        await using var host = new CloudTestHost(database);
        using var client = host.CreateClient();

        // System.Uri resolves %2e%2e to "..", so the request leaves /scada (it becomes /appsettings.json).
        var response = await client.GetAsync("/scada/%2e%2e/appsettings.json");

        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain("\"Cloud\"", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/scada")]
    [InlineData("/scada/")]
    [InlineData("/scada/app.js")]
    [InlineData("/scada/scada-core.js")]
    [InlineData("/scada/signalr.min.js")]
    [InlineData("/scada/config")]
    [InlineData("/scada/unknown.js")]
    public async Task Disabled_EveryScadaPath_Returns404(string path)
    {
        await using var host = new CloudTestHost(database, Disabled);
        using var client = host.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var response = await client.GetAsync(path);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task DataPaths_StillRequireTheViewerRole()
    {
        await using var host = new CloudTestHost(database);
        using var client = host.CreateClient();
        var roleless = host.CreateToken([]);

        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(client, HttpMethod.Get, "/api/v1/edges", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(client, HttpMethod.Post, Negotiate, null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(client, HttpMethod.Get, "/api/v1/edges", roleless)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(client, HttpMethod.Post, Negotiate, roleless)).StatusCode);

        // Control: a Viewer gets through both.
        var viewer = host.CreateToken(["Viewer"]);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(client, HttpMethod.Get, "/api/v1/edges", viewer)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(client, HttpMethod.Post, Negotiate, viewer)).StatusCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("has space")]
    [InlineData("quote\"")]
    public async Task InvalidClientId_FailsAtStart(string clientId)
    {
        await using var host = new CloudTestHost(database, new Dictionary<string, string?> { ["Cloud:Scada:ClientId"] = clientId });

        var error = Record.Exception(() => host.CreateClient());

        var validation = Find<OptionsValidationException>(error);
        Assert.NotNull(validation);
        Assert.Contains("Cloud:Scada:ClientId", validation.Message, StringComparison.Ordinal);
    }

    private const string Negotiate = "/hubs/live/negotiate?negotiateVersion=1";

    private static void AssertSecurityHeaders(HttpResponseMessage response)
    {
        Assert.Equal("no-store", Header(response, "Cache-Control"));
        Assert.Equal("nosniff", Header(response, "X-Content-Type-Options"));
        Assert.Equal("no-referrer", Header(response, "Referrer-Policy"));
        Assert.Equal(ExpectedCsp, Header(response, "Content-Security-Policy"));
    }

    private static string? Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) ? string.Join(", ", values) : null;

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, string path, string? token)
    {
        using var request = new HttpRequestMessage(method, path);
        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return await client.SendAsync(request);
    }

    private static int CountOf(string text, string value)
    {
        var count = 0;
        for (var i = text.IndexOf(value, StringComparison.Ordinal); i >= 0; i = text.IndexOf(value, i + 1, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    private static T? Find<T>(Exception? error)
        where T : Exception
    {
        for (var e = error; e is not null; e = e.InnerException)
        {
            if (e is T match)
            {
                return match;
            }
        }

        return null;
    }
}
