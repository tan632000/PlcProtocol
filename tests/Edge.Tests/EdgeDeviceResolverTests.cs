using System.Text;
using IndustrialIot.Edge.Configuration;
using IndustrialIot.Edge.Settings;
using IndustrialIot.Edge.Tests.Fakes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace IndustrialIot.Edge.Tests;

/// <summary>
/// The effective device list: a v2 configuration as written, or a v1 configuration migrated in memory to one device
/// that keeps the TagIds installed Edges already publish. Resolving never writes the settings file.
/// </summary>
public sealed class EdgeDeviceResolverTests : IDisposable
{
    // Every TagId the lab v1 file publishes today; the migration must keep each one verbatim.
    private static readonly string[] LabTagIds =
    [
        "Line1.Running", "Line1.Speed", "Line1.Setpoint", "Line1.Temperature", "Line1.DI0", "Line1.Inputs", "Line1.DO0",
        "Line1.AI0", "Line1.Clock1Hz", "Recipe.SteamSterilizationNo", "Recipe.SprayNo", "Phase.TimeMinutes",
        "Phase.TimeSeconds", "Run.TotalHours", "Run.TotalMinutes", "Run.TotalSeconds", "Temp.Preheat",
        "Temp.Sterilization", "Pressure.Sterilization", "Flow.Meter", "Pressure.Circulation", "Test.One",
    ];

    private readonly string _contentRoot = Directory.CreateTempSubdirectory("edge-resolver-").FullName;

    public void Dispose() => EdgeTestHost.DeleteDirectory(_contentRoot);

    [Fact]
    public void V2_PublishesDevicePrefixedIds_AndALegacyDevicePublishesNames()
    {
        var settings = EdgeChannelsOptionsTests.Valid();
        settings["Edge:Channels:1:Devices:0:LegacyTagIds"] = "true";

        var set = EdgeDeviceResolver.Resolve(Config(settings));

        Assert.False(set.MigratedFromV1);
        Assert.Equal(["Line1", "Press"], set.Devices.Select(d => d.Name));
        Assert.Equal(["Line1.Temp", "Line1.Speed"], set.Devices[0].Tags.Select(t => t.TagId));
        Assert.Equal(["D100"], set.Devices[1].Tags.Select(t => t.TagId));
        Assert.Equal((PlcDriverKind.Slmp3E, "FX5U", 5000), (set.Devices[1].Driver, set.Devices[1].Model, set.Devices[1].Port));
        Assert.Equal(Drivers.PlcDataType.Real, set.Devices[0].Tags[0].DataType);
    }

    [Fact]
    public async Task V2StringTag_CarriesItsLength_AndIsReadThroughTheS7Driver()
    {
        var settings = EdgeChannelsOptionsTests.Valid();
        settings["Edge:Channels:0:Devices:0:Tags:1:Address"] = "DB1.DBB20";
        settings["Edge:Channels:0:Devices:0:Tags:1:DataType"] = "String";
        settings["Edge:Channels:0:Devices:0:Tags:1:Length"] = "10";

        var device = EdgeDeviceResolver.Resolve(Config(settings)).Devices[0];
        var catalog = new Drivers.PlcTagCatalog(new Drivers.PlcTagCatalogOptions { Tags = [.. device.Tags] }, new Drivers.S7AddressSyntax());
        await using var server = FakeS7Server.StartWithMemory(pduSize: 240);
        server.Seed("DB1.DBB20", PlcValueConverterTests.S7String(max: 10, "AB"));
        await using var driver = new Drivers.SiemensS7Driver(Microsoft.Extensions.Options.Options.Create(
            new Drivers.SiemensS7Options { Host = "127.0.0.1", Port = server.Port, TimeoutMs = 3000 }));

        var results = await driver.ReadAsync([catalog.Tags[1]], CancellationToken.None);

        Assert.Equal(10, device.Tags[1].Length);
        Assert.Equal("AB", results[0].Value);
    }

    [Fact]
    public void InvalidV2_FailsNamingTheKey()
    {
        var settings = EdgeChannelsOptionsTests.Valid();
        settings["Edge:Channels:0:Driver"] = "Modbus";

        var ex = Assert.Throws<InvalidOperationException>(() => EdgeDeviceResolver.Resolve(Config(settings)));

        Assert.Contains("Edge:Channels:0:Driver", ex.Message);
    }

