namespace Cockpit.Core.Plugins;

public sealed record PluginStoreTarget(int AbstractionsVersion, Version HostVersion, string RuntimeIdentifier);
