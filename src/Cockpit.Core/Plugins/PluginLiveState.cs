namespace Cockpit.Core.Plugins;

// AC-2023: whether a plugin change took effect in the running host, or waits for a restart and why.
// AC-2025: `Failed` is a live update that did not take; RestartReason holds the error. `RolledBack` says the previous
// version runs again, so nothing waits for a restart; without it the plugin needs one.
public sealed record PluginLiveState(bool IsLive, string? RestartReason = null, bool Failed = false, bool RolledBack = false)
{
    public static PluginLiveState Active { get; } = new(true);

    public static PluginLiveState Restart(string reason) => new(false, reason);

    public static PluginLiveState UpdateRolledBack(string error) => new(false, error, Failed: true, RolledBack: true);

    public static PluginLiveState UpdateFailed(string error) => new(false, error, Failed: true);
}
