namespace Cockpit.Plugins.Abstractions;

/// <summary>
/// Identity a plugin reports to the host and the plugin manager shows in its overview (display name +
/// description + version/author).
/// <para>
/// <paramref name="Version"/> belongs to <c>plugin.json</c> and is left unset by the plugin: the manifest is
/// what the host reads everywhere it shows or compares a version (the plugin manager row, the update check,
/// the store), so a second copy in C# is a claim nothing verifies — and eleven of twenty had drifted from the
/// manifest by the time anyone looked (AC-301). The host fills it from the manifest on the way out, in
/// <c>InstalledPlugins</c>. Optional rather than removed so the constructor keeps its shape and plugins
/// already published against it keep loading.
/// </para>
/// </summary>
public sealed record PluginMetadata(string Id, string DisplayName, string Version = "", string? Author = null, string? Description = null);
