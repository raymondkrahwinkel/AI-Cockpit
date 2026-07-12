namespace Cockpit.Plugins.Abstractions.Sessions;

/// <summary>
/// What a plugin-provided <see cref="IPluginSessionDriver"/> supports, so the host's session UI renders or
/// hides controls per provider instead of showing dead ones — the plugin-facing mirror of
/// <c>Cockpit.Core.Sessions.SessionCapabilities</c> (#45/#64). Kept as a separate type (not a shared reference)
/// so this assembly never needs to reference <c>Cockpit.Core</c>; the host's driver adapter converts one to
/// the other at the plugin boundary. <see cref="SupportsTools"/>/<see cref="SupportsPermissions"/> are here
/// because a plugin driver can genuinely back them — <see cref="IPluginSessionDriver"/> has no members a live
/// model switch, plan mode, or thinking-budget control could back, so those three flags do not exist on this
/// narrow surface at all.
/// </summary>
/// <param name="SupportsTools">Whether the plugin driver has a tool source (native tools or an MCP loop of its own).</param>
/// <param name="SupportsPermissions">Whether the plugin driver knows Claude-style permission modes.</param>
/// <param name="SupportsVision">
/// Whether this plugin's driver sends pasted image attachments to the model (#64). Defaults to
/// <see langword="false"/> for back-compat with existing 2-arg construction. Unlike the three omitted
/// flags above, this one is kept on the type (so a future plugin can flip it) but stays an unbackable
/// promise until <see cref="IPluginSessionDriver.SendUserMessageAsync"/> grows an images parameter — until
/// then every built-in example plugin reports it <see langword="false"/>, and the host adapter maps it
/// straight through rather than forcing it false itself, so the flag is honest the moment a driver can
/// actually back it without another host-side change.
/// </param>
public sealed record PluginSessionCapabilities(
    bool SupportsTools,
    bool SupportsPermissions,
    bool SupportsVision = false);
