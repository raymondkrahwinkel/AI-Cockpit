namespace Cockpit.App.Plugins;

// How serious a `PluginFailure` is. An `Error` kept the plugin from loading; a
// `Warning` loaded it but flags something the operator should know — a plugin built against a
// newer SDK than this app, say, which runs but may misbehave.
public enum PluginIssueSeverity
{
    Warning,
    Error,
}
