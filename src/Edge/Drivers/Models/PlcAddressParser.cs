using System.Diagnostics.CodeAnalysis;

namespace IndustrialIot.Edge.Drivers.Models;

/// <summary>
/// Parses S7 address strings such as <c>DB1.DBW2</c>, <c>DB1.DBX0.3</c>, <c>M10.0</c>, <c>QW4</c>.
/// </summary>
/// <remarks>
/// Hand-written instead of a regex on purpose: .NET <c>\d</c> also matches non-ASCII digits
/// (e.g. Arabic-Indic), and <c>int.Parse</c> throws <see cref="OverflowException"/> on huge numbers.
/// Here only ASCII <c>0-9</c> is accepted and ranges are checked while reading each digit.
/// </remarks>
public static class PlcAddressParser
{
    private const int MaxInputLength = 64;
    private const int MaxRawInputLength = 1024;
    private const int MaxDbNumber = 65535;
    private const int MaxByteOffset = 65535;
    private const int MaxBitOffset = 7;

    public static PlcAddress Parse(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            throw new PlcAddressException(input, "address is empty");
        }

        // Coarse cap on the raw input first, so a huge string is never trimmed or echoed back in full.
        if (input.Length > MaxRawInputLength)
        {
            throw new PlcAddressException(input[..MaxInputLength] + "...", $"address is longer than {MaxInputLength} characters");
        }

        var trimmed = input.Trim();
        if (trimmed.Length > MaxInputLength)
        {
            throw new PlcAddressException(input, $"address is longer than {MaxInputLength} characters");
        }

        var cursor = new Cursor(input, trimmed.ToUpperInvariant());
        var address = cursor.Peek("DB") ? ParseDataBlock(ref cursor) : ParseOtherArea(ref cursor);

        if (!cursor.AtEnd)
        {
            throw cursor.Fail("unexpected trailing characters");
        }

        return address;
    }

    public static bool TryParse(string? input, [NotNullWhen(true)] out PlcAddress? address)
    {
        try
        {
            address = Parse(input);
            return true;
        }
        catch (PlcAddressException)
        {
            address = null;
            return false;
        }
    }

    // DB{n}.DBX{b}.{x} | DB{n}.DBB{b} | DB{n}.DBW{b} | DB{n}.DBD{b}
    private static PlcAddress ParseDataBlock(ref Cursor cursor)
    {
        cursor.Expect("DB");
        var dbNumber = cursor.ReadNumber(min: 1, max: MaxDbNumber, "DB number");
        cursor.Expect(".DB");

        var sizeLetter = cursor.ReadChar("size letter X, B, W or D");
        if (sizeLetter == 'X')
        {
            var byteOffset = cursor.ReadNumber(min: 0, max: MaxByteOffset, "byte offset");
            cursor.Expect(".");
            var bitOffset = cursor.ReadNumber(min: 0, max: MaxBitOffset, "bit offset");
            return new PlcAddress(PlcArea.DataBlock, dbNumber, byteOffset, bitOffset, sizeBytes: 0);
        }

        var size = SizeFromLetter(sizeLetter) ?? throw cursor.Fail("size letter must be X, B, W or D");
        var offset = cursor.ReadNumber(min: 0, max: MaxByteOffset, "byte offset");
        return new PlcAddress(PlcArea.DataBlock, dbNumber, offset, bitOffset: null, size);
    }

    // {M|I|Q}{b}.{x} | {M|I|Q}{B|W|D}{b}
    private static PlcAddress ParseOtherArea(ref Cursor cursor)
    {
        var areaLetter = cursor.ReadChar("area letter M, I or Q");
        var area = areaLetter switch
        {
            'M' => PlcArea.Memory,
            'I' => PlcArea.Input,
            'Q' => PlcArea.Output,
            _ => throw cursor.Fail("area must be DB, M, I or Q"),
        };

        if (cursor.CurrentIsAsciiDigit)
        {
            var byteOffset = cursor.ReadNumber(min: 0, max: MaxByteOffset, "byte offset");
            cursor.Expect(".");
            var bitOffset = cursor.ReadNumber(min: 0, max: MaxBitOffset, "bit offset");
            return new PlcAddress(area, dbNumber: 0, byteOffset, bitOffset, sizeBytes: 0);
        }

        var sizeLetter = cursor.ReadChar("size letter B, W or D, or a bit address like M10.0");
        var size = SizeFromLetter(sizeLetter) ?? throw cursor.Fail("size letter must be B, W or D");
        var offset = cursor.ReadNumber(min: 0, max: MaxByteOffset, "byte offset");
        return new PlcAddress(area, dbNumber: 0, offset, bitOffset: null, size);
    }

    private static int? SizeFromLetter(char letter) => letter switch
    {
        'B' => 1,
        'W' => 2,
        'D' => 4,
        _ => null,
    };

    /// <summary>Forward-only reader over the normalized text; keeps the original input for error messages.</summary>
    private ref struct Cursor
    {
        private readonly string _original;
        private readonly string _text;
        private int _position;

        public Cursor(string original, string normalized)
        {
            _original = original;
            _text = normalized;
            _position = 0;
        }

        public readonly bool AtEnd => _position >= _text.Length;

        public readonly bool CurrentIsAsciiDigit => !AtEnd && IsAsciiDigit(_text[_position]);

        public readonly bool Peek(string expected) =>
            _text.AsSpan(_position).StartsWith(expected, StringComparison.Ordinal);

        public void Expect(string expected)
        {
            if (!Peek(expected))
            {
                throw Fail($"expected '{expected}' at position {_position}");
            }

            _position += expected.Length;
        }

        public char ReadChar(string what)
        {
            if (AtEnd)
            {
                throw Fail($"missing {what}");
            }

            return _text[_position++];
        }

        public int ReadNumber(int min, int max, string what)
        {
            var start = _position;
            long value = 0;

            while (CurrentIsAsciiDigit)
            {
                value = (value * 10) + (_text[_position] - '0');
                _position++;

                // Stop as soon as the range is exceeded, so very long digit runs can never overflow.
                if (value > max)
                {
                    throw Fail($"{what} must be between {min} and {max}");
                }
            }

            if (_position == start)
            {
                throw Fail($"missing {what} (ASCII digits 0-9)");
            }

            if (value < min)
            {
                throw Fail($"{what} must be between {min} and {max}");
            }

            return (int)value;
        }

        public readonly PlcAddressException Fail(string reason) => new(_original, reason);

        private static bool IsAsciiDigit(char c) => c is >= '0' and <= '9';
    }
}
