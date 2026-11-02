namespace Cockpit.Plugin.Schedules.Contracts;

// Without a Profile, an agent prompt runs read-only in the assistant's conversation. A profile keeps delegation,
// its working directory ($VAR and %VAR% expanded), permissions and delivery.
public sealed record TaskAction(TaskActionKind Kind, string Text)
{
    public string? Profile { get; init; }

    public string? WorkingDirectory { get; init; }

    // Null waits as long as the host's own default.
    public TimeSpan? Timeout { get; init; }

    public AgentPermission Permission { get; init; } = AgentPermission.ReadOnly;
}
