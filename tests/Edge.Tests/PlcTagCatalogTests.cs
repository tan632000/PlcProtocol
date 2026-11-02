using System.Text.Json;
using IndustrialIot.Edge.Drivers;
using IndustrialIot.Edge.Drivers.Models;
using IndustrialIot.Edge.Drivers.Slmp;
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

    [Theory]
    [InlineData("String", "\"ABCDE\"", true)]
    [InlineData("String", "\"ABCDEF\"", false)] // longer than Length 5
    [InlineData("String", "\"\u4E2D\"", false)] // not one byte per character
    [InlineData("String", "5", false)]
    [InlineData("LReal", "1.5e300", true)]
    [InlineData("LReal", "\"1.5\"", false)]
    [InlineData("DTL", "\"2026-11-02T10:15:30.123\"", true)]
    [InlineData("DTL", "\"2026-11-02 10:15:30\"", false)]
    [InlineData("DTL", "\"2026-13-02T10:15:30.123\"", false)]
    [InlineData("DTL", "\"1969-12-31T23:59:59.999\"", false)]
    public void NewTypes_AreNormalizedOrRefusedBeforeAnyWrite(string type, string json, bool allowed)
    {
        var dataType = Enum.Parse<PlcDataType>(type);
        var catalog = new PlcTagCatalog(new PlcTagCatalogOptions
        {
            Tags = [new PlcTagDefinition { TagId = "t", Address = "DB1.DBB0", DataType = dataType, Writable = true, Length = dataType == PlcDataType.String ? 5 : null }],
        });

        var decision = catalog.EvaluateWrite("t", Json(json));

        Assert.Equal(allowed, decision.Allowed);
        Assert.Equal(allowed ? null : CommandReasons.InvalidValue, decision.Reason);
    }

    [Fact]
    public void AString_IsPaddedToItsLength_AndAnLRealBoundDoesNotOverflow()
    {
        var catalog = new PlcTagCatalog(new PlcTagCatalogOptions
        {
            Tags =
            [
                new PlcTagDefinition { TagId = "s", Address = "DB1.DBB0", DataType = PlcDataType.String, Writable = true, Length = 5 },
                new PlcTagDefinition { TagId = "l", Address = "DB1.DBB8", DataType = PlcDataType.LReal, Writable = true, Max = 1 },
            ],
        });

        Assert.Equal("AB\0\0\0", catalog.EvaluateWrite("s", Json("\"AB\"")).NormalizedValue);
        Assert.Equal(CommandReasons.ValueOutOfRange, catalog.EvaluateWrite("l", Json("1.5e300")).Reason);
        Assert.True(catalog.EvaluateWrite("l", Json("0.5")).Allowed);
    }

    [Theory]
    [InlineData("D100", "DInt", null, "D101", "Int", true)] // a writable DInt covers D100-D101
    [InlineData("D100", "Int", null, "D101", "Int", false)]
    [InlineData("D200", "String", 3, "D201", "Int", true)] // String 3 covers D200-D201
    [InlineData("D200", "String", 2, "D201", "Int", false)]
    [InlineData("D300", "LReal", null, "D303", "Int", true)]
    [InlineData("M5", "Bool", null, "M6", "Bool", false)]
    [InlineData("Y7", "Bool", null, "Y7", "Bool", true)]
    public void SlmpOverlap_UsesTheRealSizes(string writable, string writableType, int? length, string other, string otherType, bool overlaps)
    {
        PlcTagDefinition[] tags =
        [
            new() { TagId = "w", Address = writable, DataType = Enum.Parse<PlcDataType>(writableType), Writable = true, Length = length },
            new() { TagId = "o", Address = other, DataType = Enum.Parse<PlcDataType>(otherType) },
        ];

        var build = () => new PlcTagCatalog(new PlcTagCatalogOptions { Tags = tags }, new SlmpAddressSyntax());

        if (overlaps)
        {
            Assert.Contains("overlaps", Assert.Throws<InvalidOperationException>(build).Message, StringComparison.Ordinal);
        }
        else
        {
            build();
        }
    }

    [Theory]
    [InlineData("DB1.DBB10", "String", 10, "DB1.DBW20", "Int", true)] // STRING[10] covers bytes 10-21
    [InlineData("DB1.DBB10", "String", 10, "DB1.DBW22", "Int", false)]
    [InlineData("DB1.DBB0", "LReal", null, "DB1.DBB7", "Byte", true)]
    [InlineData("DB1.DBB0", "DTL", null, "DB1.DBX11.7", "Bool", true)]
    [InlineData("DB1.DBB0", "DTL", null, "DB1.DBB12", "Byte", false)]
    public void S7Overlap_UsesTheNewTypesSizes(string writable, string writableType, int? length, string other, string otherType, bool overlaps)
    {
        PlcTagDefinition[] tags =
        [
            new() { TagId = "w", Address = writable, DataType = Enum.Parse<PlcDataType>(writableType), Writable = true, Length = length },
            new() { TagId = "o", Address = other, DataType = Enum.Parse<PlcDataType>(otherType) },
        ];

        var build = () => new PlcTagCatalog(new PlcTagCatalogOptions { Tags = tags });

        if (overlaps)
        {
            Assert.Contains("overlaps", Assert.Throws<InvalidOperationException>(build).Message, StringComparison.Ordinal);
        }
        else
        {
            build();
        }
    }

    [Fact]
    public void Union_OfAnS7AndAnSlmpCatalog_HoldsBoth_AndKeepsIdsUnique()
    {
        var s7 = new PlcTagCatalog(new PlcTagCatalogOptions { Tags = [Tag("A.Speed", "DB1.DBW2", PlcDataType.Int, writable: true)] });
        var fx = new PlcTagCatalog(new PlcTagCatalogOptions { Tags = [Tag("Press.D100", "D100", PlcDataType.Int)] }, new SlmpAddressSyntax());
        var otherS7 = new PlcTagCatalog(new PlcTagCatalogOptions { Tags = [Tag("B.Speed", "DB1.DBW2", PlcDataType.Int, writable: true)] });

        var union = PlcTagCatalog.Union([s7, fx]);

        Assert.Equal(["A.Speed", "Press.D100"], union.Tags.Select(t => t.TagId));
        Assert.True(union.TryGet("press.d100", out var tag));
        Assert.Equal("D100", tag.Address.Canonical);

        // Two PLCs may use the same address: writable overlap is a rule within one PLC.
        Assert.Equal(2, PlcTagCatalog.Union([s7, otherS7]).Tags.Count);
        Assert.Throws<InvalidOperationException>(() => PlcTagCatalog.Union([s7, s7]));
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
