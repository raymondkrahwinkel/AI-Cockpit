using System.Text.RegularExpressions;

namespace Cockpit.Plugin.GitStatus;

/// <summary>
/// Recognises, in a chunk of session output, a git command that could have changed the working tree or the
/// branch's ahead/behind — a commit, push, pull, checkout/switch, merge, rebase, reset, stash, and so on — so
/// the session-following status section can refresh the moment the session touches the repo, instead of
/// showing stale counts. Substring/word-boundary based; narrow enough that ordinary prose about "git" does
/// not trigger it.
/// </summary>
internal static class GitSignalDetector
{
    private static readonly Regex GitMutation = new(
        @"\bgit\s+(commit|push|pull|fetch|checkout|switch|merge|rebase|reset|revert|add|rm|mv|stash|restore|cherry-pick|clone|init|tag|branch)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static bool ContainsSignal(string? text)
        => !string.IsNullOrEmpty(text) && GitMutation.IsMatch(text);
}
