namespace Cockpit.App.ViewModels;

/// <summary>
/// The single source of the selectable session options (permission mode, model, effort) shared by
/// the New-session dialog and the running-session panel. Splitting <see cref="AllPermissionModes"/>
/// (the four real CLI modes, offered only at launch) from <see cref="LivePermissionModes"/> (the
/// three that can be switched mid-session) keeps the panel honest: bypass can only be chosen when a
/// session is launched, never as a live switch, so it never appears as a dead control in the panel
/// dropdown (see bug #15 / the no-dead-controls convention).
/// </summary>
public static class SessionOptionCatalog
{
    // The CLI's four real --permission-mode values. There is no "auto" mode — passing it made the
    // CLI silently fall back to default while the dropdown claimed otherwise (bug #15).
    public static IReadOnlyList<PermissionModeOption> AllPermissionModes { get; } =
    [
        new("Ask permissions", "default"),
        new("Accept edits", "acceptEdits"),
        new("Plan mode", "plan"),
        new("Bypass permissions", "bypassPermissions"),
    ];

    // The modes reachable via a live set_permission_mode control request: default/acceptEdits/plan.
    // Bypass is launch-only (it requires --dangerously-skip-permissions at start; the CLI refuses to
    // switch into it live), so it is deliberately absent here.
    public static IReadOnlyList<PermissionModeOption> LivePermissionModes { get; } =
        AllPermissionModes.Where(mode => mode.Value != BypassPermissionModeValue).ToArray();

    public static IReadOnlyList<ModelOption> Models { get; } =
    [
        new("Opus 4.8", "opus"),
        new("Sonnet", "sonnet"),
        new("Haiku", "haiku"),
    ];

    // "Effort" maps to a thinking-token budget: that budget is the one live control the protocol
    // exposes (set_max_thinking_tokens, verified against claude.exe 2.1.197), so the effort level
    // simply picks a budget the session runs with and can switch mid-flight. The per-level counts are
    // Cockpit's own tuning, not a fixed SDK constant.
    public static IReadOnlyList<EffortOption> Efforts { get; } =
    [
        new("Low", "low", 4_000),
        new("Medium", "medium", 12_000),
        new("High", "high", 24_000),
        new("Extra high", "xhigh", 48_000),
        new("Max", "max", 64_000),
    ];

    /// <summary>The <c>--permission-mode</c> value that is launch-only and locks the panel dropdown.</summary>
    public const string BypassPermissionModeValue = "bypassPermissions";

    /// <summary>App-default mode (Ask permissions) used when a profile carries no defaults.</summary>
    public static PermissionModeOption DefaultPermissionMode { get; } = AllPermissionModes[0];

    /// <summary>App-default model (Sonnet) used when a profile carries no defaults.</summary>
    public static ModelOption DefaultModel { get; } = Models[1];

    /// <summary>App-default effort (Medium) used when a profile carries no defaults.</summary>
    public static EffortOption DefaultEffort { get; } = Efforts[1];

    /// <summary>Resolves a CLI mode value (e.g. from a profile's defaults) to an option, or the app default.</summary>
    public static PermissionModeOption ResolvePermissionMode(string? value) =>
        AllPermissionModes.FirstOrDefault(mode => mode.Value == value) ?? DefaultPermissionMode;

    /// <summary>Resolves a CLI model value to an option, or the app default.</summary>
    public static ModelOption ResolveModel(string? value) =>
        Models.FirstOrDefault(model => model.Value == value) ?? DefaultModel;

    /// <summary>
    /// The model values offered as suggestions in the editable Claude model field — the CLI's own aliases
    /// (<c>opus</c>/<c>sonnet</c>/<c>haiku</c>), which it resolves to the current model itself, so this list needs
    /// no per-release upkeep. The field stays free text, so an operator can still pin a specific model or snapshot.
    /// </summary>
    public static IReadOnlyList<string> ClaudeModelSuggestions { get; } = [.. Models.Select(model => model.Value)];

    /// <summary>
    /// Turns what the operator typed or picked in the editable model field back into a <see cref="ModelOption"/>:
    /// a known alias keeps its friendly label, anything else (a pinned model/snapshot) becomes its own value, and
    /// a blank field falls back to the app default — so the <see cref="ModelOption"/> pipeline is unchanged whether
    /// the value came from a dropdown or free text.
    /// </summary>
    public static ModelOption ModelForValue(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? DefaultModel
            : Models.FirstOrDefault(model => model.Value == value.Trim()) ?? new ModelOption(value.Trim(), value.Trim());

    /// <summary>Resolves an effort value to an option, or the app default.</summary>
    public static EffortOption ResolveEffort(string? value) =>
        Efforts.FirstOrDefault(effort => effort.Value == value) ?? DefaultEffort;
}
