namespace IndustrialIot.Shared.Common;

/// <summary>
/// Stable machine-readable codes for <c>CommandResult.reason</c>. Edge and Cloud both reference these
/// constants, so a typo becomes a compile error instead of a silently unknown code.
/// </summary>
public static class CommandReasons
{
    public const string UnknownTag = "unknown_tag";
    public const string TagNotWritable = "tag_not_writable";
    public const string InputAreaNotWritable = "input_area_not_writable";
    public const string InvalidValue = "invalid_value";
    public const string ValueOutOfRange = "value_out_of_range";
    public const string EdgeMismatch = "edge_mismatch";
    public const string ClockSkew = "clock_skew";
    public const string PlcUnavailable = "plc_unavailable";
    public const string PlcBusy = "plc_busy";
    public const string PlcAccessDenied = "plc_access_denied";
    public const string WriteOutcomeUnknown = "write_outcome_unknown";

    /// <summary>Every code defined above.</summary>
    public static IReadOnlyList<string> All { get; } =
    [
        UnknownTag,
        TagNotWritable,
        InputAreaNotWritable,
        InvalidValue,
        ValueOutOfRange,
        EdgeMismatch,
        ClockSkew,
        PlcUnavailable,
        PlcBusy,
        PlcAccessDenied,
        WriteOutcomeUnknown,
    ];
}
