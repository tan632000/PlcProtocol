using System.Text.Json;
using IndustrialIot.Edge.Drivers;
using IndustrialIot.Edge.Drivers.Models;

namespace IndustrialIot.Edge.Tests;

public class PlcValueConverterTests
{
    private static readonly string[] AddressesBySize = ["DB1.DBX0.0", "DB1.DBB0", "DB1.DBW0", "DB1.DBD0"];

    private static readonly Dictionary<PlcDataType, string> MatchingAddress = new()
    {
        [PlcDataType.Bool] = "DB1.DBX0.0",
        [PlcDataType.Byte] = "DB1.DBB0",
        [PlcDataType.Word] = "DB1.DBW0",
        [PlcDataType.Int] = "DB1.DBW0",
        [PlcDataType.DWord] = "DB1.DBD0",
        [PlcDataType.DInt] = "DB1.DBD0",
        [PlcDataType.Real] = "DB1.DBD0",
    };

    public static TheoryData<string, PlcDataType> MismatchedPairs()
    {
        var data = new TheoryData<string, PlcDataType>();
        foreach (var type in Enum.GetValues<PlcDataType>())
        {
            foreach (var address in AddressesBySize.Where(a => a != MatchingAddress[type]))
            {
                data.Add(address, type);
            }
        }

        return data;
    }

    public static TheoryData<string, PlcDataType> MatchingPairs()
    {
        var data = new TheoryData<string, PlcDataType>();
        foreach (var (type, address) in MatchingAddress)
        {
            data.Add(address, type);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(MismatchedPairs))]
    [InlineData("DB1.DBD0", (PlcDataType)0)]
    [InlineData("DB1.DBD0", (PlcDataType)99)]
    public void EnsureCompatible_SizeMismatch_Throws(string address, PlcDataType type)
    {
        Assert.Throws<PlcValueException>(() => PlcValueConverter.EnsureCompatible(PlcAddressParser.Parse(address), type));
    }

    [Theory]
    [MemberData(nameof(MatchingPairs))]
    public void EnsureCompatible_MatchingSize_Accepts(string address, PlcDataType type)
    {
        PlcValueConverter.EnsureCompatible(PlcAddressParser.Parse(address), type);
    }

    [Theory]
    [InlineData(PlcDataType.Byte, "-1")]
    [InlineData(PlcDataType.Byte, "256")]
    [InlineData(PlcDataType.Word, "-1")]
    [InlineData(PlcDataType.Word, "65536")]
    [InlineData(PlcDataType.Int, "-32769")]
    [InlineData(PlcDataType.Int, "32768")]
    [InlineData(PlcDataType.DWord, "-1")]
    [InlineData(PlcDataType.DWord, "4294967296")]
    [InlineData(PlcDataType.DInt, "-2147483649")]
    [InlineData(PlcDataType.DInt, "2147483648")]
    [InlineData(PlcDataType.DInt, "18446744073709551616")]
    [InlineData(PlcDataType.Real, "3.5e38")]
    [InlineData(PlcDataType.Real, "-3.5e38")]
    [InlineData(PlcDataType.Real, "1e400")]
    public void Normalize_OutOfRange_Throws(PlcDataType type, string json)
    {
        Assert.Throws<PlcValueException>(() => PlcValueConverter.Normalize(type, Json(json)));
    }

    [Theory]
    [InlineData(PlcDataType.Byte, "0", (byte)0)]
    [InlineData(PlcDataType.Byte, "255", (byte)255)]
    [InlineData(PlcDataType.Word, "0", (ushort)0)]
    [InlineData(PlcDataType.Word, "65535", (ushort)65535)]
    [InlineData(PlcDataType.Int, "-32768", (short)-32768)]
    [InlineData(PlcDataType.Int, "32767", (short)32767)]
    [InlineData(PlcDataType.DWord, "0", 0u)]
    [InlineData(PlcDataType.DWord, "4294967295", 4294967295u)]
    [InlineData(PlcDataType.DInt, "-2147483648", int.MinValue)]
    [InlineData(PlcDataType.DInt, "2147483647", int.MaxValue)]
    [InlineData(PlcDataType.Real, "3.4028235e38", float.MaxValue)]
    [InlineData(PlcDataType.Real, "-1.5", -1.5f)]
    [InlineData(PlcDataType.Bool, "true", true)]
    [InlineData(PlcDataType.Bool, "false", false)]
    public void Normalize_RangeEdges_ReturnExactClrType(PlcDataType type, string json, object expected)
    {
        var value = PlcValueConverter.Normalize(type, Json(json));

        Assert.IsType(expected.GetType(), value);
        Assert.Equal(expected, value);
    }

