namespace Cockpit.Infrastructure.Configuration;

/// <summary>
/// Root JSON shape of <c>cockpit.json</c> under the app config directory. Each store owns one
/// section and reads-modifies-writes the whole file so it never clobbers a sibling section: the
/// profile store owns <see cref="Profiles"/>, the notification store owns <see cref="Notifications"/>,
/// the permission-rule store owns <see cref="PermissionRules"/>, the session-switch store owns
/// <see cref="SessionSwitching"/>, the transcript-display store owns <see cref="TranscriptDisplay"/>.
/// Kept as a plain DTO separate from the domain records so the on-disk shape can evolve independently.
/// </summary>
internal sealed class CockpitConfigFile
{
    public List<ClaudeProfileEntry> Profiles { get; set; } = [];

    public NotificationSettingsEntry? Notifications { get; set; }

    /// <summary>Always-allow rules keyed by profile label, so each profile keeps its own allowances.</summary>
    public Dictionary<string, List<PermissionRuleEntry>> PermissionRules { get; set; } = [];

    public SessionSwitchSettingsEntry? SessionSwitching { get; set; }

    public TranscriptDisplaySettingsEntry? TranscriptDisplay { get; set; }

    public SessionBehaviorSettingsEntry? SessionBehavior { get; set; }
}
