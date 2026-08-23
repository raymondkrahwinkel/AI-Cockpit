namespace Cockpit.Plugins.Abstractions.Sessions;

/// <summary>
/// What a plugin-provided <see cref="IPluginSessionDriver"/> supports, so the host's session UI renders or hides
/// controls per provider instead of showing dead ones — the plugin-facing mirror of
/// <c>Cockpit.Core.Sessions.SessionCapabilities</c> (#45/#64).
/// </summary>
/// <remarks>
/// The host's driver adapter converts this to its core counterpart at the plugin boundary.
/// </remarks>
/// <param name="SupportsTools">
/// Whether the plugin driver has a tool source (native tools or an MCP loop of its own).
/// </param>
/// <param name="SupportsPermissions">
/// Whether the plugin driver knows Claude-style permission modes.
/// </param>
/// <param name="SupportsVision">
/// Whether this plugin's driver sends pasted image attachments to the model (#64). Defaults to
/// <see langword="false"/> for back-compat with existing 2-arg construction.
/// </param>
public sealed record PluginSessionCapabilities(
    bool SupportsTools,
    bool SupportsPermissions,
    bool SupportsVision = false)
{
    /// <summary>
    /// Whether this driver can switch the model mid-session (Fase 4 D4) — backed by
    /// <see cref="IPluginSessionDriver.SetLiveOptionAsync"/> with the <see cref="WellKnownPluginSessionOptions.Model"/>
    /// key. Defaults to <see langword="false"/>.
    /// </summary>
    public bool SupportsLiveModelSwitch { get; init; }

    /// <summary>
    /// Whether this driver can switch the Claude-style permission mode mid-session (Fase 4 D4) — backed by
    /// <see cref="IPluginSessionDriver.SetLiveOptionAsync"/> with the
    /// <see cref="WellKnownPluginSessionOptions.PermissionMode"/> key. Defaults to <see langword="false"/>, so a
    /// provider with no permission modes (an HTTP model, Codex's sandbox) never advertises it.
    /// </summary>
    public bool SupportsPermissionModeSwitch { get; init; }

    /// <summary>
    /// Whether this provider's sessions honour a profile's own environment variables at spawn (AC-22) — backed by
    /// the environment-carrying <see cref="IPluginSessionDriver.StartAsync(string?, string?, string?, IReadOnlyDictionary{string, string}?, IReadOnlyList{PluginMcpServer}?, IReadOnlyDictionary{string, string}?, CancellationToken)"/>
    /// overload. Gates the profile editor's env-var section; defaults to <see langword="false"/>.
    /// </summary>
    public bool SupportsEnvVars { get; init; }

    /// <summary>
    /// Whether this provider's own file-affecting tools stay within the session's working directory (AC-174) — the
    /// guarantee an isolated embedded run (Autopilot's worktree isolation) rests on.
    /// </summary>
    /// <remarks>
    /// The host reads this after start to refuse an isolate-in-worktree run on a non-confining provider. Defaults to
    /// <see langword="false"/>, so an unaware provider fails closed, not open.
    /// </remarks>
    public bool ConfinesFileAccessToWorkingDirectory { get; init; }

    /// <summary>
    /// Whether the confinement vouched by <see cref="ConfinesFileAccessToWorkingDirectory"/> holds only while the
    /// provider's permission system is engaged (AC-190) — set when confinement rests on permission prompts rather
    /// than a real OS sandbox, since a bypass permission mode then disables the guard.
    /// </summary>
    /// <remarks>
    /// When set, the host's driver adapter downgrades the mapped <c>ConfinesFileAccessToWorkingDirectory</c> to
    /// <see langword="false"/> for a session whose effective permission mode is not permission-engaged. Defaults to
    /// <see langword="false"/>.
    /// </remarks>
    public bool ConfinesViaPermissionsOnly { get; init; }

    /// <summary>
    /// Whether this provider can compact its own conversation in place (AC-664), backed by
    /// <see cref="IPluginSessionDriver.CompactContextAsync"/>. <see langword="false"/> leaves the host its own
    /// fallback of starting a fresh conversation.
    /// </summary>
    public bool SupportsContextCompaction { get; init; }

    /// <summary>
    /// Whether the host mounts its own permission-gated <see cref="IPluginToolset"/> for this provider's sessions,
    /// and whether that includes the host's tool-search proxies (AC-964).
    /// </summary>
    /// <remarks>
    /// A provider that mounts <see cref="PluginMcpServer"/> endpoints itself must leave this
    /// <see cref="PluginHostToolLoop.None"/>, or its servers are connected twice. Defaults to
    /// <see cref="PluginHostToolLoop.None"/>.
    /// </remarks>
    public PluginHostToolLoop HostToolLoop { get; init; }

    /// <summary>
    /// Whether this driver actually delivers a message sent while a turn is in flight to the model, instead of
    /// dropping it or leaving it unread (AC-739). Defaults to <see langword="false"/>, keeping the local send queue.
    /// </summary>
    public bool SupportsMidTurnInput { get; init; }

    /// <summary>
    /// The session options this provider actually understands, in its own vocabulary (AC-649) — Claude's
    /// <c>permission-mode</c>/<c>model</c>/<c>effort</c>, Codex's <c>sandbox</c>.
    /// </summary>
    /// <remarks>
    /// Empty by default; a provider that reads no options leaves it empty rather than inheriting anyone else's keys.
    /// </remarks>
    public IReadOnlyList<PluginSessionOptionDescriptor> DeclaredOptions { get; init; } = [];
}
