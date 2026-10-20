namespace IndustrialIot.Cloud.Messaging;

/// <summary>MQTT topic filter matching for the filters this Cloud uses (<c>+</c> = exactly one level).</summary>
public static class MqttTopicFilter
{
    public static bool Matches(string filter, string topic)
    {
        var filterLevels = filter.Split('/');
        var topicLevels = topic.Split('/');
        if (filterLevels.Length != topicLevels.Length)
        {
            return false;
        }

        for (var i = 0; i < filterLevels.Length; i++)
        {
            var level = filterLevels[i];
            if (level == "+")
            {
                if (topicLevels[i].Length == 0)
                {
                    return false;
                }

                continue;
            }

            if (!string.Equals(level, topicLevels[i], StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The edge id of <c>iiot/{edgeId}/{leaf}</c>, or null when the topic has another shape.</summary>
    public static string? EdgeIdOf(string topic, string leaf)
    {
        var levels = topic.Split('/');
        return levels.Length == 3 && levels[0] == "iiot" && levels[2] == leaf && levels[1].Length > 0 ? levels[1] : null;
    }
}
