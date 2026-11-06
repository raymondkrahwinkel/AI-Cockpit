namespace Cockpit.Core.Plugins;

// AC-2023: whether a plugin change took effect in the running host, or is held by a restart or live-session reason.
public sealed record PluginLiveState(bool IsLive, string? RestartReason = null, bool Failed = false, bool RolledBack = false, bool RestartRequired = false, string? Notice = null)
{
    public static PluginLiveState Active { get; } = new(true);

    public static PluginLiveState Restart(string reason) => new(false, reason, RestartRequired: true);

    public static PluginLiveState Blocked(string reason) => new(false, Notice: reason);

    public static PluginLiveState UpdateRolledBack(string error) => new(false, Failed: true, RolledBack: true, Notice: error);

    public static PluginLiveState UpdateFailed(string error) => new(false, error, Failed: true, RestartRequired: true);
}
