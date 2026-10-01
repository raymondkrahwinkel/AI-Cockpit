namespace Cockpit.Core.Plugins;

// One installed plugin as the plugin manager shows it (AC-1434): what discovery found, its registration, and what went
// wrong with it this run, already sorted into the three facts the manager tells apart (#184).
public sealed record InstalledPlugin(
    DiscoveredPlugin Discovered,
    PluginRegistration? Registration,
    string? ActivationFailure,
    string? CompatibilityWarning,
    string? McpContributionFailure);
