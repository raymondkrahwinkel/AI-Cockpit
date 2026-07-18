using Cockpit.Core.Sessions;
using Cockpit.Core.Sessions.Permissions;
using Cockpit.Core.Profiles;

namespace Cockpit.Core.Abstractions.Sessions;

/// <summary>
/// Drives a single, persistent, multi-turn conversation with one provider and exposes it as a typed
/// event stream (#26) — the seam every provider sits behind: the Claude CLI (headless "stream-json"
/// mode), the built-in local-model drivers (Ollama, LM Studio), and any provider a plugin registers
/// (#45). <see cref="Capabilities"/> tells the UI which of the control operations below a given driver
/// actually supports, so it renders no dead controls for providers that lack (say) live permission
/// switching.
/// </summary>
public interface ISessionDriver : IAsyncDisposable
{
    /// <summary>What this driver supports, so the UI renders/hides controls per provider.</summary>
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
    /// Starts the underlying <c>claude</c> process under the given profile — spawning with
    /// <c>CLAUDE_CONFIG_DIR</c> set to <paramref name="profile"/>'s config directory (real
    /// user env, e.g. HOME/USERPROFILE, still inherited) and that profile's working directory
    /// pre-marked as trusted. Pass <see langword="null"/> to start without a profile (uses
    /// whatever the host process's own environment/config already provides). Must be called
    /// once before <see cref="SendUserMessageAsync"/> or <see cref="Events"/> produce anything.
    /// <paramref name="model"/>, when non-null/whitespace, is passed as <c>--model &lt;value&gt;</c>
    /// at launch (e.g. <c>"opus"</c>, <c>"sonnet"</c>, <c>"haiku"</c>). <paramref name="enabledMcpServerNames"/>
    /// is the per-session MCP-server selection from the New-session dialog (#44): when non-null, it narrows
    /// the shared MCP registry to just those names for this session, on top of the registry's own
    /// enabled/scope filtering; <see langword="null"/> keeps the pre-#44 behaviour of using the full registry.
    /// </summary>
    Task StartAsync(SessionProfile? profile = null, string? permissionMode = null, string? model = null, IReadOnlySet<string>? enabledMcpServerNames = null, string? workingDirectory = null, SessionResume? resume = null, IReadOnlyDictionary<string, string>? launchOptions = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends a user message as a single stream-json line on the CLI's stdin.
    /// The session stays open for further turns afterwards. When <paramref name="images"/> is
    /// non-empty, the message content becomes an array of blocks (one <c>text</c> block plus one
    /// <c>image</c> block per attachment) instead of a plain string — the shape verified against
    /// claude.exe 2.1.197. Text-only messages keep the plain-string content shape.
    /// </summary>
    Task SendUserMessageAsync(string text, IReadOnlyList<ImageAttachment>? images = null, CancellationToken cancellationToken = default);

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
    /// Live-switches the running session's thinking budget via a
    /// <c>control_request</c>/<c>set_max_thinking_tokens</c> request, carrying the budget as
    /// <c>maxThinkingTokens</c>. This is the live surface behind the per-session effort control:
    /// "effort" maps to a thinking-token budget because that is the only budget the control
    /// protocol can set mid-session. Verified against claude.exe 2.1.197 — the sibling subtypes
    /// <c>set_thinking</c>/<c>set_thinking_tokens</c>/<c>set_effort</c> are rejected as
    /// "Unsupported control request subtype".
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
    /// Allows the outstanding decision for <paramref name="toolUseId"/> and persists an always-allow
    /// rule for the session's profile so this tool call is auto-allowed from now on — both for the
    /// rest of this session (the coordinator short-circuits future prompts) and across restarts
    /// (the rule is saved per profile). <paramref name="scope"/> chooses whether the rule matches
    /// only the same input (<see cref="PermissionRuleScope.Exact"/>, keyed on the
    /// canonical form of <paramref name="proposedInputJson"/>) or every call to
    /// <paramref name="toolName"/> (<see cref="PermissionRuleScope.Wildcard"/>).
    /// </summary>
    Task AllowPermissionAlwaysAsync(
        string toolUseId,
        string toolName,
        string proposedInputJson,
        PermissionRuleScope scope,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The session's latest status, when the provider reports it (#45 D7) — how full the context window is and the
    /// usage windows it reports, each self-labelled. The host polls this and renders the header's bars from it, so
    /// a provider that can report limits fills them without the host owning any provider-specific status code or
    /// window vocabulary. Null for a driver whose provider reports none: the Claude CLI reports its limits through
    /// the TTY statusline relay instead of this seam, and a local model (Ollama, LM Studio) has no such windows —
    /// a header shows nothing rather than a made-up zero. A default property, so a driver with no status feed need
    /// not implement it.
    /// </summary>
    SessionStatusFeed? CurrentStatus => null;

    /// <summary>
    /// The controls this session can switch mid-conversation that the host renders generically (#45 D4) — a plugin
    /// provider's model and reasoning effort, each a per-turn override. Empty for a driver whose live controls the
    /// host already knows by name (the Claude CLI drives model/permission/effort through its own typed members
    /// above) or that has none (a local model). A driver reports these once its session is up, since the values can
    /// depend on what the provider listed at start. A default property, so a driver without a generic live surface
    /// need not implement it.
    /// </summary>
    IReadOnlyList<SessionLiveOption> LiveOptions => [];

    /// <summary>
    /// The live, ordered stream of typed transcript events for this session.
    /// A single async enumeration is supported; the stream completes when the
    /// underlying process exits.
    /// </summary>
    IAsyncEnumerable<SessionEvent> Events { get; }

    /// <summary>
    /// Turns per-tool-call approval prompts on or off for this session. When enabled, tool calls run without
    /// prompting (still surfaced as tool rows) — the "allow all tools" convenience for local models, whose
    /// every MCP call would otherwise need an Allow click. Default no-op: the Claude-CLI driver gates through
    /// its own permission modes instead, so only the local (OpenAI-compatible) driver honours this.
    /// </summary>
    Task SetAutoApproveToolsAsync(bool enabled, CancellationToken cancellationToken = default) => Task.CompletedTask;

    /// <summary>
    /// Puts this session into non-interactive delegated tool-gating (AC-79): a delegated session has no human to
    /// answer a permission prompt, so every MCP tool call is decided against <paramref name="ceiling"/> and
    /// <paramref name="allowedTools"/> instead of being put to anyone — a tool above the ceiling and not on the
    /// allow-list is denied with a reason (as its tool result), never left hanging. Default no-op: only the local
    /// (OpenAI-compatible) driver, whose tool calls would otherwise prompt, honours this; the Claude/Codex CLIs
    /// run non-interactively under their own permission mode. Not called for a profile whose "Auto-Approve tool
    /// calls" is on — that uses <see cref="SetAutoApproveToolsAsync"/> to allow everything.
    /// </summary>
    Task SetDelegatedToolGateAsync(string ceiling, IReadOnlyList<string> allowedTools, CancellationToken cancellationToken = default) => Task.CompletedTask;

    /// <summary>
    /// Switches one of the generic <see cref="LiveOptions"/> for the rest of this session (#45 D4) — the operator
    /// picked a new value in the live-control panel, keyed by the option's <see cref="SessionLiveOption.Key"/>. The
    /// driver applies it to its next turn. Default no-op: a driver with no generic live options has none to switch.
    /// </summary>
    Task SetLiveOptionAsync(string key, string value, CancellationToken cancellationToken = default) => Task.CompletedTask;
}
