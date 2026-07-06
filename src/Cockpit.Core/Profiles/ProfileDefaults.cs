namespace Cockpit.Core.Profiles;

/// <summary>
/// The start defaults a <see cref="ClaudeProfile"/> pre-selects for a new session: the permission
/// mode, model and effort the New-session dialog opens with. Values are the CLI/control identifiers
/// (e.g. <c>default</c>/<c>bypassPermissions</c>, <c>sonnet</c>, <c>medium</c>) — not display labels —
/// so they can be handed straight to a session start. A <see langword="null"/>
/// <see cref="ClaudeProfile.Defaults"/> falls back to the app defaults.
/// </summary>
public sealed record ProfileDefaults(string PermissionMode, string Model, string Effort);
