using System.Text.RegularExpressions;

namespace Cockpit.Plugin.Autopilot;

// The real `IAutopilotEpicGateExecutor` (AC-1341): drives the operator's own `git`/`gh`/`dotnet` in the settled run's
// worktree, which the merge gate left standing on the collection branch's tip. The collection branch is only ever
// moved by the plain push git refuses when it is not a fast-forward — never `--force`.
internal sealed class GitCliEpicGateExecutor : IAutopilotEpicGateExecutor
{
    // Where the suite's TRX files go, under the worktree's git-ignored TestResults — one directory per pinned tip.
    private const string ResultsPlaceholder = "{results}";

    // A commit's own section in a `git log` read: the record separator, then its subject, then what the option asked for.
    private const char CommitSeparator = '\x1e';

    private static readonly Regex TestAttribute = new(@"^\+(?!\+\+)\s*\[(Fact|Theory|InlineData)\b", RegexOptions.Compiled);
    private static readonly Regex RemovedTestAttribute = new(@"^-(?!--)\s*\[(Fact|Theory|InlineData)\b", RegexOptions.Compiled);

    public async Task<AutopilotEpicGateMeasurement> MeasureAsync(string worktreePath, string collectionBranch, IReadOnlyList<string> subIds, string suiteCommand, TimeSpan suiteTimeout, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(worktreePath) || !Directory.Exists(worktreePath))
        {
            return _Failed(string.Empty, string.Empty, "not attempted", "The run worktree no longer exists.");
        }

        // EpicWorkflow §14 step 0: main first. Everything measured before this step is a state that is not going to
        // be merged, so a main that cannot be brought in ends the measurement rather than being measured around.
        var fetch = await GitCommandLine.RunAsync("git", ["fetch", "origin", "main"], worktreePath, cancellationToken);
        if (!fetch.Ok)
        {
            return _Failed(string.Empty, string.Empty, "not attempted", $"fetch of origin/main failed: {fetch.Error}");
        }

        var mainSha = (await GitCommandLine.RunAsync("git", ["rev-parse", "origin/main"], worktreePath, cancellationToken)).StdOut.Trim();
        var mainShort = mainSha.Length >= 7 ? mainSha[..7] : mainSha;
        var before = (await GitCommandLine.RunAsync("git", ["rev-parse", "HEAD"], worktreePath, cancellationToken)).StdOut.Trim();
        string mainBroughtIn;
        if ((await GitCommandLine.RunAsync("git", ["merge-base", "--is-ancestor", "origin/main", "HEAD"], worktreePath, cancellationToken)).Ok)
        {
            mainBroughtIn = $"the tip already contains origin/main ({mainShort})";
        }
        else
        {
            var merge = await GitCommandLine.RunAsync("git", ["merge", "--no-edit", "origin/main"], worktreePath, cancellationToken);
            if (!merge.Ok)
            {
                _ = await GitCommandLine.RunAsync("git", ["merge", "--abort"], worktreePath, cancellationToken);
                return _Failed(before, mainSha, "not merged", $"origin/main ({mainShort}) did not merge cleanly into {collectionBranch}: {merge.Error}");
            }

            var push = await GitCommandLine.RunAsync("git", ["push", "origin", $"HEAD:refs/heads/{collectionBranch}"], worktreePath, cancellationToken);
            if (!push.Ok)
            {
                return _Failed(before, mainSha, "merged locally only", $"the merge of origin/main could not be pushed to {collectionBranch}: {push.Error}");
            }

            mainBroughtIn = $"merged origin/main ({mainShort}) into {collectionBranch} and pushed";
        }

        // Pinned before the suite starts (§5): the branch moves under a run, the sha in the report does not.
        var tip = (await GitCommandLine.RunAsync("git", ["rev-parse", "HEAD"], worktreePath, cancellationToken)).StdOut.Trim();
        var resultsDirectory = Path.Combine(worktreePath, "TestResults", $"epic-gate-{(tip.Length >= 7 ? tip[..7] : tip)}");
        Directory.CreateDirectory(resultsDirectory);

        var commandLine = suiteCommand
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(part => part.Replace(ResultsPlaceholder, resultsDirectory, StringComparison.Ordinal))
            .ToList();
        var (exitCode, tail) = await GitCliMergeExecutor.RunToVerdictAsync(worktreePath, commandLine, suiteTimeout, cancellationToken);

