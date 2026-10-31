namespace Cockpit.Plugins.Abstractions.UI;

/// <summary>
/// A server this desktop manages, as <see cref="ICockpitUiHost.GetServerTargetsAsync"/> hands it to a plugin (AC-1547).
/// </summary>
/// <param name="Name">The server's name, as the desktop shows it.</param>
/// <param name="Host">The plugin's host on that server, or null when the plugin cannot be managed there.</param>
/// <param name="Unavailable">Why Host is null, in words for the operator; null when it is set.</param>
/// <param name="Resolve">What the operator can do about it, such as opening the server's plugins; null when nothing.</param>
/// <param name="ResolveLabel">The label for <paramref name="Resolve"/>'s button.</param>
public sealed record PluginServerTarget(string Name, ICockpitUiHost? Host, string? Unavailable = null, Func<Task>? Resolve = null, string? ResolveLabel = null);
