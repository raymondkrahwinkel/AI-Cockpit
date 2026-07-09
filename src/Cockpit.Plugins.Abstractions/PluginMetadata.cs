namespace Cockpit.Plugins.Abstractions;

/// <summary>Identity a plugin reports to the host and the plugin manager shows in its overview (display name + description + version/author).</summary>
public sealed record PluginMetadata(string Id, string DisplayName, string Version, string? Author, string? Description);
