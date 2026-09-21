using System.Text.RegularExpressions;

namespace Cockpit.Plugin.Autopilot;

/// <summary>
/// Whether an epic's sub is already delivered (AC-346) — the "merge-ready ≠ merged" half of the epic-runner: a sub
/// that finished its own run only reached a merge-ready PR, not <c>origin/main</c>. Split out of
/// <see cref="AutopilotEpicRunner"/> as its own interface so a test can fake "already merged"/"not yet"/"cannot tell"
/// per issue without shelling out to git — the real check (<see cref="GitEpicSubMergeChecker"/>) is a thin, separately
/// testable wrapper around <see cref="GitCommandLine"/>.
/// <para>
/// <see cref="RefreshAsync"/> is called once by the epic-runner, before walking an epic's subs, and
/// <see cref="IsMerged"/> answers every sub afterwards from what it already loaded — a fetch plus a log read per sub
/// (the original AC-346 shape) meant a 7-sub epic on a slow remote paid the fetch's timeout up to seven times in a
/// row, blocking the click handler each time. One refresh, then in-memory lookups, is both cheaper and gives every sub
/// in the same resolve pass a consistent view of <c>origin/main</c>.
/// </para>
/// </summary>
internal interface IEpicSubMergeChecker
{
    /// <summary>
    /// Loads (or reloads) what <c>origin/main</c> looks like right now — call once before any <see cref="IsMerged"/> call.
    /// </summary>
    Task RefreshAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Whether <paramref name="issueId"/>'s work is already in <c>origin/main</c> — true/false when it could be
    /// determined, or null when it could not (no <see cref="RefreshAsync"/> yet, no repository, no remote, a git
    /// failure). Null is deliberately not the same as false: a caller that cannot tell must not treat that as "not
    /// merged yet" and quietly re-run a sub that may already be delivered — it should pause and say so instead.
    /// </summary>
    bool? IsMerged(string issueId);

    /// <summary>
    /// The sha the checked branch's remote tip stood at when <see cref="RefreshAsync"/> last succeeded, or null when it
    /// could not be read — what the merge gate's red-build record (AC-1338) is compared against.
    /// </summary>
    string? TipSha { get; }
}

// The real `IEpicSubMergeChecker` (AC-346): a sub counts as merged when a commit *subject line* (never its
// body) in `origin/main`'s history starts with its exact ticket number, word-boundary matched in .NET rather
// than via `git log --grep`. `collectionBranch` (AC-1337), when given, checks that branch instead of `main`.
internal sealed class GitEpicSubMergeChecker(string repositoryDirectory, string? collectionBranch = null) : IEpicSubMergeChecker
{
    private IReadOnlyList<string>? _subjects;

    public string? TipSha { get; private set; }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        _subjects = null;
        TipSha = null;

        if (string.IsNullOrWhiteSpace(repositoryDirectory) || !Directory.Exists(repositoryDirectory))
        {
            return;
        }

        // A collection branch that does not exist on origin yet (an epic's very first click, before any sub ever
        // pushed to it) is not a failure to report — nothing has merged there, so main is exactly the right answer.
        var branch = string.IsNullOrWhiteSpace(collectionBranch) ? "main" : collectionBranch;
        _ = await GitCommandLine.RunAsync("git", ["fetch", "origin", branch], repositoryDirectory, cancellationToken);

        var result = await GitCommandLine.RunAsync("git", ["log", $"origin/{branch}", "--format=%s"], repositoryDirectory, cancellationToken);
        if (!result.Ok && branch != "main")
        {
            branch = "main";
            _ = await GitCommandLine.RunAsync("git", ["fetch", "origin", branch], repositoryDirectory, cancellationToken);
            result = await GitCommandLine.RunAsync("git", ["log", $"origin/{branch}", "--format=%s"], repositoryDirectory, cancellationToken);
        }

        if (!result.Ok)
        {
            return;
        }

        // The tip of the same ref the log above read (AC-1338) — the collection branch, or main when it fell back.
        var tip = await GitCommandLine.RunAsync("git", ["rev-parse", "--verify", "--quiet", $"origin/{branch}"], repositoryDirectory, cancellationToken);
        TipSha = tip.Ok ? tip.StdOut.Trim() : null;
        _subjects = result.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    public bool? IsMerged(string issueId)
    {
        if (_subjects is not { } subjects)
        {
            // No successful RefreshAsync — a missing repository, no origin/main, a git failure. "Cannot tell" is not
            // "not merged": the caller pauses on this rather than silently re-running a sub that may already be done.
            return null;
        }

        if (string.IsNullOrWhiteSpace(issueId))
        {
            return false;
        }

        // A trailing character that could extend the id (another digit/letter) disqualifies the match — "^AC-3" must
        // not catch "AC-34 - …"; a non-alphanumeric (space, dash, colon, end of line) after it is fine.
        var pattern = "^" + Regex.Escape(issueId) + "(?![A-Za-z0-9])";
        return subjects.Any(subject => Regex.IsMatch(subject, pattern));
    }
}
