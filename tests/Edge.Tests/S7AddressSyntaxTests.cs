using IndustrialIot.Edge.Drivers;
using IndustrialIot.Edge.Drivers.Models;
using IndustrialIot.Edge.Tests.Fakes;
using Microsoft.Extensions.Options;

namespace IndustrialIot.Edge.Tests;

public class S7AddressSyntaxTests
{
    private static readonly S7AddressSyntax Syntax = new();

    [Theory]
    [InlineData("DB1.DBB10", PlcDataType.String, 10, true)]
    [InlineData("DB1.DBB10", PlcDataType.String, 218, true)]
    [InlineData("DB1.DBB10", PlcDataType.String, 219, false)] // one STRING item must fit a 240-byte PDU
    [InlineData("DB1.DBB10", PlcDataType.String, 0, false)]
    [InlineData("DB1.DBW10", PlcDataType.String, 10, false)]
    [InlineData("DB1.DBB0", PlcDataType.LReal, null, true)]
    [InlineData("DB1.DBD0", PlcDataType.LReal, null, false)]
    [InlineData("MB4", PlcDataType.DTL, null, true)]
    [InlineData("DB1.DBX0.0", PlcDataType.DTL, null, false)]
    public void NewTypes_NeedAByteAddress_AndAStringLengthThatFitsOnePdu(string address, PlcDataType type, int? length, bool accepted)
    {
        if (accepted)
        {
            Assert.Equal(address, Syntax.Parse(address, type, length).Canonical);
        }
        else
        {
            Assert.ThrowsAny<PlcException>(() => Syntax.Parse(address, type, length));
        }
    }

