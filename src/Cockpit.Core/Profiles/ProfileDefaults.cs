using Cockpit.Core.Sessions;

namespace Cockpit.Core.Profiles;

// The start defaults a `SessionProfile` pre-selects for a new session: the permission
// mode, model and effort the New-session dialog opens with. Values are the CLI/control identifiers
// (e.g. `default`/`bypassPermissions`, `sonnet`, `medium`) — not display labels —
// so they can be handed straight to a session start. A `null`
// `SessionProfile.Defaults` falls back to the app defaults.
//
// `AutoApproveTools`:
// Whether a local tool session (a driver with `SupportsTools` but not `SupportsPermissions` —
// Ollama/LM Studio) starts with its "allow all tools" toggle already on, so the operator does not have to
// flip it every time for a profile they always trust (#26). Ignored by the Claude-CLI provider, which
// gates through its own permission modes instead. Defaults to `false` so existing profiles
// keep prompting for every tool call until the operator opts in.
public sealed record ProfileDefaults(
    [property: Obsolete("Legacy Claude-CLI default; Claude is a provider plugin now and its start defaults live in OptionDefaults. Read only by the one-time migration and its persistence — do not use in new code. Will be removed once no config carries it.")] string PermissionMode,
    [property: Obsolete("Legacy Claude-CLI default; use OptionDefaults instead. Read only by the one-time migration and its persistence. Will be removed.")] string Model,
    [property: Obsolete("Legacy Claude-CLI default; use OptionDefaults instead. Read only by the one-time migration and its persistence. Will be removed.")] string Effort,
    bool AutoApproveTools = false)
{
    // Per-profile defaults for the provider plugin's own declared launch options (permission mode, model and effort
    // for Claude; sandbox for Codex), keyed by each option's key. The Manage-profiles dialog fills these from the
    // plugin's declared options and the New-session dialog pre-selects them, so a plugin profile remembers its
    // preferred start settings — the provider-neutral successor to the typed `PermissionMode`/
    // `Model`/`Effort` above, which were the in-tree Claude route's own vocabulary.
    // `null` means each option falls back to its own declared default.
    public IReadOnlyDictionary<string, string>? OptionDefaults { get; init; }

    // The reading level a new SDK/chat session opens with (AC-138) — Developer/Focus/Simple. This is the
    // "Default view" the profile pre-selects; the New-session dialog inherits it and lets it be overridden,
    // and the running session's header can switch it live. `null` falls back to the app
    // default (`ReadingLevel.Developer`). Has no effect on a TTY session, which has no reading level.
    public ReadingLevel? DefaultReadingLevel { get; init; }
}
