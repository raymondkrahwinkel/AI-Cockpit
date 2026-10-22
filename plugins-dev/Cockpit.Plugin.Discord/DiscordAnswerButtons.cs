using System.Globalization;

namespace Cockpit.Plugin.Discord;

// The option buttons of one question (AC-1544): the assistant's (AC-1553) and another plugin's alike, the one button
// mechanism. A button's custom id carries the correlation id and the option's place, so a click needs no lookup to
// find its question; the label it stands for is the bridge's to remember.
internal sealed record DiscordAnswerButtons(string CorrelationId, IReadOnlyList<string> Labels)
{
    // Discord's own limits: 25 buttons on a message, 100 characters in a custom id, 80 in a label.
    public const int MaxButtons = 25;
    public const int MaxCustomIdLength = 100;
    public const int MaxLabelLength = 80;

    private const string _Prefix = "cockpit-answer";

    public string Id(int index) => $"{_Prefix}:{CorrelationId}:{index.ToString(CultureInfo.InvariantCulture)}";

    // Null when the question cannot be put as buttons: too many choices, or an id too long to round-trip.
    public static DiscordAnswerButtons? For(string? correlationId, IReadOnlyList<string> labels) =>
        correlationId is { Length: > 0 } && labels.Count is > 0 and <= MaxButtons
            && $"{_Prefix}:{correlationId}:{MaxButtons}".Length <= MaxCustomIdLength
            ? new DiscordAnswerButtons(correlationId, labels)
            : null;

    public static bool TryParse(string? customId, out string correlationId, out int index)
    {
        correlationId = string.Empty;
        index = -1;

        var parts = customId?.Split(':');
        if (parts is not { Length: 3 } || parts[0] != _Prefix || parts[1].Length == 0
            || !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out index))
        {
            return false;
        }

        correlationId = parts[1];
        return true;
    }
}
