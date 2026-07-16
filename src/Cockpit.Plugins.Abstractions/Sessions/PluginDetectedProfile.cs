namespace Cockpit.Plugins.Abstractions.Sessions;

/// <summary>
/// A profile a TTY provider found already configured on this machine, offered to the host at startup so a
/// fresh install picks up an existing login without the operator recreating it by hand — how Claude's own
/// config directories under the user's home turn into ready-to-use profiles. The provider owns the discovery
/// (it alone knows where its CLI keeps state); the host only labels and mints what the provider reports.
/// </summary>
/// <param name="Label">A human name for the detected profile, derived by the provider (e.g. its config directory name).</param>
/// <param name="ConfigJson">The provider's own configuration for this profile, in the shape the plugin defined — the same blob it later receives at launch.</param>
public sealed record PluginDetectedProfile(string Label, string ConfigJson);
