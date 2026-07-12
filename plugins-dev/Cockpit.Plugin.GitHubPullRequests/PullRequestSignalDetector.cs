using System.Text.RegularExpressions;

namespace Cockpit.Plugin.GitHubPullRequests;

/// <summary>
/// Recognises, in a chunk of session output, the signals that mean the open-PR list may have just changed —
/// a pull-request url (a PR was created/opened/referenced) or a merged/closed/reopened state change — so the
/// inline section can refresh the instant it happens instead of waiting for its periodic poll. Substring-based
/// so it fires whether the text came from Claude's prose, a shelled-out <c>gh pr create</c>, or a raw TTY
/// transcript line; deliberately narrow to avoid refreshing on unrelated chatter.
/// </summary>
internal static class PullRequestSignalDetector
{
    // e.g. https://github.com/raymondkrahwinkel/AI-Cockpit/pull/5 — the url gh prints on create, and what
    // Claude quotes when it opens/reviews a PR.
    private static readonly Regex PullUrl = new(
        @"github\.com/[^/\s]+/[^/\s]+/pull/\d+",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // "merged this pull request", "pull request … closed", "reopened pull request" — the state changes that
    // add or drop an entry from the open list. Bounded gap so it needs the words near each other, not just
    // both present somewhere in a long blob.
    private static readonly Regex StateChange = new(
        @"\b(merged|closed|reopened)\b[^\n]{0,40}\bpull request\b|\bpull request\b[^\n]{0,40}\b(merged|closed|reopened)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static bool ContainsSignal(string? text)
        => !string.IsNullOrEmpty(text) && (PullUrl.IsMatch(text) || StateChange.IsMatch(text));
}
