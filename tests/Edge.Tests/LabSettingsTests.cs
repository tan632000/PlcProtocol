using System.Net;
using System.Net.Sockets;
using IndustrialIot.Edge.Drivers;
using IndustrialIot.Edge.Tests.Fakes;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace IndustrialIot.Edge.Tests;

public sealed class LabSettingsTests
{
    private static readonly string[] ExpectedTags =
    [
        "Lab.IntD", "Lab.DIntD", "Lab.RealD", "Lab.M14", "Lab.M15", "Lab.M101", "Lab.X10", "Lab.X12", "Lab.Y10", "Lab.Y12",
    ];

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "IndustrialIot.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("IndustrialIot.sln not found above the test output.");
    }

    private static int FreePort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        return ((IPEndPoint)probe.LocalEndpoint).Port;
    }

    [Fact]
    public async Task LabFolder_LoadsAsSlmpWithTheTenTags_AndHostStartsAndStops()
    {
        var labDir = Path.Combine(RepoRoot(), "src", "Edge", "lab", "fx5u");
        await using var plc = new FakeSlmpServer();
        var bufferDir = Directory.CreateTempSubdirectory("lab-settings-");
        try
        {
            // Same construction as Program.cs; the overrides keep the test off 192.168.3.250, any real broker,
            // the developer's port 5080 and src/Edge/data.
            var builder = Host.CreateApplicationBuilder(["--contentRoot", labDir]);
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Edge:Mqtt:Host"] = "no-broker.invalid",
                ["Edge:Mqtt:Password"] = "not-a-secret-test",
                ["Edge:Plc:Host"] = "127.0.0.1",
                ["Edge:Plc:Port"] = plc.Port.ToString(),
                ["Edge:Monitor:Port"] = FreePort().ToString(),
                ["Edge:Buffer:Path"] = Path.Combine(bufferDir.FullName, "edge.db"),
            });
            builder.Services.AddEdgeAgent(builder.Configuration);

            using (var host = builder.Build())
            {
                await host.StartAsync();

                Assert.Equal(labDir, host.Services.GetRequiredService<IHostEnvironment>().ContentRootPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                Assert.Equal("Slmp", builder.Configuration["Edge:Plc:Protocol"]);
                Assert.False(host.Services.GetRequiredService<IOptions<Commands.CommandsOptions>>().Value.Enabled);
                Assert.Equal(ExpectedTags, host.Services.GetRequiredService<PlcTagCatalog>().Tags.Select(t => t.TagId));
                Assert.NotEqual(typeof(SiemensS7Driver), host.Services.GetRequiredService<IPlcDriver>().GetType());

                await host.StopAsync();
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            bufferDir.Delete(recursive: true);
        }
    }

    [Fact]
    public void LabBufferPath_ResolvesInsideTheIgnoredSrcEdgeDataFolder()
    {
        var labDir = Path.Combine(RepoRoot(), "src", "Edge", "lab", "fx5u");
        var configured = new ConfigurationBuilder()
            .SetBasePath(labDir)
            .AddJsonFile("appsettings.json")
            .Build()["Edge:Buffer:Path"];

        var resolved = Path.GetFullPath(configured!, labDir);

        Assert.Equal(Path.Combine(RepoRoot(), "src", "Edge", "data", "fx5u-lab.db"), resolved);
    }

    [Fact]
    public void EdgeProject_NeverCopiesTheLabFolderToOutputOrPublish()
    {
        var csproj = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Edge", "IndustrialIot.Edge.csproj"));

        Assert.Contains("""<Content Update="lab\**" CopyToOutputDirectory="Never" CopyToPublishDirectory="Never" />""", csproj);
    }
}
