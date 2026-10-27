using IndustrialIot.Edge.Drivers;
using IndustrialIot.Edge.Drivers.Models;
using IndustrialIot.Edge.Tests.Fakes;
using Microsoft.Extensions.Options;

namespace IndustrialIot.Edge.Tests;

public class S7AddressSyntaxTests
{
    private static readonly S7AddressSyntax Syntax = new();

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
        Assert.Equal("Tag catalog: writable tag 'A' has a non-S7 address; the overlap check only supports S7 addresses.", ex.Message);
    }

    [Fact]
    public void LoneWritableTagWithNonS7Address_FailsOverlapCheck()
    {
        var options = new PlcTagCatalogOptions
        {
            Tags = [new PlcTagDefinition { TagId = "A", Address = "X1", DataType = PlcDataType.Int, Writable = true }],
        };

        var ex = Assert.Throws<InvalidOperationException>(() => new PlcTagCatalog(options, new ForeignSyntax()));
        Assert.Equal("Tag catalog: writable tag 'A' has a non-S7 address; the overlap check only supports S7 addresses.", ex.Message);
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
        Assert.Equal("Tag catalog: tag 'B' has a non-S7 address; the overlap check only supports S7 addresses.", ex.Message);
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
