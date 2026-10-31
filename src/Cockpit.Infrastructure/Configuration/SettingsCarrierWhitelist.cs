using System.Text.Json;
using System.Text.Json.Nodes;

namespace Cockpit.Infrastructure.Configuration;

// AC-540: which parts of cockpit.json a settings carrier may hold. White, not black: a field nobody listed here
// stays on this machine, so a new field forgotten here is lost from the carrier rather than leaked to it.
// A path is property names joined by '.', `[]` for every array element and `*` for every key of a dictionary.
internal static class SettingsCarrierWhitelist
{
    public static readonly IReadOnlyList<string> Carried =
    [
        "Projects[].Assistant", "Profiles[].Label", "Profiles[].Purpose", "Profiles[].SystemPrompt", "Profiles[].Assistant",
        "Profiles[].DefaultKind", "Profiles[].MemoryCapMegabytes", "Profiles[].EnabledMcpServers",
        "Profiles[].Defaults.PermissionMode", "Profiles[].Defaults.Model", "Profiles[].Defaults.Effort",
        "Profiles[].Defaults.AutoApproveTools", "Profiles[].Defaults.OptionDefaults", "Profiles[].Defaults.DefaultReadingLevel",
        "Profiles[].Provider.Provider", "Profiles[].Provider.BaseUrl", "Profiles[].Provider.Model",
        "Profiles[].Provider.SystemPrompt", "Profiles[].Provider.TurnLimits", "Profiles[].Provider.ContextTokens",
        "Profiles[].Provider.LoadModelWithContext", "Profiles[].Provider.LoadOptions", "Profiles[].Provider.EnableThinking", "Profiles[].Provider.PluginProviderId",
        "Profiles[].Provider.ReasoningEffort",
        "Profiles[].Delegation.AllowedAsTarget", "Profiles[].Delegation.MaxConcurrent", "Profiles[].Delegation.PermissionCeiling",
        "Profiles[].Delegation.MayDelegateFurther", "Profiles[].Delegation.TimeoutMinutes", "Profiles[].Delegation.AllowedTaskTypes",
        "Profiles[].Delegation.Purpose", "Profiles[].Delegation.Tags", "Profiles[].Delegation.AllowedTools",
        "Plugins.*.Enabled", "Plugins.*.MenuOrder", "Plugins.*.HiddenInMenu", "Plugins.*.PinnedToSidebar",
        "Notifications.LocalEnabled", "Notifications.NotifyOnSessionFinished", "Notifications.NotifyOnSessionIdle",
        "Notifications.NotifyWhenAllSessionsIdle", "Notifications.NotifyOnCiFailure", "Notifications.IdleThresholdMinutes",
        "Notifications.SessionIdleMinutes",
        "SessionSwitching.IsEnabled", "SessionSwitching.Modifier",
        "TranscriptDisplay.ShowTimestamps",
        "UsagePill.VisibleFields",
        "SessionBehavior.AutoCloseOnExit", "SessionBehavior.CombineQueuedMessages", "SessionBehavior.WakeAgentsByDefault",
        "SessionBehavior.MemoryBudgetPercent",
        "ProjectsDisplay.LayoutMode",
        "UsageThresholds.ByProvider", "UsageThresholds.ByProfile", "UsageThresholds.ByAssistant",
        "Terminal.FontFamily", "Terminal.FontSize",
        "Shortcuts.Gestures", "Shortcuts.PluginGestures",
        "Layout.ThemeMode",
        "Updates.ChosenChannel", "Updates.CheckOnStartup",
    ];

    // Measured on the 2026-09-17 second-machine start: paths bound to one machine, which `Carried` must never reach.
    // Codex's `Command` lives in PluginConfigJson; KubeconfigPath and the git-status repos in a plugin's Data.
    public static readonly IReadOnlyList<string> NeverCarried =
    [
        "Projects[].Id", "Projects[].Name", "Projects[].Description", "Projects[].Category", "Projects[].GitUrl", "Projects[].SharedSourceKey",
        "Projects[].DefaultProfileLabel", "Projects[].BehaviorPrompt", "Projects[].IsolateInWorktreeByDefault",
        "CategoryOrder",
        "Projects[].SourceDirectory", "Projects[].SourceDirectories", "Projects[].Resources", "Projects[].MemoryRef",
        "Profiles[].Delegation.AllowedWorkingDirs", "Profiles[].Provider.PluginConfigJson", "Plugins.*.Data", "WorkingPaths",
    ];

    private static readonly HashSet<string> _Leaves = new(Carried, StringComparer.Ordinal);

    private static readonly HashSet<string> _Ancestors = new(
        Carried.SelectMany(path => path.Select((c, i) => c is '.' || path[i..].StartsWith("[]", StringComparison.Ordinal) ? path[..i] : null))
            .OfType<string>(),
        StringComparer.Ordinal);