    [Theory]
    [InlineData("DB1.DBB10", "String", null, null, "Length")] // String needs its Length
    [InlineData("DB1.DBW10", "Int", 10, null, "Length")] // only String has a Length
    [InlineData("DB1.DBB10", "DTL", null, 1, "Min")] // bounds are for numbers
    [InlineData("DB1.DBB10", "String", 10, 1, "Min")]
    public void Catalog_NewTypeRules_FailNamingTheTag(string address, string type, int? length, int? min, string fragment)
    {
        var definition = new PlcTagDefinition
        {
            TagId = "Line1.Recipe",
            Address = address,
            DataType = Enum.Parse<PlcDataType>(type),
            Length = length,
            Min = min,
        };

        var ex = Assert.Throws<InvalidOperationException>(() => new PlcTagCatalog(new PlcTagCatalogOptions { Tags = [definition] }));

        Assert.Contains("Line1.Recipe", ex.Message, StringComparison.Ordinal);
        Assert.Contains(fragment, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Catalog_LRealKeepsItsBounds_AndAStringItsLength()
    {
        var catalog = new PlcTagCatalog(new PlcTagCatalogOptions
        {
            Tags =
            [
                new PlcTagDefinition { TagId = "Ratio", Address = "DB1.DBB0", DataType = PlcDataType.LReal, Min = -1, Max = 1 },
                new PlcTagDefinition { TagId = "Recipe", Address = "DB1.DBB8", DataType = PlcDataType.String, Length = 10 },
            ],
        });

        Assert.Equal((-1m, 1m), (catalog.Tags[0].Min, catalog.Tags[0].Max));
        Assert.Equal(10, catalog.Tags[1].Length);
    }

    [Theory]
    [InlineData("DB1.DBW0", PlcDataType.Bool, "Address 'DB1.DBW0' cannot hold a Bool value.")]
    [InlineData("DB1.DBX0.1", PlcDataType.Int, "Address 'DB1.DBX0.1' cannot hold a Int value.")]
    [InlineData("db1.dbw2", PlcDataType.DWord, "Address 'DB1.DBW2' cannot hold a DWord value.")]
    [InlineData("MB4", PlcDataType.Real, "Address 'MB4' cannot hold a Real value.")]
    [InlineData("DB1.DBB0", PlcDataType.Int, "Address 'DB1.DBB0' cannot hold a Int value.")]
    [InlineData("DB1.DBD0", PlcDataType.Int, "Address 'DB1.DBD0' cannot hold a Int value.")]
    public void Parse_SizeMismatch_ThrowsHistoricalMessage(string address, PlcDataType type, string message)
    {
        var ex = Assert.Throws<PlcValueException>(() => Syntax.Parse(address, type));
        Assert.Equal(message, ex.Message);
    }

    [Fact]
    public void Parse_ValidAddress_ReturnsS7AddressWithCanonicalSpelling()
    {
        var address = Syntax.Parse("db1.dbw2", PlcDataType.Int);

        Assert.IsType<PlcAddress>(address);
        Assert.Equal("DB1.DBW2", address.Canonical);
        Assert.False(address.IsBit);
        Assert.Null(address.ReadOnlyReason);
    }

    [Fact]
    public void InputArea_IsReadOnly_AndWritableTagIsRejectedWithHistoricalMessage()
    {
        Assert.Equal("input area (I) tags cannot be writable", Syntax.Parse("IW0", PlcDataType.Int).ReadOnlyReason);

        var ex = Assert.Throws<InvalidOperationException>(() => new PlcTagCatalog(CatalogOptions("IW0", PlcDataType.Int, writable: true)));
        Assert.Equal("Tag catalog: tag 'T' is invalid: input area (I) tags cannot be writable.", ex.Message);
    }

    [Fact]
    public void WritableTagWithNonS7Address_FailsOverlapCheck()
    {
        var options = new PlcTagCatalogOptions
        {
            Tags =
            [
                new PlcTagDefinition { TagId = "A", Address = "X1", DataType = PlcDataType.Int, Writable = true },
                new PlcTagDefinition { TagId = "B", Address = "X2", DataType = PlcDataType.Int },
            ],
        };

        var ex = Assert.Throws<InvalidOperationException>(() => new PlcTagCatalog(options, new ForeignSyntax()));
        Assert.Equal("Tag catalog: writable tag 'A' has an address the overlap check does not know (X1).", ex.Message);
    }

    [Fact]
    public void LoneWritableTagWithNonS7Address_FailsOverlapCheck()
    {
        var options = new PlcTagCatalogOptions
        {
            Tags = [new PlcTagDefinition { TagId = "A", Address = "X1", DataType = PlcDataType.Int, Writable = true }],
        };

        var ex = Assert.Throws<InvalidOperationException>(() => new PlcTagCatalog(options, new ForeignSyntax()));
        Assert.Equal("Tag catalog: writable tag 'A' has an address the overlap check does not know (X1).", ex.Message);
    }

    [Fact]
    public void WritableS7TagNextToNonS7Tag_FailsOverlapCheck()
    {
        var options = new PlcTagCatalogOptions
        {
            Tags =
            [
                new PlcTagDefinition { TagId = "A", Address = "DB1.DBW0", DataType = PlcDataType.Int, Writable = true },
                new PlcTagDefinition { TagId = "B", Address = "X2", DataType = PlcDataType.Int },
            ],
        };

        var ex = Assert.Throws<InvalidOperationException>(() => new PlcTagCatalog(options, new MixedSyntax()));
        Assert.Equal("Tag catalog: tag 'B' has an address the overlap check does not know (X1).", ex.Message);
    }

    [Fact]
    public async Task S7Driver_DeniesNonS7Address_WithoutIo()
    {
        await using var server = FakeS7Server.Start(FakeS7Mode.HandshakeThenRespond);
        await using var driver = new SiemensS7Driver(
            Options.Create(new SiemensS7Options { Host = "127.0.0.1", Port = server.Port, TimeoutMs = 3000 }));
        var foreign = new ForeignAddress();

        await Assert.ThrowsAsync<PlcAccessDeniedException>(() => driver.ReadAsync(foreign, PlcDataType.Int, CancellationToken.None));
        await Assert.ThrowsAsync<PlcAccessDeniedException>(() => driver.WriteAsync(foreign, PlcDataType.Int, (short)1, CancellationToken.None));

        Assert.Equal(0, server.AcceptCount);
    }

    private static PlcTagCatalogOptions CatalogOptions(string address, PlcDataType type, bool writable) => new()
    {
        Tags = [new PlcTagDefinition { TagId = "T", Address = address, DataType = type, Writable = writable }],
    };

    private sealed record ForeignAddress : IPlcAddress
    {
        public string Canonical => "X1";

        public bool IsBit => false;

        public string? ReadOnlyReason => null;
    }

    private sealed class ForeignSyntax : IPlcAddressSyntax
    {
        public IPlcAddress Parse(string address, PlcDataType type) => new ForeignAddress();
    }

    /// <summary>S7 for DB addresses, foreign for anything else.</summary>
    private sealed class MixedSyntax : IPlcAddressSyntax
    {
        public IPlcAddress Parse(string address, PlcDataType type) =>
            address.StartsWith("DB", StringComparison.Ordinal) ? Syntax.Parse(address, type) : new ForeignAddress();
    }
}
