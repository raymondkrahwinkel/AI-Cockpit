namespace Cockpit.Core.Voice;

/// <summary>
/// The preset turn-start acknowledgement phrases (AC-99), one small set per read-aloud language. Spoken as-is by
/// <see cref="TurnAcknowledgmentPipeline"/> in <see cref="TurnAckMode.InstantPhrases"/> mode and as the fallback
/// for <see cref="TurnAckMode.LocalLlm"/>. Kept short and conversational — they play before every turn.
/// </summary>
public static class TurnAcknowledgmentPhrases
{
    private static readonly IReadOnlyList<string> Dutch =
    [
        "Ik kijk het even voor je na.",
        "Momentje, ik zoek het uit.",
        "Ik ga ermee aan de slag.",
        "Even kijken.",
        "Ik duik erin.",
    ];

    private static readonly IReadOnlyList<string> English =
    [
        "Let me take a look.",
        "One moment, I'll figure that out.",
        "I'm on it.",
        "Let me check that.",
        "Looking into it.",
    ];

    /// <summary>The phrase set for a read-aloud base language ("nl"/"en"); English for anything else, matching the read-aloud default.</summary>
    public static IReadOnlyList<string> For(string language) =>
        language.StartsWith("nl", StringComparison.OrdinalIgnoreCase) ? Dutch : English;
}
