namespace Cockpit.Core.Sessions;

// What a session driver can do, so the UI hides dead controls per provider (#26). `SupportsVision` (#64):
// only `ClaudeCli` sends pasted images today. `SupportsResume`: the HTTP providers keep no transcript to
// resume. `SupportsPermissionModeSwitch` is distinct from `SupportsPermissions` (Codex has the latter but no mode).
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

    // AC-174: whether this driver's own file-affecting tools stay within the session's working directory — the
    // guarantee worktree isolation rests on. Claude/Codex confine via cwd-bound native tools; an in-process
    // driver reaching files through a fixed-folder MCP server does not. Defaults to `false` (fail closed).
    public bool ConfinesFileAccessToWorkingDirectory { get; init; }

    // AC-664: whether a full context is answered by asking this driver to summarise and carry on, or by starting a
    // fresh conversation and losing the transcript. The host-side mirror of
    // `PluginSessionCapabilities.SupportsContextCompaction`; `false` keeps the fresh start.
    public bool SupportsContextCompaction { get; init; }

    // AC-739: whether this driver actually delivers a message sent mid-turn to the model, instead of dropping it or
    // leaving it unread. The host-side mirror of `PluginSessionCapabilities.SupportsMidTurnInput`; `false` keeps the
    // session panel's local send queue.
    public bool SupportsMidTurnInput { get; init; }

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
