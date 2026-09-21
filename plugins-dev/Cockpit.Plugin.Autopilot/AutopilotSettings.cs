using Cockpit.Plugins.Abstractions;

namespace Cockpit.Plugin.Autopilot;

// Autopilot's settings: a global level plus per-project overrides. Every field resolves as *project override →
// global value → built-in default*, so a project can tighten (or relax) a setting without changing the rest.
// Persisted as loose keys, per-project under a `project:{id}:` prefix; the settings view edits the global level.
internal sealed class AutopilotSettings(IPluginStorage storage)
{
    private const string MaxAttemptsKey = "maxSelfFixAttempts";
    private const string MaxConsultsKey = "maxConsultsPerStep";
    private const string CeoProfileKey = "ceoProfileLabel";
    private const string CeoModelKey = "ceoModel";
    private const string CeoValidationProfileKey = "ceoValidationProfileLabel";
    private const string CeoValidationModelKey = "ceoValidationModel";
    private const string CeoCheckpointEveryKey = "ceoCheckpointEverySteps";
    private const string AutonomyModeKey = "autonomyMode";
    private const string CostStrategyKey = "costStrategy";
    private const string MaxConcurrentRunsKey = "maxConcurrentRuns";
    private const string ExecutableStagePrefix = "executableStage:";
    private const string EpicDirectToMainKey = "epicDirectToMain";
    private const string AcceptanceHeadingsKey = "acceptanceHeadings";
    private const string MergeModeKey = "mergeMode";
    private const string MergeBuildCommandKey = "mergeBuildCommand";
    private const string MergeBuildTimeoutKey = "mergeBuildTimeoutMinutes";
    private const string ChainUncleanRunToleranceKey = "chainUncleanRunTolerance";

    // How long the merge gate lets one build run (AC-1338) — a Release build of a whole solution outruns
    // GitCommandLine's two-minute default by a wide margin; the one named place for that number.
    public const int DefaultMergeBuildTimeoutMinutes = 20;

    // How many non-clean runs in a row (AC-347) an epic's chain rides through before it stops (AC-1340); the one
    // named place for that number. Zero: the first sub that needed a correction ends the chain.
    public const int DefaultChainUncleanRunTolerance = 0;

    // The one named place the merge gate's default lives (AC-1338, D1): a run that never chose waits for a go.
    public const AutopilotMergeMode DefaultMergeMode = AutopilotMergeMode.Explicit;

    // What the merge gate builds with, before asking for a go and again on the collection tip after landing (AC-1338).
    // EpicWorkflow §3 step 5 for this repository; a setting, so another repository names its own.
    public const string DefaultMergeBuildCommand = "dotnet build Cockpit.slnx -c Release -warnaserror";

    // The description-section heading(s) that count as "this ticket states its acceptance criteria" (AC-1339) —
    // policy text, not a value baked into the code, so a differently-worded grooming convention is a settings
    // change rather than a rollout everyone must pick up at once.
    private static readonly IReadOnlyList<string> DefaultAcceptanceHeadings = ["Acceptatiecriteria", "Acceptance criteria"];

    // What "a person has judged this executable" is called on each tracker Autopilot ships with (AC-345) — a stage on
    // YouTrack, and on GitHub Issues, which has none, a label. A tracker with no default here gates on nothing
    // until the operator names its stage; the settings view offers a box per tracker so that is a choice, not a gap.
    private static readonly Dictionary<string, string> DefaultExecutableStages = new(StringComparer.OrdinalIgnoreCase)
    {
        ["youtrack"] = "Ready",
        ["github-issues"] = "ready",
    };

    // The CLI permission mode a self-driving run starts in (AC-152). Default `acceptEdits`, not `bypassPermissions`
    // (security review): bypass disables the guard keeping an isolated Claude step confined to its worktree,
    // reachable via prompt-injection. An operator may still pick bypass per profile, a deliberate choice.
    public const string DefaultAutonomyMode = "acceptEdits";

    // Raised when any setting changes, so a live surface (the workspace body, a running pipeline) picks it up
    // without a restart. Deliberately used over `ICockpitHost.OnSettingsSaved`, which has no
    // unsubscribe: a workspace body is transient, so it subscribes on build and unsubscribes when it goes away.
    public event Action? Changed;

    // How many times a step may self-fix and re-run before the run blocks (default 2).
    public int MaxSelfFixAttempts(string? projectId = null) => _ReadValue(projectId, MaxAttemptsKey, 2);

    // How many times a single step's worker may consult its manager (the CEO) before the run falls back to the
    // operator (AC-201, default 3) — a loop-cap so a worker stuck asking in circles cannot bounce off the CEO forever.
    public int MaxConsultsPerStep(string? projectId = null) => _ReadValue(projectId, MaxConsultsKey, 3);

    public void SetMaxConsultsPerStep(int max, string? projectId = null) => _Write(projectId, MaxConsultsKey, max);

