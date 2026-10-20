using Microsoft.Extensions.Options;

namespace IndustrialIot.Edge.Messaging;

/// <summary>
/// The Edge's three topics. <c>edgeId</c> is validated to <c>[A-Za-z0-9_-]</c>, so it can never inject
/// MQTT wildcards (<c>+</c>, <c>#</c>) or extra levels (<c>/</c>).
/// </summary>
public sealed class MqttTopics(IOptions<EdgeOptions> edge)
{
    public string Telemetry { get; } = $"iiot/{edge.Value.EdgeId}/telemetry";

    public string Commands { get; } = $"iiot/{edge.Value.EdgeId}/commands";

    public string CommandResults { get; } = $"iiot/{edge.Value.EdgeId}/command-results";
}
