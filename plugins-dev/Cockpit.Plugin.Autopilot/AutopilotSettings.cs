using Cockpit.Plugins.Abstractions;

namespace Cockpit.Plugin.Autopilot;

/// <summary>
/// Autopilot's settings (decision #8): a global level plus per-project overrides. Every field resolves as
/// <em>project override → global value → built-in default</em>, so a project can tighten (or relax) a gate without
/// changing what the rest do. Persisted as loose keys in the plugin's per-plugin storage (GitStatus's shape); a
/// per-project override lives under a <c>project:{id}:</c> prefix, the same way a widget instance scopes its own
/// storage. The settings view edits the global level; the pipeline reads the effective value for the project a run
/// is working in. Read with an optional <c>projectId</c> (null = the global level).
/// </summary>
internal sealed class AutopilotSettings(IPluginStorage storage)
{
    private const string GraceKey = "graceMinutes";
    private const string MaxAttemptsKey = "maxSelfFixAttempts";
    private const string ProfileKey = "defaultProfileLabel";
    private const string WorkflowKey = "defaultWorkflow";
    private const string CommentKey = "commentLevel";
    private const string ScopingProfileKey = "scopingProfileLabel";
    private const string CeoProfileKey = "ceoProfileLabel";
    private const string CeoModelKey = "ceoModel";
    private const string AutonomyModeKey = "autonomyMode";
    private const string CostStrategyKey = "costStrategy";

    /// <summary>The CLI permission mode a self-driving run starts in (AC-152). Default: the agent works without asking before edits; the host still gates shell and egress.</summary>
    public const string DefaultAutonomyMode = "bypassPermissions";

    /// <summary>
    /// Raised when any setting changes, so a live surface (the workspace body, a running pipeline) picks it up
    /// without a restart. Deliberately used over <see cref="ICockpitHost.OnSettingsSaved"/>, which has no
    /// unsubscribe: a workspace body is transient, so it subscribes on build and unsubscribes when it goes away.
    /// </summary>
    public event Action? Changed;

    /// <summary>Minutes Autopilot waits on a blocked question before it parks the run (default 5).</summary>
    public int GraceTimerMinutes(string? projectId = null) => _ReadValue(projectId, GraceKey, 5);

    /// <summary>How many times a gate may self-fix and re-run before the run blocks (default 2).</summary>
    public int MaxSelfFixAttempts(string? projectId = null) => _ReadValue(projectId, MaxAttemptsKey, 2);

    /// <summary>The session profile label a run starts on, or null to let the New-session dialog decide.</summary>
    public string? DefaultProfileLabel(string? projectId = null) => _ReadString(projectId, ProfileKey);

    /// <summary>The workflow a run drives its execution with, or null for none set.</summary>
    public string? DefaultWorkflow(string? projectId = null) => _ReadString(projectId, WorkflowKey);

    /// <summary>The profile the pre-start scoping judgment is delegated to (AC-151); null skips scoping so a run starts unjudged.</summary>
    public string? ScopingProfileLabel(string? projectId = null) => _ReadString(projectId, ScopingProfileKey);

    /// <summary>The profile the CEO planning session runs on (AC-174) — a strong reasoning profile (Opus) by default in
    /// practice; null uses the app-default profile. Determines which agent/model the operator plans with.</summary>
    public string? CeoProfileLabel(string? projectId = null) => _ReadString(projectId, CeoProfileKey);

    /// <summary>The model the CEO planning session runs on where its profile offers a choice (AC-174, e.g. <c>opus</c>);
    /// null uses the profile's own default model.</summary>
    public string? CeoModel(string? projectId = null) => _ReadString(projectId, CeoModelKey);

    /// <summary>The CLI permission mode a self-driving run starts in (AC-152), defaulting to <see cref="DefaultAutonomyMode"/> when unset or blank.</summary>
    public string AutonomyMode(string? projectId = null) =>
        _ReadString(projectId, AutonomyModeKey) is { Length: > 0 } mode ? mode : DefaultAutonomyMode;

    /// <summary>How hard the CEO leans on cost when choosing a model per step (AC-174) — the operator's cost/quality steer, default <see cref="AutopilotCostStrategy.Balanced"/>.</summary>
    public AutopilotCostStrategy CostStrategy(string? projectId = null) => _ReadValue(projectId, CostStrategyKey, AutopilotCostStrategy.Balanced);

    public void SetCostStrategy(AutopilotCostStrategy strategy, string? projectId = null) => _Write(projectId, CostStrategyKey, strategy);

    /// <summary>The tracker stage a run's phase maps to (AC-154, decision #7), or null when the phase has no mapping — then only the session status moves. The name is the tracker's own vocabulary, applied by whichever tracker the run is on.</summary>
    public string? StageFor(AutopilotRunPhase phase, string? projectId = null) => _ReadString(projectId, $"stage.{phase}");

    public void SetStageFor(AutopilotRunPhase phase, string? stage, string? projectId = null) => _Write(projectId, $"stage.{phase}", stage);

    /// <summary>How much of a run is mirrored into tracker comments (default questions + milestones).</summary>
    public CommentLevel CommentMirroring(string? projectId = null) => _ReadValue(projectId, CommentKey, CommentLevel.QuestionsAndMilestones);

    /// <summary>Whether a done-gate is hard or skippable; security is hard by default, the rest skippable.</summary>
    public GateMode Gate(GateKind kind, string? projectId = null) => _ReadValue(projectId, _GateKey(kind), _DefaultGate(kind));

    public void SetGraceTimerMinutes(int minutes, string? projectId = null) => _Write(projectId, GraceKey, minutes);

    public void SetMaxSelfFixAttempts(int attempts, string? projectId = null) => _Write(projectId, MaxAttemptsKey, attempts);

    public void SetDefaultProfileLabel(string? label, string? projectId = null) => _Write(projectId, ProfileKey, label);

    public void SetDefaultWorkflow(string? workflow, string? projectId = null) => _Write(projectId, WorkflowKey, workflow);

    public void SetScopingProfileLabel(string? label, string? projectId = null) => _Write(projectId, ScopingProfileKey, label);

    public void SetCeoProfileLabel(string? label, string? projectId = null) => _Write(projectId, CeoProfileKey, label);

    public void SetCeoModel(string? model, string? projectId = null) => _Write(projectId, CeoModelKey, model);

    public void SetAutonomyMode(string? mode, string? projectId = null) => _Write(projectId, AutonomyModeKey, mode);

    public void SetCommentMirroring(CommentLevel level, string? projectId = null) => _Write(projectId, CommentKey, level);

    public void SetGate(GateKind kind, GateMode mode, string? projectId = null) => _Write(projectId, _GateKey(kind), mode);

    /// <summary>Drops a project's override of <paramref name="kind"/> so it follows the global setting again.</summary>
    public void ClearProjectGate(GateKind kind, string projectId)
    {
        // No storage Remove on the contract, so a null override is how "not set" is written — the read then falls
        // through to the global value.
        storage.Set<GateMode?>(_ProjectKey(projectId, _GateKey(kind)), null);
        Changed?.Invoke();
    }

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

    private static string _GateKey(GateKind kind) => $"gate.{kind}";

    private static GateMode _DefaultGate(GateKind kind) => kind == GateKind.Security ? GateMode.Hard : GateMode.Skip;

    private static string _ProjectKey(string projectId, string key) => $"project:{projectId}:{key}";
}
