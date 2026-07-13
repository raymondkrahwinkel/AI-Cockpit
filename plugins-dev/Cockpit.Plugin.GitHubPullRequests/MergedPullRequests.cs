namespace Cockpit.Plugin.GitHubPullRequests;

/// <summary>
/// Which merged pull requests are <em>news</em> (#69). The same shape as <see cref="ReviewRequestInbox"/>, and for the
/// same reason: a poll sees the world, not the change, and turning one into the other is the whole job.
/// <para>
/// The first look primes and fires nothing. Every pull request the operator has ever merged is "newly seen" to a
/// process that has just started, and a flow that ran forty times the moment the cockpit opened would be the last time
/// anyone armed it.
/// </para>
/// </summary>
internal static class MergedPullRequests
{
    /// <summary>What was merged since the last look, and what to remember. <paramref name="primed"/> is false on the very first look, which fires nothing.</summary>
    public static MergedPullRequestsResult Reconcile(
        IReadOnlyList<GitHubPullRequest> merged,
        IReadOnlySet<string> seen,
        bool primed)
    {
        var keys = merged.Select(KeyOf).ToHashSet(StringComparer.Ordinal);

        var news = primed
            ? merged.Where(pullRequest => !seen.Contains(KeyOf(pullRequest))).ToList()
            : [];

        // Everything seen now is remembered, whether it fired or not — that is what makes the first look a priming
        // look rather than a silent one that fires next time instead.
        var remembered = new HashSet<string>(seen, StringComparer.Ordinal);
        remembered.UnionWith(keys);

        return new MergedPullRequestsResult(news, remembered);
    }

    /// <summary>Identifies a pull request across repositories — the number alone collides between repos.</summary>
    public static string KeyOf(GitHubPullRequest pullRequest) => $"{pullRequest.Repository}#{pullRequest.Number}";
}

/// <summary>What was merged since the last look, and everything to remember for the next one.</summary>
internal sealed record MergedPullRequestsResult(IReadOnlyList<GitHubPullRequest> Merged, IReadOnlySet<string> Seen);
