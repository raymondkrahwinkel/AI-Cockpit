namespace Cockpit.Plugin.ClaudeProvider;

/// <summary>
/// The Claude launch/live option vocabularies the plugin owns now that Claude is a provider plugin (Fase 4): the
/// permission modes, model aliases and effort levels, each with the friendly label the operator reads while the raw
/// value round-trips to the CLI. These used to live in the host's <c>SessionOptionCatalog</c> — moving them here is
/// what lets the core render Claude's options generically, knowing none of their meaning.
/// </summary>
internal static class ClaudeOptionChoices
{
    // The CLI's four real --permission-mode values (there is no "auto" mode — the CLI rejects it). bypassPermissions
    // is launch-only; the live list below drops it since the CLI cannot enter it mid-session.
    public static readonly IReadOnlyList<string> PermissionModes = ["default", "acceptEdits", "plan", "bypassPermissions"];

    public static readonly IReadOnlyList<string> LivePermissionModes = ["default", "acceptEdits", "plan"];

    public static readonly IReadOnlyDictionary<string, string> PermissionModeLabels = new Dictionary<string, string>
    {
        ["default"] = "Ask permissions",
        ["acceptEdits"] = "Accept edits",
        ["plan"] = "Plan mode",
        ["bypassPermissions"] = "Bypass permissions",
    };

    // The CLI's own aliases, offered as free-text suggestions so a specific model or snapshot can still be pinned; the
    // CLI resolves the alias to the current model itself, so this list needs no per-release upkeep.
    public static readonly IReadOnlyList<string> ModelSuggestions = ["opus", "sonnet", "haiku"];

    public static readonly IReadOnlyDictionary<string, string> ModelLabels = new Dictionary<string, string>
    {
        ["opus"] = "Opus 4.8",
        ["sonnet"] = "Sonnet",
        ["haiku"] = "Haiku",
    };

    public static readonly IReadOnlyList<string> EffortLevels = ["low", "medium", "high", "xhigh", "max"];

    public static readonly IReadOnlyDictionary<string, string> EffortLabels = new Dictionary<string, string>
    {
        ["low"] = "Low",
        ["medium"] = "Medium",
        ["high"] = "High",
        ["xhigh"] = "Extra high",
        ["max"] = "Max",
    };

    // "Effort" maps to a thinking-token budget: the one budget the control protocol can set mid-session
    // (set_max_thinking_tokens). Higher effort simply runs the session with a larger budget. These per-level counts
    // are Cockpit's own tuning (ported verbatim from the host's SessionOptionCatalog), not a fixed SDK constant.
    public static readonly IReadOnlyDictionary<string, int> EffortThinkingTokens = new Dictionary<string, int>
    {
        ["low"] = 4_000,
        ["medium"] = 12_000,
        ["high"] = 24_000,
        ["xhigh"] = 48_000,
        ["max"] = 64_000,
    };
}
