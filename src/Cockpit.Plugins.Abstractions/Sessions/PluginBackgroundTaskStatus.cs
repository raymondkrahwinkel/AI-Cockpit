namespace Cockpit.Plugins.Abstractions.Sessions;

/// <summary>
/// How a <see cref="PluginBackgroundTaskNotification"/> says its task ended.
/// </summary>
public enum PluginBackgroundTaskStatus
{
    /// <summary>
    /// The provider named a status this build does not know. Deliberately ordinal 0, so an unmapped value lands
    /// on the least authoritative option rather than silently reading as completed.
    /// </summary>
    Unknown,

    /// <summary>
    /// The task ran to completion without error.
    /// </summary>
    Completed,

    /// <summary>
    /// The task ended in error.
    /// </summary>
    Failed,
}
