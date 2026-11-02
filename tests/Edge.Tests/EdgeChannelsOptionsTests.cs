using IndustrialIot.Edge.Configuration;
using Microsoft.Extensions.Configuration;

namespace IndustrialIot.Edge.Tests;

/// <summary>
/// The multi-device configuration <c>Edge:Channels</c> binds as a tree, and every invalid case fails naming the full
/// key, so an installer can find the line to fix without reading the code.
/// </summary>
public sealed class EdgeChannelsOptionsTests
{
    [Fact]
    public void TwoChannels_Bind_AndAreValid()
    {
        var options = Bind(Valid());

        Assert.Equal(["S7", "Fx"], options.Channels.Select(c => c.Name));
        Assert.Equal("Slmp3E", options.Channels[1].Driver);
        var line1 = options.Channels[0].Devices[0];
        Assert.Equal(("Line1", "S7-1200", "10.0.0.1"), (line1.Name, line1.Model, line1.Host));
        Assert.True(line1.Enabled);
        Assert.False(line1.WritesEnabled);
        Assert.Equal(["Temp", "Speed"], line1.Tags.Select(t => t.Name));
        Assert.Equal(5000, options.Channels[1].Devices[0].Port);
        Assert.Empty(EdgeChannelsValidator.Validate(options));
    }

    [Theory]
    [InlineData("Edge:Channels:0:Name", "Line 1", "Edge:Channels:0:Name")]
    [InlineData("Edge:Channels:1:Name", "s7", "Edge:Channels:1:Name")]
    [InlineData("Edge:Channels:0:Driver", "Modbus", "Edge:Channels:0:Driver")]
    [InlineData("Edge:Channels:0:Devices:0:Model", "S7-1500", "Edge:Channels:0:Devices:0:Model")]
    [InlineData("Edge:Channels:1:Devices:0:Model", "S7-1200", "Edge:Channels:1:Devices:0:Model")]
    [InlineData("Edge:Channels:1:Devices:0:Name", "line1", "Edge:Channels:1:Devices:0:Name")]
    [InlineData("Edge:Channels:0:Devices:0:Host", "", "Edge:Channels:0:Devices:0:Host")]
    [InlineData("Edge:Channels:0:Devices:0:Tags:1:Name", "temp", "Edge:Channels:0:Devices:0:Tags:1:Name")]
    [InlineData("Edge:Channels:0:Devices:0:Tags:0:Name", "a/b", "Edge:Channels:0:Devices:0:Tags:0:Name")]
    [InlineData("Edge:Channels:0:Devices:0:Tags:0:DataType", "Float", "Edge:Channels:0:Devices:0:Tags:0:DataType")]
    [InlineData("Edge:Channels:0:Devices:0:Tags:0:Address", "", "Edge:Channels:0:Devices:0:Tags:0:Address")]
    public void InvalidValue_FailsNamingTheKey(string key, string value, string expectedKey)
    {
        var settings = Valid();
        settings[key] = value;

        var errors = EdgeChannelsValidator.Validate(Bind(settings));

        Assert.Contains(errors, e => e.StartsWith(expectedKey + " ", StringComparison.Ordinal));
    }

    [Fact]
    public void LegacyDevice_WhoseTagNameMatchesAnotherDevicesId_IgnoringCase_FailsAsDuplicate()
    {
        var settings = Valid();
        settings["Edge:Channels:1:Devices:0:LegacyTagIds"] = "true";
        settings["Edge:Channels:1:Devices:0:Tags:0:Name"] = "line1.temp";

        var errors = EdgeChannelsValidator.Validate(Bind(settings));

        Assert.Contains(errors, e => e.StartsWith("Edge:Channels:1:Devices:0:Tags:0:Name ", StringComparison.Ordinal)
            && e.Contains("Line1.Temp", StringComparison.Ordinal));
    }

    [Fact]
    public void TwoDevices_OnOneHostAndPort_Fail()
    {
        var settings = Valid();
        settings["Edge:Channels:0:Devices:1:Name"] = "Line2";
        settings["Edge:Channels:0:Devices:1:Model"] = "S7-1200";
        settings["Edge:Channels:0:Devices:1:Host"] = "10.0.0.1";

        var errors = EdgeChannelsValidator.Validate(Bind(settings));

        // Both S7-1200 devices use the default port 102.
        Assert.Contains(errors, e => e.StartsWith("Edge:Channels:0:Devices:1:Host ", StringComparison.Ordinal));
    }

    [Fact]
    public void PublishedTagId_Over64Chars_Fails()
    {
        var settings = Valid();
        settings["Edge:Channels:0:Devices:0:Name"] = new string('D', 32);
        settings["Edge:Channels:0:Devices:0:Tags:0:Name"] = new string('t', 32);

        var errors = EdgeChannelsValidator.Validate(Bind(settings));

        // 32 + "." + 32 = 65 chars.
        Assert.Contains(errors, e => e.StartsWith("Edge:Channels:0:Devices:0:Tags:0:Name ", StringComparison.Ordinal)
            && e.Contains("64", StringComparison.Ordinal));
    }

    internal static EdgeChannelsOptions Bind(Dictionary<string, string?> settings) =>
        new ConfigurationBuilder().AddInMemoryCollection(settings).Build()
            .GetSection(EdgeChannelsOptions.SectionName).Get<EdgeChannelsOptions>() ?? new EdgeChannelsOptions();

    internal static Dictionary<string, string?> Valid() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["Edge:Channels:0:Name"] = "S7",
        ["Edge:Channels:0:Driver"] = "S7",
        ["Edge:Channels:0:Devices:0:Name"] = "Line1",
        ["Edge:Channels:0:Devices:0:Model"] = "S7-1200",
        ["Edge:Channels:0:Devices:0:Host"] = "10.0.0.1",
        ["Edge:Channels:0:Devices:0:Tags:0:Name"] = "Temp",
        ["Edge:Channels:0:Devices:0:Tags:0:Address"] = "DB1.DBD0",
        ["Edge:Channels:0:Devices:0:Tags:0:DataType"] = "Real",
        ["Edge:Channels:0:Devices:0:Tags:1:Name"] = "Speed",
        ["Edge:Channels:0:Devices:0:Tags:1:Address"] = "DB1.DBW4",
        ["Edge:Channels:0:Devices:0:Tags:1:DataType"] = "Int",
        ["Edge:Channels:1:Name"] = "Fx",
        ["Edge:Channels:1:Driver"] = "Slmp3E",
        ["Edge:Channels:1:Devices:0:Name"] = "Press",
        ["Edge:Channels:1:Devices:0:Model"] = "FX5U",
        ["Edge:Channels:1:Devices:0:Host"] = "10.0.0.2",
        ["Edge:Channels:1:Devices:0:Port"] = "5000",
        ["Edge:Channels:1:Devices:0:Tags:0:Name"] = "D100",
        ["Edge:Channels:1:Devices:0:Tags:0:Address"] = "D100",
        ["Edge:Channels:1:Devices:0:Tags:0:DataType"] = "Int",
    };
}
