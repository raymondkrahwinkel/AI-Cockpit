using System.Text.Json;

namespace Cockpit.App.ViewModels;

/// <summary>
/// Classifies a single Claude-CLI transcript JSONL line for the TTY session status (#9): does it mean a turn
/// is in progress, that a turn just completed, or is it a metadata line that carries no status? A TTY session
/// hosts the real interactive <c>claude</c>, so its status is inferred from this transcript rather than a
/// parsed event stream — and, crucially, a long <em>thinking</em> pause writes no line, so "quiet" must not be
/// read as "done" (see <see cref="TtyActivityStatusTracker"/>). Pure/static so it is unit-testable against
/// real transcript lines.
/// </summary>
public static class TtyTranscriptStatus
{
    /// <summary>
    /// <see langword="true"/> when the line means a turn is in progress (a user message, a tool-result, or an
    /// assistant message that is still streaming or looping into a tool call → busy); <see langword="false"/>
    /// when it means the turn finished (an assistant message whose <c>stop_reason</c> is a terminal one →
    /// done); <see langword="null"/> for a metadata/unparseable line that should leave the status unchanged.
    /// </summary>
    public static bool? ClassifyLine(string? jsonLine)
    {
        if (string.IsNullOrWhiteSpace(jsonLine))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(jsonLine);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("type", out var typeElement)
                || typeElement.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            switch (typeElement.GetString())
            {
                case "user":
                    // A user message, or a tool-result feeding back into the model — the model owes a response.
                    return true;

                case "assistant":
                    // A terminal stop_reason (end_turn / stop_sequence / max_tokens) means the turn is over;
                    // "tool_use" means it will loop, and a missing/null stop_reason means it is still streaming.
                    var stopReason = root.TryGetProperty("message", out var message)
                        && message.ValueKind == JsonValueKind.Object
                        && message.TryGetProperty("stop_reason", out var reason)
                        && reason.ValueKind == JsonValueKind.String
                        ? reason.GetString()
                        : null;
                    return stopReason is not ("end_turn" or "stop_sequence" or "max_tokens");

                default:
                    // system, summary, last-prompt, custom-title, … carry no turn-progress signal.
                    return null;
            }
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
