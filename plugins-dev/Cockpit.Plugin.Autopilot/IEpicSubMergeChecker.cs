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
    /// <summary>Loads (or reloads) what <c>origin/main</c> looks like right now — call once before any <see cref="IsMerged"/> call.</summary>
    Task RefreshAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Whether <paramref name="issueId"/>'s work is already in <c>origin/main</c> — true/false when it could be
    /// determined, or null when it could not (no <see cref="RefreshAsync"/> yet, no repository, no remote, a git
    /// failure). Null is deliberately not the same as false: a caller that cannot tell must not treat that as "not
    /// merged yet" and quietly re-run a sub that may already be delivered — it should pause and say so instead.
    /// </summary>
    bool? IsMerged(string issueId);
}

/// <summary>
/// The real <see cref="IEpicSubMergeChecker"/> (AC-346): a sub counts as merged when a commit <em>subject line</em>
/// (the commit message's first line — never its body, which can casually mention another ticket's id in a bullet) in
/// <c>origin/main</c>'s history starts with its exact ticket number — this project's own commit format (<c>{TICKET}</c>
/// on the first line, see this repo's own <c>git log</c>). "Starts with, followed by something that cannot extend the
/// id" (a word-boundary match, not a bare prefix) is deliberate: <c>git log --grep="^AC-3"</c> also matches
/// "AC-34 - …" and "AC-350 - …", which would read a sibling sub as merged the moment any commit with a colliding
/// numeric prefix landed anywhere in history — found by an independent review, reproduced on a throwaway repo. Matching
/// is therefore done in .NET against <c>git log --format=%s</c> (subject lines only) rather than through git's own
/// <c>--grep</c>, so both the "first line only" and the "exact id, not a prefix" rules are enforced the same way
/// regardless of which regex engine the local git build happens to use.
/// <para>
/// Checked against <c>origin/main</c>, never a local branch or worktree (the ticket's own wording): a run's local
/// worktree can carry a sub's commits before they are actually merged, and trusting that would let the epic-runner
/// "see" a step as done before the human has merged its PR. <c>git fetch</c> first so a merge someone else just
/// clicked through is not missed by a stale local ref — best-effort, since an offline box or a repo with no configured
/// remote should still fall back to whatever <c>origin/main</c> already points at locally rather than refuse the whole
/// check.
/// </para>
/// </summary>
internal sealed class GitEpicSubMergeChecker(string repositoryDirectory) : IEpicSubMergeChecker
{
    private IReadOnlyList<string>? _subjects;

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        _subjects = null;

        if (string.IsNullOrWhiteSpace(repositoryDirectory) || !Directory.Exists(repositoryDirectory))
        {
            return;
        }

        _ = await GitCommandLine.RunAsync("git", ["fetch", "origin", "main"], repositoryDirectory, cancellationToken);

        var result = await GitCommandLine.RunAsync("git", ["log", "origin/main", "--format=%s"], repositoryDirectory, cancellationToken);
        if (!result.Ok)
        {
            return;
        }

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
