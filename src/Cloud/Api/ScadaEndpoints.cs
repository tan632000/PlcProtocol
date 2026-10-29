using System.Collections.Frozen;
using IndustrialIot.Cloud.Security;
using Microsoft.Extensions.Options;

namespace IndustrialIot.Cloud.Api;

/// <summary>Login settings the page reads before it has a token.</summary>
public sealed record ScadaConfig(string Authority, string ClientId);

/// <summary>
/// The display-only SCADA page: <c>/scada/</c>, its scripts and <c>/scada/config</c>, served without a token because
/// they hold no data; every data path keeps the Viewer policy. Only a fixed set of embedded files is served (never the
/// file system). Every <c>/scada</c> path is mapped anonymously, so a miss is a 404 rather than the fallback's 401.
/// </summary>
internal sealed class ScadaEndpoints : IEndpointModule
{
    private const string Html = "text/html; charset=utf-8";
    private const string JavaScript = "text/javascript; charset=utf-8";

    // Request path below /scada/ → embedded resource (LogicalName in the csproj) and its content type.
    private static readonly FrozenDictionary<string, (string Resource, string ContentType)> Files =
        new Dictionary<string, (string, string)>(StringComparer.Ordinal)
        {
            [""] = ("IndustrialIot.Cloud.Scada.index.html", Html),
            ["app.js"] = ("IndustrialIot.Cloud.Scada.app.js", JavaScript),
            ["scada-core.js"] = ("IndustrialIot.Cloud.Scada.scada-core.js", JavaScript),
            ["signalr.min.js"] = ("IndustrialIot.Cloud.Scada.signalr.min.js", JavaScript),
        }.ToFrozenDictionary(StringComparer.Ordinal);

    private static readonly Lazy<FrozenDictionary<string, byte[]>> Contents = new(LoadContents);

    public void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/scada/config", Config).AllowAnonymous();

        // Also matches "/scada" (the catch-all is optional); routing ignores a trailing slash, so a separate
        // "/scada" route would match "/scada/" too and redirect it to itself.
        endpoints.MapGet("/scada/{**path}", Serve).AllowAnonymous();
    }

    private static IResult Config(HttpContext context, IOptions<ScadaOptions> scada, IOptions<AuthOptions> auth)
    {
        SetHeaders(context, auth.Value);
        return scada.Value.Enabled ? Results.Json(new ScadaConfig(auth.Value.Authority, scada.Value.ClientId)) : NotFound();
    }

    private static IResult Serve(string? path, HttpContext context, IOptions<ScadaOptions> scada, IOptions<AuthOptions> auth)
    {
        SetHeaders(context, auth.Value);
        if (!scada.Value.Enabled)
        {
            return NotFound();
        }

        // Relative URLs in the page (app.js, signalr.min.js) resolve against /scada/, not against /.
        if (!context.Request.Path.Value!.EndsWith('/') && string.IsNullOrEmpty(path))
        {
            return Results.Redirect("/scada/");
        }

        if (!Files.TryGetValue(path ?? "", out var file))
        {
            return NotFound();
        }

        return Results.Bytes(Contents.Value[file.Resource], file.ContentType);
    }

    // On every /scada response, including redirects and 404s.
    private static void SetHeaders(HttpContext context, AuthOptions auth)
    {
        // The validator guarantees an absolute https authority; the browser talks to its origin for login and tokens.
        var idp = new Uri(auth.Authority).GetLeftPart(UriPartial.Authority);
        var headers = context.Response.Headers;
        headers.CacheControl = "no-store";
        headers.XContentTypeOptions = "nosniff";
        headers["Referrer-Policy"] = "no-referrer";
        headers.ContentSecurityPolicy =
            $"default-src 'none'; script-src 'self'; style-src 'self' 'unsafe-inline'; connect-src 'self' {idp}; " +
            // form-action also covers the redirect back here after the logout form is posted to the IdP.
            $"img-src 'self' data:; frame-ancestors 'none'; base-uri 'none'; form-action 'self' {idp}";
    }

    private static IResult NotFound() => Results.Problem(title: "not_found", statusCode: StatusCodes.Status404NotFound);

    private static FrozenDictionary<string, byte[]> LoadContents()
    {
        var assembly = typeof(ScadaEndpoints).Assembly;
        return Files.Values.ToFrozenDictionary(f => f.Resource, f =>
        {
            using var stream = assembly.GetManifestResourceStream(f.Resource)
                ?? throw new InvalidOperationException($"Embedded SCADA file {f.Resource} is missing.");
            using var copy = new MemoryStream();
            stream.CopyTo(copy);
            return copy.ToArray();
        }, StringComparer.Ordinal);
    }
}
