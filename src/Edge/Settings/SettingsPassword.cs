using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;

namespace IndustrialIot.Edge.Settings;

public enum PasswordVerdict
{
    Ok,
    Wrong,

    /// <summary>No password file: saving and testing stay closed until <c>set-settings-password</c> runs.</summary>
    NotSet,

    /// <summary>Too many wrong passwords; nothing is checked until the lockout ends.</summary>
    LockedOut,
}

/// <summary>
/// The settings password: a PBKDF2-SHA256 hash in <c>&lt;db&gt;.settings-password.json</c> beside the settings file.
/// The verifier is shared by every inner host (its lockout survives settings applies) and counts wrong passwords from
/// saves and connection tests together: 5 in a row lock both for 60 s, and attempts during the lock are not counted,
/// so they cannot extend it. The password is never logged.
/// </summary>
public sealed class SettingsPassword(TimeProvider clock, ILogger<SettingsPassword> logger)
{
    public const int MinimumLength = 12;
    public const int Iterations = 600_000;
    public const int MaxFailures = 5;
    public static readonly TimeSpan LockoutDuration = TimeSpan.FromSeconds(60);

    private const int MaxIterations = 10_000_000;
    private const string Algorithm = "PBKDF2-SHA256";
    private const int SaltBytes = 16;
    private const int HashBytes = 32;

    private readonly object _gate = new();
    private int _failures;
    private DateTimeOffset _lockedUntil = DateTimeOffset.MinValue;

    /// <summary>Time left in the current lockout, or zero.</summary>
    public TimeSpan LockoutRemaining
    {
        get
        {
            lock (_gate)
            {
                var left = _lockedUntil - clock.GetUtcNow();
                return left > TimeSpan.Zero ? left : TimeSpan.Zero;
            }
        }
    }

    /// <summary><c>&lt;db&gt;.settings-password.json</c> beside <c>&lt;db&gt;.settings.json</c>.</summary>
    public static string PathFor(string settingsFilePath)
    {
        ArgumentNullException.ThrowIfNull(settingsFilePath);
        const string suffix = ".settings.json";
        var stem = settingsFilePath.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
            ? settingsFilePath[..^suffix.Length]
            : Path.ChangeExtension(settingsFilePath, null);
        return stem + ".settings-password.json";
    }

    /// <summary>Stores a new hash of <paramref name="password"/> (atomically: temporary file, then rename).</summary>
    public static void Store(string passwordFilePath, string password)
    {
        ArgumentNullException.ThrowIfNull(passwordFilePath);
        ArgumentNullException.ThrowIfNull(password);
        if (password.Length < MinimumLength)
        {
            throw new ArgumentException($"The settings password must have at least {MinimumLength} characters.", nameof(password));
        }

        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var record = new StoredRecord(
            Algorithm,
            Iterations,
            Convert.ToBase64String(salt),
            Convert.ToBase64String(Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, HashBytes)));

