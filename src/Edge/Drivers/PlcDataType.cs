namespace IndustrialIot.Edge.Drivers;

/// <summary>
/// S7 elementary types the driver can read and write.
/// Values start at 1 so a missing <c>DataType</c> in configuration (default 0) is rejected instead of
/// silently becoming <see cref="Bool"/>.
/// </summary>
public enum PlcDataType
{
    /// <summary>1 bit, CLR <see cref="bool"/>.</summary>
    Bool = 1,

    /// <summary>1 byte unsigned, CLR <see cref="byte"/>.</summary>
    Byte,

    /// <summary>2 bytes unsigned, CLR <see cref="ushort"/>.</summary>
    Word,

    /// <summary>2 bytes signed, CLR <see cref="short"/>.</summary>
    Int,

    /// <summary>4 bytes unsigned, CLR <see cref="uint"/>.</summary>
    DWord,

    /// <summary>4 bytes signed, CLR <see cref="int"/>.</summary>
    DInt,

    /// <summary>4 bytes IEEE754 single, CLR <see cref="float"/>.</summary>
    Real,
}
