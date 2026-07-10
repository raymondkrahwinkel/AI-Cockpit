namespace Cockpit.Core.Claude;

/// <summary>
/// What a session driver can do, so the UI renders or hides controls per provider instead of showing
/// dead ones (#26). The Claude-CLI driver supports everything; the HTTP providers advertise a narrower
/// set (e.g. no plan mode, model switch = a new request rather than live control).
/// </summary>
public sealed record SessionCapabilities(
    bool SupportsTools,
    bool SupportsPermissions,
    bool SupportsLiveModelSwitch,
    bool SupportsPlanMode,
    bool SupportsThinking)
{
    /// <summary>The Claude-CLI driver: native tools, permission prompts, live model/permission control, plan mode and thinking.</summary>
    public static SessionCapabilities ClaudeCli { get; } = new(
        SupportsTools: true,
        SupportsPermissions: true,
        SupportsLiveModelSwitch: true,
        SupportsPlanMode: true,
        SupportsThinking: true);
}