        Directory.CreateDirectory(Path.GetDirectoryName(passwordFilePath)!);
        var temporary = $"{passwordFilePath}.{Guid.NewGuid():N}.tmp";
        try
        {
            // Owner-only on Unix (Windows: the directory's ACL applies), like the buffer database.
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
            if (!OperatingSystem.IsWindows())
            {
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            }

            using (var stream = new FileStream(temporary, options))
            {
                stream.Write(JsonSerializer.SerializeToUtf8Bytes(record));
            }

            File.Move(temporary, passwordFilePath, overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    public PasswordVerdict Verify(string passwordFilePath, string? password)
    {
        ArgumentNullException.ThrowIfNull(passwordFilePath);
        lock (_gate)
        {
            var now = clock.GetUtcNow();
            if (now < _lockedUntil)
            {
                return PasswordVerdict.LockedOut;
            }

            if (!TryLoad(passwordFilePath, out var record))
            {
                return PasswordVerdict.NotSet;
            }

            if (password is not null && Matches(record, password))
            {
                _failures = 0;
                return PasswordVerdict.Ok;
            }

            _failures++;
            logger.LogWarning("Wrong settings password ({Failures}/{Max}).", _failures, MaxFailures);
            if (_failures >= MaxFailures)
            {
                _failures = 0;
                _lockedUntil = now + LockoutDuration;
                logger.LogWarning("Settings saves and connection tests locked for {Seconds} s after {Max} wrong passwords.",
                    (int)LockoutDuration.TotalSeconds, MaxFailures);
            }

            return PasswordVerdict.Wrong;
        }
    }

    private bool TryLoad(string path, out PasswordRecord record)
    {
        record = default!;
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(path);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return false;
        }

        // A damaged or tampered file must neither open saving nor stall it (a huge iteration count or hash length
        // would hold the lock for hours): accept only the shape Store writes, otherwise treat it as not set.
        string? problem;
        try
        {
            var stored = JsonSerializer.Deserialize<StoredRecord>(bytes);
            problem = Check(stored, out record);
        }
        catch (Exception ex) when (ex is JsonException or FormatException)
        {
            problem = ex.Message;
        }

        if (problem is not null)
        {
            logger.LogError("Settings password file {Path} is not valid ({Reason}); run set-settings-password again.", path, problem);
            return false;
        }

        return true;
    }

    private static string? Check(StoredRecord? stored, out PasswordRecord record)
    {
        record = default!;
        if (stored is null || stored.Algorithm != Algorithm)
        {
            return $"algorithm is not {Algorithm}";
        }

        if (stored.Iterations is < Iterations or > MaxIterations)
        {
            return $"iterations must be within {Iterations}..{MaxIterations}";
        }

        if (stored.Salt is null || stored.Hash is null)
        {
            return "salt or hash is missing";
        }

        var salt = Convert.FromBase64String(stored.Salt);
        var hash = Convert.FromBase64String(stored.Hash);
        if (salt.Length < SaltBytes || hash.Length != HashBytes)
        {
            return $"salt must have at least {SaltBytes} bytes and the hash {HashBytes}";
        }

        record = new PasswordRecord(stored.Iterations, salt, hash);
        return null;
    }

    private static bool Matches(PasswordRecord record, string password) =>
        CryptographicOperations.FixedTimeEquals(
            Rfc2898DeriveBytes.Pbkdf2(password, record.Salt, record.Iterations, HashAlgorithmName.SHA256, HashBytes),
            record.Hash);

    private sealed record StoredRecord(string? Algorithm, int Iterations, string? Salt, string? Hash);

    private sealed record PasswordRecord(int Iterations, byte[] Salt, byte[] Hash);
}

/// <summary>Where <c>set-settings-password</c> prompts and reports; the real one reads without echo.</summary>
public interface ISettingsPasswordConsole
{
    string? ReadSecret(string prompt);

    void WriteLine(string line);

    void WriteError(string line);
}

/// <summary>
/// <c>IndustrialIot.Edge.exe set-settings-password [--service] [--contentRoot &lt;dir&gt;]</c>. Without
/// <c>--service</c> the target follows the configuration of that content root, as a console run of the Edge does; with
/// it, the installed service's <c>Edge__Buffer__Path</c> (registry Environment), which an administrator's console
/// does not see. The password is prompted twice and never accepted as an argument.
/// </summary>
public static class SettingsPasswordCommand
{
    public const string Name = "set-settings-password";

    private const string Usage = $"Usage: IndustrialIot.Edge.exe {Name} [--service] [--contentRoot <dir>]";

    /// <param name="args">The arguments after the command name.</param>
    /// <param name="serviceBufferPath">Reads the installed service's Edge__Buffer__Path; null when there is none.</param>
    /// <returns>0 stored, 1 refused or failed, 2 bad arguments.</returns>
    public static int Run(IReadOnlyList<string> args, ISettingsPasswordConsole console, Func<string?> serviceBufferPath)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(console);
        ArgumentNullException.ThrowIfNull(serviceBufferPath);

        var service = false;
        string? contentRoot = null;
        for (var i = 0; i < args.Count; i++)
        {
            if (args[i] == "--service")
            {
                service = true;
            }
            else if (args[i] == "--contentRoot" && i + 1 < args.Count && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                contentRoot = args[++i];
            }
            else
            {
                // Never echo the argument: it may be a password typed on the command line.
                console.WriteError(Usage);
                console.WriteError("The password is prompted; it is never given as an argument.");
                return 2;
            }
        }

        if (service && contentRoot is not null)
        {
            console.WriteError(Usage);
            console.WriteError("--service uses the installed service's paths; --contentRoot does not apply to it.");
            return 2;
        }

        try
        {
            return Store(service, contentRoot, console, serviceBufferPath);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            // DataDir and the service's registry key are readable by administrators only.
            console.WriteError($"The settings password was not stored: {ex.Message} Run the command from an elevated prompt.");
            return 1;
        }
    }

