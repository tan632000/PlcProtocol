using System.Text;
using IndustrialIot.Edge.Drivers;
using IndustrialIot.Edge.Drivers.Models;
using IndustrialIot.Edge.Drivers.Slmp;
using IndustrialIot.Edge.Settings;
using IndustrialIot.Edge.Tests.Fakes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace IndustrialIot.Edge.Tests;

/// <summary>
/// The settings file saved by the settings page is the only source of Edge:Plc, Edge:Poll and Edge:Tags while it
/// exists: no merge with appsettings.json or the environment, and nothing else may be set through it.
/// </summary>
public sealed class EdgeSettingsFileTests : IDisposable
{
    private readonly string _contentRoot = Directory.CreateTempSubdirectory("edge-settings-").FullName;

    public void Dispose() => EdgeTestHost.DeleteDirectory(_contentRoot);

    [Fact]
    public void NoFile_TagsComeFromBaseConfiguration()
    {
        using var host = Build(BaseSettings(tagCount: 3));

        Assert.Equal(3, host.Services.GetRequiredService<PlcTagCatalog>().Tags.Count);
        Assert.False(File.Exists(SettingsPath()));
    }

    [Fact]
    public void File_ReplacesBaseTagArray_InsteadOfMergingByIndex()
    {
        WriteFile("""
            {"SchemaVersion":1,"Edge":{
              "Plc":{"Protocol":"S7","Host":"127.0.0.1","Port":102,"TimeoutMs":500},
              "Poll":{"IntervalMs":500},
              "Tags":[{"TagId":"A.One","Address":"DB5.DBW0","DataType":"Int"},
                      {"TagId":"A.Two","Address":"DB5.DBW2","DataType":"Int"}]}}
            """);

        using var host = Build(BaseSettings(tagCount: 21));

        var tags = host.Services.GetRequiredService<PlcTagCatalog>().Tags;
        Assert.Equal(["A.One", "A.Two"], tags.Select(t => t.TagId));
    }

    [Fact]
    public void File_HostWinsOverEnvironmentOverride()
    {
        WriteFile("""
            {"SchemaVersion":1,"Edge":{"Plc":{"Host":"10.9.8.7","TimeoutMs":500},
              "Tags":[{"TagId":"A.One","Address":"DB5.DBW0","DataType":"Int"}]}}
            """);

        // Stands in for the installer's Edge__Plc__Host in the service environment (a later, stronger source).
        var environment = new Dictionary<string, string?> { ["Edge:Plc:Host"] = "192.168.3.20" };
        var settings = BaseSettings(tagCount: 1);
        settings["Edge:Poll:IntervalMs"] = "2500";
        using var host = Build(settings, environment);

        Assert.Equal("10.9.8.7", host.Services.GetRequiredService<IOptions<SiemensS7Options>>().Value.Host);

        // The file has no Poll section: the default applies, not the base's 2500.
        Assert.Equal(new Polling.PollOptions().IntervalMs, host.Services.GetRequiredService<IOptions<Polling.PollOptions>>().Value.IntervalMs);
    }

    [Fact]
    public void File_SwitchesProtocol_ThroughTheRawConfigurationRead()
    {
        WriteFile("""
            {"SchemaVersion":1,"Edge":{"Plc":{"Protocol":"Slmp","Host":"127.0.0.1","Port":5000,"TimeoutMs":1000},
              "Tags":[{"TagId":"F.D100","Address":"D100","DataType":"Int"}]}}
            """);

        // The base is S7 with commands enabled; the file's Slmp needs commands off, which stays a base-only key.
        var settings = BaseSettings(tagCount: 2);
        settings["Edge:Commands:Enabled"] = "false";
        using var host = Build(settings);

        Assert.IsType<SlmpAddressSyntax>(host.Services.GetRequiredService<IPlcAddressSyntax>());
        Assert.IsType<MitsubishiSlmpDriver>(host.Services.GetRequiredService<IPlcDriver>());
        Assert.Equal(["F.D100"], host.Services.GetRequiredService<PlcTagCatalog>().Tags.Select(t => t.TagId));
    }