        var subs = await _MeasureSubsAsync(worktreePath, subIds, cancellationToken);
        return new AutopilotEpicGateMeasurement(tip, mainSha, mainBroughtIn, exitCode, tail, resultsDirectory, subs.Subs, subs.Error);
    }

    public async Task<AutopilotPrPublishResult> OpenPullRequestAsync(string worktreePath, string collectionBranch, string title, string body, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(worktreePath) || !Directory.Exists(worktreePath))
        {
            return new AutopilotPrPublishResult(true, null, "The run worktree no longer exists.");
        }

        if (!(await GitCommandLine.RunAsync("gh", ["auth", "status"], worktreePath, cancellationToken)).Ok)
        {
            return new AutopilotPrPublishResult(true, null, $"gh is not available or not logged in — open the pull request from {collectionBranch} to main by hand.");
        }

        // The branch is already on origin (every merge gate pushed it); only the PR is missing.
        var pr = await GitCommandLine.RunAsync("gh", ["pr", "create", "--base", "main", "--head", collectionBranch, "--title", title, "--body", body], worktreePath, cancellationToken);
        return pr.Ok
            ? new AutopilotPrPublishResult(true, pr.StdOut.Trim(), null)
            : new AutopilotPrPublishResult(true, null, pr.Error);
    }

    // Per merged sub, from the commits the epic put on top of main whose subject starts with the sub's id (the same
    // rule GitEpicSubMergeChecker counts a merge by): the test files they added, which are gone from the tip, and
    // the net test attributes they added.
    private static async Task<(IReadOnlyList<AutopilotEpicGateSubMeasurement> Subs, string? Error)> _MeasureSubsAsync(string worktreePath, IReadOnlyList<string> subIds, CancellationToken cancellationToken)
    {
        var added = await GitCommandLine.RunAsync("git", ["log", $"--format={CommitSeparator}%s", "--name-only", "--diff-filter=A", "origin/main..HEAD"], worktreePath, cancellationToken);
        var patches = await GitCommandLine.RunAsync("git", ["log", $"--format={CommitSeparator}%s", "-p", "origin/main..HEAD", "--", "*Tests*"], worktreePath, cancellationToken);
        var tree = await GitCommandLine.RunAsync("git", ["ls-tree", "-r", "--name-only", "HEAD"], worktreePath, cancellationToken);
        if (!added.Ok || !patches.Ok || !tree.Ok)
        {
            return ([], $"the sub measurement could not read the branch: {(!added.Ok ? added.Error : !patches.Ok ? patches.Error : tree.Error)}");
        }

        var onTip = tree.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.Ordinal);
        var subs = new List<AutopilotEpicGateSubMeasurement>();
        foreach (var subId in subIds)
        {
            var files = _Commits(added.StdOut, subId)
                .SelectMany(lines => lines)
                .Where(IsTestFile)
                .Distinct(StringComparer.Ordinal)
                .ToList();
            var attributes = _Commits(patches.StdOut, subId)
                .SelectMany(lines => lines)
                .Sum(line => TestAttribute.IsMatch(line) ? 1 : RemovedTestAttribute.IsMatch(line) ? -1 : 0);
            subs.Add(new AutopilotEpicGateSubMeasurement(subId, files, files.Where(file => !onTip.Contains(file)).ToList(), attributes));
        }

        return (subs, null);
    }

    // The body lines of every commit in a separator-formatted log whose subject starts with `subId`.
    private static IEnumerable<string[]> _Commits(string log, string subId)
    {
        var pattern = new Regex("^" + Regex.Escape(subId) + "(?![A-Za-z0-9])");
        foreach (var section in log.Split(CommitSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var lines = section.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length > 0 && pattern.IsMatch(lines[0].Trim()))
            {
                yield return lines.Skip(1).Select(line => line.TrimEnd('\r')).ToArray();
            }
        }
    }

    // A test file by where it lives: under a `*.Tests` project or named `*Tests.cs` — the shapes this repository uses.
    internal static bool IsTestFile(string path)
    {
        var trimmed = path.Trim();
        return trimmed.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
            && (trimmed.EndsWith("Tests.cs", StringComparison.OrdinalIgnoreCase) || trimmed.Contains(".Tests/", StringComparison.OrdinalIgnoreCase));
    }

    private static AutopilotEpicGateMeasurement _Failed(string tip, string mainSha, string mainBroughtIn, string error) =>
        new(tip, mainSha, mainBroughtIn, null, string.Empty, string.Empty, [], error);
}
