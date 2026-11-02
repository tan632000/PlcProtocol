using IndustrialIot.Edge.Configuration;
using IndustrialIot.Edge.Drivers;
using Microsoft.Extensions.Configuration;

namespace IndustrialIot.Edge.Tests;

/// <summary>
/// The model a device names decides its driver, its connection defaults and what its tags may use, so a wrong
/// model, port, address or type fails the start naming the key.
/// </summary>
public sealed class PlcModelCatalogTests
{
    [Fact]
    public void S7_1200_Defaults_AreThoseTheDriverUses_AndAGivenPortOverridesThem()
    {
        Assert.True(PlcModelCatalog.TryGet("s7-1200", out var model));
        Assert.Equal((PlcDriverKind.S7, 102, 0, 1), (model.Driver, model.DefaultPort, model.DefaultRack, model.DefaultSlot));

        var omitted = new ResolvedDevice("S7", PlcDriverKind.S7, "A", "S7-1200", "10.0.0.1", null, null, null, null, 3000, 1000, true, false, false, []);
        var options = PlcDeviceRegistry.S7Options(omitted);
        Assert.Equal((model.DefaultPort, model.DefaultRack, model.DefaultSlot), (options.Port, options.Rack, options.Slot));
        Assert.Equal(1102, PlcDeviceRegistry.S7Options(omitted with { Port = 1102 }).Port);
    }

    [Theory]
    [InlineData("FX5U", PlcDriverKind.Slmp3E)]
    [InlineData("FX3U", PlcDriverKind.Mc1E)]
    public void MitsubishiModels_HaveNoDefaultPort(string name, PlcDriverKind driver)
    {
        Assert.True(PlcModelCatalog.TryGet(name, out var model));
        Assert.Equal(driver, model.Driver);
        Assert.Null(model.DefaultPort);
    }

