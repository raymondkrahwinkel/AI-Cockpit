namespace Cockpit.Plugin.YouTrack;

/// <summary>
/// Recognises the YouTrack MCP tool calls whose completion should attach the message's images to an issue
/// (AC-116): creating an issue, or updating an existing one. Matched loosely on the tool name — the
/// "youtrack" the server this plugin registers ("YouTrack: {label}") carries, plus the verb — rather than an
/// exact string, because each provider prefixes and sanitises MCP tool names its own way (Claude
/// <c>mcp__youtrack_personal__create_issue</c>, Codex a TOML-safe variant). A draft (<c>create_draft_issue</c>)
/// is left out: it is not yet an issue an attachment belongs on, and its name does not contain "create_issue".
/// </summary>
internal static class YouTrackToolActivity
{
    public static bool IsIssueCreateOrUpdate(string toolName)
    {
        if (string.IsNullOrEmpty(toolName))
        {
            return false;
        }

        var lower = toolName.ToLowerInvariant();
        return lower.Contains("youtrack")
            && (lower.Contains("create_issue") || lower.Contains("update_issue"));
    }
}
