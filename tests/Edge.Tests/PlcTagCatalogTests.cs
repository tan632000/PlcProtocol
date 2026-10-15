using System.Text.Json;
using IndustrialIot.Edge.Drivers;
using IndustrialIot.Edge.Drivers.Models;
using IndustrialIot.Shared.Common;

namespace IndustrialIot.Edge.Tests;

public class PlcTagCatalogTests
{
    private static readonly PlcTagCatalog Catalog = new(new PlcTagCatalogOptions
    {
        Tags =
        [
            Tag("Motor.Start", "DB1.DBX0.0", PlcDataType.Bool, writable: true),
            Tag("Speed", "DB1.DBW2", PlcDataType.Int, writable: true, min: 0, max: 1500),
            Tag("Setpoint", "DB1.DBD4", PlcDataType.Real, writable: true, min: -10.5m, max: 100m),
            Tag("Counter", "DB1.DBD8", PlcDataType.DInt),
            Tag("Sensor", "I0.0", PlcDataType.Bool),
            Tag("Level", "IW2", PlcDataType.Int),
            Tag("Lamp", "Q0.1", PlcDataType.Bool, writable: true),
            Tag("Unbounded", "MW20", PlcDataType.Word, writable: true),
            Tag("Gain", "MD24", PlcDataType.Real, writable: true, min: 0m, max: 0.1m),
        ],
    });

    [Fact]
    public void Empty_DeniesAll()
    {
        var empty = new PlcTagCatalog(new PlcTagCatalogOptions());
        var nullTags = new PlcTagCatalog(new PlcTagCatalogOptions { Tags = null! });

        foreach (var catalog in new[] { empty, nullTags })
        {
            var decision = catalog.EvaluateWrite("Motor.Start", Json("true"));

            Assert.False(decision.Allowed);
            Assert.Equal(CommandReasons.UnknownTag, decision.Reason);
            Assert.Null(decision.NormalizedValue);
            Assert.False(catalog.TryGet("Motor.Start", out _));
        }
    }

    [Theory]
    [InlineData("Nope", "true", CommandReasons.UnknownTag)]
    [InlineData("", "true", CommandReasons.UnknownTag)]
    [InlineData("Sensor", "true", CommandReasons.InputAreaNotWritable)]
    [InlineData("Level", "\"abc\"", CommandReasons.InputAreaNotWritable)]  // area is checked before the value
    [InlineData("Counter", "5", CommandReasons.TagNotWritable)]
    [InlineData("Counter", "\"abc\"", CommandReasons.TagNotWritable)]      // writability is checked before the value
    [InlineData("Speed", "1.0", CommandReasons.InvalidValue)]
    [InlineData("Speed", "\"12\"", CommandReasons.InvalidValue)]
    [InlineData("Speed", "40000", CommandReasons.InvalidValue)]            // beyond Int itself
    [InlineData("Motor.Start", "1", CommandReasons.InvalidValue)]
    [InlineData("Speed", "1501", CommandReasons.ValueOutOfRange)]
    [InlineData("Speed", "-1", CommandReasons.ValueOutOfRange)]
    [InlineData("Setpoint", "100.5", CommandReasons.ValueOutOfRange)]
    [InlineData("Setpoint", "-10.6", CommandReasons.ValueOutOfRange)]
    [InlineData("Gain", "0.11", CommandReasons.ValueOutOfRange)]
    [InlineData("Speed", "0", null)]
    [InlineData("Speed", "1500", null)]
    [InlineData("Setpoint", "-10.5", null)]
    [InlineData("Setpoint", "100", null)]
    [InlineData("Gain", "0.1", null)]
    [InlineData("Motor.Start", "true", null)]
    [InlineData("motor.START", "false", null)]
    [InlineData("Lamp", "true", null)]
    [InlineData("Unbounded", "65535", null)]
    public void EvaluateWrite_ReasonCodes(string tagId, string json, string? expectedReason)
    {
        var decision = Catalog.EvaluateWrite(tagId, Json(json));

        Assert.Equal(expectedReason, decision.Reason);
        Assert.Equal(expectedReason is null, decision.Allowed);
        if (decision.Allowed)
        {
            Assert.NotNull(decision.ResolvedTag);
            Assert.NotNull(decision.NormalizedValue);
            Assert.Equal(decision.ResolvedTag.DataType, TypeOf(decision.NormalizedValue));
        }
        else
        {
            Assert.Null(decision.NormalizedValue);
        }
    }

    [Fact]
    public void TryGet_IsCaseInsensitive_AndReturnsParsedAddress()
    {
        Assert.True(Catalog.TryGet("SPEED", out var tag));
        Assert.Equal("Speed", tag.TagId);
        Assert.Equal("DB1.DBW2", tag.Address.Canonical);
        Assert.Equal(PlcDataType.Int, tag.DataType);
        Assert.False(Catalog.TryGet(null!, out _));
    }

