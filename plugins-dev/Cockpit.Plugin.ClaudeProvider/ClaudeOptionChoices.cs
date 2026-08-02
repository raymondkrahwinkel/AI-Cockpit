using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Plugin.ClaudeProvider;

// The Claude launch/live option vocabularies the plugin owns now that Claude is a provider plugin (Fase 4): the
// permission modes, model aliases and effort levels, each with the friendly label the operator reads while the raw
// value round-trips to the CLI. These used to live in the host's `SessionOptionCatalog` — moving them here is
// what lets the core render Claude's options generically, knowing none of their meaning.
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
    public static readonly IReadOnlyList<string> ModelSuggestions = ["fable", "opus", "sonnet", "haiku"];

    // A label names the family, never a release: the value stays an alias the CLI re-points at will, so "Opus 4.8"
    // would keep claiming a release the dropdown no longer launches (AC-418).
    public static readonly IReadOnlyDictionary<string, string> ModelLabels = new Dictionary<string, string>
    {
        ["fable"] = "Fable",
        ["opus"] = "Opus",
        ["sonnet"] = "Sonnet",
        ["haiku"] = "Haiku",
    };

    // What those aliases cost, cheapest first — the ordering consumers route on, since ModelSuggestions above is
    // ordered for the picker and says nothing about price. Estimates, and only ever that: Anthropic publishes prices
    // on a page rather than through an API, an alias re-points at a new model without this list moving, and
    // promotional rates (Sonnet's introductory tier) are deliberately not tracked here — a figure that quietly drifts
    // is worse than one the reader knows is approximate. Whoever changes a number here changes only a claim; the real
    // spend still arrives from the CLI as total_cost_usd.
    public static readonly IReadOnlyList<PluginModelCostEstimate> ModelCostEstimatesCheapestFirst =
    [
        new("haiku") { EstimatedInputUsdPerMillionTokens = 1m, EstimatedOutputUsdPerMillionTokens = 5m },
        new("sonnet") { EstimatedInputUsdPerMillionTokens = 3m, EstimatedOutputUsdPerMillionTokens = 15m },
        new("opus") { EstimatedInputUsdPerMillionTokens = 5m, EstimatedOutputUsdPerMillionTokens = 25m },
        new("fable") { EstimatedInputUsdPerMillionTokens = 10m, EstimatedOutputUsdPerMillionTokens = 50m },
    ];

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
