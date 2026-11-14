using Cockpit.Core.Assistant;
using Cockpit.Core.Voice;

namespace Cockpit.Infrastructure.Configuration;

// On-disk shape of `AssistantSettings` in the `assistant` section of `cockpit.json`.
internal sealed class AssistantSettingsEntry
{
    public bool IsEnabled { get; set; }

    public bool SpeakReplies { get; set; } = true;

    public string? SpeechMode { get; set; }
    public string? SpeechMethod { get; set; }
    public string? SpeechProfileLabel { get; set; }

    public string PushToTalkKeyName { get; set; } = "F10";

    public bool AlwaysOnCostAcknowledged { get; set; }

    public bool WakeWordEnabled { get; set; }
    public string WakeWord { get; set; } = "computer";
    public int WakeWordListeningSeconds { get; set; } = 8;

    // AC-681. Defaults true to match `AssistantSettings.AlwaysOnTop`'s own default.
    public bool AlwaysOnTop { get; set; } = true;

    public double? ContextCompactionPercent { get; set; }

    // AC-2357. Absent in an older config, which reads as the default: on.
    public bool RequireRole { get; set; } = true;

    // AC-138: stored as the enum's name, not its ordinal, so a level a newer build wrote and this one
    // does not recognise reads back as "no match" instead of silently landing on ordinal 0.
    public string? ReadingLevel { get; set; }

    // AC-575: consent-bypass sources are plain string lists, not an enum per source — an unrecognised
    // enum value would corrupt the reader, while an unrecognised name here just matches nothing.
    public List<string> ConsentBypassSources { get; set; } = [];

    public List<string> ConsentBypassDangerousSources { get; set; } = [];

    // "Allow all" (#AC-637). Nullable so an `assistant` section written before this build — where the property is
    // absent rather than false — can be told apart from an operator's deliberate off; see `ToDomain`.
    public bool? ConsentBypassAll { get; set; }

    public static AssistantSettingsEntry FromDomain(AssistantSettings settings) => new()
    {
        IsEnabled = settings.IsEnabled,
        SpeakReplies = settings.SpeakReplies,
        SpeechMode = settings.SpeechMode.ToString(),
        SpeechMethod = settings.SpeechMethod.ToString(),
        SpeechProfileLabel = settings.SpeechProfileLabel,
        PushToTalkKeyName = settings.PushToTalkKeyName,
        AlwaysOnCostAcknowledged = settings.AlwaysOnCostAcknowledged,
        WakeWordEnabled = settings.WakeWordEnabled,
        WakeWord = settings.WakeWord,
        WakeWordListeningSeconds = settings.WakeWordListeningSeconds,
        AlwaysOnTop = settings.AlwaysOnTop,
        ContextCompactionPercent = settings.ContextCompactionPercent,
        RequireRole = settings.RequireRole,
        ReadingLevel = settings.ReadingLevel.ToString(),
        ConsentBypassSources = [.. settings.ConsentBypassSources],
        ConsentBypassDangerousSources = [.. settings.ConsentBypassDangerousSources],
        ConsentBypassAll = settings.ConsentBypassAll,
    };

    public AssistantSettings ToDomain() => new()
    {
        IsEnabled = IsEnabled,
        SpeakReplies = SpeakReplies,
        SpeechMode = Enum.TryParse<SpokenReplyMode>(SpeechMode, out var mode) && Enum.IsDefined(mode) ? mode : SpokenReplyMode.Short,
        SpeechMethod = Enum.TryParse<SpokenReplyMethod>(SpeechMethod, out var method) && Enum.IsDefined(method) ? method : SpokenReplyMethod.Assistant,
        SpeechProfileLabel = SpeechProfileLabel,
        PushToTalkKeyName = PushToTalkKeyName,
        AlwaysOnCostAcknowledged = AlwaysOnCostAcknowledged,
        WakeWordEnabled = WakeWordEnabled,
        WakeWord = WakeWord?.Trim() ?? string.Empty,
        WakeWordListeningSeconds = AssistantSettings.NormalizeWakeWordListeningSeconds(WakeWordListeningSeconds),
        AlwaysOnTop = AlwaysOnTop,
        ContextCompactionPercent = AssistantSettings.NormalizeCompactionPercent(ContextCompactionPercent),
        RequireRole = RequireRole,
        // A value written by an older build, or a hand-edited config, that does not name one of the three levels
        // falls back to the app default (Developer) rather than throwing.
        ReadingLevel = Enum.TryParse<Cockpit.Core.Sessions.ReadingLevel>(ReadingLevel, out var readingLevel)
            ? readingLevel
            : Cockpit.Core.Sessions.ReadingLevel.Developer,
        // A null from a hand-edited or older config is an empty list, never "every source": the per-source lists
        // stay something the operator ticked one at a time. Skipping everything is `ConsentBypassAll`'s job, and
        // it says so under its own name rather than by a list quietly reading as wider than it was written.
        ConsentBypassSources = [.. ConsentBypassSources ?? []],
        ConsentBypassDangerousSources = [.. ConsentBypassDangerousSources ?? []],
        // AC-637: absent means a pre-switch config, whose cockpit was asking about everything already — it
        // keeps doing so rather than silently narrowing an existing permission on upgrade.
        ConsentBypassAll = ConsentBypassAll ?? false,
    };
}
