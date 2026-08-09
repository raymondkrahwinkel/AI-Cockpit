using Cockpit.Plugins.Abstractions;

namespace Cockpit.Plugin.Autopilot;

// Autopilot's settings: a global level plus per-project overrides. Every field resolves as *project override →
// global value → built-in default*, so a project can tighten (or relax) a setting without changing what the rest
// do. Persisted as loose keys in the plugin's per-plugin storage; a per-project override lives under a
// `project:{id}:` prefix. The settings view edits the global level; a run reads the effective value for the
// project it works in. Read with an optional `projectId` (null = the global level).
internal sealed class AutopilotSettings(IPluginStorage storage)
{
    private const string MaxAttemptsKey = "maxSelfFixAttempts";
    private const string MaxConsultsKey = "maxConsultsPerStep";
    private const string CeoProfileKey = "ceoProfileLabel";
    private const string CeoModelKey = "ceoModel";
    private const string CeoValidationProfileKey = "ceoValidationProfileLabel";
    private const string CeoValidationModelKey = "ceoValidationModel";
    private const string AutonomyModeKey = "autonomyMode";
    private const string CostStrategyKey = "costStrategy";
    private const string MaxConcurrentRunsKey = "maxConcurrentRuns";
    private const string ExecutableStagePrefix = "executableStage:";

    // What "a person has judged this executable" is called on each tracker Autopilot ships with (AC-345) — a stage on
    // YouTrack, and on GitHub Issues, which has none, a label. A tracker with no default here gates on nothing until
    // the operator names its stage; the settings view offers a box for every installed tracker so that is a choice
    // rather than a gap.
    private static readonly Dictionary<string, string> DefaultExecutableStages = new(StringComparer.OrdinalIgnoreCase)
    {
        ["youtrack"] = "Ready",
        ["github-issues"] = "ready",
    };

    // The CLI permission mode a self-driving run starts in (AC-152). Default `acceptEdits`, not `bypassPermissions`
    // (security review): an isolated step's confinement to its worktree must hold. Codex is genuinely
    // OS-sandboxed and maps both modes to `workspace-write`, so it is unaffected. Claude, though, has no OS sandbox —
    // its confinement to cwd is enforced by the permission system, and `bypassPermissions` (`--dangerously-skip-permissions`)
    // disables exactly that guard, letting an isolated Claude step write to an absolute path outside its worktree (the
    // real checkout, a dotfile) — reachable via prompt-injection from an untrusted issue in the step brief. `acceptEdits`
    // keeps that guard: in-worktree edits auto-apply, an out-of-worktree write prompts and, with no human, is denied. A
    // step that genuinely needs autonomous shell (build/test) belongs on Codex, which bashes confined; Claude stays edit-only.
    // An operator may still pick `bypassPermissions` per profile — a deliberate choice, the way Codex's danger-full-access is.
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

    // The profile the CEO's per-step validation runs on (AC-254) — a cheaper model than planning's, since validation is
    // the run's high-frequency, growing-context part. Same precedence as the AC-233 threshold resolver applied one
    // level further: project override → global override → the planning profile above, so a run that never sets a
    // validation override behaves exactly as it did before this split (one shared pair).
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

    // The permission mode that turns off a permission-based (Claude) provider's worktree confinement (AC-209):
    // coerced out of an autonomous run's effective mode. Public callers read the coerced value through `AutonomyMode`.
    private const string BypassAutonomyMode = "bypassPermissions";

    // The CLI permission mode a self-driving run starts in (AC-152), defaulting to `DefaultAutonomyMode` when
    // unset or blank. A stored `bypassPermissions` is coerced back to `DefaultAutonomyMode` (AC-209): a
    // legacy value from the AC-152 era — when bypass was briefly the default — would otherwise stick and disable exactly
    // the permission guard an isolated Claude step relies on (see `DefaultAutonomyMode`), so the host's
    // fail-closed isolation gate refuses every Claude step of the run. Bypass is therefore not a valid effective mode for
    // an autonomous, isolated run; a step that genuinely needs autonomous shell belongs on Codex, which is OS-sandboxed
    // and confines in either mode. The coercion covers every step type — impl and the code-/security-review gates all read
    // this one value — and a per-project override alike, so no persisted bypass (global or scoped) can silently block a run.
    // An operator who deliberately wants bypass on a specific Codex profile picks it per session, not through this run-wide setting.
    public string AutonomyMode(string? projectId = null) =>
        _ReadString(projectId, AutonomyModeKey) is { Length: > 0 } mode && !_IsBypassMode(mode) ? mode : DefaultAutonomyMode;

    private static bool _IsBypassMode(string mode) => string.Equals(mode, BypassAutonomyMode, StringComparison.OrdinalIgnoreCase);

    // How hard the CEO leans on cost when choosing a model per step (AC-174) — the operator's cost/quality steer, default `AutopilotCostStrategy.Balanced`.
    public AutopilotCostStrategy CostStrategy(string? projectId = null) => _ReadValue(projectId, CostStrategyKey, AutopilotCostStrategy.Balanced);

    // How many approved runs may execute at once (AC-174) — the rest wait in the queue. Default 1 (one
    // at a time); clamped to at least 1 so a stored 0 never stalls the queue.
    public int MaxConcurrentRuns(string? projectId = null) => Math.Max(1, _ReadValue(projectId, MaxConcurrentRunsKey, 1));

    public void SetMaxConcurrentRuns(int max, string? projectId = null) => _Write(projectId, MaxConcurrentRunsKey, Math.Max(1, max));

    // The stage on `trackerId` that means "a person judged this executable" — what the start gate
    // keys on (AC-345). Unset falls back to that tracker's default (`Ready` on YouTrack, the `ready` label on
    // GitHub Issues); stored blank means the operator turned the gate off for that tracker, and is honoured as such.
    // Global only, unlike the settings around it: the gate belongs to the tracker's own vocabulary, which does not
    // change per project, and a per-project level nothing reads would be a promise this class could not keep.
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
