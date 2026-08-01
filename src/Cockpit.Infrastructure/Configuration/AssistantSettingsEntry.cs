using Cockpit.Core.Assistant;

namespace Cockpit.Infrastructure.Configuration;

/// <summary>On-disk shape of <see cref="AssistantSettings"/> in the <c>assistant</c> section of <c>cockpit.json</c>.</summary>
internal sealed class AssistantSettingsEntry
{
    public bool IsEnabled { get; set; }

    public bool SpeakReplies { get; set; } = true;

    public string PushToTalkKeyName { get; set; } = "F10";

    public bool AlwaysOnCostAcknowledged { get; set; }

    /// <summary>
    /// The consent-bypass switches (#AC-575), on disk as two plain string lists rather than one enum per source.
    /// Deliberate: an unknown enum value costs <c>JsonlAuditLog</c>'s reader the line it is on and this file's
    /// reader the whole section, and the default of a non-nullable enum is ordinal 0 — so a three-state
    /// <c>None/LowRisk/Everything</c> written by a newer build is exactly the shape that reads back as whichever
    /// value happens to sit at 0 in an older one. A source name that means nothing to this build is simply a name
    /// that matches no source, which is the least powerful thing it could mean.
    /// </summary>
    public List<string> ConsentBypassSources { get; set; } = [];

    /// <inheritdoc cref="ConsentBypassSources"/>
    public List<string> ConsentBypassDangerousSources { get; set; } = [];

    public static AssistantSettingsEntry FromDomain(AssistantSettings settings) => new()
    {
        IsEnabled = settings.IsEnabled,
        SpeakReplies = settings.SpeakReplies,
        PushToTalkKeyName = settings.PushToTalkKeyName,
        AlwaysOnCostAcknowledged = settings.AlwaysOnCostAcknowledged,
        ConsentBypassSources = [.. settings.ConsentBypassSources],
        ConsentBypassDangerousSources = [.. settings.ConsentBypassDangerousSources],
    };

    public AssistantSettings ToDomain() => new()
    {
        IsEnabled = IsEnabled,
        SpeakReplies = SpeakReplies,
        PushToTalkKeyName = PushToTalkKeyName,
        AlwaysOnCostAcknowledged = AlwaysOnCostAcknowledged,
        // A null from a hand-edited or older config is an empty list, never "everything": the whole point of this
        // setting is that it is off until someone deliberately turned it on.
        ConsentBypassSources = [.. ConsentBypassSources ?? []],
        ConsentBypassDangerousSources = [.. ConsentBypassDangerousSources ?? []],
    };
}
