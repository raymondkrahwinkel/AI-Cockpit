namespace Cockpit.Core.Plugins;

// One installed plugin as the plugin manager shows it (AC-1434): what discovery found, its registration, and what went
// wrong with it this run, already sorted into the three facts the manager tells apart (#184).
public sealed record InstalledPlugin(
    DiscoveredPlugin Discovered,
    PluginRegistration? Registration,
    string? ActivationFailure,
    string? CompatibilityWarning,
    string? McpContributionFailure,
    // AC-2025: the version in memory when it differs from the one on disk (a live update, or a restart that is waiting).
    string? RunningVersion = null);
