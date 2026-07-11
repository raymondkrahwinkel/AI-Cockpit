namespace Cockpit.Plugin.YouTrack;

/// <summary>
/// The prompt dropped into the active session (or the clipboard, with no active session) when a YouTrack
/// issue is clicked. Editable in the plugin's settings; placeholders are substituted per issue: <c>{id}</c>,
/// <c>{idReadable}</c>, <c>{summary}</c>, <c>{url}</c>, <c>{project}</c>, <c>{description}</c>.
/// </summary>
internal static class PromptTemplate
{
    public const string Default =
        "Please open and review YouTrack issue {idReadable} (\"{summary}\") from project {project}.\n" +
        "Link: {url}\n\n" +
        "Description:\n{description}\n\n" +
        "Read the issue, then help me work on it.";

    public static string Render(string template, YouTrackIssue issue, string url) =>
        template
            .Replace("{id}", issue.Id)
            .Replace("{idReadable}", issue.IdReadable)
            .Replace("{summary}", issue.Summary)
            .Replace("{url}", url)
            .Replace("{project}", issue.Project)
            .Replace("{description}", string.IsNullOrWhiteSpace(issue.Description) ? "(no description)" : issue.Description);
}