    // The profile the CEO planning session runs on (AC-174) — a strong reasoning profile (Opus) by default in
    // practice; null uses the app-default profile. Determines which agent/model the operator plans with.
    public string? CeoProfileLabel(string? projectId = null) => _ReadString(projectId, CeoProfileKey);

    // The model the CEO planning session runs on where its profile offers a choice (AC-174, e.g. `opus`);
    // null uses the profile's own default model.
    public string? CeoModel(string? projectId = null) => _ReadString(projectId, CeoModelKey);

    // The profile the CEO's per-step validation runs on (AC-254) — cheaper than planning's, since validation is
    // the run's high-frequency, growing-context part. Falls back to the planning profile so a run that never
    // sets a validation override behaves exactly as it did before this split (one shared pair).
    public string? CeoValidationProfileLabel(string? projectId = null) =>
        _ReadString(projectId, CeoValidationProfileKey) is { Length: > 0 } value ? value : CeoProfileLabel(projectId);

    // The model the CEO's validation session runs on (AC-254); falls back to the planning model on the same
    // project/global → planning precedence as `CeoValidationProfileLabel`.
    public string? CeoValidationModel(string? projectId = null) =>
        _ReadString(projectId, CeoValidationModelKey) is { Length: > 0 } value ? value : CeoModel(projectId);

    // The validation override as stored, without falling back to the planning pair — what the settings view shows so
    // "blank" reads as "follows planning" rather than pre-filling today's planning value into a field that would then
    // stop following it.
    public string? CeoValidationProfileLabelOverride(string? projectId = null) => _ReadString(projectId, CeoValidationProfileKey);

    public string? CeoValidationModelOverride(string? projectId = null) => _ReadString(projectId, CeoValidationModelKey);

    // AC-253: after how many validation turns the run replaces its validator CEO with a fresh session carrying only the
    // ledger of what it already judged. 0 never checkpoints — what makes a before/after measurable at all (AC-251) —
    // and a negative value reads as 0 rather than as a checkpoint on every turn.
    public int CeoCheckpointEverySteps(string? projectId = null) => Math.Max(0, _ReadValue(projectId, CeoCheckpointEveryKey, 3));

    public void SetCeoCheckpointEverySteps(int everySteps, string? projectId = null) =>
        _Write(projectId, CeoCheckpointEveryKey, Math.Max(0, everySteps));

    // The permission mode that turns off a permission-based (Claude) provider's worktree confinement (AC-209):
    // coerced out of an autonomous run's effective mode. Public callers read the coerced value through `AutonomyMode`.
    private const string BypassAutonomyMode = "bypassPermissions";

    // Defaults to `DefaultAutonomyMode` when unset or blank. A stored `bypassPermissions` is coerced back
    // (AC-209): a legacy value would otherwise disable the permission guard an isolated Claude step relies on.
    public string AutonomyMode(string? projectId = null) =>
        _ReadString(projectId, AutonomyModeKey) is { Length: > 0 } mode && !_IsBypassMode(mode) ? mode : DefaultAutonomyMode;

    private static bool _IsBypassMode(string mode) => string.Equals(mode, BypassAutonomyMode, StringComparison.OrdinalIgnoreCase);

    // How hard the CEO leans on cost when choosing a model per step (AC-174) — the operator's cost/quality steer, default `AutopilotCostStrategy.Balanced`.
    public AutopilotCostStrategy CostStrategy(string? projectId = null) => _ReadValue(projectId, CostStrategyKey, AutopilotCostStrategy.Balanced);

    // How many approved runs may execute at once (AC-174) — the rest wait in the queue. Default 1 (one
    // at a time); clamped to at least 1 so a stored 0 never stalls the queue.
    public int MaxConcurrentRuns(string? projectId = null) => Math.Max(1, _ReadValue(projectId, MaxConcurrentRunsKey, 1));

    public void SetMaxConcurrentRuns(int max, string? projectId = null) => _Write(projectId, MaxConcurrentRunsKey, Math.Max(1, max));

    // The stage on `trackerId` that means "a person judged this executable" — what the start gate keys on
    // (AC-345). Unset falls back to that tracker's default; stored blank means the gate is off for that tracker.
    // Global only: the gate belongs to the tracker's own vocabulary, which does not change per project.
    public string ExecutableStage(string trackerId) =>
        _ReadString(null, _ExecutableStageKey(trackerId))
        ?? DefaultExecutableStages.GetValueOrDefault(trackerId)
        ?? string.Empty;

    public void SetExecutableStage(string trackerId, string? stage) =>
        _Write(null, _ExecutableStageKey(trackerId), stage ?? string.Empty);

    // The tracker ids Autopilot ships a default executable stage for, so a settings view can offer them even
    // when their plugin is not installed on this machine.
    public static IReadOnlyCollection<string> TrackersWithADefaultStage => DefaultExecutableStages.Keys;

    private static string _ExecutableStageKey(string trackerId) => ExecutableStagePrefix + trackerId.ToLowerInvariant();

