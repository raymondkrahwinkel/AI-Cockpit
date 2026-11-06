namespace Cockpit.Infrastructure.Plugins;

public enum PluginActivationStatus
{
    Activated,
    AlreadyActive,
    Busy,
    NeedsRestart,
    // The manifest explicitly requires a host restart.
    NeedsHostRestart,
    Refused,
    Deactivated,
    NotActive
}
