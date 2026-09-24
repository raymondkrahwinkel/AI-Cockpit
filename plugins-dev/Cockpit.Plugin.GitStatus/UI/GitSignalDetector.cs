using System.Text.RegularExpressions;

namespace Cockpit.Plugin.GitStatus.UI;

// Recognises in session output a git command that could have moved the working tree or ahead/behind (commit,
// push, checkout, rebase, stash, ...), so the header badge refreshes the moment the session touches the repo.
// Word-boundary based, so ordinary prose about "git" does not trigger it.
internal static class GitSignalDetector
{
    private static readonly Regex GitMutation = new(
        @"\bgit\s+(commit|push|pull|fetch|checkout|switch|merge|rebase|reset|revert|add|rm|mv|stash|restore|cherry-pick|clone|init|tag|branch)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static bool ContainsSignal(string? text)
        => !string.IsNullOrEmpty(text) && GitMutation.IsMatch(text);
}
