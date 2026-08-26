namespace Cockpit.Plugins.Abstractions.Sessions;

/// <summary>
/// A background task named by an earlier <see cref="PluginBackgroundTasksChanged"/> reached its end (AC-1057).
/// </summary>
/// <remarks>
/// A provider that never raises <see cref="PluginBackgroundTasksChanged"/> simply never raises this either — the
/// host falls back to inferring the outcome from the task leaving that ledger, same as before this event existed.
/// </remarks>
public sealed record PluginBackgroundTaskNotification : PluginSessionEvent
{
    /// <summary>
    /// The same id <see cref="PluginBackgroundTask.TaskId"/> reported while the task was outstanding.
    /// </summary>
    public required string TaskId { get; init; }

    /// <summary>
    /// The tool call that started this task, so the host can attach the outcome to that call's own row.
    /// </summary>
    public string? ToolUseId { get; init; }

    /// <summary>
    /// How the task ended, per the provider.
    /// </summary>
    public required PluginBackgroundTaskStatus Status { get; init; }
}