    public static TheoryData<string, PlcTagDefinition?[]> InvalidCatalogs() => new()
    {
        { "null entry", [null] },
        { "empty tagId", [Tag("", "DB1.DBW0", PlcDataType.Int)] },
        { "tagId with space", [Tag("a b", "DB1.DBW0", PlcDataType.Int)] },
        { "tagId 65 chars", [Tag(new string('a', 65), "DB1.DBW0", PlcDataType.Int)] },
        { "invalid address", [Tag("A", "DB1.XYZ", PlcDataType.Int)] },
        { "empty address", [Tag("A", "", PlcDataType.Int)] },
        { "size mismatch", [Tag("A", "DB1.DBW2", PlcDataType.Real)] },
        { "bit address as Int", [Tag("A", "DB1.DBX0.0", PlcDataType.Int)] },
        { "missing DataType", [Tag("A", "DB1.DBW0", default)] },
        { "duplicate tagId (case)", [Tag("Speed", "DB1.DBW0", PlcDataType.Int), Tag("SPEED", "DB1.DBW2", PlcDataType.Int)] },
        { "writable input", [Tag("A", "IW0", PlcDataType.Int, writable: true)] },
        { "writable input bit", [Tag("A", "I0.0", PlcDataType.Bool, writable: true)] },
        { "Min on Bool", [Tag("A", "M0.0", PlcDataType.Bool, min: 0)] },
        { "Max on Bool", [Tag("A", "M0.0", PlcDataType.Bool, max: 1)] },
        { "Min > Max", [Tag("A", "MW0", PlcDataType.Int, min: 10, max: 5)] },
        { "Min below Int", [Tag("A", "MW0", PlcDataType.Int, min: -32769)] },
        { "Max above Byte", [Tag("A", "MB0", PlcDataType.Byte, max: 256)] },
        { "Min below Word", [Tag("A", "MW0", PlcDataType.Word, min: -1)] },
        { "Max above DWord", [Tag("A", "MD0", PlcDataType.DWord, max: 4294967296m)] },
    };

    [Theory]
    [MemberData(nameof(InvalidCatalogs))]
    public void InvalidCatalog_ThrowsAtConstruction(string scenario, PlcTagDefinition?[] tags)
    {
        Assert.False(string.IsNullOrEmpty(scenario));
        Assert.Throws<InvalidOperationException>(() => new PlcTagCatalog(new PlcTagCatalogOptions { Tags = tags! }));
    }

    public static TheoryData<string, PlcTagDefinition[]> OverlappingCatalogs() => new()
    {
        { "writable word over read-only bit", [Tag("W", "DB1.DBW0", PlcDataType.Int, writable: true), Tag("B", "DB1.DBX0.3", PlcDataType.Bool)] },
        { "writable word over read-only bit (second byte)", [Tag("W", "DB1.DBW0", PlcDataType.Int, writable: true), Tag("B", "DB1.DBX1.7", PlcDataType.Bool)] },
        { "writable bit inside read-only dword", [Tag("D", "DB1.DBD0", PlcDataType.DInt), Tag("B", "DB1.DBX3.0", PlcDataType.Bool, writable: true)] },
        { "writable word straddling dword", [Tag("D", "MD0", PlcDataType.DInt), Tag("W", "MW2", PlcDataType.Int, writable: true)] },
        { "writable word straddling words", [Tag("W1", "QW0", PlcDataType.Int), Tag("W2", "QW1", PlcDataType.Int, writable: true)] },
        { "two bools same bit", [Tag("B1", "M0.0", PlcDataType.Bool, writable: true), Tag("B2", "M0.0", PlcDataType.Bool)] },
        { "writable byte over bool", [Tag("Y", "QB0", PlcDataType.Byte, writable: true), Tag("B", "Q0.1", PlcDataType.Bool)] },
    };

    [Theory]
    [MemberData(nameof(OverlappingCatalogs))]
    public void Overlap_WritableTag_Throws(string scenario, PlcTagDefinition[] tags)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => new PlcTagCatalog(new PlcTagCatalogOptions { Tags = tags }));
        Assert.Contains("overlaps", ex.Message, StringComparison.Ordinal);
        Assert.False(string.IsNullOrEmpty(scenario));
    }

    public static TheoryData<string, PlcTagDefinition[]> NonOverlappingCatalogs() => new()
    {
        { "two writable bools, different bits", [Tag("B1", "DB1.DBX0.0", PlcDataType.Bool, writable: true), Tag("B2", "DB1.DBX0.1", PlcDataType.Bool, writable: true)] },
        { "adjacent writable words", [Tag("W1", "DB1.DBW0", PlcDataType.Int, writable: true), Tag("W2", "DB1.DBW2", PlcDataType.Int, writable: true)] },
        { "same offset, different DB", [Tag("W1", "DB1.DBW0", PlcDataType.Int, writable: true), Tag("W2", "DB2.DBW0", PlcDataType.Int, writable: true)] },
        { "same offset, different area", [Tag("W1", "MW0", PlcDataType.Int, writable: true), Tag("W2", "QW0", PlcDataType.Int, writable: true)] },
        { "read-only tags may overlap", [Tag("D", "DB1.DBD0", PlcDataType.DInt), Tag("W", "DB1.DBW0", PlcDataType.Int)] },
    };

    [Theory]
    [MemberData(nameof(NonOverlappingCatalogs))]
    public void NonOverlapping_Accepted(string scenario, PlcTagDefinition[] tags)
    {
        var catalog = new PlcTagCatalog(new PlcTagCatalogOptions { Tags = tags });

        Assert.All(tags, t => Assert.True(catalog.TryGet(t.TagId, out _), scenario));
    }

    private static PlcTagDefinition Tag(
        string tagId, string address, PlcDataType type, bool writable = false, decimal? min = null, decimal? max = null) =>
        new() { TagId = tagId, Address = address, DataType = type, Writable = writable, Min = min, Max = max };

    private static PlcDataType TypeOf(object value) => value switch
    {
        bool => PlcDataType.Bool,
        byte => PlcDataType.Byte,
        ushort => PlcDataType.Word,
        short => PlcDataType.Int,
        uint => PlcDataType.DWord,
        int => PlcDataType.DInt,
        float => PlcDataType.Real,
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    private static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
