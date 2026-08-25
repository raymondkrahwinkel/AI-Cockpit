using Cockpit.Core.Sessions;

namespace Cockpit.Core.Profiles;

// The start defaults a `SessionProfile` pre-selects for a new session (mode/model/effort as CLI identifiers, not
// display labels; `null` falls back to app defaults). `AutoApproveTools`: whether a local tool session
// (`SupportsTools` but not `SupportsPermissions` — Ollama/LM Studio) starts with "allow all tools" on (#26); ignored by Claude-CLI.
public sealed record ProfileDefaults(
    [property: Obsolete("Legacy Claude-CLI default; Claude is a provider plugin now and its start defaults live in OptionDefaults. Read only by the one-time migration and its persistence — do not use in new code. Will be removed once no config carries it.")] string PermissionMode,
    [property: Obsolete("Legacy Claude-CLI default; use OptionDefaults instead. Read only by the one-time migration and its persistence. Will be removed.")] string Model,
    [property: Obsolete("Legacy Claude-CLI default; use OptionDefaults instead. Read only by the one-time migration and its persistence. Will be removed.")] string Effort,
    bool AutoApproveTools = false)
{
    // Per-profile defaults for the provider plugin's own declared launch options (permission mode/model/effort for
    // Claude, sandbox for Codex), keyed by option key — the provider-neutral successor to the typed `PermissionMode`/
    // `Model`/`Effort` above. The Manage-profiles dialog fills these; `null` means each option falls back to its own default.
    public IReadOnlyDictionary<string, string>? OptionDefaults { get; init; }

    // The reading level a new SDK/chat session opens with (AC-138) — Developer/Focus/Simple, the "Default view" the
    // profile pre-selects. The New-session dialog inherits it and lets it be overridden; the running session's header
    // can switch it live. `null` falls back to `ReadingLevel.Developer`. No effect on a TTY session, which has no reading level.
    public ReadingLevel? DefaultReadingLevel { get; init; }
}
