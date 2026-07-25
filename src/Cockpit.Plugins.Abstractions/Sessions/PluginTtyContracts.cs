namespace Cockpit.Plugins.Abstractions.Sessions;

/// <summary>
/// A CLI a plugin can run as the real interactive TUI in one of the cockpit's panes. It answers exactly one
/// question — <em>how do I start this program?</em> — and knows nothing about pseudo consoles, panes or
/// terminals; the host owns all of that.
/// <para>
/// Deliberately far smaller than <see cref="IPluginSessionDriver"/>: a pty has no approvals, no model switching,
/// no events and no thinking budget. It has a program, arguments, an environment and a window size. A provider
/// offers whichever of the two it can — a local model has no TUI, a TUI-only agent has no headless driver, and
/// Claude and Codex have both.
/// </para>
/// </summary>
public interface IPluginTtyProvider
{
    /// <summary>
    /// Composes the launch for one session: resolves the executable and builds its command line from
    /// <paramref name="context"/>'s options, writing any files the session needs and naming them in the spec so
    /// the host can clean them up when the session ends.
    /// </summary>
    PluginTtyLaunchSpec BuildLaunch(PluginTtyLaunchContext context);
}

/// <summary>What the host tells a provider about the session it is about to start.</summary>
/// <param name="ConfigJson">The profile's own configuration for this provider, in whatever shape the plugin defined.</param>
/// <param name="Options">
/// The start defaults the operator chose, keyed by the <see cref="PluginTtyLaunchOption.Key"/>s this provider
/// declared. The host does not know what they mean — it renders what the provider asks for and hands back what
/// was picked, which is what keeps Claude's vocabulary (permission modes, effort) out of everyone else's CLI.
/// </param>
/// <param name="WorkingDirectory">Absolute path the session runs in, already resolved by the host.</param>
/// <param name="Resume">Which earlier conversation to pick up, or <see langword="null"/> for a fresh one.</param>
/// <param name="BaseEnvironment">
/// The environment the child will start from, already scrubbed by the host. Read it to compose an overlay that
/// extends an inherited value; it is not yours to return.
/// </param>
public sealed record PluginTtyLaunchContext(
    string ConfigJson,
    IReadOnlyDictionary<string, string> Options,
    string WorkingDirectory,
    PluginTtyResume? Resume,
    IReadOnlyDictionary<string, string> BaseEnvironment)
{
    /// <summary>
    /// The MCP servers the host resolved from its shared registry for this session (#26), so a provider that hosts
    /// tools of its own can fan them into the TUI (Claude writes them to a <c>--mcp-config</c>). Empty when none
    /// apply. Init-only so an already-compiled plugin keeps its old constructor and simply ignores them.
    /// </summary>
    public IReadOnlyList<PluginMcpServer> McpServers { get; init; } = [];

    /// <summary>
    /// The system prompt to append when the operator enabled the orchestrator (#67), so this TUI session may hand
    /// work to another profile — or <see langword="null"/> when delegation is off. The host owns the prompt text and
    /// whether it applies; the provider only appends it. Init-only, so an already-compiled plugin ignores it.
    /// </summary>
    public string? DelegationSystemPrompt { get; init; }
}

/// <summary>Pick up an earlier conversation: the most recent one, or a specific one by the id the CLI gave it.</summary>
/// <param name="SessionId">The conversation to resume; <see langword="null"/> means the most recent one.</param>
public sealed record PluginTtyResume(string? SessionId);

