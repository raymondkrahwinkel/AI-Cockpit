namespace Cockpit.Core.Plugins;

public sealed record PluginSignatureCheck(bool Ok, string? Reason, string? Fingerprint);
