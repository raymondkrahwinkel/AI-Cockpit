using Cockpit.Core.Assistant;

namespace Cockpit.Infrastructure.Configuration;

/// <summary>On-disk shape of <see cref="AssistantSettings"/> in the <c>assistant</c> section of <c>cockpit.json</c>.</summary>
internal sealed class AssistantSettingsEntry
{
    public bool IsEnabled { get; set; }

    public bool SpeakReplies { get; set; } = true;

    public string PushToTalkKeyName { get; set; } = "F10";

    public bool AlwaysOnCostAcknowledged { get; set; }

    public static AssistantSettingsEntry FromDomain(AssistantSettings settings) => new()
    {
        IsEnabled = settings.IsEnabled,
        SpeakReplies = settings.SpeakReplies,
        PushToTalkKeyName = settings.PushToTalkKeyName,
        AlwaysOnCostAcknowledged = settings.AlwaysOnCostAcknowledged,
    };

    public AssistantSettings ToDomain() => new()
    {
        IsEnabled = IsEnabled,
        SpeakReplies = SpeakReplies,
        PushToTalkKeyName = PushToTalkKeyName,
        AlwaysOnCostAcknowledged = AlwaysOnCostAcknowledged,
    };
}
