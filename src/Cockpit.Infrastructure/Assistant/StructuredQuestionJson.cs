using System.Text.Json;
using Cockpit.Plugins.Abstractions.Channels;

namespace Cockpit.Infrastructure.Assistant;

// The `questions` array of an AskUserQuestion input (AC-715), the shape ask_structured_question writes too (AC-955):
// one reading for the card in the app and for a chat channel (AC-1553), so the two never disagree on the options.
public static class StructuredQuestionJson
{
    // Empty for any other input; a question without text is dropped rather than shown blank.
    public static IReadOnlyList<AssistantChannelQuestion> Parse(string? inputJson)
    {
        if (string.IsNullOrWhiteSpace(inputJson))
        {
            return [];
        }

        try
        {
            using var document = JsonDocument.Parse(inputJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("questions", out var questions)
                || questions.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            return [.. questions.EnumerateArray().Select(_Question).OfType<AssistantChannelQuestion>()];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    // A broker question's answer, as the operator's next message: spelled out because the model never sees the card,
    // and a bare label could otherwise read as a fresh instruction rather than an answer to this one.
    public static string AnswerText(string question, string answer) => $"\"{question}\" → {answer}";

    private static AssistantChannelQuestion? _Question(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object || _String(element, "question") is not { Length: > 0 } question)
        {
            return null;
        }

        // Defaults to on: a native AskUserQuestion carries no such field and its SDK guarantees the fallback
        // regardless (AC-715); only ask_structured_question's own payload can turn it off explicitly (AC-955).
        return new AssistantChannelQuestion
        {
            Question = question,
            Header = _String(element, "header") is { Length: > 0 } header ? header : null,
            MultiSelect = element.TryGetProperty("multiSelect", out var multi) && multi.ValueKind == JsonValueKind.True,
            AllowOther = !element.TryGetProperty("allowOther", out var other) || other.ValueKind != JsonValueKind.False,
            Options = _Options(element),
        };
    }

    private static IReadOnlyList<AssistantChannelOption> _Options(JsonElement question)
    {
        if (!question.TryGetProperty("options", out var options) || options.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return [.. options.EnumerateArray()
            .Where(option => option.ValueKind == JsonValueKind.Object)
            .Select(option => (Label: _String(option, "label"), Description: _String(option, "description")))
            .Where(option => option.Label.Length > 0)
            .Select(option => new AssistantChannelOption(option.Label, option.Description.Length > 0 ? option.Description : null))];
    }

    private static string _String(JsonElement parent, string property) =>
        parent.TryGetProperty(property, out var element) && element.ValueKind == JsonValueKind.String
            ? element.GetString() ?? string.Empty
            : string.Empty;
}
