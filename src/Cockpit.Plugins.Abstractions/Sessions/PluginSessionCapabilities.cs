namespace Cockpit.Plugins.Abstractions.Sessions;

/// <summary>
/// What a plugin-provided <see cref="IPluginSessionDriver"/> supports, so the host's session UI renders or
/// hides controls per provider instead of showing dead ones — the plugin-facing mirror of
/// <c>Cockpit.Core.Sessions.SessionCapabilities</c> (#45/#64). Kept as a separate type (not a shared reference)
/// so this assembly never needs to reference <c>Cockpit.Core</c>; the host's driver adapter converts one to
/// the other at the plugin boundary. <see cref="SupportsTools"/>/<see cref="SupportsPermissions"/> are here
/// because a plugin driver can genuinely back them.
/// </summary>
/// <param name="SupportsTools">Whether the plugin driver has a tool source (native tools or an MCP loop of its own).</param>
/// <param name="SupportsPermissions">Whether the plugin driver knows Claude-style permission modes.</param>
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
    /// key, which the host's driver adapter wires its own <c>SetModelAsync</c> to. An init-only property (not a
    /// primary-constructor parameter) so adding it does not change the record's constructor signature — an
    /// already-compiled plugin that constructs this the old way keeps loading. Defaults to <see langword="false"/>.
    /// </summary>
    public bool SupportsLiveModelSwitch { get; init; }

    /// <summary>
    /// Whether this driver can switch the Claude-style permission mode mid-session (Fase 4 D4) — backed by
    /// <see cref="IPluginSessionDriver.SetLiveOptionAsync"/> with the
    /// <see cref="WellKnownPluginSessionOptions.PermissionMode"/> key, which the host's driver adapter wires its own
    /// <c>SetPermissionModeAsync</c> to. Init-only for the same back-compat reason as
    /// <see cref="SupportsLiveModelSwitch"/>; defaults to <see langword="false"/>, so a provider with no permission
    /// modes (an HTTP model, Codex's sandbox) never advertises it.
    /// </summary>
    public bool SupportsPermissionModeSwitch { get; init; }

    /// <summary>
    /// Whether this provider's sessions honour a profile's own environment variables at spawn (AC-22) — backed by
    /// the environment-carrying <see cref="IPluginSessionDriver.StartAsync(string?, string?, string?, IReadOnlyDictionary{string, string}?, IReadOnlyList{PluginMcpServer}?, IReadOnlyDictionary{string, string}?, CancellationToken)"/>
    /// overload, which a driver that spawns a process overrides to apply them. Gates the profile editor's
    /// env-var section, so a provider with nothing to inject into (an HTTP model) never shows a dead editor.
    /// Init-only for the same back-compat reason as <see cref="SupportsLiveModelSwitch"/>; defaults to
    /// <see langword="false"/>.
    /// </summary>
    public bool SupportsEnvVars { get; init; }

    /// <summary>
    /// Whether this provider's own file-affecting tools stay within the session's working directory (AC-174) — the
    /// guarantee an isolated embedded run (Autopilot's worktree isolation) rests on. A driver that spawns a process
    /// in the working directory and edits with cwd-bound native tools (Claude, Codex) confines them; an HTTP-backed
    /// provider (a local model) has no process cwd and reaches files only through out-of-process MCP servers rooted
    /// at a fixed folder, so it does not. The host's driver adapter maps this onto its core mirror, which the host
    /// reads after start to refuse an isolate-in-worktree run on a non-confining provider rather than let it write
    /// the operator's real checkout. Init-only for the same back-compat reason as
    /// <see cref="SupportsLiveModelSwitch"/>; defaults to <see langword="false"/>, so a provider that has not
    /// vouched for confinement fails closed, not open.
    /// </summary>
    public bool ConfinesFileAccessToWorkingDirectory { get; init; }

    /// <summary>
    /// Whether the confinement vouched by <see cref="ConfinesFileAccessToWorkingDirectory"/> holds <em>only while the
    /// provider's permission system is engaged</em> (AC-190). A provider that confines via a real OS sandbox
    /// (Codex's <c>workspace-write</c>) leaves this <see langword="false"/>: its confinement is independent of the
    /// permission mode and holds unconditionally. A provider whose confinement to the working directory rests on its
    /// permission prompts (Claude — cwd-bound native tools kept in check by per-tool approval) sets this
    /// <see langword="true"/>, because a bypass permission mode (<c>bypassPermissions</c>,
    /// <c>--dangerously-skip-permissions</c>) disables exactly that guard and lets the session write to an absolute
    /// path outside its worktree. When set, the host's driver adapter downgrades the mapped
    /// <c>ConfinesFileAccessToWorkingDirectory</c> to <see langword="false"/> for a session whose effective permission
    /// mode is not a known permission-engaged one, so the fail-closed isolation gate refuses an isolate-in-worktree run
    /// that a bypass mode would leave unconfined. Init-only for the same back-compat reason as
    /// <see cref="SupportsLiveModelSwitch"/>; defaults to <see langword="false"/>, so an unaware provider keeps its
    /// unconditional confinement contract unchanged.
    /// </summary>
    public bool ConfinesViaPermissionsOnly { get; init; }
}
