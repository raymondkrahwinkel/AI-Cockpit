namespace Cockpit.Plugin.GitHubIssues;

/// <summary>
/// What a session working on an issue is called, and what it says it is doing. One definition, because the two
/// routes onto a session — starting a new one from the dialog and linking an issue to one already running (#AC-310)
/// — must not drift into naming the same session two different things.
/// </summary>
internal static class SessionLabel
{
    /// <summary>
    /// The session's name. The sidebar is a column of names, and "#42" on its own does not say which repository it
    /// came from — open two repos and there are two rows reading "#42" with nothing to tell them apart, while the
    /// working directory that would is not in the name you scan (AC-313). The owner is left off: it is the same one
    /// across nearly every repo an operator has open, so it costs sidebar width to repeat the half that does not
    /// vary. Taking everything past the last separator needs no special case for the repository <c>gh</c> can leave
    /// empty — there is nothing before the "#" then, which is the name this used to have.
    /// </summary>
    public static string Name(GitHubIssue issue) =>
        $"{issue.Repository[(issue.Repository.LastIndexOf('/') + 1)..]}#{issue.Number}";

    /// <summary>The line under the name: the same short id, plus the title, which is the part you actually read.</summary>
    public static string Statusline(GitHubIssue issue) => $"{Name(issue)} — {issue.Title}";
}
