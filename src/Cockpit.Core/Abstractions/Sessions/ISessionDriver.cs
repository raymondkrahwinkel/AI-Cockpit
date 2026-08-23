using Cockpit.Core.Sessions;
using Cockpit.Core.Sessions.Permissions;
using Cockpit.Core.Profiles;

namespace Cockpit.Core.Abstractions.Sessions;

/// <summary>
/// Drives a single, persistent, multi-turn conversation with one provider and exposes it as a typed event
/// stream (#26) — the seam every provider sits behind: the Claude CLI, built-in local-model drivers, and any
/// plugin provider (#45). <see cref="Capabilities"/> tells the UI which operations a driver actually supports.
/// </summary>
public interface ISessionDriver : IAsyncDisposable
{
    /// <summary>
    /// What this driver supports, so the UI renders/hides controls per provider.
    /// </summary>
    SessionCapabilities Capabilities { get; }

    /// <summary>
    /// The process this session runs in, when it has one (#78) — what the resource meter measures, together with
    /// everything that process spawned. Null for a provider that is an HTTP call rather than a process (Ollama,
    /// LM Studio): there is nothing local to weigh, and reporting zero would be a claim, not a measurement.
    /// </summary>
    int? ProcessId => null;

    /// <summary>
    /// The CLI session id once reported by the <c>system/init</c> event, or <see langword="null"/> before that.
    /// </summary>
    string? SessionId { get; }

    /// <summary>
    /// The profile the running session was started under, once <see cref="StartAsync"/> has
    /// been called with one; <see langword="null"/> before start or when started profile-less
    /// (falls back to whatever environment/config the host process already has).
    /// </summary>
    SessionProfile? Profile { get; }

