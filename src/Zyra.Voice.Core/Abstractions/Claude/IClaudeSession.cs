using Zyra.Voice.Core.Claude;

namespace Zyra.Voice.Core.Abstractions.Claude;

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
    /// Starts the underlying <c>claude</c> process. Must be called once before
    /// <see cref="SendUserMessageAsync"/> or <see cref="Events"/> produce anything.
    /// </summary>
    Task StartAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends a user message as a single stream-json line on the CLI's stdin.
    /// The session stays open for further turns afterwards.
    /// </summary>
    Task SendUserMessageAsync(string text, CancellationToken cancellationToken = default);

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
