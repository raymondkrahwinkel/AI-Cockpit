using Cockpit.Core.Claude;
using Cockpit.Core.Profiles;

namespace Cockpit.Core.Abstractions.Claude;

/// <summary>
/// Drives a single, persistent, multi-turn <c>claude</c> CLI conversation
/// (headless "stream-json" mode) and exposes it as a typed event stream.
/// </summary>
public interface IClaudeSession : IAsyncDisposable
{
    /// <summary>
    /// The CLI session id once reported by the <c>system/init</c> event, or <see langword="null"/> before that.
    /// </summary>
    string? SessionId { get; }

    /// <summary>
    /// The profile the running session was started under, once <see cref="StartAsync"/> has
    /// been called with one; <see langword="null"/> before start or when started profile-less
    /// (falls back to whatever environment/config the host process already has).
    /// </summary>
    ClaudeProfile? Profile { get; }

    /// <summary>
    /// Starts the underlying <c>claude</c> process under the given profile — spawning with
    /// <c>CLAUDE_CONFIG_DIR</c> set to <paramref name="profile"/>'s config directory (real
    /// user env, e.g. HOME/USERPROFILE, still inherited) and that profile's working directory
    /// pre-marked as trusted. Pass <see langword="null"/> to start without a profile (uses
    /// whatever the host process's own environment/config already provides). Must be called
    /// once before <see cref="SendUserMessageAsync"/> or <see cref="Events"/> produce anything.
    /// <paramref name="model"/>, when non-null/whitespace, is passed as <c>--model &lt;value&gt;</c>
    /// at launch (e.g. <c>"opus"</c>, <c>"sonnet"</c>, <c>"haiku"</c>).
    /// </summary>
    Task StartAsync(ClaudeProfile? profile = null, string? permissionMode = null, string? model = null, CancellationToken cancellationToken = default);

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
    /// request (<c>control_request</c>/<c>set_permission_mode</c> over stdin). UNVERIFIED: the
    /// exact wire subtype/field names below are a best guess from the SDK's public
    /// <c>Query.setPermissionMode(mode)</c> surface — this sandbox has no logged-in <c>claude</c>
    /// CLI to confirm the request shape end-to-end against. Verify against a real session
    /// before relying on this.
    /// </summary>
    Task SetPermissionModeAsync(string mode, CancellationToken cancellationToken = default);

    /// <summary>
    /// Live-switches the running session's model via a <c>control_request</c>/<c>set_model</c>
    /// request. UNVERIFIED — see <see cref="SetPermissionModeAsync"/> remarks; same caveat
    /// applies to the request shape here.
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
    /// request. UNVERIFIED — see <see cref="SetPermissionModeAsync"/> remarks; same caveat
    /// applies to the request shape here.
    /// </summary>
    Task InterruptAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves an outstanding <see cref="PermissionRequested"/> decision.
    /// F-C1 note: the CLI process spawned by <see cref="Abstractions.Claude.IClaudeSession"/>
    /// implementations may not yet be wired to a live permission-prompt channel (see
    /// ClaudeCliSession remarks); until then this only updates local/UI-observable state.
    /// </summary>
    Task RespondToPermissionAsync(string toolUseId, bool allow, CancellationToken cancellationToken = default);

    /// <summary>
    /// The live, ordered stream of typed transcript events for this session.
    /// A single async enumeration is supported; the stream completes when the
    /// underlying process exits.
    /// </summary>
    IAsyncEnumerable<ClaudeSessionEvent> Events { get; }
}
