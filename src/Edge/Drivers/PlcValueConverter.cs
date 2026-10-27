using System.Buffers.Binary;
using System.Text.Json;
using IndustrialIot.Edge.Drivers.Models;

namespace IndustrialIot.Edge.Drivers;

/// <summary>
/// Converts between JSON command values, CLR values, and S7 bytes. S7 stores multi-byte values
/// big-endian, while x86/ARM .NET is little-endian, so every conversion goes through
/// <see cref="BinaryPrimitives"/> with an explicit byte order.
/// </summary>
public static class PlcValueConverter
{
    /// <summary>Bytes the type occupies; 0 for <see cref="PlcDataType.Bool"/> (a single bit).</summary>
    public static int SizeOf(PlcDataType type) => type switch
    {
        PlcDataType.Bool => 0,
        PlcDataType.Byte => 1,
        PlcDataType.Word or PlcDataType.Int => 2,
        PlcDataType.DWord or PlcDataType.DInt or PlcDataType.Real => 4,
        _ => throw new PlcValueException($"Unsupported data type '{type}'."),
    };

    /// <summary>Throws <see cref="PlcValueException"/> when the address size cannot hold the type.</summary>
    public static void EnsureCompatible(PlcAddress address, PlcDataType type) =>
        S7AddressSyntax.EnsureCompatible(address, type);

    /// <summary>
    /// Turns a JSON command value into the CLR value for <paramref name="type"/>, rejecting the wrong
    /// JSON kind, non-integer tokens for integer types, and values outside the type's range.
    /// </summary>
    public static object Normalize(PlcDataType type, JsonElement value) => type switch
    {
        PlcDataType.Bool => value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw WrongKind(type, value),
        },
        PlcDataType.Byte => (byte)ReadInteger(type, value, byte.MinValue, byte.MaxValue),
        PlcDataType.Word => (ushort)ReadInteger(type, value, ushort.MinValue, ushort.MaxValue),
        PlcDataType.Int => (short)ReadInteger(type, value, short.MinValue, short.MaxValue),
        PlcDataType.DWord => (uint)ReadInteger(type, value, uint.MinValue, uint.MaxValue),
        PlcDataType.DInt => (int)ReadInteger(type, value, int.MinValue, int.MaxValue),
        PlcDataType.Real => ReadReal(value),
        _ => throw new PlcValueException($"Unsupported data type '{type}'."),
    };

    /// <summary>Encodes a CLR value (exact type from <see cref="Normalize"/>) as big-endian S7 bytes.</summary>
    public static byte[] ToBytes(PlcDataType type, object value)
    {
        ArgumentNullException.ThrowIfNull(value);

        var bytes = new byte[Math.Max(SizeOf(type), 1)];
        switch (type, value)
        {
            case (PlcDataType.Bool, bool v):
                bytes[0] = v ? (byte)1 : (byte)0;
                break;
            case (PlcDataType.Byte, byte v):
                bytes[0] = v;
                break;
            case (PlcDataType.Word, ushort v):
                BinaryPrimitives.WriteUInt16BigEndian(bytes, v);
                break;
            case (PlcDataType.Int, short v):
                BinaryPrimitives.WriteInt16BigEndian(bytes, v);
                break;
            case (PlcDataType.DWord, uint v):
                BinaryPrimitives.WriteUInt32BigEndian(bytes, v);
                break;
            case (PlcDataType.DInt, int v):
                BinaryPrimitives.WriteInt32BigEndian(bytes, v);
                break;
            case (PlcDataType.Real, float v) when float.IsFinite(v):
                BinaryPrimitives.WriteSingleBigEndian(bytes, v);
                break;
            default:
                throw new PlcValueException($"Value of CLR type {value.GetType().Name} cannot be written as {type}.");
        }

        return bytes;
    }

    /// <summary>Decodes big-endian S7 bytes into the CLR value mapped to <paramref name="type"/>.</summary>
    public static object FromBytes(PlcDataType type, ReadOnlySpan<byte> bytes)
    {
        var expected = Math.Max(SizeOf(type), 1);
        if (bytes.Length != expected)
        {
            throw new PlcValueException($"{type} needs {expected} byte(s) but {bytes.Length} were given.");
        }

        return type switch
        {
            PlcDataType.Bool => bytes[0] != 0,
            PlcDataType.Byte => bytes[0],
            PlcDataType.Word => BinaryPrimitives.ReadUInt16BigEndian(bytes),
            PlcDataType.Int => BinaryPrimitives.ReadInt16BigEndian(bytes),
            PlcDataType.DWord => BinaryPrimitives.ReadUInt32BigEndian(bytes),
            PlcDataType.DInt => BinaryPrimitives.ReadInt32BigEndian(bytes),
            PlcDataType.Real => BinaryPrimitives.ReadSingleBigEndian(bytes),
            _ => throw new PlcValueException($"Unsupported data type '{type}'."),
        };
    }

    /// <summary>Inclusive range of an integer type; <c>null</c> for Bool and Real.</summary>
    internal static (decimal Min, decimal Max)? IntegerRange(PlcDataType type) => type switch
    {
        PlcDataType.Byte => (byte.MinValue, byte.MaxValue),
        PlcDataType.Word => (ushort.MinValue, ushort.MaxValue),
        PlcDataType.Int => (short.MinValue, short.MaxValue),
        PlcDataType.DWord => (uint.MinValue, uint.MaxValue),
        PlcDataType.DInt => (int.MinValue, int.MaxValue),
        _ => null,
    };

    private static long ReadInteger(PlcDataType type, JsonElement value, long min, long max)
    {
        if (value.ValueKind != JsonValueKind.Number)
        {
            throw WrongKind(type, value);
        }

        // "Integer token": 1.0 or 1e2 would be integral after parsing, but the sender wrote a
        // floating-point value, which usually means a mapping bug upstream; reject rather than guess.
        var raw = value.GetRawText();
        if (raw.AsSpan().IndexOfAny('.', 'e', 'E') >= 0)
        {
            throw new PlcValueException($"{type} needs an integer token, got '{raw}'.");
        }

        // TryGetInt64 fails for integers beyond Int64, which are out of range for every S7 type anyway.
        if (!value.TryGetInt64(out var number) || number < min || number > max)
        {
            throw new PlcValueException($"{raw} is outside the {type} range {min}..{max}.");
        }

        return number;
    }

    private static float ReadReal(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Number)
        {
            throw WrongKind(PlcDataType.Real, value);
        }

        // A finite double above float.MaxValue becomes Infinity when narrowed, so check after the cast.
        if (!value.TryGetDouble(out var number) || !float.IsFinite((float)number))
        {
            throw new PlcValueException($"{value.GetRawText()} is outside the finite Real range.");
        }

        return (float)number;
    }

    private static PlcValueException WrongKind(PlcDataType type, JsonElement value) =>
        new($"{type} cannot be set from a JSON {value.ValueKind} value.");
}
