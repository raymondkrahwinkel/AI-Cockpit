namespace Cockpit.Plugins.Abstractions.Sessions;

/// <summary>
/// One piece of work that outlived the turn that started it — see <see cref="PluginBackgroundTasksChanged"/>.
/// </summary>
/// <param name="TaskId">
/// The provider's own id for the task, unique within the session.
/// </param>
/// <param name="Kind">
/// Whether this is a sub-agent or a shell, which the host weighs differently.
/// </param>
/// <param name="Description">
/// A short human-readable label (the command, or the agent's task), for display.
/// </param>
public sealed record PluginBackgroundTask(string TaskId, PluginBackgroundTaskKind Kind, string? Description)
{
    /// <summary>The stable provider agent id, when available.</summary>
    public string? AgentId { get; init; }

    /// <summary>The current invocation of the agent.</summary>
    public string? RunId { get; init; }

    public string State { get; init; } = "running";
    public string? AgentType { get; init; }
    public string? SourceRevision { get; init; }
    public long? DurationMilliseconds { get; init; }
    public long? StartedAtUnixMilliseconds { get; init; }

    /// <summary>The owning provider task or thread.</summary>
    public string? ParentTaskId { get; init; }

    /// <summary>The parent tool call used to nest the agent's activity.</summary>
    public string? ParentToolUseId { get; init; }

    /// <summary>The last tool reported for this task, or null when unavailable.</summary>
    public string? LastTool { get; init; }

    /// <summary>The task's reported token count, or null when unavailable.</summary>
    public long? Tokens { get; init; }
}
