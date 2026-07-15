namespace Cockpit.Plugins.Abstractions.Sessions;

/// <summary>
/// Drives a single, persistent, multi-turn conversation with a plugin-registered provider and exposes it as
/// a typed event stream (#45) — the narrow, plugin-facing analogue of <c>Cockpit.Core.Abstractions.Sessions.ISessionDriver</c>.
/// Deliberately trimmed to what a third-party HTTP provider can support: no Claude-CLI-only live controls
/// (permission-mode/model/thinking-budget switch, always-allow rule persistence). The host's driver adapter
/// wraps an implementation of this interface to satisfy the real <c>ISessionDriver</c> contract, no-opping
/// the members this interface has no equivalent for.
/// </summary>
public interface IPluginSessionDriver : IAsyncDisposable
{
    /// <summary>What this driver supports, so the UI renders/hides controls per provider.</summary>
    PluginSessionCapabilities Capabilities { get; }

    /// <summary>The provider's own session id, once known; <see langword="null"/> before that.</summary>
    string? SessionId { get; }

    /// <summary>
    /// Starts the underlying provider session. <paramref name="model"/>, when non-null/whitespace, selects
    /// the model to use for this session. Must be called once before <see cref="SendUserMessageAsync"/> or
    /// <see cref="Events"/> produce anything.
    /// </summary>
    Task StartAsync(string? model = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Starts the session with the working directory and resume target the cockpit already knows (#45 D5) —
    /// the surface the host's driver adapter actually calls. <paramref name="workingDirectory"/>, when
    /// non-null/whitespace, is the directory the session runs in, so a provider that needs one (a spawned CLI)
    /// takes it from here rather than asking the operator. <paramref name="resumeSessionId"/>, when
    /// non-null/whitespace, resumes that existing conversation instead of starting fresh. The default drops
    /// both and calls <see cref="StartAsync(string?, CancellationToken)"/>: a driver with no working directory
    /// or conversation history of its own (an HTTP provider) needs neither, so it need not override this. Added
    /// as a default method rather than by changing the signature above so no already-compiled plugin breaks.
    /// </summary>
    Task StartAsync(string? model, string? workingDirectory, string? resumeSessionId, CancellationToken cancellationToken) =>
        StartAsync(model, cancellationToken);

    /// <summary>Sends a user message; the session stays open for further turns afterwards.</summary>
    Task SendUserMessageAsync(string text, CancellationToken cancellationToken = default);

    /// <summary>Interrupts the current in-flight turn, if any.</summary>
    Task InterruptAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves an outstanding <see cref="PluginPermissionRequested"/> decision — the operator's allow/deny
    /// for a pending tool call, correlated on <paramref name="toolUseId"/>.
    /// </summary>
    Task RespondToPermissionAsync(string toolUseId, bool allow, CancellationToken cancellationToken = default);

    /// <summary>The live, ordered stream of typed transcript events for this session.</summary>
    IAsyncEnumerable<PluginSessionEvent> Events { get; }

    /// <summary>
    /// Turns per-tool-call approval prompts on or off for this session. Default no-op: a driver with no
    /// tool source of its own has nothing to gate.
    /// </summary>
    Task SetAutoApproveToolsAsync(bool enabled, CancellationToken cancellationToken = default) => Task.CompletedTask;
}
