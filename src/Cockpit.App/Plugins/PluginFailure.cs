namespace Cockpit.App.Plugins;

// A plugin issue surfaced in the startup banner and the plugin manager (#14): which plugin, in which phase,
// why, and how serious. Defaults to `PluginIssueSeverity.Error` — the original meaning, a plugin
// that failed to load or initialize — so a `PluginIssueSeverity.Warning` (loaded, but flagged) is
// the deliberate exception.
public sealed record PluginFailure(
    string FolderId,
    string DisplayName,
    string Phase,
    string Error,
    PluginIssueSeverity Severity = PluginIssueSeverity.Error);