    [Fact]
    public void V1S7_MigratesToPlc1_WithLegacyIds_AndEveryPlcKey()
    {
        var settings = new Dictionary<string, string?>
        {
            ["Edge:Plc:Host"] = "192.168.3.20",
            ["Edge:Plc:Port"] = "1102",
            ["Edge:Plc:Rack"] = "0",
            ["Edge:Plc:Slot"] = "2",
            ["Edge:Plc:TimeoutMs"] = "1500",
            ["Edge:Poll:IntervalMs"] = "500",
            ["Edge:Commands:Enabled"] = "true",
            ["Edge:Tags:0:TagId"] = "Line1.Speed",
            ["Edge:Tags:0:Address"] = "DB1.DBW2",
            ["Edge:Tags:0:DataType"] = "Int",
            ["Edge:Tags:0:Writable"] = "true",
            ["Edge:Tags:1:TagId"] = "Temp",
            ["Edge:Tags:1:Address"] = "DB1.DBD6",
            ["Edge:Tags:1:DataType"] = "Real",
        };

        var set = EdgeDeviceResolver.Resolve(Config(settings));

        Assert.True(set.MigratedFromV1);
        var device = Assert.Single(set.Devices);
        Assert.Equal(("S7", PlcDriverKind.S7, "Plc1", "S7-1200"), (device.ChannelName, device.Driver, device.Name, device.Model));
        Assert.Equal(("192.168.3.20", 1102, 0, 2, 1500), (device.Host, device.Port, device.Rack, device.Slot, device.TimeoutMs));
        Assert.Equal(500, device.PollIntervalMs);
        Assert.True(device.Enabled);
        Assert.True(device.WritesEnabled);
        Assert.True(device.LegacyTagIds);
        Assert.Equal(["Line1.Speed", "Temp"], device.Tags.Select(t => t.TagId));
        Assert.True(device.Tags[0].Writable);
    }

    [Fact]
    public void V1Slmp_MigratesToAnFx5uDeviceOnASlmp3eChannel_WithWritesOff()
    {
        var settings = new Dictionary<string, string?>
        {
            ["Edge:Plc:Protocol"] = "slmp",
            ["Edge:Plc:Host"] = "192.168.3.39",
            ["Edge:Plc:Port"] = "5000",
            ["Edge:Tags:0:TagId"] = "F.D100",
            ["Edge:Tags:0:Address"] = "D100",
            ["Edge:Tags:0:DataType"] = "Int",
        };

        var device = Assert.Single(EdgeDeviceResolver.Resolve(Config(settings)).Devices);

        Assert.Equal(("Slmp3E", PlcDriverKind.Slmp3E, "FX5U", 5000), (device.ChannelName, device.Driver, device.Model, device.Port));
        Assert.False(device.WritesEnabled);
        Assert.Equal(1000, device.PollIntervalMs);
        Assert.Equal(["F.D100"], device.Tags.Select(t => t.TagId));
    }

    [Fact]
    public void V1WithS71500_KeepsThatCpuTypeForTheDriver_AndWarns()
    {
        var settings = new Dictionary<string, string?> { ["Edge:Plc:Host"] = "10.0.0.5", ["Edge:Plc:CpuType"] = "S71500" };
        var logs = new LogCapture();

        var device = Assert.Single(EdgeDeviceResolver.Resolve(Config(settings), logs.CreateLogger("resolver")).Devices);

        Assert.Equal("S7-1200", device.Model);
        Assert.Equal("S71500", device.CpuType);
        Assert.Contains(logs.Messages, m => m.Level == LogLevel.Warning
            && m.Text.Contains("S7-1500 is not supported; treated as S7-1200 for model rules", StringComparison.Ordinal));
    }

    [Fact]
    public void LabV1File_ResolvesToThe22TagIdsItPublishesToday_AndIsNotWritten()
    {
        var path = CopyFixture();
        var before = File.ReadAllBytes(path);

        var composed = EdgeSettingsFile.Compose(Config(BaseSettings()), _contentRoot, out var file);
        var device = Assert.Single(EdgeDeviceResolver.Resolve(composed).Devices);

        Assert.Equal(EdgeSettingsFile.SchemaVersion, file.Version);
        Assert.Equal("Plc1", device.Name);
        Assert.True(device.LegacyTagIds);
        Assert.Equal(LabTagIds, device.Tags.Select(t => t.TagId));
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Fact]
    public void V1AndV2Keys_WithoutASchema2File_FailNamingTheKeysPresent()
    {
        var settings = EdgeChannelsOptionsTests.Valid();
        settings["Edge:Tags:0:TagId"] = "Line1.Speed";

        var ex = Assert.Throws<InvalidOperationException>(() => EdgeDeviceResolver.Resolve(Config(settings)));

        // Names the single-PLC key actually present, not a fixed list.
        Assert.StartsWith("Edge:Tags (one PLC) and Edge:Channels", ex.Message);
    }