    [Theory]
    [InlineData("""{"SchemaVersion":2,"Edge":{"Tags":[]}}""", "SchemaVersion")]
    [InlineData("""{"Edge":{"Tags":[]}}""", "SchemaVersion")]
    [InlineData("""{"SchemaVersion":1,"Edge":{"Tags":[}""", "not valid JSON")]
    [InlineData("""{"SchemaVersion":1,"Edge":{"Commands":{"Enabled":true}}}""", "Edge:Commands")]
    [InlineData("""{"SchemaVersion":1,"Edge":{"Mqtt":{"Host":"evil"}}}""", "Edge:Mqtt")]
    [InlineData("""{"SchemaVersion":1,"Edge":{"Buffer":{"Path":"x.db"}}}""", "Edge:Buffer")]
    [InlineData("""{"SchemaVersion":1,"Edge":{"EdgeId":"other"}}""", "Edge:EdgeId")]
    [InlineData("""{"SchemaVersion":1,"Edge":{"Plc":{"Password":"x"}}}""", "Edge:Plc:Password")]
    [InlineData("""{"SchemaVersion":1,"Edge":{"Tags":[{"TagId":"A","Address":"DB1.DBW0","DataType":"Int","Scale":2}]}}""", "Edge:Tags:0:Scale")]
    [InlineData("""{"SchemaVersion":1,"Other":1}""", "Other")]
    [InlineData("""{"schemaversion":1,"edge":{"commands":{"enabled":true}}}""", "Edge:commands")]
    [InlineData("""{"SchemaVersion":1,"Edge":{"PlcExtra":{"Host":"x"}}}""", "Edge:PlcExtra")]
    [InlineData("""{"SchemaVersion":1,"Edge":{"Tags":{"TagId":"A"}}}""", "must be an array")]
    [InlineData("""{"SchemaVersion":1,"Edge":{"Plc":{"Host":"a","host":"b"}}}""", "not valid JSON")]
    public void File_WithWrongVersionShapeOrForeignKey_FailsNamingFileAndKey(string json, string expectedFragment)
    {
        WriteFile(json);

        var ex = Assert.Throws<InvalidOperationException>(() => Build(BaseSettings(tagCount: 1)).Dispose());

        Assert.Contains(SettingsPath(), ex.Message);
        Assert.Contains(expectedFragment, ex.Message);
    }

    [Fact]
    public void DuplicateKey_IsRefusedByReadAlone()
    {
        WriteFile("""{"SchemaVersion":1,"Edge":{"Plc":{"Host":"a","host":"b"}}}""");

        var ex = Assert.Throws<InvalidOperationException>(() => EdgeSettingsFile.Read(SettingsPath()));

        Assert.Contains(SettingsPath(), ex.Message);
    }

