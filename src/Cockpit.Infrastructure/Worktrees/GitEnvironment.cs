namespace Cockpit.Infrastructure.Worktrees;

// Environment handed to a child git process, kept in one place so every path that talks to a remote agrees on it.
internal static class GitEnvironment
{
    // Turns off git's interactive credential prompting. Without a helper (or headless) git then fails fast with a
    // message instead of blocking on a terminal prompt no window can answer. This is v1's whole auth story — lean on
    // the host credential helper (GCM, `gh`) — and the seam a later in-memory token injection (AC-88:
    // GIT_ASKPASS plus the token in this child env only) extends, never a token in the URL.
    public static readonly IReadOnlyDictionary<string, string> NonInteractive =
        new Dictionary<string, string> { ["GIT_TERMINAL_PROMPT"] = "0" };
}