    [Theory]
    [InlineData("FX3U", "102", "Edge:Channels:0:Devices:0:Port", "S7 port")]
    [InlineData("FX3U", null, "Edge:Channels:0:Devices:0:Port", "GX Works2")]
    [InlineData("FX5U", null, "Edge:Channels:0:Devices:0:Port", "GX Works3")]
    [InlineData("FX5U", "102", "Edge:Channels:0:Devices:0:Port", "S7 port")]
    public void MitsubishiPort_MustBeGiven_AndNotTheS7Port(string model, string? port, string key, string hint)
    {
        var settings = Fx(model, "D100", "Int");
        settings["Edge:Channels:0:Devices:0:Port"] = port;

        var errors = Validate(settings);

        Assert.Contains(errors, e => e.StartsWith(key + " ", StringComparison.Ordinal) && e.Contains(hint, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("X400", "Bool", "Address")] // X/Y are octal: 377 is the last one
    [InlineData("X8", "Bool", "Address")] // not an octal digit
    [InlineData("D8000", "Int", "Address")]
    [InlineData("M7680", "Bool", "Address")]
    [InlineData("M5", "Real", "Address")] // bit devices hold Bool
    [InlineData("D7999", "DInt", "Address")] // the second word would be D8000
    [InlineData("DB1.DBW2", "Int", "Address")]
    [InlineData("D100", "Byte", "DataType")]
    public void Fx3uTag_OutsideItsRangesOrTypes_FailsNamingTheTag(string address, string dataType, string field)
    {
        var errors = Validate(Fx("FX3U", address, dataType, port: "5001"));

        Assert.Contains(errors, e => e.StartsWith($"Edge:Channels:0:Devices:0:Tags:0:{field} ", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("X377", "Bool")]
    [InlineData("Y0", "Bool")]
    [InlineData("M7679", "Bool")]
    [InlineData("D7998", "DInt")]
    [InlineData("d100", "Real")]
    public void Fx3uTag_WithinItsRanges_IsAccepted(string address, string dataType)
    {
        Assert.Empty(Validate(Fx("FX3U", address, dataType, port: "5001")));
    }

    [Fact]
    public void ATypeTheModelDoesNotSupport_FailsWithTheModelsMessage()
    {
        var errors = Validate(Fx("FX5U", "D100", "Byte", port: "5000"));

        Assert.Contains(errors, e => e.StartsWith("Edge:Channels:0:Devices:0:Tags:0:DataType ", StringComparison.Ordinal)
            && e.Contains("FX5U", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("FX5U", "DTL", "5000")]
    [InlineData("FX3U", "LReal", "5001")]
    public void ANewTypeTheModelDoesNotSupport_FailsWithTheModelsMessage_NotATypeParseError(string model, string dataType, string port)
    {
        var errors = Validate(Fx(model, "D100", dataType, port));

        var error = Assert.Single(errors);
        Assert.StartsWith("Edge:Channels:0:Devices:0:Tags:0:DataType ", error, StringComparison.Ordinal);
        Assert.Contains($"not supported by {model}", error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("FX5U", "String", "5000")]
    [InlineData("FX5U", "LReal", "5000")]
    [InlineData("FX3U", "String", "5001")]
    public void ANewTypeTheModelSupports_IsAccepted(string model, string dataType, string port)
    {
        Assert.Empty(Validate(Fx(model, "D100", dataType, port)));
    }

    [Theory]
    [InlineData("Edge:Channels:0:Devices:0:WritesEnabled", "Edge:Channels:0:Devices:0:WritesEnabled")]
    [InlineData("Edge:Channels:0:Devices:0:Tags:0:Writable", "Edge:Channels:0:Devices:0:Tags:0:Writable")]
    public void Fx3uWrites_AreRefusedAtStart_UntilVerifiedOnARealFx3u(string key, string expectedKey)
    {
        var settings = Fx("FX3U", "D100", "Int", port: "5001");
        settings[key] = "true";

        var errors = Validate(settings);

        Assert.Contains(errors, e => e == $"{expectedKey} FX3U writes are disabled until verified on a real FX3U.");
    }

    [Fact]
    public void Fx5uWrites_AreAllowed()
    {
        var settings = Fx("FX5U", "D100", "Int", port: "5000");
        settings["Edge:Channels:0:Devices:0:WritesEnabled"] = "true";
        settings["Edge:Channels:0:Devices:0:Tags:0:Writable"] = "true";

        Assert.Empty(Validate(settings));
    }

    [Fact]
    public void AnS7_1200UnderASlmp3eChannel_FailsNamingTheModel()
    {
        var settings = Fx("FX5U", "D100", "Int", port: "5000");
        settings["Edge:Channels:0:Devices:0:Model"] = "S7-1200";

        var errors = Validate(settings);

        Assert.Contains(errors, e => e.StartsWith("Edge:Channels:0:Devices:0:Model ", StringComparison.Ordinal)
            && e.Contains("S7", StringComparison.Ordinal));
    }

    private static IReadOnlyList<string> Validate(Dictionary<string, string?> settings) =>
        EdgeChannelsValidator.Validate(new ConfigurationBuilder().AddInMemoryCollection(settings).Build()
            .GetSection(EdgeChannelsOptions.SectionName).Get<EdgeChannelsOptions>()!);

    private static Dictionary<string, string?> Fx(string model, string address, string dataType, string? port = null) => new()
    {
        ["Edge:Channels:0:Name"] = "Fx",
        ["Edge:Channels:0:Driver"] = model == "FX3U" ? "Mc1E" : "Slmp3E",
        ["Edge:Channels:0:Devices:0:Name"] = "Press",
        ["Edge:Channels:0:Devices:0:Model"] = model,
        ["Edge:Channels:0:Devices:0:Host"] = "10.0.0.2",
        ["Edge:Channels:0:Devices:0:Port"] = port,
        ["Edge:Channels:0:Devices:0:Tags:0:Name"] = "T",
        ["Edge:Channels:0:Devices:0:Tags:0:Address"] = address,
        ["Edge:Channels:0:Devices:0:Tags:0:DataType"] = dataType,
    };
}
