using Cockpit.Plugins.Abstractions;

namespace Cockpit.Plugin.Autopilot;

/// <summary>
/// Autopilot's settings: a global level plus per-project overrides. Every field resolves as <em>project override →
/// global value → built-in default</em>, so a project can tighten (or relax) a setting without changing what the rest
/// do. Persisted as loose keys in the plugin's per-plugin storage; a per-project override lives under a
/// <c>project:{id}:</c> prefix. The settings view edits the global level; a run reads the effective value for the
/// project it works in. Read with an optional <c>projectId</c> (null = the global level).
/// </summary>
internal sealed class AutopilotSettings(IPluginStorage storage)
{
    private const string MaxAttemptsKey = "maxSelfFixAttempts";
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

    /// <summary>How many times a step may self-fix and re-run before the run blocks (default 2).</summary>
    public int MaxSelfFixAttempts(string? projectId = null) => _ReadValue(projectId, MaxAttemptsKey, 2);

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

    public void SetMaxSelfFixAttempts(int attempts, string? projectId = null) => _Write(projectId, MaxAttemptsKey, attempts);

    public void SetCeoProfileLabel(string? label, string? projectId = null) => _Write(projectId, CeoProfileKey, label);

    public void SetCeoModel(string? model, string? projectId = null) => _Write(projectId, CeoModelKey, model);

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
