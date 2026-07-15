using Cockpit.Core.Sessions;
using Cockpit.Core.Sessions.Permissions;
using Cockpit.Core.Profiles;

namespace Cockpit.Core.Abstractions.Sessions;

/// <summary>
/// A live session: owns its <see cref="ISessionDriver"/>, pumps the driver's event stream, and keeps the
/// state a consumer needs (status, capabilities, the events so far, the last assistant reply) — all without
/// touching a UI thread. It is the one place a session's lifetime lives, whichever kind of consumer is
/// watching it: the interactive panel subscribes to <see cref="EventAppended"/> and marshals to the UI
/// thread itself, while a headless consumer (a delegated task, #67) reads <see cref="EventsSince"/> and
/// <see cref="LastAssistantText"/> and never marshals anything.
/// </summary>
public interface ISessionRuntime : IAsyncDisposable
{
    /// <summary>Identifies this runtime in the <see cref="ISessionManager"/> register; stable for its lifetime.</summary>
    string Id { get; }

    /// <summary>The profile the session runs under, once started.</summary>
    SessionProfile? Profile { get; }

    /// <summary>
    /// What the running driver supports, so a consumer only offers controls the provider can back. Meaningful
    /// only after <see cref="StartAsync"/>: a driver settles its capabilities while connecting (a local
    /// provider's tool support flips on only once its MCP tool session is up), so reading them before start
    /// would always see the pre-start defaults.
    /// </summary>
    SessionCapabilities? Capabilities { get; }

    /// <summary>The process this session runs in, once its driver started one (#78) — what the resource meter weighs, along with everything that process spawns. Null for an HTTP-backed provider.</summary>
    int? ProcessId => null;

    /// <summary>
    /// The session's latest limits, when its provider reports them (#45 D7) — passed straight from the driver so
    /// the header can poll one place. Null when the driver has no limits feed (a local model, or Claude, whose
    /// TTY route carries limits through the statusline relay instead).
    /// </summary>
    SessionLimits? CurrentLimits => null;

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
    /// The events produced from <paramref name="cursor"/> onwards, plus the cursor to pass next time. Lets a
    /// consumer that attached late (or that polls, as the orchestrator's <c>get_task_output</c> does) catch up
    /// without missing what happened before it subscribed. The log is bounded, so a very long session drops its
    /// oldest events — <see cref="LastAssistantText"/> and <see cref="Capabilities"/> stay correct regardless.
    /// </summary>
    (IReadOnlyList<SessionEvent> Events, int NextCursor) EventsSince(int cursor);

    /// <summary>
    /// Creates the driver for <paramref name="profile"/>'s provider, starts it, and begins pumping its events.
    /// Throws if the driver cannot be created or started — the caller decides how to surface that.
    /// </summary>
    Task StartAsync(
        SessionProfile? profile,
        string? permissionMode = null,
        string? model = null,
        IReadOnlySet<string>? enabledMcpServerNames = null,
        string? workingDirectory = null,
        SessionResume? resume = null,
        IReadOnlyDictionary<string, string>? launchOptions = null,
        CancellationToken cancellationToken = default);

    Task SendUserMessageAsync(string text, IReadOnlyList<ImageAttachment>? images = null, CancellationToken cancellationToken = default);

    Task InterruptAsync(CancellationToken cancellationToken = default);

    Task SetPermissionModeAsync(string mode, CancellationToken cancellationToken = default);

    Task SetModelAsync(string? model, CancellationToken cancellationToken = default);

    Task SetMaxThinkingTokensAsync(int maxThinkingTokens, CancellationToken cancellationToken = default);

    Task SetAutoApproveToolsAsync(bool autoApprove, CancellationToken cancellationToken = default);

    Task RespondToPermissionAsync(string toolUseId, bool allow, CancellationToken cancellationToken = default);

    Task AllowPermissionAlwaysAsync(string toolUseId, string toolName, string inputJson, PermissionRuleScope scope, CancellationToken cancellationToken = default);
}
