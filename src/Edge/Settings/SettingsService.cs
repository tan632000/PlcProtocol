using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using IndustrialIot.Edge.Commands;
using IndustrialIot.Edge.Configuration;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;

namespace IndustrialIot.Edge.Settings;

/// <summary>
/// The settings API of the loopback monitor: <c>GET /api/settings</c>, <c>POST /api/settings/validate</c> and
/// <c>PUT /api/settings</c>, on the Channel → Device → Tag tree of a schema-2 settings file. A save is checked in this
/// order and refused at the first failing step, before anything is written: request guard (cross-site, media type,
/// size), the tree's shape and limits, the validator, each device's own checks, the Edge's startup registration,
/// revision, password, apply already running. Only then is the apply queued; the supervisor rebuilds the agent after
/// the response has been sent. Errors give the node's key path and a fixed text, never the submitted values.
/// </summary>
public sealed partial class SettingsService(
    IConfiguration configuration,
    EdgeSettingsFile file,
    EdgeDeviceSet devices,
    IOptions<CommandsOptions> commands,
    SettingsPassword password,
    EdgeRestarter? restarter = null)
{
    /// <summary>The settings routes' body limit: 5000 tags with descriptions fit.</summary>
    public const int MaxBodyBytes = 4 * 1024 * 1024;

    /// <summary>The body limit of the other routes that share the guard.</summary>
    public const int DefaultMaxBodyBytes = 1024 * 1024;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly string[] TopLevelKeys = ["revision", "password", "settings"];

    private const string ShapeMessage =
        "Not allowed here: only the keys of a channel (Name, Driver, Devices), a device or a tag, each a single value.";

    public async Task GetAsync(HttpContext context)
    {
        await context.Response.WriteAsJsonAsync(
            new
            {
                revision = file.Revision,
                source = file.Exists ? "file" : "defaults",
                settings = new { Channels = SettingsTree.Channels(devices) },
                models = SettingsTree.Models(),
                warnings = SettingsTree.Warnings(devices),
                commandsEnabled = commands.Value.Enabled,
                passwordSet = File.Exists(SettingsPassword.PathFor(file.Path)),
            },
            Json).ConfigureAwait(false);
    }

    public async Task ValidateAsync(HttpContext context, int monitorPort)
    {
        var body = await ReadGuardedAsync(context, monitorPort, MaxBodyBytes).ConfigureAwait(false);
        if (body is null)
        {
            return;
        }

        using (body)
        {
            var errors = new List<SettingsError>();
            if (TopLevel(body.RootElement, errors) is { } settings)
            {
                Check(settings, errors, out _);
            }

            await context.Response.WriteAsJsonAsync(new { ok = errors.Count == 0, errors }, Json).ConfigureAwait(false);
        }
    }

    public async Task SaveAsync(HttpContext context, int monitorPort)
    {
        var body = await ReadGuardedAsync(context, monitorPort, MaxBodyBytes).ConfigureAwait(false);
        if (body is null)
        {
            return;
        }

        using (body)
        {
            var root = body.RootElement;
            var errors = new List<SettingsError>();
            var settings = TopLevel(root, errors);
            EdgeChannelsOptions? tree = null;
            var bytes = settings is { } proposed ? Check(proposed, errors, out tree) : null;
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

            if (!restarter.TryRequest(bytes, out var applyId, WriteChanges(devices, tree!)))
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
    /// "simple" cross-site request cannot send that), or a body over <paramref name="maxBodyBytes"/>. Returns the parsed
    /// body, or null when the response has been written.
    /// </summary>
    internal static async Task<JsonDocument?> ReadGuardedAsync(HttpContext context, int monitorPort, int maxBodyBytes = DefaultMaxBodyBytes)
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
            if (buffer.Length + read > maxBodyBytes)
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
            errors.Add(new SettingsError("settings", "Required, as an object with Channels."));
            return null;
        }

        return errors.Count == 0 ? settings : null;
    }

    /// <summary>Runs every check on a proposed tree; returns the file bytes to save when there is no error.</summary>
    private byte[]? Check(JsonElement settings, List<SettingsError> errors, out EdgeChannelsOptions? tree)
    {
        tree = null;
        SettingsTree.CheckShapeAndLimits(settings, errors);
        if (errors.Count > 0)
        {
            return null;
        }

        var bytes = JsonSerializer.SerializeToUtf8Bytes(new JsonObject
        {
            ["SchemaVersion"] = EdgeSettingsFile.MultiDeviceSchemaVersion,
            ["Edge"] = JsonNode.Parse(settings.GetRawText()),
        });

        IReadOnlyList<KeyValuePair<string, string?>> pairs;
        try
        {
            pairs = EdgeSettingsFile.ParseOwned(bytes, "proposed settings");
        }
        catch (InvalidOperationException ex)
        {
            errors.Add(new SettingsError(NodeOf(PathOf(ex.Message)), ShapeMessage));
            return null;
        }

        EdgeChannelsOptions proposed;
        try
        {
            proposed = new ConfigurationBuilder().AddInMemoryCollection(pairs).Build()
                .GetSection(EdgeChannelsOptions.SectionName).Get<EdgeChannelsOptions>() ?? new EdgeChannelsOptions();
        }
        catch (InvalidOperationException ex)
        {
            // The binder names the value before the key ("value '…' at 'Edge:…'"): take the key after "at".
            var path = BinderPathPattern().Match(ex.Message) is { Success: true } m ? m.Groups[1].Value : "";
            errors.Add(new SettingsError(NodeOf(path), "The value does not have the expected type."));
            return null;
        }

        // The validator's errors start with their key; each device's own checks are pinned by SettingsTree. Both run,
        // so the page lists every error at once; one node keeps its first error.
        var found = EdgeChannelsValidator.Validate(proposed)
            .Select(error => PathOf(error))
            .Select(path => new SettingsError(NodeOf(path), FixedMessage(NodeOf(path))))
            .Concat(SettingsTree.DeviceErrors(proposed))
            .DistinctBy(error => error.Path)
            .ToList();
        if (found.Count > 0)
        {
            errors.AddRange(found);
            return null;
        }

        RunStartupRegistration(pairs, errors);
        tree = proposed;
        return errors.Count == 0 ? bytes : null;
    }

    /// <summary>
    /// Registers the agent exactly as a start would, on the proposed tree over this Edge's configuration with every key
    /// a schema-2 file owns removed (so channels from appsettings never mix into the proposal). The buffer path points
    /// nowhere, so no settings file is read.
    /// </summary>
    private void RunStartupRegistration(IReadOnlyList<KeyValuePair<string, string?>> proposed, List<SettingsError> errors)
    {
        var values = configuration.AsEnumerable()
            .Where(pair => pair.Value is not null && !EdgeSettingsFile.IsOwnedByMultiDevice(pair.Key))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        values["Edge:Buffer:Path"] = Path.Combine(Path.GetTempPath(), "edge-settings-validate", Guid.NewGuid().ToString("N"), "none.db");
        foreach (var (key, value) in proposed)
        {
            values[key] = value;
        }

        var registrations = new ServiceCollection();
        registrations.AddLogging();
        try
        {
            registrations.AddEdgeAgent(new ConfigurationBuilder().AddInMemoryCollection(values).Build());
        }
        catch (Exception ex) when (ex is OptionsValidationException or InvalidOperationException)
        {
            // Every rule this repeats was checked above with its node; this only guards against a rule added later.
            var path = NodeOf(PathOf(ex.Message));
            errors.Add(new SettingsError(path, FixedMessage(path)));
        }
    }

    /// <summary>
    /// What a save changes in the effective write permissions, against the running devices: each device's
    /// <c>WritesEnabled</c>, and each TagId writable through its device (<c>WritesEnabled</c> and <c>Writable</c>).
    /// Compared by name, so a device or tag deleted, added or renamed counts as a change. Devices first, then tags.
    /// </summary>
    internal static IReadOnlyList<WriteChange> WriteChanges(EdgeDeviceSet running, EdgeChannelsOptions proposed)
    {
        var before = running.Devices.Where(device => device.WritesEnabled).ToList();
        var after = proposed.Channels.SelectMany(channel => channel.Devices).Where(device => device.WritesEnabled).ToList();
        return
        [
            .. Diff("device", before.Select(device => device.Name), after.Select(device => device.Name)),
            .. Diff(
                "tag",
                before.SelectMany(device => device.Tags.Where(tag => tag.Writable).Select(tag => tag.TagId)),
                after.SelectMany(device => device.Tags.Where(tag => tag.Writable).Select(tag => EdgeChannelsValidator.PublishedTagId(device, tag)))),
        ];

        static IEnumerable<WriteChange> Diff(string subject, IEnumerable<string> before, IEnumerable<string> after)
        {
            var was = before.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var now = after.ToHashSet(StringComparer.OrdinalIgnoreCase);
            return was.Union(now, StringComparer.OrdinalIgnoreCase)
                .Where(name => was.Contains(name) != now.Contains(name))
                .Order(StringComparer.OrdinalIgnoreCase)
                .Select(name => new WriteChange(subject, name, was.Contains(name), now.Contains(name)));
        }
    }

    /// <summary>
    /// A key path no shorter than a channel: an error is never pinned to the whole list, which the page cannot show.
    /// </summary>
    private static string NodeOf(string path) =>
        path.Length <= EdgeChannelsOptions.ChannelsKey.Length && EdgeChannelsOptions.ChannelsKey.StartsWith(path, StringComparison.OrdinalIgnoreCase)
            ? $"{EdgeChannelsOptions.ChannelsKey}:0"
            : path;

    /// <summary>
    /// The fixed text for a key path. Validator text names submitted values (addresses, hosts, numbers), so a response
    /// carries only this. Schema-1 paths stay for the connection tester.
    /// </summary>
    internal static string FixedMessage(string path)
    {
        if (path.StartsWith(EdgeChannelsOptions.ChannelsKey, StringComparison.Ordinal))
        {
            var segments = path.Split(':');
            var isTag = segments.Length >= 7 && segments[5] == "Tags";
            return (segments[^1], isTag) switch
            {
                ("Name", true) => "Tag name: letters, digits, _ . - only; unique as a TagId (case-insensitive), at most 64 characters with the device prefix.",
                ("Name", false) => "Name: 1..32 letters, digits, _ or -, unique (case-insensitive).",
                ("Driver", _) => "Driver must be S7, Slmp3E or Mc1E.",
                ("Model", _) => "Model must be S7-1200, FX5U or FX3U, and belong to the channel's driver.",
                ("Host", _) => "Host is required, and no other PLC may use the same host and port.",
                ("Port", _) => "Port is not valid for this model.",
                ("WritesEnabled", _) => "Writes are not allowed on this model.",
                ("Writable", _) => "This tag cannot be writable: an input address, or a model whose writes are not allowed.",
                ("Address", _) => "Address is not valid for this model and data type.",
                ("DataType", _) => "Data type is not supported by this model.",
                ("Length", _) => "Length is required for String tags (at least 1) and not allowed on other types.",
                ("Min" or "Max", _) => "Min/Max are not valid for this data type.",
                ("Rack" or "Slot" or "TimeoutMs" or "PollIntervalMs" or "CpuType", _) => $"{segments[^1]} is out of range.",
                ("Tags", _) => "A tag of this PLC is not valid.",
                _ when isTag || segments[^2] == "Devices" => "This tag or PLC is not valid.",
                _ => "The settings are not valid.",
            };
        }

        return path switch
        {
            "Edge:Plc:Protocol" => "Protocol must be S7 or Slmp, and only S7 allows commands.",
            _ when path.StartsWith("Edge:Plc:", StringComparison.Ordinal) => $"{path} is not valid for this protocol.",
            _ => "The settings are not valid.",
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
}

public sealed record SettingsError(string Path, string Message);
