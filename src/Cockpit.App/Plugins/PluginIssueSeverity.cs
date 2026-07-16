namespace Cockpit.App.Plugins;

/// <summary>
/// How serious a <see cref="PluginFailure"/> is. An <see cref="Error"/> kept the plugin from loading; a
/// <see cref="Warning"/> loaded it but flags something the operator should know — a plugin built against a
/// newer SDK than this app, say, which runs but may misbehave.
/// </summary>
public enum PluginIssueSeverity
{
    Warning,
    Error,
}