    [Fact]
    public void Schema1File_WithChannelsFromTheEnvironment_FailsNamingBoth()
    {
        CopyFixture();
        var environment = new Dictionary<string, string?>
        {
            ["Edge:Channels:0:Name"] = "S7",
            ["Edge:Channels:0:Driver"] = "S7",
        };

        var composed = EdgeSettingsFile.Compose(Config(BaseSettings(), environment), _contentRoot, out _);
        var ex = Assert.Throws<InvalidOperationException>(() => EdgeDeviceResolver.Resolve(composed));

        Assert.Contains("Edge:Plc", ex.Message);
        Assert.Contains("Edge:Channels", ex.Message);
    }

    [Fact]
    public void Schema2File_DropsTheV1KeysOfAppsettingsAndTheInstallerEnvironment_AndResolvesItsDevices()
    {
        var path = WriteFile("""
            {"SchemaVersion":2,"Edge":{"Channels":[
              {"Name":"S7","Driver":"S7","Devices":[
                {"Name":"Line1","Model":"S7-1200","Host":"10.0.0.1",
                 "Tags":[{"Name":"Temp","Address":"DB1.DBD0","DataType":"Real","Description":"Oven"}]}]},
              {"Name":"Fx","Driver":"Slmp3E","Devices":[
                {"Name":"Press","Model":"FX5U","Host":"10.0.0.2","Port":5000,"PollIntervalMs":250,
                 "Tags":[{"Name":"D100","Address":"D100","DataType":"Int"}]}]}]}}
            """);
        var before = File.ReadAllBytes(path);

        // appsettings.json ships Edge:Plc and Edge:Tags; the installer sets Edge__Plc__Host in the service environment.
        // An environment Edge:Channels value would merge into the file's array by index if it were not dropped.
        var environment = new Dictionary<string, string?>
        {
            ["Edge:Plc:Host"] = "192.168.3.20",
            ["Edge:Channels:0:Devices:0:Host"] = "10.9.9.9",
        };
        var composed = EdgeSettingsFile.Compose(Config(BaseSettings(), environment), _contentRoot, out var file);
        var set = EdgeDeviceResolver.Resolve(composed);

        Assert.Equal(EdgeSettingsFile.MultiDeviceSchemaVersion, file.Version);
        Assert.DoesNotContain(composed.AsEnumerable(), pair => pair.Value is not null && EdgeSettingsFile.IsOwned(pair.Key));
        Assert.Equal("settings-test", composed["Edge:EdgeId"]);
        Assert.Equal(["Line1.Temp", "Press.D100"], set.Devices.SelectMany(d => d.Tags).Select(t => t.TagId));
        Assert.Equal(250, set.Devices[1].PollIntervalMs);
        Assert.Equal("10.0.0.1", set.Devices[0].Host);
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    private static IConfiguration Config(Dictionary<string, string?> settings, Dictionary<string, string?>? environment = null)
    {
        var builder = new ConfigurationBuilder().AddInMemoryCollection(settings);
        if (environment is not null)
        {
            builder.AddInMemoryCollection(environment);
        }

        return builder.Build();
    }

    private static Dictionary<string, string?> BaseSettings() => new()
    {
        ["Edge:EdgeId"] = "settings-test",
        ["Edge:Buffer:Path"] = "data/edge.db",
        ["Edge:Plc:Host"] = "192.168.3.20",
        ["Edge:Plc:Protocol"] = "S7",
        ["Edge:Tags:0:TagId"] = "Line1.Running",
        ["Edge:Tags:0:Address"] = "DB1.DBX0.0",
        ["Edge:Tags:0:DataType"] = "Bool",
    };

    private string SettingsPath() => Path.Combine(_contentRoot, "data", "edge.settings.json");

    private string CopyFixture()
    {
        Directory.CreateDirectory(Path.Combine(_contentRoot, "data"));
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "lab-v1.settings.json"), SettingsPath());
        return SettingsPath();
    }

    private string WriteFile(string json)
    {
        Directory.CreateDirectory(Path.Combine(_contentRoot, "data"));
        File.WriteAllText(SettingsPath(), json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return SettingsPath();
    }

    private sealed class LogCapture : ILoggerProvider
    {
        public List<(LogLevel Level, string Text)> Messages { get; } = [];

        public ILogger CreateLogger(string categoryName) => new Logger(this);

        public void Dispose()
        {
        }

        private sealed class Logger(LogCapture owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                lock (owner.Messages)
                {
                    owner.Messages.Add((logLevel, formatter(state, exception)));
                }
            }
        }
    }
}
