using IndustrialIot.Edge.Drivers;
using IndustrialIot.Edge.Tests.Fakes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using S7Net = S7.Net;

namespace IndustrialIot.Edge.Tests;

public sealed class PlcProtocolSwitchTests
{
    private static Dictionary<string, string?> Plc(string? protocol, string? cpuType = null, string commands = "false") => new()
    {
        ["Edge:Plc:Protocol"] = protocol,
        ["Edge:Plc:CpuType"] = cpuType,
        ["Edge:Commands:Enabled"] = commands,
    };

    [Fact]
    public async Task DefaultProtocol_IsS7With1200()
    {
        await using var edge = EdgeTestHost.Create(Plc(protocol: null));
        await edge.Host.StartAsync();

        var driver = Assert.IsType<SiemensS7Driver>(edge.Services.GetRequiredService<IPlcDriver>());
        Assert.Equal(S7Net.CpuType.S71200, driver.CreatePlc().CPU);
        await edge.Host.StopAsync();
    }

    [Fact]
    public async Task CpuTypeS71500_ReachesS7netplus()
    {
        await using var edge = EdgeTestHost.Create(Plc("s7", "S71500"));
        await edge.Host.StartAsync();

        var driver = Assert.IsType<SiemensS7Driver>(edge.Services.GetRequiredService<IPlcDriver>());
        Assert.Equal(S7Net.CpuType.S71500, driver.CreatePlc().CPU);
        await edge.Host.StopAsync();
    }

    [Theory]
    [InlineData("Modbus")]
    [InlineData("1")]
    public async Task UnknownProtocol_FailsRealStartNamingTheKey(string protocol)
    {
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => StartRealHostAsync(Plc(protocol)));

        Assert.Contains("Edge:Plc:Protocol", ex.Message);
        Assert.DoesNotContain("Unable to resolve", ex.Message);
    }

    [Fact]
    public async Task UnknownCpuType_FailsStartNamingTheKey()
    {
        await using var edge = EdgeTestHost.Create(Plc("S7", "S7300"));

        var ex = await Assert.ThrowsAsync<OptionsValidationException>(() => edge.Host.StartAsync());
        Assert.Contains("Edge:Plc:CpuType", ex.Message);
    }

    [Fact]
    public async Task Slmp_Starts()
    {
        var settings = Plc("Slmp");
        await using var server = new FakeSlmpServer();
        settings["Edge:Plc:Port"] = server.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        settings["Edge:Plc:TimeoutMs"] = "1000";
        settings["Edge:Tags:0:Address"] = "D0";
        await using var edge = EdgeTestHost.Create(settings, runAgentServices: true);

        await edge.Host.StartAsync();

        Assert.IsType<IndustrialIot.Edge.Drivers.Slmp.MitsubishiSlmpDriver>(edge.Services.GetRequiredService<IPlcDriver>());
        await edge.Host.StopAsync();
        Assert.Empty(server.UnexpectedFrames);
    }

    [Fact]
    public async Task SlmpWithCommandsEnabled_FailsRealStartNamingBothKeys()
    {
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => StartRealHostAsync(Plc("Slmp", commands: "true")));

        Assert.Contains("Edge:Commands:Enabled", ex.Message);
        Assert.Contains("Edge:Plc:Protocol", ex.Message);
        Assert.DoesNotContain("Unable to resolve", ex.Message);
    }

    [Fact]
    public async Task UnknownProtocolWithCommandsEnabled_FailsRealStartNamingTheProtocolKey()
    {
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => StartRealHostAsync(Plc("Modbus", commands: "true")));

        Assert.Contains("Edge:Plc:Protocol", ex.Message);
    }

    [Fact]
    public void DriverRegistrations_AreOneForS7AndSlmp_AndZeroWhenTheProtocolIsRejected()
    {
        Assert.Equal(1, DriverRegistrations("S7"));
        Assert.Equal(1, DriverRegistrations(null));
        Assert.Equal(1, DriverRegistrations("Slmp"));
        Assert.Equal(0, DriverRegistrations("Modbus"));
    }

    [Theory]
    [InlineData("1")]
    [InlineData("40")]
    public async Task NumericCpuType_FailsStartNamingTheKey(string cpuType)
    {
        await using var edge = EdgeTestHost.Create(Plc("S7", cpuType));

        var ex = await Assert.ThrowsAsync<OptionsValidationException>(() => edge.Host.StartAsync());
        Assert.Contains("Edge:Plc:CpuType", ex.Message);
    }

    /// <summary>Builds and starts the real host with every agent hosted service, as Program.cs does.</summary>
    private static async Task StartRealHostAsync(Dictionary<string, string?> settings)
    {
        await using var edge = EdgeTestHost.Create(settings, runAgentServices: true);
        await edge.Host.StartAsync();
    }

    private static int DriverRegistrations(string? protocol)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Edge:Plc:Protocol"] = protocol,
                ["Edge:Plc:Host"] = "127.0.0.1",
            })
            .Build();
        var services = new ServiceCollection();
        try
        {
            services.AddPlcProtocol(configuration);
        }
        catch (OptionsValidationException)
        {
            // A rejected protocol throws before registering anything; the count below proves it.
        }

        return services.Count(d => d.ServiceType == typeof(IPlcDriver));
    }

    [Fact]
    public void ShippedAppSettings_HaveNoPlcPort_AndS7KeepsPort102()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "IndustrialIot.sln")))
        {
            root = root.Parent;
        }

        Assert.NotNull(root);
        var configuration = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(root.FullName, "src", "Edge", "appsettings.json"))
            .Build();

        Assert.Null(configuration["Edge:Plc:Port"]);
        Assert.Equal("S7", configuration["Edge:Plc:Protocol"]);
        var s7 = configuration.GetSection(SiemensS7Options.SectionName).Get<SiemensS7Options>();
        Assert.NotNull(s7);
        Assert.Equal(102, s7.Port);
    }
}
