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
        PlcDataType.LReal => 8,
        PlcDataType.DTL => 12,
        PlcDataType.String => throw new PlcValueException("String has no fixed size; its size comes from Length."),
        _ => throw new PlcValueException($"Unsupported data type '{type}'."),
    };

    /// <summary>Bytes one S7 item of <paramref name="type"/> occupies: <c>STRING[n]</c> is n + 2 (max and current length).</summary>
    public static int S7ItemSize(PlcDataType type, int? length) => type == PlcDataType.String
        ? (length ?? throw new PlcValueException("String tags need Length.")) + 2
        : Math.Max(SizeOf(type), 1);

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
        PlcDataType.LReal => ReadLReal(value),
        PlcDataType.String => value.ValueKind == JsonValueKind.String ? value.GetString()! : throw WrongKind(type, value),
        PlcDataType.DTL => ReadDtl(value),
        _ => throw new PlcValueException($"Unsupported data type '{type}'."),
    };

    /// <summary>
    /// The bytes an S7 String write sends, starting at byte 1 of the <c>STRING[n]</c>: the current length, then the
    /// characters. Byte 0, the maximum the PLC declared, is never written. Trailing NULs (the catalog pads to Length)
    /// are not part of the text.
    /// </summary>
    public static byte[] S7StringPayload(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var current = text.TrimEnd('\0');
        if (current.Length > byte.MaxValue)
        {
            throw new PlcValueException($"A String of {current.Length} characters does not fit an S7 STRING.");
        }

        return [(byte)current.Length, .. System.Text.Encoding.Latin1.GetBytes(current)];
    }

    /// <summary>
    /// A value as Mitsubishi D-register words, low word first: one word for Int/Word, two for DInt/DWord/Real, four for
    /// LReal, and for a String two characters per word, low byte first (the catalog pads it to Length with NULs).
    /// </summary>
    public static ushort[] ToWords(PlcDataType type, object value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return (type, value) switch
        {
            (PlcDataType.Int, short v) => [unchecked((ushort)v)],
            (PlcDataType.Word, ushort v) => [v],
            (PlcDataType.DInt, int v) => Split(unchecked((uint)v)),
            (PlcDataType.DWord, uint v) => Split(v),
            (PlcDataType.Real, float v) when float.IsFinite(v) => Split(BitConverter.SingleToUInt32Bits(v)),
            (PlcDataType.LReal, double v) when double.IsFinite(v) => Split4(BitConverter.DoubleToUInt64Bits(v)),
            (PlcDataType.String, string v) => StringWords(v),
            _ => throw new PlcValueException($"Value of CLR type {value.GetType().Name} cannot be written as {type} to D registers."),
        };

        static ushort[] Split(uint v) => [(ushort)v, (ushort)(v >> 16)];

        static ushort[] Split4(ulong v) => [(ushort)v, (ushort)(v >> 16), (ushort)(v >> 32), (ushort)(v >> 48)];

        static ushort[] StringWords(string text)
        {
            var bytes = System.Text.Encoding.Latin1.GetBytes(text);
            var words = new ushort[(bytes.Length + 1) / 2];
            for (var i = 0; i < bytes.Length; i++)
            {
                words[i / 2] |= (ushort)(bytes[i] << (8 * (i % 2)));
            }

            return words;
        }
    }

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
            case (PlcDataType.LReal, double v) when double.IsFinite(v):
                BinaryPrimitives.WriteDoubleBigEndian(bytes, v);
                break;
            case (PlcDataType.DTL, DateTime v):
                EncodeDtl(v, bytes);
                break;
            default:
                throw new PlcValueException($"Value of CLR type {value.GetType().Name} cannot be written as {type}.");
        }

        return bytes;
    }

    /// <summary>
    /// Decodes big-endian S7 bytes into the CLR value mapped to <paramref name="type"/>; <paramref name="length"/> is a
    /// String tag's Length. A value the PLC holds but that cannot be valid (a STRING declared with another length, a DTL
    /// with an impossible field) throws <see cref="PlcValueException"/>, which marks only that tag bad.
    /// </summary>
    public static object FromBytes(PlcDataType type, ReadOnlySpan<byte> bytes, int? length = null)
    {
        var expected = S7ItemSize(type, length);
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
            PlcDataType.LReal => BinaryPrimitives.ReadDoubleBigEndian(bytes),
            PlcDataType.DTL => DecodeDtl(bytes),
            PlcDataType.String => DecodeS7String(bytes, length!.Value),
            _ => throw new PlcValueException($"Unsupported data type '{type}'."),
        };
    }

    /// <summary>
    /// <c>STRING[n]</c>: byte 0 is the declared maximum, byte 1 the current length, then the characters (one byte each).
    /// A maximum other than the tag's Length means the tag points at another variable.
    /// </summary>
    private static string DecodeS7String(ReadOnlySpan<byte> bytes, int length)
    {
        if (bytes[0] != length)
        {
            throw new PlcValueException($"STRING length mismatch: the PLC holds STRING[{bytes[0]}], the tag Length is {length}.");
        }

        if (bytes[1] > length)
        {
            throw new PlcValueException($"STRING current length {bytes[1]} exceeds its maximum {length}.");
        }

        return System.Text.Encoding.Latin1.GetString(bytes.Slice(2, bytes[1]));
    }

    /// <summary>
    /// DTL: year (2 bytes), month, day, weekday, hour, minute, second, nanoseconds (4 bytes), as the PLC's local time
    /// without an offset. The weekday is not checked: it is derived from the date and not part of the text.
    /// </summary>
    private static readonly DateTime LastDtl = new(2262, 4, 11, 23, 47, 16, 854, DateTimeKind.Unspecified);

    private static string DecodeDtl(ReadOnlySpan<byte> bytes)
    {
        int year = BinaryPrimitives.ReadUInt16BigEndian(bytes);
        int month = bytes[2], day = bytes[3], hour = bytes[5], minute = bytes[6], second = bytes[7];
        var nanoseconds = BinaryPrimitives.ReadUInt32BigEndian(bytes[8..]);

        // DTL covers 1970-01-01 00:00:00 to 2262-04-11 23:47:16.854775807 (TIA Portal help).
        var valid = year is >= 1970 and <= 2262
            && month is >= 1 and <= 12
            && day >= 1 && day <= DateTime.DaysInMonth(year, month)
            && hour < 24 && minute < 60 && second < 60
            && nanoseconds < 1_000_000_000;
        if (!valid)
        {
            throw new PlcValueException("DTL holds an impossible date or time.");
        }

        var value = new DateTime(year, month, day, hour, minute, second, (int)(nanoseconds / 1_000_000), DateTimeKind.Unspecified);
        if (value > LastDtl)
        {
            throw new PlcValueException("DTL holds an impossible date or time.");
        }

        return value.ToString("yyyy-MM-dd'T'HH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture);
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

    private static double ReadLReal(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Number)
        {
            throw WrongKind(PlcDataType.LReal, value);
        }

        return value.TryGetDouble(out var number) && double.IsFinite(number)
            ? number
            : throw new PlcValueException("LReal must be a finite number.");
    }

    /// <summary>
    /// The DTL text a command carries, exactly as the Edge publishes it (<c>yyyy-MM-ddTHH:mm:ss.fff</c>, the PLC's local
    /// time without an offset), within the DTL range.
    /// </summary>
    private static DateTime ReadDtl(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.String
            || !DateTime.TryParseExact(value.GetString(), "yyyy-MM-dd'T'HH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var time))
        {
            throw new PlcValueException("DTL must be text like 2026-11-02T10:15:30.123.");
        }

        return time.Year >= 1970 && time <= LastDtl
            ? time
            : throw new PlcValueException("DTL must be within 1970-01-01T00:00:00.000 and 2262-04-11T23:47:16.854.");
    }

    /// <summary>DTL bytes: year, month, day, weekday (1 = Sunday), hour, minute, second, nanoseconds.</summary>
    private static void EncodeDtl(DateTime time, Span<byte> bytes)
    {
        BinaryPrimitives.WriteUInt16BigEndian(bytes, (ushort)time.Year);
        bytes[2] = (byte)time.Month;
        bytes[3] = (byte)time.Day;
        bytes[4] = (byte)((int)time.DayOfWeek + 1);
        bytes[5] = (byte)time.Hour;
        bytes[6] = (byte)time.Minute;
        bytes[7] = (byte)time.Second;
        BinaryPrimitives.WriteUInt32BigEndian(bytes[8..], (uint)time.Millisecond * 1_000_000);
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
