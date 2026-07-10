namespace Cockpit.App.Plugins;

/// <summary>A plugin that failed to load or initialize (#14): which plugin, in which phase, and why — surfaced in a startup banner and in the plugin manager.</summary>
public sealed record PluginFailure(string FolderId, string DisplayName, string Phase, string Error);
