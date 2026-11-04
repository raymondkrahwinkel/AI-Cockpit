namespace Cockpit.Infrastructure.Plugins;

public enum PluginActivationStatus
{
    Activated,
    AlreadyActive,
    Busy,
    NeedsRestart,
    Refused,
    Deactivated,
    NotActive
}
