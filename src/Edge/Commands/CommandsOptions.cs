namespace IndustrialIot.Edge.Commands;

/// <summary>
/// Remote writes, bound from configuration section <c>Edge:Commands</c>. Off by default: the Edge is a read-only data
/// source unless a deployment opts in. A write also needs the Cloud switch, the broker's command ACL and a
/// <c>Writable</c> tag, so this is one of four independent locks.
/// </summary>
public sealed class CommandsOptions
{
    public const string SectionName = "Edge:Commands";

    /// <summary>Read at start; changing it needs a restart.</summary>
    public bool Enabled { get; set; }
}
