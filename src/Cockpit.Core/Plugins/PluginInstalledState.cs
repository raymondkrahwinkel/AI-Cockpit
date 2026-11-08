namespace Cockpit.Core.Plugins;

public sealed record PluginInstalledState(bool Exists, string? Version);