    [Theory]
    [InlineData(PlcDataType.Int, "1", true)]
    [InlineData(PlcDataType.Int, "-0", true)]
    [InlineData(PlcDataType.Int, "1.0", false)]
    [InlineData(PlcDataType.Int, "1e2", false)]
    [InlineData(PlcDataType.Int, "1E2", false)]
    [InlineData(PlcDataType.Int, "1.5", false)]
    [InlineData(PlcDataType.Byte, "1.0", false)]
    [InlineData(PlcDataType.Word, "1e0", false)]
    [InlineData(PlcDataType.DWord, "2.0", false)]
    [InlineData(PlcDataType.DInt, "1.0", false)]
    [InlineData(PlcDataType.Real, "1", true)]
    [InlineData(PlcDataType.Real, "1.0", true)]
    [InlineData(PlcDataType.Real, "1e2", true)]
    public void Normalize_IntegerToken_Rule(PlcDataType type, string json, bool accepted)
    {
        if (accepted)
        {
            PlcValueConverter.Normalize(type, Json(json));
        }
        else
        {
            Assert.Throws<PlcValueException>(() => PlcValueConverter.Normalize(type, Json(json)));
        }
    }

    [Theory]
    [InlineData(PlcDataType.Int, "\"12\"")]
    [InlineData(PlcDataType.Int, "null")]
    [InlineData(PlcDataType.Int, "true")]
    [InlineData(PlcDataType.Real, "\"1.5\"")]
    [InlineData(PlcDataType.Real, "[1]")]
    [InlineData(PlcDataType.Bool, "1")]
    [InlineData(PlcDataType.Bool, "\"true\"")]
    [InlineData(PlcDataType.Bool, "{}")]
    public void Normalize_WrongKind_Throws(PlcDataType type, string json)
    {
        Assert.Throws<PlcValueException>(() => PlcValueConverter.Normalize(type, Json(json)));
    }

    [Fact]
    public void Normalize_UndefinedElement_Throws()
    {
        Assert.Throws<PlcValueException>(() => PlcValueConverter.Normalize(PlcDataType.Int, default));
    }

    [Theory]
    [InlineData(PlcDataType.Int, (short)-2, "FFFE")]
    [InlineData(PlcDataType.Real, 1.5f, "3FC00000")]
    [InlineData(PlcDataType.Word, (ushort)0x1234, "1234")]
    [InlineData(PlcDataType.DWord, 0x01020304u, "01020304")]
    [InlineData(PlcDataType.DInt, -2, "FFFFFFFE")]
    [InlineData(PlcDataType.Byte, (byte)0xAB, "AB")]
    [InlineData(PlcDataType.Bool, true, "01")]
    [InlineData(PlcDataType.Bool, false, "00")]
    public void ToBytes_BigEndianExamples(PlcDataType type, object value, string expectedHex)
    {
        var bytes = PlcValueConverter.ToBytes(type, value);

        Assert.Equal(expectedHex, Convert.ToHexString(bytes));
        Assert.Equal(value, PlcValueConverter.FromBytes(type, bytes));
    }

    [Theory]
    [InlineData(PlcDataType.Int, 1)]          // int instead of short
    [InlineData(PlcDataType.Word, (short)1)]  // signed instead of unsigned
    [InlineData(PlcDataType.Real, 1.5d)]      // double instead of float
    [InlineData(PlcDataType.Real, float.NaN)]
    [InlineData(PlcDataType.Real, float.PositiveInfinity)]
    [InlineData(PlcDataType.Bool, 1)]
    public void ToBytes_WrongClrTypeOrNonFinite_Throws(PlcDataType type, object value)
    {
        Assert.Throws<PlcValueException>(() => PlcValueConverter.ToBytes(type, value));
    }

    [Theory]
    [InlineData(PlcDataType.Int, "FF")]
    [InlineData(PlcDataType.Real, "3FC000")]
    [InlineData(PlcDataType.Byte, "")]
    public void FromBytes_WrongLength_Throws(PlcDataType type, string hex)
    {
        Assert.Throws<PlcValueException>(() => PlcValueConverter.FromBytes(type, Convert.FromHexString(hex)));
    }

    private static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