    [Fact]
    public void FileWithUtf8Bom_AndLowerCaseKeys_IsAccepted()
    {
        Directory.CreateDirectory(Path.Combine(_contentRoot, "data"));
        File.WriteAllText(
            SettingsPath(),
            """{"schemaversion":1,"edge":{"tags":[{"tagid":"A.One","address":"DB5.DBW0","datatype":"Int"}]}}""",
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        using var host = Build(BaseSettings(tagCount: 4));

        Assert.Equal(["A.One"], host.Services.GetRequiredService<PlcTagCatalog>().Tags.Select(t => t.TagId));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SettingsFile_SitsBesideTheBufferDatabase_ForTheHostsContentRoot(bool relativeContentRoot)
    {
        // A relative --contentRoot is relative to the app base directory; no contentRoot key (the SCM case, where
        // the working directory is System32) means the app base directory itself.
        var relative = Path.Combine("edge-settings-root", Guid.NewGuid().ToString("N"));
        var absolute = Path.Combine(AppContext.BaseDirectory, relative);
        Directory.CreateDirectory(absolute);
        try
        {
            var builder = relativeContentRoot
                ? Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings { Args = ["--contentRoot", relative] })
                : Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
            builder.Configuration.AddInMemoryCollection(BaseSettings(tagCount: 1));
            builder.Services.AddEdgeAgent(builder.Configuration);
            using var host = builder.Build();

            var database = host.Services.GetRequiredService<Buffering.EdgeDatabase>().FilePath;
            var settings = host.Services.GetRequiredService<EdgeSettingsFile>().Path;
            Assert.Equal(Path.GetDirectoryName(database), Path.GetDirectoryName(settings));
            Assert.Equal("edge.settings.json", Path.GetFileName(settings));
        }
        finally
        {
            EdgeTestHost.DeleteDirectory(Path.Combine(AppContext.BaseDirectory, "edge-settings-root"));
        }
    }

    [Fact]
    public void TwoBufferDatabasesInOneDirectory_GetDistinctSettingsFiles()
    {
        var dev = EdgeSettingsFile.ResolvePath(Config(new() { ["Edge:Buffer:Path"] = "data/edge.db" }), _contentRoot);
        var lab = EdgeSettingsFile.ResolvePath(Config(new() { ["Edge:Buffer:Path"] = "data/fx5u-lab.db" }), _contentRoot);

        Assert.Equal(Path.Combine(_contentRoot, "data", "edge.settings.json"), dev);
        Assert.Equal(Path.Combine(_contentRoot, "data", "fx5u-lab.settings.json"), lab);
    }

    [Fact]
    public void Revision_IsNoneWithoutFile_AndTheSha256OfTheBytesWithOne()
    {
        using (var host = Build(BaseSettings(tagCount: 1)))
        {
            Assert.Equal("none", host.Services.GetRequiredService<EdgeSettingsFile>().Revision);
        }

        const string json = """{"SchemaVersion":1,"Edge":{"Tags":[{"TagId":"A.One","Address":"DB5.DBW0","DataType":"Int"}]}}""";
        WriteFile(json);
        using var withFile = Build(BaseSettings(tagCount: 1));

        var file = withFile.Services.GetRequiredService<EdgeSettingsFile>();
        Assert.True(file.Exists);
        Assert.Equal(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(json))), file.Revision);
    }

    private IHost Build(Dictionary<string, string?> settings, Dictionary<string, string?>? environment = null)
    {
        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings { ContentRootPath = _contentRoot });
        builder.Configuration.AddInMemoryCollection(settings);
        if (environment is not null)
        {
            builder.Configuration.AddInMemoryCollection(environment);
        }

        builder.Services.AddEdgeAgent(builder.Configuration);
        return builder.Build();
    }

    private static IConfiguration Config(Dictionary<string, string?> settings) =>
        new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

    private string SettingsPath() => Path.Combine(_contentRoot, "data", "edge.settings.json");

    private void WriteFile(string json)
    {
        Directory.CreateDirectory(Path.Combine(_contentRoot, "data"));
        File.WriteAllText(SettingsPath(), json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private static Dictionary<string, string?> BaseSettings(int tagCount)
    {
        var settings = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["Edge:EdgeId"] = "settings-test",
            ["Edge:Plc:Host"] = "127.0.0.1",
            ["Edge:Plc:TimeoutMs"] = "500",
            ["Edge:Poll:IntervalMs"] = "1000",
            ["Edge:Buffer:Path"] = "data/edge.db",
            ["Edge:Mqtt:Host"] = "no-broker.invalid",
            ["Edge:Mqtt:Username"] = "settings-test",
            ["Edge:Mqtt:Password"] = "not-a-secret-test",
            ["Edge:Commands:Enabled"] = "true",
            ["Edge:Monitor:Enabled"] = "false",
        };
        for (var i = 0; i < tagCount; i++)
        {
            settings[$"Edge:Tags:{i}:TagId"] = $"Base.T{i}";
            settings[$"Edge:Tags:{i}:Address"] = $"DB1.DBW{i * 2}";
            settings[$"Edge:Tags:{i}:DataType"] = "Int";
        }

        return settings;
    }
}
