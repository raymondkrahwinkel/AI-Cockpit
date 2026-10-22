using System.Globalization;

namespace Cockpit.Plugins.Abstractions.Channels;

/// <summary>
/// One choice on an <see cref="AssistantChannelQuestion"/>. The label is what comes back as the answer.
/// </summary>
public sealed record AssistantChannelOption(string Label, string? Description = null);

/// <summary>
/// A question with its choices, as a channel renders it (AC-1553): buttons where the platform has them, else
/// <see cref="FallbackPrompt"/> as text. The same model carries the assistant's questions and a plugin's own.
/// </summary>
public sealed record AssistantChannelQuestion
{
    /// <summary>
    /// The question itself, in the words it was asked.
    /// </summary>
    public required string Question { get; init; }

    /// <summary>
    /// The choices, in order; a text reply names them by their number in this list, starting at 1.
    /// </summary>
    public required IReadOnlyList<AssistantChannelOption> Options { get; init; }

    /// <summary>
    /// A short caption shown above the question, or null for none.
    /// </summary>
    public string? Header { get; init; }

    /// <summary>
    /// Whether several choices may be picked at once.
    /// </summary>
    public bool MultiSelect { get; init; }

    /// <summary>
    /// Whether an answer in the operator's own words is welcome beside the choices ("Other").
    /// </summary>
    public bool AllowOther { get; init; } = true;

    /// <summary>
    /// The question as plain text for a channel without buttons: the numbered choices and how to answer them.
    /// </summary>
    public string FallbackPrompt()
    {
        var numbers = Enumerable.Range(1, Options.Count).Select(number => number.ToString(CultureInfo.InvariantCulture)).ToList();
        var lines = Options.Select((option, index) => option.Description is { Length: > 0 } description
            ? $"{numbers[index]}. {option.Label} — {description}"
            : $"{numbers[index]}. {option.Label}");
        var list = numbers.Count > 1 ? $"{string.Join(", ", numbers[..^1])} or {numbers[^1]}" : string.Concat(numbers);
        var how = MultiSelect ? $"Reply with one or more of {list}, separated by commas" : $"Reply with {list}";
        return $"{Question}\n{string.Join("\n", lines)}\n{how}{(AllowOther ? ", or in your own words" : string.Empty)}.";
    }

    /// <summary>
    /// Reads a text reply as a choice: a label, or numbers from <see cref="FallbackPrompt"/>. Null when the text
    /// is no choice, which for a question that <see cref="AllowOther"/> is an answer in the operator's own words.
    /// </summary>
    public IReadOnlyList<string>? ReadReply(string text)
    {
        var reply = text.Trim();
        if (Options.FirstOrDefault(option => string.Equals(option.Label, reply, StringComparison.OrdinalIgnoreCase)) is { } named)
        {
            return [named.Label];
        }

        var parts = reply.Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries);
        var picked = new List<string>();
        foreach (var part in parts)
        {
            if (!int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number < 1 || number > Options.Count)
            {
                return null;
            }

            picked.Add(Options[number - 1].Label);
        }

        picked = [.. picked.Distinct()];
        return picked.Count == 0 || (!MultiSelect && picked.Count > 1) ? null : picked;
    }
}

/// <summary>
/// A file or image sent out as part of a row (AC-1553), under the name the channel shows it by.
/// </summary>
public sealed record AssistantChannelAttachment(string Name, byte[] Content, bool IsImage);
