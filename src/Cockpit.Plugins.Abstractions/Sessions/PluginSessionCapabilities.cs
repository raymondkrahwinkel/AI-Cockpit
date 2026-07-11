namespace Cockpit.Plugins.Abstractions.Sessions;

/// <summary>
/// What a plugin-provided <see cref="IPluginSessionDriver"/> supports, so the host's session UI renders or
/// hides controls per provider instead of showing dead ones — the plugin-facing mirror of
/// <c>Cockpit.Core.Claude.SessionCapabilities</c> (#45). Kept as a separate type (not a shared reference)
/// so this assembly never needs to reference <c>Cockpit.Core</c>; the host's driver adapter converts one to
/// the other at the plugin boundary. Only <see cref="SupportsTools"/>/<see cref="SupportsPermissions"/> are
/// exposed here — <see cref="IPluginSessionDriver"/> has no members a live model switch, plan mode, or
/// thinking-budget control could actually back, so those three flags do not exist on this narrow surface;
/// a plugin has nothing to set them true against, and the host adapter always reports them unsupported.
/// </summary>
public sealed record PluginSessionCapabilities(
    bool SupportsTools,
    bool SupportsPermissions);
