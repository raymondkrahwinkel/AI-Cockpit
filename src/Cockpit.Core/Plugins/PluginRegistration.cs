namespace Cockpit.Core.Plugins;

// AC-1013 (#72): Persisted per-plugin state in cockpit.json — enabled flag, consented SHA-256, and left-menu
// placement (order/hidden/pinned-to-sidebar, AC-937).
// (Omitted: per-field rationale — hidden-vs-disabled distinction, sidebar migration-default resolution; see ticket.)
public sealed record PluginRegistration(bool Enabled, string PinnedSha256, int MenuOrder = 0, bool HiddenInMenu = false, bool PinnedToSidebar = false);
