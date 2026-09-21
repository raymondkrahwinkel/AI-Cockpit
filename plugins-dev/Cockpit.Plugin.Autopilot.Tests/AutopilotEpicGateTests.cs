namespace Cockpit.Plugin.Autopilot.Tests;

// AC-1341: the mid- and end-of-epic gate, driven through the chain the way the settle-hook drives it. The TRX files
// are real (written by the rows, read by the gate), so the red-set classification is satisfied only by what the
// logger wrote — EpicWorkflow §4 — while git, dotnet, the tracker and the go are delegates, as in AutopilotEpicChainTests.
public sealed class AutopilotEpicGateTests : IDisposable
{
    private const string Epic = "AC-EPIC";
    private const string Collection = "epic/ac-epic";
    private const string AddedTestFile = "plugins-dev/Cockpit.Plugin.Autopilot.Tests/AutopilotEpicGateTests.cs";

    // What origin/main stands at when the gate measures; a baseline recorded on an older commit classifies under a caveat.
    private const string MainSha = "def5678abcdef0123456789abcdef0123456789a";

    private static readonly string[] FiveMerged = ["AC-1", "AC-2", "AC-3", "AC-4", "AC-5"];

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"ac1341-{Guid.NewGuid():N}");
    private readonly List<string> _comments = [];
    private readonly List<string> _notes = [];
    private readonly List<string> _started = [];
    private readonly List<string> _pullRequests = [];

    private static readonly AutopilotRunRecord Settled =
        new("run", "goal", AutopilotPlanPhase.MergeReady, null, "2026-09-21T00:00:00+00:00", [new AutopilotRunStepRecord("Build", AutopilotStepStatus.Passed, string.Empty)])
        {
            Ticket = "AC-5",
            EpicId = Epic,
        };

    private static readonly AutopilotMergeResult Green = new(true, "pr merge", "tip-green", 0, string.Empty, null);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    // Columns: whether AC-1's added test file is still on the tip, the tests red on the tip, the tests red on the
    // baseline and the commit its manifest says it ran on — then whether the chain went on to the next sub, and the
    // fragments the report carries. The rows are opposites per column, so no row is satisfied by another's reading.
    public static IEnumerable<object?[]> MidGates() =>
    [
        // §6d: red on both is the environment; the file is present, so nothing is found and the chain goes on.
        [true, new[] { "Suite.Flaky" }, new[] { "Suite.Flaky" }, MainSha, true, new[] { "environment (also red on the baseline): Suite.Flaky", "ours (not red on the baseline): none", "AC-1: 1 added, all still on the tip", "Findings: none." }],
        // Red on the tip and not on the baseline is ours; the file is gone — two findings, and the chain holds.
        [false, new[] { "Suite.Broken" }, new[] { "Suite.Flaky" }, MainSha, false, new[] { "ours (not red on the baseline): Suite.Broken", "MISSING on the tip: " + AddedTestFile, "Findings (2):" }],
        // A baseline recorded on an older main still classifies (§6d is sha-independent) — the caveat is named, not a finding.
        [true, new[] { "Suite.Flaky" }, new[] { "Suite.Flaky" }, "0ld0000000000000000000000000000000000000", true, new[] { "recorded at 0ld0000, origin/main is now def5678 — environment classification is against an older main; a test fixed on main since then and red on the tip reads as environment here.", "environment (also red on the baseline): Suite.Flaky", "Findings: none." }],
    ];

    [Theory]
    [MemberData(nameof(MidGates))]
    public async Task ContinueAsync_AtTheMidGate_ReadsTheTipAgainstTheBaseline_AndHoldsOnAFinding(bool filePresent, string[] tipRed, string[] baselineRed, string baselineSha, bool expectedChained, string[] expectedFragments)
    {
        var results = _WriteTrx("tip", tipRed, sha: null);
        var baseline = _WriteTrx("baseline", baselineRed, baselineSha);
        var executor = new FakeExecutor(results, filePresent, _pullRequests);
        var chain = _Chain(_Gate(executor, baseline, (_, _) => Task.FromResult(new AutopilotMergeGo(false, "not asked mid-epic", "test"))), _NextReady());

        var stop = await chain.ContinueAsync(Settled, [Settled], Green, strandedCommits: false);

        Assert.Equal(expectedChained, stop is null);
        Assert.Equal(expectedChained, _started.Count == 1);
        Assert.Empty(_pullRequests);
        var report = Assert.Single(_comments, comment => comment.StartsWith("Autopilot epic gate (mid-epic, after 5 merged sub(s))", StringComparison.Ordinal));
        Assert.All(expectedFragments, fragment => Assert.Contains(fragment, report));
        Assert.Contains("AC-1: 3, budget 5", report);
        Assert.Contains(report, _notes);
    }

    // Columns: the answer at the end gate — then how many pull requests were opened and what the epic was told.
    [Theory]
    [InlineData(false, 0, "no pull request from epic/ac-epic to main for AC-EPIC — refused by the reviewer: not yet")]
    [InlineData(true, 1, "the pull request from epic/ac-epic to main for AC-EPIC is open on a go from the reviewer: https://example.test/pr/1")]
    public async Task ContinueAsync_AtTheEndGate_OpensThePullRequestToMainOnlyOnAGo(bool go, int expectedPullRequests, string expectedFragment)
    {
        var executor = new FakeExecutor(_WriteTrx("tip", [], sha: null), filePresent: true, _pullRequests);
        var chain = _Chain(_Gate(executor, null, (_, _) => Task.FromResult(new AutopilotMergeGo(go, "not yet", "the reviewer"))), AutopilotEpicOutcome.Complete with { MergedSubs = FiveMerged });

        var stop = await chain.ContinueAsync(Settled, [Settled], Green, strandedCommits: false);

        Assert.Equal("every sub is merged", stop);
        Assert.Equal(expectedPullRequests, _pullRequests.Count);
        Assert.All(_pullRequests, title => Assert.Equal("AC-EPIC — epic/ac-epic to main", title));
        // The go is asked on the report, findings included — here the one for a baseline that was never set.
        Assert.Contains(_comments, comment => comment.StartsWith("Autopilot epic gate (end of epic)", StringComparison.Ordinal) && comment.Contains("no verdict on the red set: no baseline directory is set", StringComparison.Ordinal));
        Assert.Contains(_comments, comment => comment.Contains(expectedFragment, StringComparison.Ordinal));
        Assert.Contains(_notes, note => note.Contains("autopilot_merge_go(issue: \"AC-EPIC\", go: true|false, reason)", StringComparison.Ordinal));
    }

    private AutopilotEpicGate _Gate(FakeExecutor executor, string? baselineDirectory, Func<string, CancellationToken, Task<AutopilotMergeGo>> awaitGo) =>
        new(
            executor,
            Epic,
            Collection,
            _root,
            "dotnet test --logger:trx --results-directory {results}",
            TimeSpan.FromMinutes(1),
            baselineDirectory,
            (_, _) => Task.FromResult<string?>("Scope.\n\n**Testbudget:** 5."),
            awaitGo,
            (text, _) =>
            {
                _comments.Add(text);
                return Task.CompletedTask;
            },
            text =>
            {
                _notes.Add(text);
                return Task.FromResult(true);
            });

    private AutopilotEpicChain _Chain(AutopilotEpicGate gate, AutopilotEpicOutcome next) =>
        new(
            _ => Task.FromResult(next),
            run =>
            {
                _started.Add(run.IssueId);
                return Task.FromResult<string?>(null);
            },
            (text, _) =>
            {
                _comments.Add(text);
                return Task.CompletedTask;
            },
            text =>
            {
                _notes.Add(text);
                return Task.FromResult(true);
            },
            uncleanRunTolerance: 0,
            gate.RunAsync,
            gateEverySubs: 5);

    private static AutopilotEpicOutcome _NextReady() =>
        AutopilotEpicOutcome.Ready(new AutopilotRun("youtrack", "AC-6", "Sixth", "Ready", new Dictionary<string, string>())) with { MergedSubs = FiveMerged };

    // One TRX in its own directory, in the shape the VSTest logger writes: a passed test that is on both sides, and
    // the row's failed tests — plus, for a baseline, the manifest with the commit it ran on.
    private string _WriteTrx(string name, string[] failed, string? sha)
    {
        var directory = Path.Combine(_root, name);
        Directory.CreateDirectory(directory);
        if (sha is not null)
        {
            File.WriteAllText(Path.Combine(directory, AutopilotTrxResults.ManifestFileName), "{\"sha\": \"" + sha + "\"}");
        }

        var results = string.Concat(failed.Select(test => $"<UnitTestResult testName=\"{test}\" outcome=\"Failed\" />"));
        File.WriteAllText(
            Path.Combine(directory, "run.trx"),
            "<?xml version=\"1.0\" encoding=\"utf-8\"?><TestRun xmlns=\"http://microsoft.com/schemas/VisualStudio/TeamTest/2010\"><Results>"
            + "<UnitTestResult testName=\"Suite.Steady\" outcome=\"Passed\" />" + results + "</Results></TestRun>");
        return directory;
    }

    // The git/dotnet seam: the suite "ran" into the directory the row already wrote, and only AC-1 added a test file.
    private sealed class FakeExecutor(string resultsDirectory, bool filePresent, List<string> pullRequests) : IAutopilotEpicGateExecutor
    {
        public Task<AutopilotEpicGateMeasurement> MeasureAsync(string worktreePath, string collectionBranch, IReadOnlyList<string> subIds, string suiteCommand, TimeSpan suiteTimeout, CancellationToken cancellationToken = default)
        {
            var subs = subIds
                .Select(sub => sub == "AC-1"
                    ? new AutopilotEpicGateSubMeasurement(sub, [AddedTestFile], filePresent ? [] : [AddedTestFile], 3)
                    : new AutopilotEpicGateSubMeasurement(sub, [], [], 0))
                .ToList();
            return Task.FromResult(new AutopilotEpicGateMeasurement("abc1234", MainSha, "merged origin/main (def5678) into epic/ac-epic and pushed", 1, "1 failed", resultsDirectory, subs, null));
        }

        public Task<AutopilotPrPublishResult> OpenPullRequestAsync(string worktreePath, string collectionBranch, string title, string body, CancellationToken cancellationToken = default)
        {
            pullRequests.Add(title);
            return Task.FromResult(new AutopilotPrPublishResult(true, "https://example.test/pr/1", null));
        }
    }
}
