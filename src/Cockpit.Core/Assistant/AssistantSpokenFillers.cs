namespace Cockpit.Core.Assistant;

// The short lines the cockpit speaks on the assistant's behalf when the model itself said nothing: that it is
// going to look something up (AC-597), and that it is still at it (AC-598). ponytail: two languages only,
// silence otherwise — a filler in the wrong language is worse than none.
public static class AssistantSpokenFillers
{
    private static readonly IReadOnlyDictionary<string, string[]> GoingToLook = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
    {
        ["nl"] = ["Momentje, ik kijk even.", "Ik zoek het even op.", "Even kijken.", "Ik duik er even in."],
        ["en"] = ["One moment, let me look.", "I'll go and check.", "Let me have a look.", "Just a second, looking now."],
    };

    private static readonly IReadOnlyDictionary<string, string[]> StillWorking = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
    {
        ["nl"] = ["Ik ben er nog mee bezig.", "Nog even geduld, ik ben er mee bezig.", "Het duurt wat langer, ik ben er nog mee."],
        ["en"] = ["Still on it.", "Still working on it, bear with me.", "This one is taking a while — still going."],
    };

    // What to say before going quiet on a tool call, or empty for a language we have no words in.
    public static string GoingToLookUpSomething(string? language, int turn) => _Pick(GoingToLook, language, turn);

    // What to say while a turn is still running, or empty for a language we have no words in.
    public static string StillAtIt(string? language, int repeat) => _Pick(StillWorking, language, repeat);

    // How long to stay quiet before saying it again (AC-598): half a minute, then wider each time.
    // Widening rather than a fixed beat, and capped. A sign of life every thirty seconds through a three-minute
    // wait is nagging; the first one is reassurance and the fourth is an interruption of the operator's own work.
    public static TimeSpan SignOfLifeDelay(int repeat)
    {
        var seconds = 30 * Math.Pow(1.5, Math.Max(0, repeat));
        return TimeSpan.FromSeconds(Math.Min(seconds, 180));
    }

    // Rotated rather than random: the same words twice in a row is what makes a filler grating, and a rotation
    // gives that guarantee where a draw only makes it likely — and can be asserted.
    private static string _Pick(IReadOnlyDictionary<string, string[]> lines, string? language, int index)
    {
        if (language is null || !lines.TryGetValue(language, out var choices) || choices.Length == 0)
        {
            return string.Empty;
        }

        return choices[(int)((uint)index % (uint)choices.Length)];
    }
}
