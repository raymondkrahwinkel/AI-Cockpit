namespace Cockpit.Infrastructure.Worktrees;

// Environment handed to a child git process, kept in one place so every path that talks to a remote agrees on it.
internal static class GitEnvironment
{
    // Turns off git's interactive credential prompting so git fails fast instead of blocking on a prompt no window
    // can answer. v1's whole auth story leans on the host credential helper (GCM, `gh`); this is the seam a later
    // token injection (AC-88) extends.
    public static readonly IReadOnlyDictionary<string, string> NonInteractive =
        new Dictionary<string, string> { ["GIT_TERMINAL_PROMPT"] = "0" };
}
