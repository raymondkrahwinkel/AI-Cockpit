namespace Cockpit.Infrastructure.Plugins;

public enum PluginActivationStatus
{
    Activated,
    AlreadyActive,
    Busy,
    NeedsRestart,
    // AC-2025: these bytes can only start with a host restart (services for the container, or a manifest flag); no retry helps.
    NeedsHostRestart,
    Refused,
    Deactivated,
    NotActive
}
