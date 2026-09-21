namespace Cockpit.Plugin.Autopilot;

// What one merged sub left on the collection branch (AC-1341, EpicWorkflow §5): the test files its commits added,
// which of those are gone from the pinned tip, and how many test attributes (`[Fact]`, `[Theory]`, `[InlineData`)
// its commits added net — the count the ticket's test budget is read against.
internal sealed record AutopilotEpicGateSubMeasurement(string IssueId, IReadOnlyList<string> AddedTestFiles, IReadOnlyList<string> MissingTestFiles, int TestAttributesAdded);

// What the epic gate measured (AC-1341): the pinned tip the suite ran on after main was brought in (§14 step 0), the
// origin/main sha a baseline must match, a null `SuiteExitCode` for a suite without a verdict, where the TRX went,
// and an `Error` that is null only when every step ran — "could not measure" never reads as "found nothing".
internal sealed record AutopilotEpicGateMeasurement(
    string TipSha,
    string MainSha,
    string MainBroughtIn,
    int? SuiteExitCode,
    string SuiteOutputTail,
    string ResultsDirectory,
    IReadOnlyList<AutopilotEpicGateSubMeasurement> Subs,
    string? Error);

/// <summary>
/// The git/dotnet work behind the epic gate (AC-1341) — the injectable seam, like <see cref="IAutopilotMergeExecutor"/>,
/// so the gate's judgement is tested with a fake and the real <see cref="GitCliEpicGateExecutor"/> drives the operator's
/// own CLIs in the app. Never throws: a fault comes back in the result, because a gate fault must not crash a settle.
/// </summary>
internal interface IAutopilotEpicGateExecutor
{
    /// <summary>
    /// In <paramref name="worktreePath"/>, standing on <paramref name="collectionBranch"/>'s tip: merges <c>origin/main</c>
    /// in and pushes, pins the sha, runs <paramref name="suiteCommand"/> (its <c>{results}</c> replaced by a fresh TRX directory)
    /// within <paramref name="suiteTimeout"/>, and reads what each of <paramref name="subIds"/> added in test files and test attributes. Never throws.
    /// </summary>
    Task<AutopilotEpicGateMeasurement> MeasureAsync(string worktreePath, string collectionBranch, IReadOnlyList<string> subIds, string suiteCommand, TimeSpan suiteTimeout, CancellationToken cancellationToken = default);

    /// <summary>
    /// Opens the pull request from <paramref name="collectionBranch"/> to <c>main</c> (EpicWorkflow §14 step 7); the result
    /// carries its url, or the reason none was opened. Never throws.
    /// </summary>
    Task<AutopilotPrPublishResult> OpenPullRequestAsync(string worktreePath, string collectionBranch, string title, string body, CancellationToken cancellationToken = default);
}
