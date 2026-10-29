// Edge agent entry point (.NET Generic Host). Configuration comes from appsettings.json, environment variables
// (Edge__...) and the settings file saved by the settings page; AddEdgeAgent validates it before anything starts.
using IndustrialIot.Edge.Settings;

// Sets the password of the settings page; no host runs.
if (args.Length > 0 && args[0] == SettingsPasswordCommand.Name)
{
    return SettingsPasswordCommand.Run(args[1..], new TerminalPasswordConsole(), SettingsPasswordCommand.ReadServiceBufferPath);
}

var builder = Host.CreateApplicationBuilder(args);

// The outer host owns the process lifetime. Under the Windows Service Control Manager: service lifetime and Event
// Log logging; in a console (dotnet run) it does nothing. Relative paths still resolve against the exe's folder
// there, because the host ignores a System32 working directory for its content root; the installer also writes
// absolute paths for safety. The agent runs in inner hosts the supervisor rebuilds when settings are applied.
builder.Services.AddWindowsService(options => options.ServiceName = "IndustrialIot.Edge");
builder.Services.AddEdgeSupervisor();

var host = builder.Build();
var supervisor = host.Services.GetRequiredService<EdgeSupervisor>();
host.Run();
return supervisor.ExitCode;

/// <summary>Prompts on the terminal without echoing what is typed.</summary>
internal sealed class TerminalPasswordConsole : ISettingsPasswordConsole
{
    public string? ReadSecret(string prompt)
    {
        Console.Write(prompt);
        if (Console.IsInputRedirected)
        {
            return Console.ReadLine();
        }

        var typed = new System.Text.StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
            {
                Console.WriteLine();
                return typed.ToString();
            }

            if (key.Key == ConsoleKey.Backspace)
            {
                if (typed.Length > 0)
                {
                    typed.Length--;
                }
            }
            else if (!char.IsControl(key.KeyChar))
            {
                typed.Append(key.KeyChar);
            }
        }
    }

    public void WriteLine(string line) => Console.WriteLine(line);

    public void WriteError(string line) => Console.Error.WriteLine(line);
}
