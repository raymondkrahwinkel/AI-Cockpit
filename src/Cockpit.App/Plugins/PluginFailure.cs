namespace Cockpit.App.Plugins;

/// <summary>
/// A plugin issue surfaced in the startup banner and the plugin manager (#14): which plugin, in which phase,
/// why, and how serious. Defaults to <see cref="PluginIssueSeverity.Error"/> — the original meaning, a plugin
/// that failed to load or initialize — so a <see cref="PluginIssueSeverity.Warning"/> (loaded, but flagged) is
/// the deliberate exception.
/// </summary>
public sealed record PluginFailure(
    string FolderId,
    string DisplayName,
    string Phase,
    string Error,
    PluginIssueSeverity Severity = PluginIssueSeverity.Error);
