using Cockpit.Core.Sessions;

namespace Cockpit.Core.Profiles;

/// <summary>
/// The start defaults a <see cref="SessionProfile"/> pre-selects for a new session: the permission
/// mode, model and effort the New-session dialog opens with. Values are the CLI/control identifiers
/// (e.g. <c>default</c>/<c>bypassPermissions</c>, <c>sonnet</c>, <c>medium</c>) — not display labels —
/// so they can be handed straight to a session start. A <see langword="null"/>
/// <see cref="SessionProfile.Defaults"/> falls back to the app defaults.
/// </summary>
/// <param name="AutoApproveTools">
/// Whether a local tool session (a driver with <c>SupportsTools</c> but not <c>SupportsPermissions</c> —
/// Ollama/LM Studio) starts with its "allow all tools" toggle already on, so the operator does not have to
/// flip it every time for a profile they always trust (#26). Ignored by the Claude-CLI provider, which
/// gates through its own permission modes instead. Defaults to <see langword="false"/> so existing profiles
/// keep prompting for every tool call until the operator opts in.
/// </param>
public sealed record ProfileDefaults(
    [property: Obsolete("Legacy Claude-CLI default; Claude is a provider plugin now and its start defaults live in OptionDefaults. Read only by the one-time migration and its persistence — do not use in new code. Will be removed once no config carries it.")] string PermissionMode,
    [property: Obsolete("Legacy Claude-CLI default; use OptionDefaults instead. Read only by the one-time migration and its persistence. Will be removed.")] string Model,
    [property: Obsolete("Legacy Claude-CLI default; use OptionDefaults instead. Read only by the one-time migration and its persistence. Will be removed.")] string Effort,
    bool AutoApproveTools = false)
{
    /// <summary>
    /// Per-profile defaults for the provider plugin's own declared launch options (permission mode, model and effort
    /// for Claude; sandbox for Codex), keyed by each option's key. The Manage-profiles dialog fills these from the
    /// plugin's declared options and the New-session dialog pre-selects them, so a plugin profile remembers its
    /// preferred start settings — the provider-neutral successor to the typed <see cref="PermissionMode"/>/
    /// <see cref="Model"/>/<see cref="Effort"/> above, which were the in-tree Claude route's own vocabulary.
    /// <see langword="null"/> means each option falls back to its own declared default.
    /// </summary>
    public IReadOnlyDictionary<string, string>? OptionDefaults { get; init; }

    /// <summary>
    /// The reading level a new SDK/chat session opens with (AC-138) — Developer/Focus/Simple. This is the
    /// "Default view" the profile pre-selects; the New-session dialog inherits it and lets it be overridden,
    /// and the running session's header can switch it live. <see langword="null"/> falls back to the app
    /// default (<see cref="ReadingLevel.Developer"/>). Has no effect on a TTY session, which has no reading level.
    /// </summary>
    public ReadingLevel? DefaultReadingLevel { get; init; }
}
