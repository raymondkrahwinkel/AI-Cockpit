using Cockpit.Core.Sessions;
using Cockpit.Core.Sessions.Permissions;
using Cockpit.Core.Profiles;

namespace Cockpit.Core.Abstractions.Sessions;

/// <summary>
/// A live session: owns its <see cref="ISessionDriver"/>, pumps its events, and keeps state without touching a
/// UI thread. One lifetime owner regardless of consumer: the panel subscribes to <see cref="EventAppended"/>
/// and marshals itself; a headless consumer (a delegated task, #67) reads <see cref="EventsSince"/> instead.
/// </summary>
public interface ISessionRuntime : IAsyncDisposable
{
    /// <summary>Identifies this runtime in the <see cref="ISessionManager"/> register; stable for its lifetime.</summary>
    string Id { get; }

    /// <summary>The profile the session runs under, once started.</summary>
    SessionProfile? Profile { get; }

    /// <summary>
    /// What the running driver supports, so a consumer only offers controls the provider can back. Meaningful
    /// only after <see cref="StartAsync"/>: a driver settles capabilities while connecting (a local provider's
    /// tool support flips on only once its MCP tool session is up), so reading them before start always sees defaults.
    /// </summary>
    SessionCapabilities? Capabilities { get; }

    /// <summary>The process this session runs in, once its driver started one (#78) — what the resource meter weighs, along with everything that process spawns. Null for an HTTP-backed provider.</summary>
    int? ProcessId => null;

    /// <summary>
    /// The session's latest status, when its provider reports it (#45 D7) — passed straight from the driver so the
    /// header can poll one place. Null when the driver has no status feed (a local model, or Claude, whose TTY
    /// route carries limits through the statusline relay instead).
    /// </summary>
    SessionStatusFeed? CurrentStatus => null;

    /// <summary>
    /// The generic mid-session controls the running driver reports (#45 D4) — a plugin's model/effort, passed
    /// through without host-side vocabulary. Empty for a driver the host drives via typed members (Claude) or
    /// with nothing to switch. Meaningful only after start, like <see cref="Capabilities"/>.
    /// </summary>
    IReadOnlyList<SessionLiveOption> LiveOptions => [];

    /// <summary>True once <see cref="StartAsync"/> has brought a driver up and the event pump is running.</summary>
    bool IsRunning { get; }

    /// <summary>The assistant text of the most recently completed turn — a delegated task's result (#67).</summary>
    string? LastAssistantText { get; }

    /// <summary>
    /// Raised for every event the driver produces, in order, on the pump's own thread — never the UI thread.
    /// A subscriber that touches UI marshals for itself.
    /// </summary>
    event Action<SessionEvent>? EventAppended;

    /// <summary>
    /// The events from <paramref name="cursor"/> onwards, plus the cursor for next time — lets a late-attached
    /// or polling consumer (the orchestrator's <c>get_task_output</c>) catch up. Bounded log: a long session
    /// drops its oldest events, but <see cref="LastAssistantText"/>/<see cref="Capabilities"/> stay correct.
    /// </summary>
    (IReadOnlyList<SessionEvent> Events, int NextCursor) EventsSince(int cursor);

    /// <summary>
    /// Creates the driver for <paramref name="profile"/>'s provider, starts it, and pumps its events. Throws on
    /// failure. Worktree isolation (AC-85) is handed in via <paramref name="workingDirectory"/>; <paramref
    /// name="projectId"/> (AC-218) scopes the driver's MCP fan-out to that project's registry view.
    /// </summary>
    Task StartAsync(
        SessionProfile? profile,
        string? permissionMode = null,
        string? model = null,
        IReadOnlySet<string>? enabledMcpServerNames = null,
        string? workingDirectory = null,
        SessionResume? resume = null,
        IReadOnlyDictionary<string, string>? launchOptions = null,
        string? projectId = null,
        CancellationToken cancellationToken = default);

    Task SendUserMessageAsync(string text, IReadOnlyList<ImageAttachment>? images = null, CancellationToken cancellationToken = default);

    Task InterruptAsync(CancellationToken cancellationToken = default);

    /// <summary>Asks the provider to compact this conversation in place (AC-664). See <see cref="ISessionDriver.CompactContextAsync"/>.</summary>
    Task CompactContextAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    Task SetPermissionModeAsync(string mode, CancellationToken cancellationToken = default);

    Task SetModelAsync(string? model, CancellationToken cancellationToken = default);

    Task SetMaxThinkingTokensAsync(int maxThinkingTokens, CancellationToken cancellationToken = default);

    Task SetLiveOptionAsync(string key, string value, CancellationToken cancellationToken = default);

    Task SetAutoApproveToolsAsync(bool autoApprove, CancellationToken cancellationToken = default);

    /// <summary>Non-interactive delegated tool-gating (AC-79): tool calls are decided against the ceiling + allow-list rather than prompted. See <see cref="ISessionDriver.SetDelegatedToolGateAsync"/>.</summary>
    Task SetDelegatedToolGateAsync(string ceiling, IReadOnlyList<string> allowedTools, CancellationToken cancellationToken = default);

    Task RespondToPermissionAsync(string toolUseId, bool allow, CancellationToken cancellationToken = default);

    /// <summary>Resolves the decision carrying the operator's answers as well (AC-715). See <see cref="ISessionDriver.RespondToPermissionAsync(string, bool, string?, CancellationToken)"/>.</summary>
    Task RespondToPermissionAsync(string toolUseId, bool allow, string? answersJson, CancellationToken cancellationToken) =>
        RespondToPermissionAsync(toolUseId, allow, cancellationToken);

    Task AllowPermissionAlwaysAsync(string toolUseId, string toolName, string inputJson, PermissionRuleScope scope, CancellationToken cancellationToken = default);
}
