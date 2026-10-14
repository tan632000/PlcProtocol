namespace IndustrialIot.Shared.Common;

/// <summary>Quality of a telemetry sample. Serialized as camelCase strings (<c>good</c>, <c>bad</c>, <c>uncertain</c>).</summary>
public enum TelemetryQuality
{
    Good,
    Bad,
    Uncertain,
}
