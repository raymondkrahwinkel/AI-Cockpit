namespace Cockpit.Core.Plugins;

/// <summary>Persisted per-plugin state in <c>cockpit.json</c>'s plugins section, keyed by folder id: whether it is enabled and the SHA-256 the operator consented to.</summary>
public sealed record PluginRegistration(bool Enabled, string PinnedSha256);
