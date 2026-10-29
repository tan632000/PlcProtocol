using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using IndustrialIot.Edge.Drivers;
using IndustrialIot.Edge.Drivers.Models;
using IndustrialIot.Edge.Drivers.Slmp;
using IndustrialIot.Edge.Polling;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;

namespace IndustrialIot.Edge.Settings;

/// <summary>
/// The settings API of the loopback monitor: <c>GET /api/settings</c>, <c>POST /api/settings/validate</c> and
/// <c>PUT /api/settings</c>. A save is checked in this order and refused at the first failing step, before anything
/// is written: request guard (cross-site, media type, size), shape, the Edge's own startup validators, the writable
/// rule, revision, password, apply already running. Only then is the apply queued; the supervisor rebuilds the agent
/// after the response has been sent. Errors give key paths and fixed text, never the submitted values.
/// </summary>
public sealed partial class SettingsService(
    IConfiguration configuration,
    EdgeSettingsFile file,
    IOptions<PlcTagCatalogOptions> tags,
    PlcTagCatalog catalog,
    IPlcAddressSyntax syntax,
    IOptions<PlcProtocolOptions> protocol,
    IServiceProvider services,
    IOptions<PollOptions> poll,
    SettingsPassword password,
    EdgeRestarter? restarter = null)
{
    public const int MaxBodyBytes = 1024 * 1024;
    public const int MaxTags = 1000;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly string[] TopLevelKeys = ["revision", "password", "settings"];

    private const string ShapeMessage =
        "Not allowed here: only Plc (Protocol, Host, Port, Rack, Slot, CpuType, TimeoutMs), Poll (IntervalMs) and Tags (TagId, Address, DataType, Writable, Min, Max), each a single value.";

    public async Task GetAsync(HttpContext context)
    {
        await context.Response.WriteAsJsonAsync(
            new
            {
                revision = file.Revision,
                source = file.Exists ? "file" : "defaults",
                settings = CurrentSettings(),
                writableTags = tags.Value.Tags.Where(t => t.Writable).Select(t => t.TagId).ToArray(),
                passwordSet = File.Exists(SettingsPassword.PathFor(file.Path)),
            },
            Json).ConfigureAwait(false);
    }

    public async Task ValidateAsync(HttpContext context, int monitorPort)
    {
        var body = await ReadGuardedAsync(context, monitorPort).ConfigureAwait(false);
        if (body is null)
        {
            return;
        }

        using (body)
        {
            var errors = new List<SettingsError>();
            if (TopLevel(body.RootElement, errors) is { } settings)
            {
                Check(settings, errors);
            }

            await context.Response.WriteAsJsonAsync(new { ok = errors.Count == 0, errors }, Json).ConfigureAwait(false);
        }
    }

    public async Task SaveAsync(HttpContext context, int monitorPort)
    {
        var body = await ReadGuardedAsync(context, monitorPort).ConfigureAwait(false);
        if (body is null)
        {
            return;
        }

        using (body)
        {
            var root = body.RootElement;
            var errors = new List<SettingsError>();
            var settings = TopLevel(root, errors);
            var bytes = settings is { } proposed ? Check(proposed, errors) : null;
            if (errors.Count > 0 || bytes is null)
            {
                await Reply(context, StatusCodes.Status400BadRequest, new { ok = false, errors }).ConfigureAwait(false);
                return;
            }

            if (!root.TryGetProperty("revision", out var revision) || revision.ValueKind != JsonValueKind.String
                || revision.GetString() != file.Revision)
            {
                await Reply(context, StatusCodes.Status409Conflict, new { error = "stale-revision", revision = file.Revision }).ConfigureAwait(false);
                return;
            }

            if (!await PasswordAcceptedAsync(context, root, password, file).ConfigureAwait(false))
            {
                return;
            }

            if (restarter is null)
            {
                await Reply(context, StatusCodes.Status503ServiceUnavailable, new { error = "no-supervisor" }).ConfigureAwait(false);
                return;
            }

            if (!restarter.TryRequest(bytes, out var applyId))
            {
                await Reply(context, StatusCodes.Status409Conflict, new { error = "apply-in-progress" }).ConfigureAwait(false);
                return;
            }

            // The supervisor stops this very host: let the response go out first.
            context.Response.OnCompleted(() =>
            {
                restarter.Release(applyId);
                return Task.CompletedTask;
            });
            await Reply(context, StatusCodes.Status202Accepted, new { applyId }).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Refuses a request another site could have sent from the operator's browser (D-11), before any password is
    /// looked at: a cross-site <c>Origin</c> or <c>Sec-Fetch-Site</c>, a media type other than application/json (a
    /// "simple" cross-site request cannot send that), or a body over 1 MB. Returns the parsed body, or null when the
    /// response has been written.
    /// </summary>
    internal static async Task<JsonDocument?> ReadGuardedAsync(HttpContext context, int monitorPort)
    {
        var request = context.Request;
        var origin = request.Headers.Origin.ToString();
        if (origin.Length > 0
            && origin != $"http://localhost:{monitorPort}" && origin != $"http://127.0.0.1:{monitorPort}")
        {
            await Reply(context, StatusCodes.Status403Forbidden, new { error = "cross-site" }).ConfigureAwait(false);
            return null;
        }

        if (string.Equals(request.Headers["Sec-Fetch-Site"].ToString(), "cross-site", StringComparison.OrdinalIgnoreCase))
        {
            await Reply(context, StatusCodes.Status403Forbidden, new { error = "cross-site" }).ConfigureAwait(false);
            return null;
        }

        if (!MediaTypeHeaderValue.TryParse(request.ContentType, out var mediaType)
            || !mediaType.MediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase))
        {
            await Reply(context, StatusCodes.Status415UnsupportedMediaType, new { error = "json-only" }).ConfigureAwait(false);
            return null;
        }

        var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await request.Body.ReadAsync(chunk, context.RequestAborted).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > MaxBodyBytes)
            {
                await Reply(context, StatusCodes.Status413PayloadTooLarge, new { error = "too-large" }).ConfigureAwait(false);
                return null;
            }

            buffer.Write(chunk, 0, read);
        }

        try
        {
            return JsonDocument.Parse(buffer.ToArray());
        }
        catch (JsonException)
        {
            await Reply(context, StatusCodes.Status400BadRequest,
                new { ok = false, errors = new[] { new SettingsError("", "The body is not valid JSON.") } }).ConfigureAwait(false);
            return null;
        }
    }

    private static JsonElement? TopLevel(JsonElement root, List<SettingsError> errors)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            errors.Add(new SettingsError("", "The body must be a JSON object."));
            return null;
        }

        foreach (var property in root.EnumerateObject())
        {
            if (!TopLevelKeys.Contains(property.Name, StringComparer.Ordinal))
            {
                errors.Add(new SettingsError(property.Name, "Unknown key."));
            }
        }

        if (!root.TryGetProperty("settings", out var settings) || settings.ValueKind != JsonValueKind.Object)
        {
            errors.Add(new SettingsError("settings", "Required, as an object with Plc, Poll and Tags."));
            return null;
        }

        return errors.Count == 0 ? settings : null;
    }

    /// <summary>Runs every check on proposed settings; returns the file bytes to save when there is no error.</summary>
    private byte[]? Check(JsonElement settings, List<SettingsError> errors)
    {
        JsonObject edge;
        try
        {
            edge = JsonNode.Parse(settings.GetRawText())!.AsObject();
        }
        catch (JsonException)
        {
            errors.Add(new SettingsError("settings", "Not valid JSON."));
            return null;
        }

        var candidate = FileBytes(edge);
        IReadOnlyList<KeyValuePair<string, string?>> pairs;
        try
        {
            pairs = EdgeSettingsFile.ParseOwned(candidate, "proposed settings");
        }
        catch (InvalidOperationException ex)
        {
            errors.Add(new SettingsError(PathOf(ex.Message), ShapeMessage));
            return null;
        }

        var proposed = Bind(pairs, errors);
        if (proposed is null)
        {
            return null;
        }

        // An empty tag object binds to nothing and would shift the binder's indices against the JSON array.
        var tagsNode = edge.FirstOrDefault(p => p.Key.Equals("Tags", StringComparison.OrdinalIgnoreCase)).Value as JsonArray;
        if ((tagsNode?.Count ?? 0) != proposed.Tags.Length)
        {
            errors.Add(new SettingsError("Edge:Tags", "Every tag must be a non-empty object."));
            return null;
        }

        if (proposed.Tags.Length > MaxTags)
        {
            errors.Add(new SettingsError("Edge:Tags", $"At most {MaxTags} tags."));
            return null;
        }

        // The startup validators run below even when the writable rule failed, so the page lists every error at once.
        var kept = ApplyWritableRule(proposed, errors);

        // Kept writable tags are saved writable again; everything else as sent (Writable false).
        foreach (var index in kept)
        {
            var tag = tagsNode![index]!.AsObject();
            foreach (var key in tag.Select(p => p.Key).Where(k => k.Equals("Writable", StringComparison.OrdinalIgnoreCase)).ToList())
            {
                tag.Remove(key);
            }

            tag["Writable"] = true;
        }

        var bytes = FileBytes(edge);
        RunStartupValidators(EdgeSettingsFile.ParseOwned(bytes, "proposed settings"), proposed.Tags, errors);
        return errors.Count == 0 ? bytes : null;
    }

    private static byte[] FileBytes(JsonObject edge) =>
        JsonSerializer.SerializeToUtf8Bytes(new JsonObject
        {
            ["SchemaVersion"] = EdgeSettingsFile.SchemaVersion,
            ["Edge"] = JsonNode.Parse(edge.ToJsonString()),
        });

    private static ProposedSettings? Bind(IReadOnlyList<KeyValuePair<string, string?>> pairs, List<SettingsError> errors)
    {
        var view = new ConfigurationBuilder().AddInMemoryCollection(pairs).Build();
        try
        {
            var plc = view.GetSection(PlcProtocolOptions.SectionName);
            return new ProposedSettings(
                view.GetSection(EdgeOptions.SectionName).Get<PlcTagCatalogOptions>()?.Tags ?? [],
                plc[nameof(PlcProtocolOptions.Protocol)],
                plc.Get<SiemensS7Options>() ?? new SiemensS7Options(),
                plc.Get<SlmpOptions>() ?? new SlmpOptions());
        }
        catch (InvalidOperationException ex)
        {
            // The binder names the value before the key ("value '…' at 'Edge:…'"): take the key after "at".
            var path = BinderPathPattern().Match(ex.Message) is { Success: true } m ? m.Groups[1].Value : "";
            errors.Add(new SettingsError(path, "The value does not have the expected type."));
            return null;
        }
    }

    /// <summary>
    /// D-06: the page cannot make a tag writable. A writable tag survives a save only when sent back exactly as it is
    /// (ordinal TagId, canonical address, DataType, Min, Max), and while one survives the PLC cannot change, or
    /// Cloud commands would land on another PLC or address. Returns the indices of the kept writable tags.
    /// </summary>
    private List<int> ApplyWritableRule(ProposedSettings proposed, List<SettingsError> errors)
    {
        var writable = tags.Value.Tags.Where(t => t.Writable).ToList();
        var kept = new List<int>();
        for (var i = 0; i < proposed.Tags.Length; i++)
        {
            var tag = proposed.Tags[i];
            if (tag.Writable)
            {
                errors.Add(new SettingsError($"Edge:Tags:{i}:Writable",
                    "The settings page cannot make a tag writable; an administrator edits Writable in the settings file."));
                continue;
            }

            var current = writable.FirstOrDefault(w => w.TagId.Equals(tag.TagId, StringComparison.OrdinalIgnoreCase));
            if (current is null)
            {
                continue;
            }

            if (Unchanged(current, tag))
            {
                kept.Add(i);
            }
            else
            {
                errors.Add(new SettingsError($"Edge:Tags:{i}",
                    "A writable tag can only be kept unchanged (TagId, Address, DataType, Min, Max) or deleted."));
            }
        }

        if (kept.Count > 0)
        {
            foreach (var key in ChangedPlcKeys(proposed))
            {
                errors.Add(new SettingsError($"Edge:Plc:{key}",
                    "The PLC cannot change while writable tags are kept; delete them first, or an administrator edits the settings file."));
            }
        }

        return kept;
    }

    private bool Unchanged(PlcTagDefinition current, PlcTagDefinition proposed)
    {
        if (!string.Equals(current.TagId, proposed.TagId, StringComparison.Ordinal)
            || current.DataType != proposed.DataType || current.Min != proposed.Min || current.Max != proposed.Max
            || !catalog.TryGet(current.TagId, out var resolved))
        {
            return false;
        }

        try
        {
            return syntax.Parse(proposed.Address, proposed.DataType).Canonical == resolved.Address.Canonical;
        }
        catch (PlcException)
        {
            return false;
        }
    }

    private IEnumerable<string> ChangedPlcKeys(ProposedSettings proposed)
    {
        PlcProtocolOptions.TryParse(protocol.Value.Protocol, out var currentProtocol);
        if (!PlcProtocolOptions.TryParse(proposed.Protocol, out var proposedProtocol) || proposedProtocol != currentProtocol)
        {
            yield return nameof(PlcProtocolOptions.Protocol);
            yield break;
        }

        if (currentProtocol == PlcProtocol.Slmp)
        {
            var now = services.GetRequiredService<IOptions<SlmpOptions>>().Value;
            if (!string.Equals(now.Host, proposed.Slmp.Host, StringComparison.OrdinalIgnoreCase))
            {
                yield return nameof(SlmpOptions.Host);
            }

            if (now.Port != proposed.Slmp.Port)
            {
                yield return nameof(SlmpOptions.Port);
            }

            yield break;
        }

        var s7 = services.GetRequiredService<IOptions<SiemensS7Options>>().Value;
        if (!string.Equals(s7.Host, proposed.S7.Host, StringComparison.OrdinalIgnoreCase))
        {
            yield return nameof(SiemensS7Options.Host);
        }

        if (s7.Port != proposed.S7.Port)
        {
            yield return nameof(SiemensS7Options.Port);
        }

        if (s7.Rack != proposed.S7.Rack)
        {
            yield return nameof(SiemensS7Options.Rack);
        }

        if (s7.Slot != proposed.S7.Slot)
        {
            yield return nameof(SiemensS7Options.Slot);
        }

        if (!string.Equals(s7.CpuType, proposed.S7.CpuType, StringComparison.OrdinalIgnoreCase))
        {
            yield return nameof(SiemensS7Options.CpuType);
        }
    }

    /// <summary>
    /// Registers the agent exactly as a start would, on the proposed settings, and resolves the validated options: the
    /// same validators decide, no rule is copied. The buffer path points nowhere, so no settings file is read.
    /// </summary>
    private void RunStartupValidators(
        IReadOnlyList<KeyValuePair<string, string?>> proposed, PlcTagDefinition[] proposedTags, List<SettingsError> errors)
    {
        var values = configuration.AsEnumerable()
            .Where(pair => pair.Value is not null && !EdgeSettingsFile.IsOwned(pair.Key))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        values["Edge:Buffer:Path"] = Path.Combine(Path.GetTempPath(), "edge-settings-validate", Guid.NewGuid().ToString("N"), "none.db");
        foreach (var (key, value) in proposed)
        {
            values[key] = value;
        }

        var view = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var registrations = new ServiceCollection();
        registrations.AddLogging();
        try
        {
            registrations.AddEdgeAgent(view);
        }
        catch (Exception ex) when (ex is OptionsValidationException or InvalidOperationException)
        {
            AddFailures(ex, proposedTags, errors);
            return;
        }

        using var provider = registrations.BuildServiceProvider();
        PlcProtocolOptions.TryParse(view[PlcProtocolOptions.KeyName], out var chosen);
        var checks = new List<Func<object>>
        {
            () => provider.GetRequiredService<IOptions<PlcProtocolOptions>>().Value,
            () => provider.GetRequiredService<IOptions<PollOptions>>().Value,
            () => provider.GetRequiredService<IOptions<PlcTagCatalogOptions>>().Value,
            chosen == PlcProtocol.Slmp
                ? () => provider.GetRequiredService<IOptions<SlmpOptions>>().Value
                : () => provider.GetRequiredService<IOptions<SiemensS7Options>>().Value,
        };
        foreach (var check in checks)
        {
            try
            {
                _ = check();
            }
            catch (Exception ex) when (ex is OptionsValidationException or InvalidOperationException)
            {
                AddFailures(ex, proposedTags, errors);
            }
        }
    }

    /// <summary>
    /// Validator text names submitted values (addresses, ids, numbers), so the response carries only the key path and
    /// a fixed message. A tag error is pinned to its row when the validator text names that tag's id.
    /// </summary>
    private static void AddFailures(Exception ex, PlcTagDefinition[] proposedTags, List<SettingsError> errors)
    {
        var failures = ex is OptionsValidationException validation ? validation.Failures : [ex.Message];
        foreach (var failure in failures)
        {
            var path = PathOf(failure);
            if (path.StartsWith("Edge:Tags", StringComparison.Ordinal))
            {
                var row = Array.FindIndex(proposedTags, t => t.TagId.Length > 0 && failure.Contains($"'{t.TagId}'", StringComparison.Ordinal));
                path = row >= 0 ? $"Edge:Tags:{row}" : "Edge:Tags";
            }

            errors.Add(new SettingsError(path, FixedMessage(path)));
        }
    }

    internal static string FixedMessage(string path) => path switch
    {
        _ when path.StartsWith("Edge:Tags", StringComparison.Ordinal) =>
            "Tag is not valid: check TagId, Address and DataType, duplicate ids (case-insensitive), Min/Max, and overlap with writable tags.",
        "Edge:Plc:Protocol" => "Protocol must be S7 or Slmp, and only S7 allows commands.",
        _ when path.StartsWith("Edge:Plc:", StringComparison.Ordinal) => $"{path} is not valid for this protocol.",
        _ when path.StartsWith("Edge:Poll:", StringComparison.Ordinal) => $"{path} is out of range.",
        _ => "The settings are not valid.",
    };
    private object CurrentSettings()
    {
        PlcProtocolOptions.TryParse(protocol.Value.Protocol, out var selected);
        object plc;
        if (selected == PlcProtocol.Slmp)
        {
            var o = services.GetRequiredService<IOptions<SlmpOptions>>().Value;
            plc = new { Protocol = nameof(PlcProtocol.Slmp), o.Host, o.Port, o.TimeoutMs };
        }
        else
        {
            var o = services.GetRequiredService<IOptions<SiemensS7Options>>().Value;
            plc = new { Protocol = nameof(PlcProtocol.S7), o.Host, o.Port, o.Rack, o.Slot, o.CpuType, o.TimeoutMs };
        }

        return new
        {
            Plc = plc,
            Poll = new { poll.Value.IntervalMs },
            Tags = tags.Value.Tags.Select(t => new
            {
                t.TagId,
                t.Address,
                DataType = t.DataType.ToString(),
                t.Writable,
                t.Min,
                t.Max,
            }).ToArray(),
        };
    }

    /// <summary>Checks the "password" property; writes 401/403/429 and returns false when it does not pass.</summary>
    internal static async Task<bool> PasswordAcceptedAsync(HttpContext context, JsonElement root, SettingsPassword password, EdgeSettingsFile file)
    {
        var secret = root.TryGetProperty("password", out var given) && given.ValueKind == JsonValueKind.String ? given.GetString() : null;
        switch (password.Verify(SettingsPassword.PathFor(file.Path), secret))
        {
            case PasswordVerdict.Ok:
                return true;
            case PasswordVerdict.NotSet:
                await Reply(context, StatusCodes.Status403Forbidden, new { error = "password-not-set" }).ConfigureAwait(false);
                return false;
            case PasswordVerdict.LockedOut:
                await Reply(context, StatusCodes.Status429TooManyRequests,
                    new { error = "locked", retryAfterSeconds = (int)Math.Ceiling(password.LockoutRemaining.TotalSeconds) }).ConfigureAwait(false);
                return false;
            default:
                await Reply(context, StatusCodes.Status401Unauthorized, new { error = "wrong-password" }).ConfigureAwait(false);
                return false;
        }
    }

    internal static Task Reply(HttpContext context, int status, object body)
    {
        context.Response.StatusCode = status;
        return context.Response.WriteAsJsonAsync(body, Json);
    }

    /// <summary>The first configuration key named in a message (e.g. <c>Edge:Plc:Host</c>), or empty.</summary>
    internal static string PathOf(string message) => KeyPattern().Match(message) is { Success: true } m ? m.Value : "";

    [GeneratedRegex(@"Edge(:[A-Za-z0-9_]+)+")]
    private static partial Regex KeyPattern();

    [GeneratedRegex(@" at '(Edge(:[A-Za-z0-9_]+)+)'")]
    private static partial Regex BinderPathPattern();

    private sealed record ProposedSettings(PlcTagDefinition[] Tags, string? Protocol, SiemensS7Options S7, SlmpOptions Slmp);
}

public sealed record SettingsError(string Path, string Message);
