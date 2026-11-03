namespace Cockpit.Core.Plugins;

// AC-2023: whether a plugin change took effect in the running host, or waits for a restart and why.
public sealed record PluginLiveState(bool IsLive, string? RestartReason = null)
{
    public static PluginLiveState Active { get; } = new(true);

    public static PluginLiveState Restart(string reason) => new(false, reason);
}
