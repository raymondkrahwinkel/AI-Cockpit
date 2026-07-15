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
    /// <summary>The Claude-CLI driver: native tools, permission prompts, live model/permission control, plan mode, thinking, image input, and resuming an earlier conversation.</summary>
    public static SessionCapabilities ClaudeCli { get; } = new(
        SupportsTools: true,
        SupportsPermissions: true,
        SupportsLiveModelSwitch: true,
        SupportsPlanMode: true,
        SupportsThinking: true,
        SupportsVision: true,
        SupportsResume: true,
        SupportsPermissionModeSwitch: true);
}