/// <summary>
/// How to start the provider's CLI.
/// </summary>
/// <param name="ExecutablePath">The program to run. The provider resolves it: only it knows where its CLI lives.</param>
/// <param name="Arguments">Launch-only start defaults, in the provider's own CLI syntax.</param>
/// <param name="EnvironmentOverlay">
/// Laid over the host's base environment, never in place of it. A <see langword="null"/> value removes a
/// variable. Variables the host controls — the markers of the agent session the cockpit was launched from, the
/// host terminal's identity, any Anthropic credential — cannot be set here: they are stripped for the operator's
/// safety, and a scrub a provider could opt out of is not a scrub. Removing one is allowed; the attempt to set
/// one is ignored and logged by name.
/// </param>
/// <param name="WorkingDirectory">Absolute path the pty child runs in.</param>
/// <param name="SessionScopedFiles">Files written for this one session. The host deletes them when it ends.</param>
public sealed record PluginTtyLaunchSpec(
    string ExecutablePath,
    IReadOnlyList<string> Arguments,
    IReadOnlyDictionary<string, string?> EnvironmentOverlay,
    string WorkingDirectory,
    IReadOnlyList<string> SessionScopedFiles)
{
    /// <summary>
    /// The status/limits snapshot file the provider's CLI writes to — Claude's statusline relay is the only
    /// machine-readable route to its context/usage limits — which the host polls to fill the session header's
    /// limit bars. <see langword="null"/> when the provider surfaces no limits this way (Codex's TUI has no such
    /// file). Init-only, so an already-compiled plugin that never sets it keeps reporting none.
    /// </summary>
    public string? StatusFile { get; init; }
}

/// <summary>
/// A start default the provider wants the New-session dialog to ask about — Claude has a permission mode and an
/// effort level, Codex has a sandbox. The provider declares them; the host renders them and hands the answers
/// back in <see cref="PluginTtyLaunchContext.Options"/>. The host never learns what any of them mean, which is
/// the only way a dialog can serve providers it has never heard of.
/// </summary>
/// <param name="Key">How the answer comes back in <see cref="PluginTtyLaunchContext.Options"/>.</param>
/// <param name="Label">What the operator reads.</param>
/// <param name="Choices">The values on offer. Empty means free text.</param>
/// <param name="DefaultValue">Pre-selected, or <see langword="null"/> to leave the option unset (the CLI's own default then applies).</param>
public sealed record PluginTtyLaunchOption(
    string Key,
    string Label,
    IReadOnlyList<string> Choices,
    string? DefaultValue = null)
{
    /// <summary>
    /// A friendly label per <see cref="Choices"/> value the operator reads instead of the raw value — how Claude
    /// shows "Ask permissions" for the CLI's <c>default</c> permission mode. Keyed by value; a value with no entry
    /// falls back to showing itself, and the value handed back in <see cref="PluginTtyLaunchContext.Options"/> is
    /// always the raw <see cref="Choices"/> entry, never the label. <see langword="null"/> (the default) means the
    /// provider wants none, so every value renders as itself — the current behaviour. Init-only rather than a
    /// primary-ctor parameter so adding it does not change the record's constructor signature; an already-compiled
    /// plugin keeps constructing this the old way and simply reports no labels.
    /// </summary>
    public IReadOnlyDictionary<string, string>? ChoiceLabels { get; init; }
}