    private static int Store(bool service, string? contentRoot, ISettingsPasswordConsole console, Func<string?> serviceBufferPath)
    {
        string settingsPath;
        if (service)
        {
            var bufferPath = serviceBufferPath();
            if (string.IsNullOrWhiteSpace(bufferPath))
            {
                console.WriteError(OperatingSystem.IsWindows()
                    ? "The IndustrialIot.Edge service is not installed, or its environment has no Edge__Buffer__Path."
                    : "--service reads the Windows service's settings; it is only available on Windows.");
                return 1;
            }

            settingsPath = EdgeSettingsFile.ResolvePath(Configuration(new() { ["Edge:Buffer:Path"] = bufferPath }), AppContext.BaseDirectory);
        }
        else
        {
            // The same configuration sources as a console run of the Edge with that content root.
            var builder = Host.CreateApplicationBuilder(contentRoot is null ? [] : ["--contentRoot", contentRoot]);
            settingsPath = EdgeSettingsFile.ResolvePath(builder.Configuration, EdgeSettingsFile.ResolveContentRoot(builder.Configuration));
        }

        var passwordPath = SettingsPassword.PathFor(settingsPath);
        var directory = Path.GetDirectoryName(passwordPath)!;
        var created = !Directory.Exists(directory);
        Directory.CreateDirectory(directory);

        var refusal = UnsafeDirectoryReason(directory) is { } reason
            ? $"Refusing to store the settings password in '{directory}': {reason}"
            : null;
        string? first = null;
        if (refusal is null)
        {
            first = console.ReadSecret("New settings password: ");
            if (first is null || first.Length < SettingsPassword.MinimumLength)
            {
                refusal = $"The settings password must have at least {SettingsPassword.MinimumLength} characters.";
            }
            else if (console.ReadSecret("Repeat the settings password: ") != first)
            {
                refusal = "The two passwords differ; nothing was stored.";
            }
        }

        if (refusal is not null)
        {
            // Leave nothing behind for a refused attempt.
            if (created)
            {
                Directory.Delete(directory);
            }

            console.WriteError(refusal);
            return 1;
        }

        SettingsPassword.Store(passwordPath, first!);
        console.WriteLine($"Settings password stored in '{passwordPath}'.");
        return 0;
    }
    /// <summary>Reads Edge__Buffer__Path from the installed service's registry Environment (Windows only).</summary>
    public static string? ReadServiceBufferPath()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\IndustrialIot.Edge");
        if (key?.GetValue("Environment") is not string[] environment)
        {
            return null;
        }

        const string prefix = "Edge__Buffer__Path=";
        return environment.FirstOrDefault(entry => entry.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))?[prefix.Length..];
    }

    private static IConfiguration Configuration(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    /// <summary>A reparse point, or a directory other users may write to (they could swap the hash file).</summary>
    private static string? UnsafeDirectoryReason(string directory)
    {
        var info = new DirectoryInfo(directory);
        if (info.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            return "it is a link (reparse point).";
        }

        if (OperatingSystem.IsWindows())
        {
            return WritableByOthers(info) ? "other users can write to it." : null;
        }

        return (info.UnixFileMode & UnixFileMode.OtherWrite) != 0 ? "other users can write to it." : null;
    }

    [SupportedOSPlatform("windows")]
    private static bool WritableByOthers(DirectoryInfo directory)
    {
        var others = new[] { WellKnownSidType.BuiltinUsersSid, WellKnownSidType.AuthenticatedUserSid, WellKnownSidType.WorldSid }
            .Select(type => new SecurityIdentifier(type, null))
            .ToHashSet();
        // Generic write/all (0x40000000, 0x10000000) count too: an ACE may carry only those.
        const FileSystemRights writeRights = (FileSystemRights)0x50000000 | FileSystemRights.WriteData | FileSystemRights.AppendData | FileSystemRights.Delete
            | FileSystemRights.DeleteSubdirectoriesAndFiles | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;

        var rules = directory.GetAccessControl().GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier));
        return rules.Cast<FileSystemAccessRule>().Any(rule =>
            rule.AccessControlType == AccessControlType.Allow
            && others.Contains((SecurityIdentifier)rule.IdentityReference)
            && (rule.FileSystemRights & writeRights) != 0);
    }
}
