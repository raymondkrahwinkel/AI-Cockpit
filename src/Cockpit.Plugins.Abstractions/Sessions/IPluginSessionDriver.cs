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

    /// <summary>
    /// Starts the session with, additionally, the profile's own environment variables to inject into the spawned
    /// process (AC-22) — already scrubbed host-side (a host-controlled key never crosses this boundary), so a
    /// driver applies them as-is, before its own variables: the driver's config-dir/credential rules keep the
    /// last word. The default drops them and calls the overload above: a provider that spawns nothing (an HTTP
    /// model) has no process to put them in, and an already-compiled plugin keeps loading. A driver that does
    /// spawn overrides this and reports <see cref="PluginSessionCapabilities.SupportsEnvVars"/> so the host
    /// offers the profile editor in the first place.
    /// </summary>
    Task StartAsync(string? model, string? workingDirectory, string? resumeSessionId, IReadOnlyDictionary<string, string>? options, IReadOnlyList<PluginMcpServer>? mcpServers, IReadOnlyDictionary<string, string>? environment, CancellationToken cancellationToken) =>
        StartAsync(model, workingDirectory, resumeSessionId, options, mcpServers, cancellationToken);

    /// <summary>Sends a user message; the session stays open for further turns afterwards.</summary>
    Task SendUserMessageAsync(string text, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends a user message with pasted/attached images (#64) — the surface the host's driver adapter calls when the
    /// operator attaches an image and the provider declared <see cref="PluginSessionCapabilities.SupportsVision"/>. The
    /// default drops the images and calls the text-only overload above: a provider that cannot send images (or an
    /// already-compiled plugin built before this member existed) simply ignores them, so nothing breaks. A driver that
    /// supports vision overrides this to carry the images to its provider, and reports <c>SupportsVision: true</c> so
    /// the host offers the attach affordance in the first place. A default method, so no already-compiled plugin breaks.
    /// </summary>
    Task SendUserMessageAsync(string text, IReadOnlyList<PluginImageAttachment>? images, CancellationToken cancellationToken) =>
        SendUserMessageAsync(text, cancellationToken);

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

    /// <summary>
    /// The provider's latest limits snapshot (#45 D7) — how full the context window is and how much of its usage
    /// windows are spent, which the host polls and renders as the session header's limit bars. The default is
    /// <see langword="null"/>: a provider that reports no usage (an HTTP model with no such feed) has none, and a
    /// header shows nothing rather than a made-up zero. A driver that receives usage updates from its provider
    /// (Codex's <c>thread/tokenUsage/updated</c> and <c>account/rateLimits/updated</c>) keeps the newest snapshot
    /// here. A default property polled off the event stream, so no already-compiled plugin breaks.
    /// </summary>
    PluginSessionStatus? Status => null;

    /// <summary>
    /// The controls this running session can switch mid-conversation (#45 D4) — Codex's model and reasoning
    /// effort, each a per-turn override the driver applies to the next turn it sends. The provider owns the whole
    /// vocabulary: it names each control (<see cref="PluginSessionLaunchOption.Key"/>), labels it, and offers the
    /// values, so the host renders them in a generic panel without knowing what any of them mean — the running-session
    /// mirror of <see cref="SessionProviderRegistration.Options"/>. The current value rides each option's
    /// <see cref="PluginSessionLaunchOption.DefaultValue"/> so the panel opens on what the session is actually using.
    /// The default is empty: a provider with nothing to switch live (an HTTP model) shows no panel. A driver reports
    /// these once its session is up (the values can depend on what the provider listed at start), the same moment the
    /// host reads <see cref="Capabilities"/>. A default property, so no already-compiled plugin breaks.
    /// </summary>
    IReadOnlyList<PluginSessionLaunchOption> LiveOptions => [];

    /// <summary>The live, ordered stream of typed transcript events for this session.</summary>
    IAsyncEnumerable<PluginSessionEvent> Events { get; }

    /// <summary>
    /// Turns per-tool-call approval prompts on or off for this session. Default no-op: a driver with no
    /// tool source of its own has nothing to gate.
    /// </summary>
    Task SetAutoApproveToolsAsync(bool enabled, CancellationToken cancellationToken = default) => Task.CompletedTask;

    /// <summary>
    /// Switches one of the <see cref="LiveOptions"/> for the rest of this session (#45 D4) — the operator picked a
    /// new value in the live-control panel. <paramref name="key"/> is the option's
    /// <see cref="PluginSessionLaunchOption.Key"/> and <paramref name="value"/> the chosen entry; the driver applies
    /// it to the next turn it sends (Codex carries model/effort as per-turn overrides on <c>turn/start</c>). Default
    /// no-op: a driver that declares no live options has none to switch, so it need not implement it.
    /// </summary>
    Task SetLiveOptionAsync(string key, string value, CancellationToken cancellationToken = default) => Task.CompletedTask;
}