    // The operator's opt-out back to v1 (AC-1337, D2): false (default) puts an epic run's subs on their own
    // collection branch; true keeps every sub's PR going straight to main. Global only, like AutonomyMode.
    public bool EpicDirectToMain(string? projectId = null) => _ReadValue(projectId, EpicDirectToMainKey, false);

    public void SetEpicDirectToMain(bool directToMain, string? projectId = null) => _Write(projectId, EpicDirectToMainKey, directToMain);

    // The heading(s) AutopilotEpicRunner looks for to decide a Ready sub states its own acceptance criteria
    // (AC-1339). Global only, like AutonomyMode: the wording belongs to how tickets are groomed, not a
    // per-project choice. No settings panel yet — set through plugin storage directly.
    public IReadOnlyList<string> AcceptanceHeadings() =>
        storage.Get<List<string>>(AcceptanceHeadingsKey) is { Count: > 0 } configured ? configured : DefaultAcceptanceHeadings;

    public void SetAcceptanceHeadings(IReadOnlyList<string> headings) => _Write(null, AcceptanceHeadingsKey, headings);

    // The merge mode a run starts with (AC-1338, D1) — what the approval checkbox is pre-filled with, and what an
    // auto-submitted epic sub (AC-1339, no approval click) takes as its own. Resolves like every other field.
    public AutopilotMergeMode MergeMode(string? projectId = null) => _ReadValue(projectId, MergeModeKey, DefaultMergeMode);

    public void SetMergeMode(AutopilotMergeMode mode, string? projectId = null) => _Write(projectId, MergeModeKey, mode);

    // The build the merge gate runs (AC-1338); blank falls back to the default rather than to "no build".
    public string MergeBuildCommand(string? projectId = null) =>
        _ReadString(projectId, MergeBuildCommandKey) is { Length: > 0 } command ? command : DefaultMergeBuildCommand;

    public void SetMergeBuildCommand(string? command, string? projectId = null) => _Write(projectId, MergeBuildCommandKey, command);

    // The merge gate's build deadline in minutes (AC-1338); a stored zero or negative value reads as the default
    // rather than as a build that is killed at once.
    public int MergeBuildTimeoutMinutes(string? projectId = null) =>
        _ReadValue(projectId, MergeBuildTimeoutKey, DefaultMergeBuildTimeoutMinutes) is > 0 and var minutes ? minutes : DefaultMergeBuildTimeoutMinutes;

    public void SetMergeBuildTimeoutMinutes(int minutes, string? projectId = null) => _Write(projectId, MergeBuildTimeoutKey, minutes);

    // The chain's tolerance for non-clean runs in a row (AC-1340); a stored negative value reads as the default.
    // No settings panel yet, like AcceptanceHeadings — set through plugin storage directly.
    public int ChainUncleanRunTolerance(string? projectId = null) =>
        _ReadValue(projectId, ChainUncleanRunToleranceKey, DefaultChainUncleanRunTolerance) is >= 0 and var tolerance ? tolerance : DefaultChainUncleanRunTolerance;

    public void SetChainUncleanRunTolerance(int tolerance, string? projectId = null) => _Write(projectId, ChainUncleanRunToleranceKey, tolerance);

    public void SetMaxSelfFixAttempts(int attempts, string? projectId = null) => _Write(projectId, MaxAttemptsKey, attempts);

    public void SetCeoProfileLabel(string? label, string? projectId = null) => _Write(projectId, CeoProfileKey, label);

    public void SetCeoModel(string? model, string? projectId = null) => _Write(projectId, CeoModelKey, model);

    public void SetCeoValidationProfileLabel(string? label, string? projectId = null) => _Write(projectId, CeoValidationProfileKey, label);

    public void SetCeoValidationModel(string? model, string? projectId = null) => _Write(projectId, CeoValidationModelKey, model);

    public void SetAutonomyMode(string? mode, string? projectId = null) => _Write(projectId, AutonomyModeKey, mode);

    public void SetCostStrategy(AutopilotCostStrategy strategy, string? projectId = null) => _Write(projectId, CostStrategyKey, strategy);

    private TValue _ReadValue<TValue>(string? projectId, string key, TValue fallback) where TValue : struct
    {
        if (projectId is not null && storage.Get<TValue?>(_ProjectKey(projectId, key)) is { } scoped)
        {
            return scoped;
        }

        return storage.Get<TValue?>(key) ?? fallback;
    }

    private string? _ReadString(string? projectId, string key)
    {
        // A blank project override reads as "not set" so it falls through to the global value rather than blanking it.
        if (projectId is not null && storage.Get<string>(_ProjectKey(projectId, key)) is { Length: > 0 } scoped)
        {
            return scoped;
        }

        return storage.Get<string>(key);
    }

    private void _Write<TValue>(string? projectId, string key, TValue value)
    {
        storage.Set(projectId is null ? key : _ProjectKey(projectId, key), value);
        Changed?.Invoke();
    }

    private static string _ProjectKey(string projectId, string key) => $"project:{projectId}:{key}";
}
