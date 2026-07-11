namespace Cockpit.Core.Profiles;

/// <summary>
/// The start defaults a <see cref="ClaudeProfile"/> pre-selects for a new session: the permission
/// mode, model and effort the New-session dialog opens with. Values are the CLI/control identifiers
/// (e.g. <c>default</c>/<c>bypassPermissions</c>, <c>sonnet</c>, <c>medium</c>) — not display labels —
/// so they can be handed straight to a session start. A <see langword="null"/>
/// <see cref="ClaudeProfile.Defaults"/> falls back to the app defaults.
/// </summary>
/// <param name="AutoApproveTools">
/// Whether a local tool session (a driver with <c>SupportsTools</c> but not <c>SupportsPermissions</c> —
/// Ollama/LM Studio) starts with its "allow all tools" toggle already on, so the operator does not have to
/// flip it every time for a profile they always trust (#26). Ignored by the Claude-CLI provider, which
/// gates through its own permission modes instead. Defaults to <see langword="false"/> so existing profiles
/// keep prompting for every tool call until the operator opts in.
/// </param>
public sealed record ProfileDefaults(string PermissionMode, string Model, string Effort, bool AutoApproveTools = false);
