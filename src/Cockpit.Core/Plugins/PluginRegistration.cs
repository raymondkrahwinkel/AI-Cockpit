namespace Cockpit.Core.Plugins;

// Persisted per-plugin state in `cockpit.json`'s plugins section, keyed by folder id: whether it is
// enabled, the SHA-256 the operator consented to, and where its contributions sit in the left menu (#72).
//
// `Enabled`: Whether the plugin loads at all.
// `PinnedSha256`: The bytes the operator consented to; a mismatch means the plugin changed and needs consent again.
// `MenuOrder`: Position of this plugin's buttons/sections in the left menu, low first. Ties keep discovery order, so a plugin nobody has moved sits where it always did.
// `HiddenInMenu`: Keeps the plugin's contributions out of the left menu while the plugin itself keeps running — its shortcut and command-palette entry still work. That is the difference from disabling it, and the manager has to say so, or "hidden" reads as "off".
public sealed record PluginRegistration(bool Enabled, string PinnedSha256, int MenuOrder = 0, bool HiddenInMenu = false);
