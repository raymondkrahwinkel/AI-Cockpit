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
    /// The OS process this session runs in, when the provider spawns one (#78, D10) — what the host's resource
    /// meter measures, together with everything that process spawned. The default is <see langword="null"/>: a
    /// provider that is an HTTP call rather than a local process has nothing to weigh, and a value the host would
    /// have to treat as a real pid. A default property, so no already-compiled plugin breaks.
    /// </summary>
    int? ProcessId => null;

    /// <summary>
    /// Starts the underlying provider session. <paramref name="model"/>, when non-null/whitespace, selects
    /// the model to use for this session. Must be called once before <see cref="SendUserMessageAsync"/> or
    /// <see cref="Events"/> produce anything.
    /// </summary>
    Task StartAsync(string? model = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Starts the session with the working directory, resume target, per-session launch options and MCP servers
    /// the cockpit knows (#45 D5, #44) — the surface the host's driver adapter actually calls.
    /// <paramref name="workingDirectory"/>, when non-null/whitespace, is the directory the session runs in, so a
    /// provider that needs one (a spawned CLI) takes it from here rather than asking the operator.
    /// <paramref name="resumeSessionId"/>, when non-null/whitespace, resumes that existing conversation instead of
    /// starting fresh. <paramref name="options"/> carries the operator's answers to this provider's declared
    /// <see cref="SessionProviderRegistration.Options"/> (sandbox, model, …), keyed by each option's
    /// <see cref="PluginSessionLaunchOption.Key"/>. <paramref name="mcpServers"/> are the endpoints the host
    /// resolved from its shared registry for this session (#26); a provider that hosts tools of its own (an agent
    /// CLI) exposes them, one that has no tool source ignores them. The default drops all of these and calls
    /// <see cref="StartAsync(string?, CancellationToken)"/>: a driver with no working directory, history, launch
    /// options or MCP tool source of its own (an HTTP provider) needs none, so it need not override this. A
    /// default method rather than a signature change on the abstract member above, so no already-compiled plugin
    /// breaks.
    /// </summary>
    Task StartAsync(string? model, string? workingDirectory, string? resumeSessionId, IReadOnlyDictionary<string, string>? options, IReadOnlyList<PluginMcpServer>? mcpServers, CancellationToken cancellationToken) =>
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

    /// <summary>
    /// Allows the outstanding decision for <paramref name="toolUseId"/> <em>and</em> stops prompting for the like
    /// of it for the rest of this session (D4) — the operator's "allow always". A provider that can say this to
    /// its agent (Codex's <c>acceptForSession</c>) overrides this; the default falls back to a one-time allow, so
    /// a driver that cannot persist the decision still resolves the prompt. The rule is session-scoped only — the
    /// narrow surface has no profile-rule vocabulary, so cross-restart persistence stays a host/Claude concern.
    /// A default method, so no already-compiled plugin breaks.
    /// </summary>
    Task AllowPermissionAlwaysAsync(string toolUseId, CancellationToken cancellationToken = default) =>
        RespondToPermissionAsync(toolUseId, allow: true, cancellationToken);

    /// <summary>The live, ordered stream of typed transcript events for this session.</summary>
    IAsyncEnumerable<PluginSessionEvent> Events { get; }

    /// <summary>
    /// Turns per-tool-call approval prompts on or off for this session. Default no-op: a driver with no
    /// tool source of its own has nothing to gate.
    /// </summary>
    Task SetAutoApproveToolsAsync(bool enabled, CancellationToken cancellationToken = default) => Task.CompletedTask;
}
