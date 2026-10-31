namespace Cockpit.Plugins.Abstractions.UI;

/// <summary>
/// A server this desktop manages, as <see cref="ICockpitUiHost.GetServerTargetsAsync"/> hands it to a plugin (AC-1547).
/// </summary>
/// <param name="Name">The server's name, as the desktop shows it.</param>
/// <param name="Host">The plugin's host on that server, or null when the plugin cannot be managed there.</param>
/// <param name="Unavailable">Why Host is null, in words for the operator; null when it is set.</param>
public sealed record PluginServerTarget(string Name, ICockpitUiHost? Host, string? Unavailable = null);
