namespace Cockpit.Plugins.Abstractions.Sessions;

/// <summary>
/// What a plugin-provided <see cref="IPluginSessionDriver"/> supports, so the host's session UI renders or
/// hides controls per provider instead of showing dead ones — the plugin-facing mirror of
/// <c>Cockpit.Core.Sessions.SessionCapabilities</c> (#45/#64). Kept as a separate type (not a shared reference)
/// so this assembly never needs to reference <c>Cockpit.Core</c>; the host's driver adapter converts one to
/// the other at the plugin boundary. <see cref="SupportsTools"/>/<see cref="SupportsPermissions"/> are here
/// because a plugin driver can genuinely back them.
/// </summary>
/// <param name="SupportsTools">Whether the plugin driver has a tool source (native tools or an MCP loop of its own).</param>
/// <param name="SupportsPermissions">Whether the plugin driver knows Claude-style permission modes.</param>
/// <param name="SupportsVision">
/// Whether this plugin's driver sends pasted image attachments to the model (#64). Defaults to
/// <see langword="false"/> for back-compat with existing 2-arg construction.
/// </param>
/// <param name="SupportsLiveModelSwitch">
/// Whether this driver can switch the model mid-session (Fase 4 D4) — backed by
/// <see cref="IPluginSessionDriver.SetLiveOptionAsync"/> with the <c>"model"</c> key, which the host's driver adapter
/// wires its own <c>SetModelAsync</c> to. Defaults to <see langword="false"/>: an already-compiled plugin, or one that
/// cannot switch models live, leaves it off and the host renders no live model control.
/// </param>
/// <param name="SupportsPermissionModeSwitch">
/// Whether this driver can switch the Claude-style permission mode mid-session (Fase 4 D4) — backed by
/// <see cref="IPluginSessionDriver.SetLiveOptionAsync"/> with the <see cref="WellKnownPluginSessionOptions.PermissionMode"/>
/// key, which the host's driver adapter wires its own <c>SetPermissionModeAsync</c> to. Defaults to
/// <see langword="false"/>, so a provider with no permission modes (an HTTP model, Codex's sandbox) never advertises it.
/// </param>
public sealed record PluginSessionCapabilities(
    bool SupportsTools,
    bool SupportsPermissions,
    bool SupportsVision = false,
    bool SupportsLiveModelSwitch = false,
    bool SupportsPermissionModeSwitch = false);
