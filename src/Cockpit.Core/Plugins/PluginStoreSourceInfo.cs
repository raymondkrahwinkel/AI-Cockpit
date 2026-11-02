namespace Cockpit.Core.Plugins;

public sealed record PluginStoreSourceInfo(PluginStoreConfig Store, string? Name = null, bool IsHidden = false);
