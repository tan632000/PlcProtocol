using IndustrialIot.Edge.Drivers;
using IndustrialIot.Edge.Tests.Fakes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace IndustrialIot.Edge.Tests;

public class EdgeHostTests
{
    [Fact]
    public async Task ValidConfig_StartsAndResolvesServices()
    {
        await using var edge = EdgeTestHost.Create();

        await edge.Host.StartAsync();

        Assert.IsType<SiemensS7Driver>(edge.Services.GetRequiredService<IPlcDriver>());
        Assert.Same(TimeProvider.System, edge.Services.GetRequiredService<TimeProvider>());
        Assert.Equal("test-edge", edge.Services.GetRequiredService<IOptions<EdgeOptions>>().Value.EdgeId);

        // A catalog bound from the wrong section would be empty and still "resolve".
        var catalog = edge.Services.GetRequiredService<PlcTagCatalog>();
        Assert.True(catalog.TryGet("Line1.Speed", out var tag));
        Assert.Equal("DB1.DBW2", tag.Address.Canonical);

        await edge.Host.StopAsync();
    }

    [Theory]
    [InlineData("Edge:EdgeId", "", "Edge:EdgeId")]
    [InlineData("Edge:EdgeId", "a b", "Edge:EdgeId")]
    [InlineData("Edge:EdgeId", "a+", "Edge:EdgeId")]
    [InlineData("Edge:EdgeId", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "Edge:EdgeId")] // 65 chars
    [InlineData("Edge:Plc:Host", "", "Edge:Plc:Host")]
    [InlineData("Edge:Tags:0:Address", "I0.0", "Edge:Tags")] // with the overrides below: a writable input
    public async Task InvalidConfig_FailsAtStart(string key, string value, string expectedKey)
    {
        var overrides = new Dictionary<string, string?> { [key] = value };
        if (expectedKey == "Edge:Tags")
        {
            overrides["Edge:Tags:0:DataType"] = "Bool";
            overrides["Edge:Tags:0:Writable"] = "true";
        }

        // Building succeeds: the failure must come from StartAsync, not from a later first use.
        await using var edge = EdgeTestHost.Create(overrides);

        var ex = await Assert.ThrowsAsync<OptionsValidationException>(() => edge.Host.StartAsync());
        Assert.Contains(expectedKey, ex.Message, StringComparison.Ordinal);
    }
}
