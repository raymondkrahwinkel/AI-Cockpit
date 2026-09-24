using System.Text.RegularExpressions;

namespace Cockpit.Core.Sessions;

// AC-1056: the provider's background-task id from a tool result that announces one. Here rather than on the row view
// model, because the session host forms the rows now (AC-1377) and a restore still reads it the same way.
public static class BackgroundTaskAnnouncement
{
    // The two sentences a hand-off to the background is announced with, in the tool result itself: "Command
    // running in background with ID: <id>" (Bash) and "moved to the background as task <id>" (an MCP tool that
    // overran, AC-1053). Measured against the real CLI rather than taken from documentation.
    private static readonly Regex BackgroundTaskIdPattern = new(
        @"background\s+(?:with\s+ID:\s*|as\s+task\s+)([A-Za-z0-9_-]+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // Read off the full content, before any clamp: the hand-off line can sit anywhere in it.
    public static string? TaskId(string? resultText)
    {
        if (string.IsNullOrEmpty(resultText))
        {
            return null;
        }

        var match = BackgroundTaskIdPattern.Match(resultText);
        return match.Success ? match.Groups[1].Value : null;
    }
}
