using IndustrialIot.Edge.Drivers;
using IndustrialIot.Edge.Drivers.Models;

namespace IndustrialIot.Edge.Tests;

public class PlcAddressParserTests
{
    [Theory]
    // input,            area,               db,    byte,  bit,  size
    [InlineData("DB1.DBX0.3", PlcArea.DataBlock, 1, 0, 3, 0)]
    [InlineData("DB10.DBB5", PlcArea.DataBlock, 10, 5, null, 1)]
    [InlineData("DB10.DBW2", PlcArea.DataBlock, 10, 2, null, 2)]
    [InlineData("DB65535.DBD65535", PlcArea.DataBlock, 65535, 65535, null, 4)]
    [InlineData("M10.0", PlcArea.Memory, 0, 10, 0, 0)]
    [InlineData("MB0", PlcArea.Memory, 0, 0, null, 1)]
    [InlineData("MW20", PlcArea.Memory, 0, 20, null, 2)]
    [InlineData("MD4", PlcArea.Memory, 0, 4, null, 4)]
    [InlineData("I0.0", PlcArea.Input, 0, 0, 0, 0)]
    [InlineData("IB1", PlcArea.Input, 0, 1, null, 1)]
    [InlineData("IW2", PlcArea.Input, 0, 2, null, 2)]
    [InlineData("ID4", PlcArea.Input, 0, 4, null, 4)]
    [InlineData("Q0.1", PlcArea.Output, 0, 0, 1, 0)]
    [InlineData("QB0", PlcArea.Output, 0, 0, null, 1)]
    [InlineData("QW2", PlcArea.Output, 0, 2, null, 2)]
    [InlineData("QD8", PlcArea.Output, 0, 8, null, 4)]
    public void Parse_ValidForms_ReturnsExpectedFields(
        string input, PlcArea area, int dbNumber, int byteOffset, int? bitOffset, int sizeBytes)
    {
        var address = PlcAddressParser.Parse(input);

        Assert.Equal(area, address.Area);
        Assert.Equal(dbNumber, address.DbNumber);
        Assert.Equal(byteOffset, address.ByteOffset);
        Assert.Equal(bitOffset, address.BitOffset);
        Assert.Equal(sizeBytes, address.SizeBytes);
        Assert.Equal(bitOffset.HasValue, address.IsBit);
    }

    [Theory]
    [InlineData(" db01.dbw02 ", "DB1.DBW2")]
    [InlineData("Db1.dBx0.0", "DB1.DBX0.0")]
    [InlineData("m010.07", "M10.7")]
    [InlineData("qw0", "QW0")]
    [InlineData("DB0007.DBD0010", "DB7.DBD10")]
    public void Canonical_NormalizesCaseAndZeros(string input, string expectedCanonical)
    {
        var address = PlcAddressParser.Parse(input);

        Assert.Equal(expectedCanonical, address.Canonical);
        Assert.Equal(expectedCanonical, address.ToString());

        // Canonical text must parse back to an equal address (round-trip).
        Assert.Equal(address, PlcAddressParser.Parse(address.Canonical));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("DB0.DBW0")]            // DB number starts at 1
    [InlineData("DB1.DBX0")]            // bit address without bit
    [InlineData("M10")]                 // missing size letter or bit
    [InlineData("M0.8")]                // bit > 7
    [InlineData("DB1.DBW-1")]           // sign not allowed
    [InlineData("DB1.DBW2x")]           // trailing characters
    [InlineData("DB70000.DBW0")]        // DB number > 65535
    [InlineData("DB٣.DBW0")]       // Arabic-Indic digit three
    [InlineData("DB99999999999.DBW0")]  // would overflow Int32
    [InlineData("DB1.DBW65536")]        // byte offset > 65535
    [InlineData("DB1.DBX0.0.0")]
    [InlineData("DB1")]
    [InlineData("DB1.")]
    [InlineData("DB1.DBQ0")]            // unknown size letter
    [InlineData("DB1.DBW 2")]           // inner whitespace
    [InlineData("MW")]
    [InlineData("X0.0")]                // unknown area
    [InlineData("E0.0")]                // German mnemonic not supported
    [InlineData("PIW0")]                // peripheral area not supported
    [InlineData("+M0.0")]
    public void Parse_InvalidForms_Throws(string input)
    {
        var exception = Assert.Throws<PlcAddressException>(() => PlcAddressParser.Parse(input));

        Assert.Contains(input, exception.Message);
        Assert.Equal(input, exception.Input);
        Assert.IsAssignableFrom<PlcException>(exception);
        Assert.False(PlcAddressParser.TryParse(input, out var address));
        Assert.Null(address);
    }

    [Fact]
    public void Parse_Null_Throws()
    {
        var exception = Assert.Throws<PlcAddressException>(() => PlcAddressParser.Parse(null));

        Assert.Contains("<null>", exception.Message);
    }

    [Fact]
    public void Parse_ValidAddressWithLongPadding_IsTrimmedFirst()
    {
        var padding = new string(' ', 60);

        var address = PlcAddressParser.Parse(padding + "DB1.DBW2" + padding);

        Assert.Equal("DB1.DBW2", address.Canonical);
    }

    [Fact]
    public void Parse_OverlongInput_Throws()
    {
        var input = "DB1.DBW" + new string('0', 100);

        Assert.Throws<PlcAddressException>(() => PlcAddressParser.Parse(input));
    }
}