    // The blob a carrier is handed: cockpit.json as it is on disk, cut down to `Carried`.
    public static byte[] Extract(JsonObject cockpitJson) => Extract(cockpitJson, SettingsCarrierFilter.All);

    public static byte[] Extract(JsonObject cockpitJson, SettingsCarrierFilter filter) =>
        JsonSerializer.SerializeToUtf8Bytes(_Keep(cockpitJson, "", filter));

    private static JsonNode? _Keep(JsonNode? node, string path, SettingsCarrierFilter filter)
    {
        if (!filter.Allows(path) || path == "Profiles[]" && node is JsonObject profile && !filter.AllowsProfile(profile["Label"]?.GetValue<string>()))
        {
            return null;
        }

        if (_Leaves.Contains(path))
        {
            return node?.DeepClone();
        }

        switch (node)
        {
            case JsonObject obj:
                var kept = new JsonObject();
                foreach (var (key, value) in obj)
                {
                    var named = path.Length == 0 ? key : $"{path}.{key}";
                    var child = Reaches(named) ? named : $"{path}.*";
                    if (Reaches(child))
                    {
                        if (_Keep(value, child, filter) is { } valueKept)
                        {
                            kept[key] = valueKept;
                        }
                    }
                }

                return kept;
            case JsonArray array:
                var items = array.Select(item => _Keep(item, path + "[]", filter));
                return path == "Profiles" ? new JsonArray([.. items.Where(item => item is not null)]) : new JsonArray([.. items]);
            default:
                return null;
        }
    }

    // Whether a path is a carried leaf, or lies on the way to one.
    public static bool Reaches(string path) => _Leaves.Contains(path) || _Ancestors.Contains(path);

    public static bool IsLeaf(string path) => _Leaves.Contains(path);
}

internal sealed class SettingsCarrierFilter
{
    private static readonly IReadOnlyDictionary<string, string[]> CategoryRoots = new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        ["Profiles"] = ["Profiles", "CategoryOrder"],
        ["Plugins"] = ["Plugins"],
        ["Notifications"] = ["Notifications"],
        ["Shortcuts"] = ["Shortcuts"],
        ["SessionBehaviour"] = ["SessionSwitching", "TranscriptDisplay", "UsagePill", "SessionBehavior"],
        ["Usage"] = ["UsageThresholds"],
        ["Appearance"] = ["Terminal", "Layout"],
        ["Updates"] = ["Updates"],
    };

    private readonly HashSet<string>? _categories;
    // AC-1847: per profile label, whether this carrier takes it; a profile that is not in here follows the Profiles category.
    private readonly Dictionary<string, bool> _overrides;

    private SettingsCarrierFilter(HashSet<string>? categories, Dictionary<string, bool> overrides) => (_categories, _overrides) = (categories, overrides);

    public static SettingsCarrierFilter All { get; } = new(null, new Dictionary<string, bool>(StringComparer.Ordinal));

    internal static IEnumerable<string> Categories => CategoryRoots.Keys;

    public static SettingsCarrierFilter FromConfig(JsonObject config, string carrierId)
    {
        if (config["SettingsSync"]?["Categories"] is not JsonObject categories)
        {
            return All;
        }

        var categoriesForCarrier = categories
            .Where(pair => CategoryRoots.ContainsKey(pair.Key) && pair.Value is JsonArray ids && ids.Any(id => id?.GetValue<string>() == carrierId))
            .Select(pair => pair.Key);
        var overrides = (config["SettingsSync"]?["Profiles"] as JsonObject)?
            .Where(pair => pair.Value is JsonArray)
            .ToDictionary(pair => pair.Key, pair => pair.Value is JsonArray ids && ids.Any(id => id?.GetValue<string>() == carrierId), StringComparer.Ordinal) ?? new Dictionary<string, bool>(StringComparer.Ordinal);
        return new([.. categoriesForCarrier], overrides);
    }

    public bool Allows(string path) => _categories is null || path.Length == 0 || !CategoryRoots.Values.SelectMany(roots => roots).Any(root => path == root || path.StartsWith(root + ".", StringComparison.Ordinal) || path.StartsWith(root + "[]", StringComparison.Ordinal)) ||
        _categories.Any(category => CategoryRoots[category].Any(root => path == root || path.StartsWith(root + ".", StringComparison.Ordinal) || path.StartsWith(root + "[]", StringComparison.Ordinal))) ||
        // A profile this carrier was picked for takes the Profiles section along, even when the category itself does not include it.
        _overrides.ContainsValue(true) && (path == "Profiles" || path.StartsWith("Profiles[]", StringComparison.Ordinal));

    public bool AllowsProfile(string? label) => _categories is null || (label is not null && _overrides.TryGetValue(label, out var picked) ? picked : _categories.Contains("Profiles"));
}
