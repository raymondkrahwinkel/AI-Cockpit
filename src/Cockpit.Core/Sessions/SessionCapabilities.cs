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
public sealed record SessionCapabilities(
    bool SupportsTools,
    bool SupportsPermissions,
    bool SupportsLiveModelSwitch,
    bool SupportsPlanMode,
    bool SupportsThinking,
    bool SupportsVision = false)
{
    /// <summary>The Claude-CLI driver: native tools, permission prompts, live model/permission control, plan mode, thinking and image input.</summary>
    public static SessionCapabilities ClaudeCli { get; } = new(
        SupportsTools: true,
        SupportsPermissions: true,
        SupportsLiveModelSwitch: true,
        SupportsPlanMode: true,
        SupportsThinking: true,
        SupportsVision: true);
}