/// <summary>
/// What a plugin hands <c>ICockpitHost.AddTtyProvider</c> to offer its CLI as a TTY session.
/// </summary>
/// <param name="ProviderId">
/// The same id the plugin registers its session provider under, when it has one. A profile names a provider;
/// what that provider can do — a headless driver, a TUI, or both — is what it registered.
/// </param>
/// <param name="DisplayName">Shown wherever the operator picks a provider.</param>
/// <param name="CreateProvider">Builds the provider from the container, once, when the host first needs it.</param>
/// <param name="Options">The start defaults this provider wants asked about; empty when it wants none.</param>
public sealed record TtyProviderRegistration(
    string ProviderId,
    string DisplayName,
    Func<IServiceProvider, IPluginTtyProvider> CreateProvider,
    IReadOnlyList<PluginTtyLaunchOption> Options)
{
    /// <summary>
    /// An optional way to refresh <see cref="Options"/> with live values when the New-session dialog opens for a
    /// profile under this provider — the TTY mirror of <see cref="SessionProviderRegistration.ResolveOptionsAsync"/>,
    /// so Codex fills its Model choices from the app-server's <c>model/list</c> in TTY mode too. The argument is the
    /// profile's opaque config JSON; the result replaces the declared options for that dialog. The dialog renders
    /// the declared <see cref="Options"/> first and calls this in the background, so opening is never blocked; on
    /// <see langword="null"/>, a timeout, or any failure it keeps the declared options. Init-only rather than a
    /// primary-ctor parameter so adding it does not change the record's constructor signature — an already-compiled
    /// plugin keeps constructing this the old way and simply reports no dynamic options.
    /// </summary>
    public Func<string, CancellationToken, Task<IReadOnlyList<PluginTtyLaunchOption>>>? ResolveOptionsAsync { get; init; }

    /// <summary>
    /// What sessions under this provider can run out of (AC-229) — a context window, a rolling cap, whatever it
    /// measures — so the host can warn about it and offer to resume against it. Empty (the default) when the
    /// provider measures nothing, and then no pill, no warning and no setting appears for it. Init-only, so an
    /// already-compiled plugin keeps its constructor and simply declares none.
    /// </summary>
    public IReadOnlyList<PluginUsageSignal> UsageSignals { get; init; } = [];

    /// <summary>
    /// Turns the contents of the session's status snapshot file into readings for the signals declared above.
    /// <para>
    /// The host owns the polling — it knows about files, timers and when a session is alive — and the provider
    /// owns the meaning, because the shape of that file is the provider's business and has moved between versions
    /// before. Handed the whole file as text; returns a reading per signal it could make out, and an empty list
    /// for a snapshot it cannot use. It must not throw: a half-written file caught mid-flush is ordinary, and the
    /// next poll brings a whole one.
    /// </para>
    /// <see langword="null"/> (the default) when this provider writes no such file, and the host then polls
    /// nothing for it. Init-only for the same reason as everything else here.
    /// </summary>
    public Func<string, IReadOnlyList<PluginUsageReading>>? ReadUsage { get; init; }

    /// <summary>
    /// Builds the provider's transcript reader for the host's read-aloud (#35b) and status (#39) — the piece that
    /// tails this provider's own on-disk conversation record, keeping the host free of any transcript format. The
    /// host resolves this once from the container and dispatches to it for a session under this provider.
    /// <see langword="null"/> (the default) when the provider records no tailable transcript, and the host offers
    /// no read-aloud/status-from-transcript for it. Init-only so adding it does not change the constructor
    /// signature — an already-compiled plugin keeps constructing this the old way and simply reports no reader.
    /// </summary>
    public Func<IServiceProvider, IPluginTranscriptReader>? CreateTranscriptReader { get; init; }

    /// <summary>
    /// Answers whether a profile under this provider is logged in, from its opaque <c>ConfigJson</c> — the host
    /// gates a session start and shows the login prompt generically without knowing what "logged in" means for any
    /// CLI. Existence-only by contract (Iron Law #8 — never read a credential's contents). <see langword="null"/>
    /// (the default) when the provider has no login concept, and the host treats such a profile as always ready.
    /// Init-only, so an already-compiled plugin keeps its constructor and simply reports no login gate.
    /// </summary>
    public Func<string, bool>? IsLoggedIn { get; init; }

    /// <summary>
    /// Discovers profiles already configured on this machine for this provider, offered to the host at startup so
    /// a fresh install adopts an existing login instead of the operator recreating it. The provider owns the
    /// discovery (only it knows where its CLI keeps state); the host labels and mints what it reports.
    /// <see langword="null"/> (the default) when the provider self-detects nothing. Init-only, so an
    /// already-compiled plugin keeps its constructor and simply contributes no auto-detected profiles.
    /// </summary>
    public Func<IReadOnlyList<PluginDetectedProfile>>? DetectProfiles { get; init; }
}