    /// <summary>
    /// Starts the underlying <c>claude</c> process under the profile (config dir, trusted working directory);
    /// <see langword="null"/> starts profile-less. Must precede <see cref="SendUserMessageAsync"/>/<see
    /// cref="Events"/>; <paramref name="enabledMcpServerNames"/> narrows the MCP registry (#44), <paramref name="projectId"/> (AC-218) scopes it per-project.
    /// </summary>
    Task StartAsync(SessionProfile? profile = null, string? permissionMode = null, string? model = null, IReadOnlySet<string>? enabledMcpServerNames = null, string? workingDirectory = null, SessionResume? resume = null, IReadOnlyDictionary<string, string>? launchOptions = null, string? projectId = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends a user message as a single stream-json line on stdin; the session stays open for further turns.
    /// When <paramref name="images"/> is non-empty, content becomes an array of blocks (verified against
    /// claude.exe 2.1.197) instead of a plain string; text-only messages keep the plain-string shape.
    /// </summary>
    Task SendUserMessageAsync(string text, IReadOnlyList<ImageAttachment>? images = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Asks the provider to compact this conversation in place (AC-664) — summarise what has been said so far
    /// and carry on as the same conversation, instead of starting a fresh one and losing the transcript. Only
    /// meaningful when <see cref="SessionCapabilities.SupportsContextCompaction"/>; default is a no-op.
    /// </summary>
    Task CompactContextAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    /// <summary>
    /// Live-switches the running session's permission mode via an Agent SDK control-protocol
    /// request (<c>control_request</c>/<c>set_permission_mode</c> over stdin). Verified end-to-end
    /// against claude.exe 2.1.197 — the request returns <c>control_response success</c>.
    /// </summary>
    Task SetPermissionModeAsync(string mode, CancellationToken cancellationToken = default);

    /// <summary>
    /// Live-switches the running session's model via a <c>control_request</c>/<c>set_model</c>
    /// request. Verified against claude.exe 2.1.197 (returns <c>control_response success</c>).
    /// </summary>
    Task SetModelAsync(string? model, CancellationToken cancellationToken = default);

    /// <summary>
    /// Live-switches the thinking budget via <c>control_request</c>/<c>set_max_thinking_tokens</c> — the surface
    /// behind per-session effort, since a thinking-token budget is the only one the control protocol can set
    /// mid-session. Verified against claude.exe 2.1.197 — sibling subtypes <c>set_thinking</c>/<c>set_effort</c> are rejected.
    /// </summary>
    Task SetMaxThinkingTokensAsync(int maxThinkingTokens, CancellationToken cancellationToken = default);

    /// <summary>
    /// Interrupts the current in-flight turn via a <c>control_request</c>/<c>interrupt</c>
    /// request. Verified against claude.exe 2.1.197 (returns <c>control_response success</c>).
    /// </summary>
    Task InterruptAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves an outstanding <see cref="PermissionRequested"/> decision by feeding the operator's
    /// allow/deny back to the CLI in-band through the cockpit's MCP permission server (see
    /// ClaudeCliSession / PermissionCoordinator), correlated on <c>tool_use_id</c>.
    /// </summary>
    Task RespondToPermissionAsync(string toolUseId, bool allow, CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves the decision carrying the operator's answers too (AC-715) — for a clarifying-question tool like
    /// <c>AskUserQuestion</c>, where allow/deny alone leaves it unanswered. Default drops <paramref
    /// name="answersJson"/> and falls back to the plain allow above.
    /// </summary>
    Task RespondToPermissionAsync(string toolUseId, bool allow, string? answersJson, CancellationToken cancellationToken) =>
        RespondToPermissionAsync(toolUseId, allow, cancellationToken);

    /// <summary>
    /// Allows the outstanding decision for <paramref name="toolUseId"/> and persists an always-allow rule for
    /// the profile, auto-allowing it this session and across restarts. <paramref name="scope"/> matches only
    /// this input (<see cref="PermissionRuleScope.Exact"/>) or every <paramref name="toolName"/> call (<see cref="PermissionRuleScope.Wildcard"/>).
    /// </summary>
    Task AllowPermissionAlwaysAsync(
        string toolUseId,
        string toolName,
        string proposedInputJson,
        PermissionRuleScope scope,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The session's latest status, when the provider reports it (#45 D7) — context fullness and self-labelled
    /// usage windows, polled to render the header's bars without host-owned status code. Null when the provider
    /// reports none (Claude uses the TTY statusline relay instead) — nothing shown, never a made-up zero.
    /// </summary>
    SessionStatusFeed? CurrentStatus => null;

    /// <summary>
    /// The controls this session can switch mid-conversation that the host renders generically (#45 D4) — a
    /// plugin's model/effort overrides. Empty for a driver the host knows by name (Claude, via typed members
    /// above) or with nothing to switch. Reported once the session is up, since values depend on provider start.
    /// </summary>
    IReadOnlyList<SessionLiveOption> LiveOptions => [];

    /// <summary>
    /// The live, ordered stream of typed transcript events for this session.
    /// A single async enumeration is supported; the stream completes when the
    /// underlying process exits.
    /// </summary>
    IAsyncEnumerable<SessionEvent> Events { get; }

    /// <summary>
    /// Turns per-tool-call approval prompts on or off for this session — the "allow all tools" convenience for
    /// local models, whose every MCP call would otherwise need a click. Default no-op; only the local
    /// (OpenAI-compatible) driver honours this, the Claude-CLI driver uses its own permission modes instead.
    /// </summary>
    Task SetAutoApproveToolsAsync(bool enabled, CancellationToken cancellationToken = default) => Task.CompletedTask;

    /// <summary>
    /// Puts this session into non-interactive delegated tool-gating (AC-79): a call is decided against
    /// <paramref name="ceiling"/>/<paramref name="allowedTools"/>, denied with a reason, never left hanging.
    /// Default no-op: only the local driver honours this; Claude/Codex gate under their own permission mode.
    /// </summary>
    Task SetDelegatedToolGateAsync(string ceiling, IReadOnlyList<string> allowedTools, CancellationToken cancellationToken = default) => Task.CompletedTask;

    /// <summary>
    /// Switches one of the generic <see cref="LiveOptions"/> for the rest of this session (#45 D4) — the
    /// operator picked a value in the live-control panel, keyed by <see cref="SessionLiveOption.Key"/>, applied
    /// on the driver's next turn. Default no-op: a driver with no generic live options has none to switch.
    /// </summary>
    Task SetLiveOptionAsync(string key, string value, CancellationToken cancellationToken = default) => Task.CompletedTask;
}
