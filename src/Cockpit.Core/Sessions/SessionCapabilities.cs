namespace Cockpit.Core.Sessions;

// What a session driver can do, so the UI renders or hides controls per provider instead of showing
// dead ones (#26). The Claude-CLI driver supports everything; the HTTP providers advertise a narrower
// set (e.g. no plan mode, model switch = a new request rather than live control).
//
// `SupportsVision`:
// Whether this driver actually sends pasted image attachments to the model (#64) — driver-backed, not a
// declarative hint: only `ClaudeCli` is true today, since `ClaudeCliSession.SendUserMessageAsync`
// is the only one that builds image content blocks. Defaults to `false` so existing 5-arg
// construction (e.g. `OpenAiCompatSessionDriver`) keeps compiling and stays non-vision until it can
// carry images too. Gates the session panel's image-paste handling so a provider that would otherwise
// silently drop a pasted image never gets the chance to.
// `SupportsResume`:
// Whether this driver can pick up an earlier conversation (`SessionResume`) instead of starting a
// fresh one — true for the Claude CLI, which keeps its own transcript history; false for the HTTP providers,
// which keep no history to resume from. Gates the New-session dialog's resume controls.
// `SupportsPermissionModeSwitch`:
// Whether this driver can live-switch Claude's permission *mode* (default/acceptEdits/plan) mid-session
// via `SetPermissionModeAsync` — true only for the Claude CLI. Distinct from `SupportsPermissions`,
// which a plugin like Codex reports true because it does tool approvals, yet it has no permission-mode vocabulary:
// Codex switches its approval *policy* instead, through the generic live-control panel (#45 D4). Gates the
// header's Claude permission-mode dropdown so it no longer shows as a dead control on a provider that cannot honour
// it. Defaults to `false` so existing construction stays non-switching.
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
    // Whether this driver's sessions honour a profile's own environment variables at spawn (AC-22) — the
    // host-side mirror of `PluginSessionCapabilities.SupportsEnvVars`, which gates the profile editor's
    // env-var section. Defaults to `false` so existing construction stays non-injecting.
    public bool SupportsEnvVars { get; init; }

    // Whether this driver's own file-affecting tools stay within the session's working directory (AC-174) — the
    // guarantee worktree isolation rests on. A driver that spawns a process in the working directory and edits
    // files with cwd-bound native tools (Claude, Codex) confines them; an HTTP/in-process driver (a local model)
    // has no process cwd and reaches files only through out-of-process MCP servers rooted at a fixed folder, so it
    // does *not*. The host reads this after start to refuse an isolate-in-worktree embedded run on a
    // non-confining provider rather than let it write the operator's real checkout. Defaults to
    // `false` so a provider that has not vouched for confinement fails closed, not open.
    public bool ConfinesFileAccessToWorkingDirectory { get; init; }

    // The Claude-CLI driver: native tools, permission prompts, live model/permission control, plan mode, thinking, image input, and resuming an earlier conversation.
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
