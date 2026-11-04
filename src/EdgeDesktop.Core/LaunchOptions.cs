using System.Globalization;

namespace IndustrialIot.EdgeDesktop.Core;

/// <summary>The app's command line: only <c>--port N</c>, the Edge monitor port on this machine (default 5080).</summary>
public sealed record LaunchOptions(int Port)
{
    public const int DefaultPort = 5080;

    public const string Usage = "Cách dùng: IndustrialIot.EdgeDesktop.exe [--port N] (N từ 1 đến 65535, mặc định 5080).";

    /// <summary>The web settings page, opened in the default browser until the app has its own settings screens.</summary>
    public Uri SettingsUri => new($"http://localhost:{Port}/settings");

    public static bool TryParse(IReadOnlyList<string> args, out LaunchOptions? options, out string? error)
    {
        ArgumentNullException.ThrowIfNull(args);
        options = null;
        error = null;

        var port = DefaultPort;
        for (var i = 0; i < args.Count; i++)
        {
            if (args[i] != "--port")
            {
                error = $"Tham số không hợp lệ: {args[i]}. {Usage}";
                return false;
            }

            if (i + 1 >= args.Count
                || !int.TryParse(args[i + 1], NumberStyles.None, CultureInfo.InvariantCulture, out port)
                || port is < 1 or > 65535)
            {
                error = $"--port cần một số từ 1 đến 65535. {Usage}";
                return false;
            }

            i++;
        }

        options = new LaunchOptions(port);
        return true;
    }
}
