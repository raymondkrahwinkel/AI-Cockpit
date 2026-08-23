namespace Cockpit.App.Plugins;

// A plugin issue surfaced in the startup banner and the plugin manager (#14). Defaults to
// `PluginIssueSeverity.Error`, the original "failed to load" meaning, so `Warning` (loaded but
// flagged) is the deliberate exception.
public sealed record PluginFailure(
    string FolderId,
    string DisplayName,
    string Phase,
    string Error,
    PluginIssueSeverity Severity = PluginIssueSeverity.Error);
