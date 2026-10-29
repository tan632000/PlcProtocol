using System.Security.Cryptography;
using System.Text.Json;
using IndustrialIot.Edge.Buffering;

namespace IndustrialIot.Edge.Settings;

/// <summary>
/// The settings file written by the settings page: <c>&lt;db&gt;.settings.json</c> beside the buffer database. While it
/// exists it is the only source of <c>Edge:Plc</c>, <c>Edge:Poll</c> and <c>Edge:Tags</c>; it may hold nothing else,
/// so a hand edit cannot turn on commands or move the broker, the buffer or the identity.
/// </summary>
public sealed class EdgeSettingsFile
{
    public const int SchemaVersion = 1;

    /// <summary>The revision of the configuration when no settings file exists.</summary>
    public const string NoFileRevision = "none";

    private static readonly string[] OwnedSections = ["Edge:Plc", "Edge:Poll", "Edge:Tags"];

    private static readonly HashSet<string> PlcKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "Protocol", "Host", "Port", "Rack", "Slot", "CpuType", "TimeoutMs",
    };

    private static readonly byte[] Utf8Bom = [0xEF, 0xBB, 0xBF];

    private static readonly HashSet<string> PollKeys = new(StringComparer.OrdinalIgnoreCase) { "IntervalMs" };

    private static readonly HashSet<string> TagKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "TagId", "Address", "DataType", "Writable", "Min", "Max",
    };

    private EdgeSettingsFile(string path, byte[]? bytes)
    {
        Path = path;
        Bytes = bytes;
        Revision = bytes is null ? NoFileRevision : Convert.ToHexStringLower(SHA256.HashData(bytes));
    }

    /// <summary>Full path of the settings file, whether or not it exists.</summary>
    public string Path { get; }

    /// <summary>The bytes the configuration was built from; null when there is no file.</summary>
    public byte[]? Bytes { get; }

    public bool Exists => Bytes is not null;

    /// <summary>SHA-256 (lowercase hex) of <see cref="Bytes"/>, or <see cref="NoFileRevision"/>.</summary>
    public string Revision { get; }

    /// <summary>
    /// The content root the host resolves (<c>IHostEnvironment.ContentRootPath</c>), computed the same way from
    /// configuration: no <c>contentRoot</c> key (the host leaves it out under the SCM, whose working directory is
    /// System32) means the app base directory, and a relative one is relative to that directory.
    /// </summary>
    public static string ResolveContentRoot(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var baseDirectory = System.IO.Path.GetFullPath(AppContext.BaseDirectory);
        var contentRoot = configuration[HostDefaults.ContentRootKey];
        if (string.IsNullOrEmpty(contentRoot))
        {
            return baseDirectory;
        }

        return System.IO.Path.IsPathRooted(contentRoot) ? contentRoot : System.IO.Path.Combine(baseDirectory, contentRoot);
    }

    /// <summary>
    /// <c>&lt;db&gt;.settings.json</c> in the directory of <c>Edge:Buffer:Path</c>, resolved against the content root
    /// as the buffer database is. Naming by the database keeps two profiles sharing one data folder apart.
    /// </summary>
    public static string ResolvePath(IConfiguration configuration, string contentRoot)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(contentRoot);

        var bufferPath = configuration[$"{BufferOptions.SectionName}:Path"];
        if (string.IsNullOrWhiteSpace(bufferPath))
        {
            bufferPath = new BufferOptions().Path;
        }

        var database = System.IO.Path.GetFullPath(bufferPath, contentRoot);
        return System.IO.Path.Combine(
            System.IO.Path.GetDirectoryName(database)!,
            System.IO.Path.GetFileNameWithoutExtension(database) + ".settings.json");
    }

    /// <summary>Reads and checks the file at <paramref name="path"/>; an absent file is not an error.</summary>
    /// <exception cref="InvalidOperationException">The file is unreadable, malformed or holds a foreign key.</exception>
    public static EdgeSettingsFile Read(string path)
    {
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(path);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return new EdgeSettingsFile(path, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw Invalid(path, $"cannot be read ({ex.Message}).");
        }

        // Flattening as the configuration provider does also refuses duplicate keys, so Read is a full check.
        CheckShape(path, bytes);
        _ = Flatten(path, bytes);
        return new EdgeSettingsFile(path, bytes);
    }

    /// <summary>
    /// Returns <paramref name="configuration"/> unchanged when there is no settings file. Otherwise returns a snapshot
    /// in which <c>Edge:Plc</c>, <c>Edge:Poll</c> and <c>Edge:Tags</c> come only from the file: a layered source would
    /// merge arrays by index (stale tags) and lose to environment variables such as the installer's
    /// <c>Edge__Plc__Host</c>.
    /// </summary>
    public static IConfiguration Compose(IConfiguration configuration, string contentRoot, out EdgeSettingsFile file)
    {
        file = Read(ResolvePath(configuration, contentRoot));
        if (!file.Exists)
        {
            return configuration;
        }

        var fromFile = Flatten(file.Path, file.Bytes!);
        var kept = configuration.AsEnumerable().Where(pair => pair.Value is not null && !IsOwned(pair.Key));
        return new ConfigurationBuilder()
            .AddInMemoryCollection(kept)
            .AddInMemoryCollection(fromFile)
            .Build();
    }

    /// <summary>
    /// Checks proposed settings file bytes exactly as <see cref="Read"/> checks a file and returns their
    /// <c>Edge:Plc</c>/<c>Edge:Poll</c>/<c>Edge:Tags</c> configuration pairs. Errors name <paramref name="label"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">Malformed bytes or a foreign key.</exception>
    public static IReadOnlyList<KeyValuePair<string, string?>> ParseOwned(byte[] bytes, string label)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        CheckShape(label, bytes);
        return Flatten(label, bytes);
    }

    /// <summary>Whether <paramref name="key"/> belongs to a section the settings file replaces.</summary>
    public static bool IsOwned(string key) => OwnedSections.Any(section =>
        key.Equals(section, StringComparison.OrdinalIgnoreCase)
        || key.StartsWith(section + ConfigurationPath.KeyDelimiter, StringComparison.OrdinalIgnoreCase));

    private static List<KeyValuePair<string, string?>> Flatten(string path, byte[] bytes)
    {
        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            return new ConfigurationBuilder().AddJsonStream(stream).Build().AsEnumerable()
                .Where(pair => pair.Value is not null && IsOwned(pair.Key))
                .ToList();
        }
        catch (Exception ex) when (ex is FormatException or InvalidDataException or JsonException)
        {
            throw Invalid(path, $"is not valid JSON ({ex.Message}).");
        }
    }

    private static void CheckShape(string path, byte[] bytes)
    {
        JsonDocument document;
        try
        {
            // The configuration provider accepts a UTF-8 BOM (PowerShell 5.1 writes one); JsonDocument does not.
            var json = bytes.AsMemory();
            if (json.Span.StartsWith(Utf8Bom))
            {
                json = json[Utf8Bom.Length..];
            }

            document = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw Invalid(path, $"is not valid JSON ({ex.Message}).");
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw Invalid(path, "must be a JSON object.");
            }

            var hasVersion = false;
            foreach (var property in root.EnumerateObject())
            {
                if (property.Name.Equals(nameof(SchemaVersion), StringComparison.OrdinalIgnoreCase))
                {
                    hasVersion = property.Value.ValueKind == JsonValueKind.Number
                        && property.Value.TryGetInt32(out var version) && version == SchemaVersion;
                    if (!hasVersion)
                    {
                        throw Invalid(path, $"SchemaVersion must be {SchemaVersion}.");
                    }
                }
                else if (property.Name.Equals("Edge", StringComparison.OrdinalIgnoreCase))
                {
                    CheckEdge(path, property.Value);
                }
                else
                {
                    throw Invalid(path, $"unknown key '{property.Name}'.");
                }
            }

            if (!hasVersion)
            {
                throw Invalid(path, $"SchemaVersion must be {SchemaVersion}.");
            }
        }
    }

    private static void CheckEdge(string path, JsonElement edge)
    {
        ExpectObject(path, edge, "Edge");
        foreach (var section in edge.EnumerateObject())
        {
            var key = $"Edge:{section.Name}";
            if (section.Name.Equals("Plc", StringComparison.OrdinalIgnoreCase))
            {
                CheckKeys(path, section.Value, key, PlcKeys);
            }
            else if (section.Name.Equals("Poll", StringComparison.OrdinalIgnoreCase))
            {
                CheckKeys(path, section.Value, key, PollKeys);
            }
            else if (section.Name.Equals("Tags", StringComparison.OrdinalIgnoreCase))
            {
                if (section.Value.ValueKind != JsonValueKind.Array)
                {
                    throw Invalid(path, $"'{key}' must be an array.");
                }

                var index = 0;
                foreach (var tag in section.Value.EnumerateArray())
                {
                    CheckKeys(path, tag, $"{key}:{index++}", TagKeys);
                }
            }
            else
            {
                throw Invalid(path, $"key '{key}' is not allowed (only Edge:Plc, Edge:Poll and Edge:Tags).");
            }
        }
    }

    private static void CheckKeys(string path, JsonElement element, string key, HashSet<string> allowed)
    {
        ExpectObject(path, element, key);
        foreach (var property in element.EnumerateObject())
        {
            if (!allowed.Contains(property.Name))
            {
                throw Invalid(path, $"key '{key}:{property.Name}' is not allowed.");
            }

            if (property.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
            {
                throw Invalid(path, $"'{key}:{property.Name}' must be a single value.");
            }
        }
    }

    private static void ExpectObject(string path, JsonElement element, string key)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw Invalid(path, $"'{key}' must be a JSON object.");
        }
    }

    private static InvalidOperationException Invalid(string path, string reason) =>
        new($"Settings file '{path}': {reason}");
}
