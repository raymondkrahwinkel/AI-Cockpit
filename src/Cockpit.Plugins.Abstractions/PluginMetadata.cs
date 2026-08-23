namespace Cockpit.Plugins.Abstractions;

/// <summary>
/// Identity a plugin reports to the host and the plugin manager shows in its overview (display name +
/// description + version/author).
/// </summary>
/// <remarks>
/// <paramref name="Version"/> belongs to <c>plugin.json</c> and should be left unset by the plugin — the host
/// fills it from the manifest on the way out, in <c>InstalledPlugins</c>. Optional rather than removed so the
/// constructor keeps its shape and already-published plugins keep loading.
/// </remarks>
public sealed record PluginMetadata(string Id, string DisplayName, string Version = "", string? Author = null, string? Description = null);
