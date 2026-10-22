namespace Cockpit.Plugin.Schedules.Contracts;

// What a task does when it fires. A notification delivers `Text` as it is; an agent prompt hands `Text` to `Profile`
// as its prompt, in `WorkingDirectory` ($VAR and %VAR% expanded), and delivers the answer.
public sealed record TaskAction(TaskActionKind Kind, string Text)
{
    public string? Profile { get; init; }

    public string? WorkingDirectory { get; init; }

    // Null waits as long as the host's own default.
    public TimeSpan? Timeout { get; init; }

    public AgentPermission Permission { get; init; } = AgentPermission.ReadOnly;
}
