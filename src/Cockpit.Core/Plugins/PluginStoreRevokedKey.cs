namespace Cockpit.Core.Plugins;

public sealed record PluginStoreRevokedKey(string Fingerprint, string? Name, string? RevokedAt);
