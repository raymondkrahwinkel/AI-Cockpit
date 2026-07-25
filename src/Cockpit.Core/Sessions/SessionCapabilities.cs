namespace Cockpit.Core.Sessions;

/// <summary>
/// What a session driver can do, so the UI renders or hides controls per provider instead of showing
/// dead ones (#26). The Claude-CLI driver supports everything; the HTTP providers advertise a narrower
/// set (e.g. no plan mode, model switch = a new request rather than live control).
/// </summary>
/// <param name="SupportsVision">
/// Whether this driver actually sends pasted image attachments to the model (#64) — driver-backed, not a
/// declarative hint: only <see cref="ClaudeCli"/> is true today, since <c>ClaudeCliSession.SendUserMessageAsync</c>
/// is the only one that builds image content blocks. Defaults to <see langword="false"/> so existing 5-arg
/// construction (e.g. <c>OpenAiCompatSessionDriver</c>) keeps compiling and stays non-vision until it can
/// carry images too. Gates the session panel's image-paste handling so a provider that would otherwise
/// silently drop a pasted image never gets the chance to.
/// </param>
/// <param name="SupportsResume">
/// Whether this driver can pick up an earlier conversation (<see cref="SessionResume"/>) instead of starting a
/// fresh one — true for the Claude CLI, which keeps its own transcript history; false for the HTTP providers,
/// which keep no history to resume from. Gates the New-session dialog's resume controls.</param>
/// </param>
/// <param name="SupportsPermissionModeSwitch">
/// Whether this driver can live-switch Claude's permission <em>mode</em> (default/acceptEdits/plan) mid-session
/// via <c>SetPermissionModeAsync</c> — true only for the Claude CLI. Distinct from <see cref="SupportsPermissions"/>,
/// which a plugin like Codex reports true because it does tool approvals, yet it has no permission-mode vocabulary:
/// Codex switches its approval <em>policy</em> instead, through the generic live-control panel (#45 D4). Gates the
/// header's Claude permission-mode dropdown so it no longer shows as a dead control on a provider that cannot honour
/// it. Defaults to <see langword="false"/> so existing construction stays non-switching.</param>
public sealed record SessionCapabilities(
    bool SupportsTools,
    bool SupportsPermissions,
    bool SupportsLiveModelSwitch,
    bool SupportsPlanMode,
    bool SupportsThinking,
    bool SupportsVision = false,
    bool SupportsResume = false,
    bool SupportsPermissionModeSwitch = false)
{
    /// <summary>
    /// Whether this driver's sessions honour a profile's own environment variables at spawn (AC-22) — the
    /// host-side mirror of <c>PluginSessionCapabilities.SupportsEnvVars</c>, which gates the profile editor's
    /// env-var section. Defaults to <see langword="false"/> so existing construction stays non-injecting.
    /// </summary>
    public bool SupportsEnvVars { get; init; }

    /// <summary>
    /// Whether this driver's own file-affecting tools stay within the session's working directory (AC-174) — the
    /// guarantee worktree isolation rests on. A driver that spawns a process in the working directory and edits
    /// files with cwd-bound native tools (Claude, Codex) confines them; an HTTP/in-process driver (a local model)
    /// has no process cwd and reaches files only through out-of-process MCP servers rooted at a fixed folder, so it
    /// does <em>not</em>. The host reads this after start to refuse an isolate-in-worktree embedded run on a
    /// non-confining provider rather than let it write the operator's real checkout. Defaults to
    /// <see langword="false"/> so a provider that has not vouched for confinement fails closed, not open.
    /// </summary>
    public bool ConfinesFileAccessToWorkingDirectory { get; init; }

    /// <summary>The Claude-CLI driver: native tools, permission prompts, live model/permission control, plan mode, thinking, image input, and resuming an earlier conversation.</summary>
    public static SessionCapabilities ClaudeCli { get; } = new(
        SupportsTools: true,
        SupportsPermissions: true,
        SupportsLiveModelSwitch: true,
        SupportsPlanMode: true,
        SupportsThinking: true,
        SupportsVision: true,
        SupportsResume: true,
        SupportsPermissionModeSwitch: true)
    {
        // The TTY route injects a profile's variables host-side (TtyLauncher), so a Claude session honours them.
        SupportsEnvVars = true,
        // Claude spawns in the session's working directory and edits with cwd-bound native tools, so an isolated
        // run stays inside its worktree (AC-174).
        ConfinesFileAccessToWorkingDirectory = true,
    };
}
