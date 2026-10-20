using System.Text.Json;
using IndustrialIot.Shared.Common;

namespace IndustrialIot.Cloud.Commands;

/// <summary>
/// Values of <c>commands.status</c>. Non-final statuses can still take the Edge's outcome; final ones never change.
/// </summary>
public static class CommandStatus
{
    /// <summary>Stored, not yet handed to the broker (or the Cloud stopped before recording the publish).</summary>
    public const string Pending = "pending";

    /// <summary>The broker confirmed the message.</summary>
    public const string Published = "published";

    /// <summary>Publishing was attempted without a confirmation: the command may have reached the Edge.</summary>
    public const string PublishUnknown = "publish_unknown";

    /// <summary>No Edge result by <c>expiresAtUtc + TimeoutGraceSeconds</c>; a late result still replaces it.</summary>
    public const string TimedOut = "timed_out";

    /// <summary>Final: there was no broker connection, so nothing was sent; never published later.</summary>
    public const string NotSent = "not_sent";

    /// <summary>Statuses an Edge result may still replace.</summary>
    public static readonly string[] NonFinal = [Pending, Published, PublishUnknown, TimedOut];

    /// <summary>Statuses the timeout sweeper moves to <see cref="TimedOut"/>.</summary>
    public static readonly string[] AwaitingResult = [Pending, Published, PublishUnknown];

    /// <summary>The final status for an Edge outcome: its wire name (<c>applied</c>, <c>rejected</c>, ...).</summary>
    public static string FromOutcome(CommandOutcome outcome) => JsonNamingPolicy.CamelCase.ConvertName(outcome.ToString());
}
