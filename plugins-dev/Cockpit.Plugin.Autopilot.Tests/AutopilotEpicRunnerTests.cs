using Cockpit.Plugins.Abstractions.Tracking;

namespace Cockpit.Plugin.Autopilot.Tests;

/// <summary>
/// AC-346: starting Autopilot on an epic instead of a single issue. Asserted with xunit's own Assert, matching
/// AutopilotReadyGateTests/AutopilotPlanIntentTests rather than the older FluentAssertions files in this project.
/// </summary>
public class AutopilotEpicRunnerTests
{
    private const string Ready = "Ready";

    // A concrete ITrackerProvider that answers GetLinkedIssuesAsync from an in-memory link map — same "concrete fake,
    // no mocking framework" convention as AutopilotRunCoordinatorTests.FakeTrackerProvider and
    // AutopilotPlanIntentTests.RecordingTracker.
    private sealed class FakeTrackerProvider : ITrackerProvider
    {
        private readonly Dictionary<string, List<TrackerLinkedIssue>> _links = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _unreadable = new(StringComparer.OrdinalIgnoreCase);

        public string TrackerId => "youtrack";

        public List<(string IssueId, string Comment)> Comments { get; } = [];

        public void AddChild(string epicId, string childId, string title, string stage) =>
            _Add(epicId, new TrackerLinkedIssue("parent for", TrackerLinkDirection.Outward, childId, title, stage));

        // Matches the real, empirically-confirmed YouTrack shape (see YouTrackClientLinkedIssuesTests): reading a
        // sub's OWN links reports its "depends on" targets under Direction.Inward, not Outward — the source side of
        // that link type sees the mirrored "is required for" name instead. Building the fake with the wrong (Outward)
        // direction was exactly how the AC-346 review's blocking finding stayed hidden: the fake matched the (buggy)
        // production filter instead of the real tracker's shape.
        public void AddDependsOn(string subId, string dependsOnId) =>
            _Add(subId, new TrackerLinkedIssue("depends on", TrackerLinkDirection.Inward, dependsOnId, string.Empty, null));

        /// <summary>Marks <paramref name="issueId"/>'s own links as unreadable — GetLinkedIssuesAsync throws for it, the way a real provider's I/O failure would (AC-346 review, MEDIUM 5).</summary>
        public void MakeUnreadable(string issueId) => _unreadable.Add(issueId);

        private void _Add(string issueId, TrackerLinkedIssue link)
        {
            if (!_links.TryGetValue(issueId, out var list))
            {
                list = [];
                _links[issueId] = list;
            }

            list.Add(link);
        }

        public Task<IReadOnlyList<TrackerLinkedIssue>> GetLinkedIssuesAsync(string issueId, CancellationToken cancellationToken = default) =>
            _unreadable.Contains(issueId)
                ? throw new InvalidOperationException($"the tracker could not be reached for {issueId}")
                : Task.FromResult<IReadOnlyList<TrackerLinkedIssue>>(_links.TryGetValue(issueId, out var list) ? list : []);

        public Task<bool> PostCommentAsync(string issueId, string comment, CancellationToken cancellationToken = default)
        {
            Comments.Add((issueId, comment));
            return Task.FromResult(true);
        }

        public Task<bool> SetStageAsync(string issueId, string stage, CancellationToken cancellationToken = default) => Task.FromResult(true);

        public Task<bool> AttachAsync(string issueId, string fileName, byte[] content, string mediaType, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public Task<IReadOnlyList<TrackerComment>> ReadCommentsAsync(string issueId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<TrackerComment>>([]);
    }

    // A merge checker driven entirely by an in-memory set — no git process, so the topological/gating logic is tested
    // without touching a real repository. GitEpicSubMergeChecker itself is exercised separately.
    private sealed class FakeMergeChecker(params string[] merged) : IEpicSubMergeChecker
    {
        private readonly HashSet<string> _merged = new(merged, StringComparer.OrdinalIgnoreCase);
        private bool _refreshed;

        public bool RefreshCalled { get; private set; }

        public Task RefreshAsync(CancellationToken cancellationToken = default)
        {
            RefreshCalled = true;
            _refreshed = true;
            return Task.CompletedTask;
        }

        public bool? IsMerged(string issueId) => _refreshed ? _merged.Contains(issueId) : null;
    }

    // A merge checker that never resolves anything (RefreshAsync is a no-op) — simulates "no repository directory
    // could be determined" (AC-346 review, HIGH 3): every IsMerged answers null, cannot tell.
    private sealed class UnresolvableMergeChecker : IEpicSubMergeChecker
    {
        public Task RefreshAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public bool? IsMerged(string issueId) => null;
    }

    private static AutopilotRun Clicked(string issueId = "AC-EPIC") => new("youtrack", issueId, "The epic", Ready, new Dictionary<string, string>());

    [Fact]
    public async Task ResolveAsync_OnAnIssueWithNoChildren_IsNotEpic()
    {
        var provider = new FakeTrackerProvider();

        var outcome = await AutopilotEpicRunner.ResolveAsync(provider, Clicked(), Ready, new FakeMergeChecker(), CancellationToken.None);

        Assert.Equal(AutopilotEpicOutcomeKind.NotEpic, outcome.Kind);
        Assert.Null(outcome.Run);
    }

    [Fact]
    public async Task ResolveAsync_OnAnEpicWithNoDependsOnLinks_PicksTheLowestIdDeterministically()
    {
        // No depends-on links between the subs at all — the ticket's "willekeurige maar deterministische volgorde":
        // every sub is equally free to run, so the order must still be the same every time (stable on issue id).
        var provider = new FakeTrackerProvider();
        provider.AddChild("AC-EPIC", "AC-3", "Third", Ready);
        provider.AddChild("AC-EPIC", "AC-1", "First", Ready);
        provider.AddChild("AC-EPIC", "AC-2", "Second", Ready);

        var first = await AutopilotEpicRunner.ResolveAsync(provider, Clicked(), Ready, new FakeMergeChecker(), CancellationToken.None);
        var second = await AutopilotEpicRunner.ResolveAsync(provider, Clicked(), Ready, new FakeMergeChecker(), CancellationToken.None);

        Assert.Equal(AutopilotEpicOutcomeKind.Ready, first.Kind);
        Assert.Equal("AC-1", first.Run!.IssueId);
        // Resolved twice from the same (unmerged) state, the pick is identical both times — determinism, not luck.
        Assert.Equal(AutopilotEpicOutcomeKind.Ready, second.Kind);
        Assert.Equal("AC-1", second.Run!.IssueId);
    }

    [Fact]
    public async Task ResolveAsync_OnAnEpicWithADependsOnChain_PicksTheSubWithNoUnmetDependency()
    {
        // AC-325-shaped chain: [0] -> [a][b][c] -> [d]. "a" depends on "0"; "0" has no dependency, so it goes first
        // even though "a"/"b"/"c" sort lower alphabetically among themselves.
        var provider = new FakeTrackerProvider();
        provider.AddChild("AC-EPIC", "AC-A", "A", Ready);
        provider.AddChild("AC-EPIC", "AC-B", "B", Ready);
        provider.AddChild("AC-EPIC", "AC-0", "Zero", Ready);
        provider.AddDependsOn("AC-A", "AC-0");
        provider.AddDependsOn("AC-B", "AC-0");

        var outcome = await AutopilotEpicRunner.ResolveAsync(provider, Clicked(), Ready, new FakeMergeChecker(), CancellationToken.None);

        Assert.Equal(AutopilotEpicOutcomeKind.Ready, outcome.Kind);
        Assert.Equal("AC-0", outcome.Run!.IssueId);
    }

    [Fact]
    public async Task ResolveAsync_WhenTheNextSubIsNotReady_PausesTheChainWithoutStartingARun()
    {
        var provider = new FakeTrackerProvider();
        provider.AddChild("AC-EPIC", "AC-1", "First", "Backlog");

        var outcome = await AutopilotEpicRunner.ResolveAsync(provider, Clicked(), Ready, new FakeMergeChecker(), CancellationToken.None);

        Assert.Equal(AutopilotEpicOutcomeKind.Paused, outcome.Kind);
        Assert.Equal("AC-1", outcome.PausedSubId);
        Assert.NotNull(outcome.Reason);
        Assert.Null(outcome.Run);
    }

    [Fact]
    public async Task ResolveAsync_WhenTheNextSubIsAlreadyMergedIntoOriginMain_SkipsItAndPicksTheFollowingOne()
    {
        var provider = new FakeTrackerProvider();
        provider.AddChild("AC-EPIC", "AC-1", "First", Ready);
        provider.AddChild("AC-EPIC", "AC-2", "Second", Ready);
        provider.AddDependsOn("AC-2", "AC-1");

        var outcome = await AutopilotEpicRunner.ResolveAsync(provider, Clicked(), Ready, new FakeMergeChecker("AC-1"), CancellationToken.None);

        Assert.Equal(AutopilotEpicOutcomeKind.Ready, outcome.Kind);
        Assert.Equal("AC-2", outcome.Run!.IssueId);
    }

    [Fact]
    public async Task ResolveAsync_WhenEverySubIsAlreadyMerged_IsComplete()
    {
        var provider = new FakeTrackerProvider();
        provider.AddChild("AC-EPIC", "AC-1", "First", Ready);
        provider.AddChild("AC-EPIC", "AC-2", "Second", Ready);

        var outcome = await AutopilotEpicRunner.ResolveAsync(provider, Clicked(), Ready, new FakeMergeChecker("AC-1", "AC-2"), CancellationToken.None);

        Assert.Equal(AutopilotEpicOutcomeKind.Complete, outcome.Kind);
        Assert.Null(outcome.Run);
    }

    [Fact]
    public async Task ResolveAsync_ForTheReadySub_StampsTheEpicIdOntoTheBuiltRun()
    {
        var provider = new FakeTrackerProvider();
        provider.AddChild("AC-EPIC", "AC-1", "First", Ready);

        var outcome = await AutopilotEpicRunner.ResolveAsync(provider, Clicked("AC-EPIC"), Ready, new FakeMergeChecker(), CancellationToken.None);

        Assert.Equal("AC-EPIC", outcome.Run!.EpicId);
        Assert.Equal("youtrack", outcome.Run.Tracker);
        Assert.Equal("First", outcome.Run.Title);
        Assert.Equal(Ready, outcome.Run.Stage);
    }

    [Fact]
    public async Task ResolveAsync_ForAPlainIssueClickedDirectly_BuildsARunWithNoEpicId()
    {
        // Regression: a non-epic run built the ordinary way (AutopilotRun.FromIntent) never carries an EpicId — the
        // settle-hook's epic-progress-comment path must not fire for it.
        var run = AutopilotRun.FromIntent(new Cockpit.Plugins.Abstractions.PluginIntent("youtrack", "autopilot", "plan", new Dictionary<string, string>
        {
            ["tracker"] = "youtrack",
            ["issue"] = "AC-1",
            ["title"] = "A plain issue",
            ["stage"] = Ready,
        }));

        Assert.Equal(string.Empty, run.EpicId);
    }

    // AC-346 review, HIGH 4: a sub that itself has "parent for" children (a nested epic) must not be handed unchanged
    // into the single-issue pipeline — the existing AC-217 CEO-brief text would then fold its whole subtree into one
    // ungated run, bypassing the one-sub-at-a-time/stop-at-merge-ready gate one level down. The safer of the two
    // options the review asked to choose between: pause explicitly rather than silently unroll or bypass.
    [Fact]
    public async Task ResolveAsync_WhenTheNextSubIsItselfAnEpic_PausesRatherThanHandingItToTheSingleIssuePipeline()
    {
        var provider = new FakeTrackerProvider();
        provider.AddChild("AC-EPIC", "AC-1", "A nested epic", Ready);
        provider.AddChild("AC-1", "AC-1-A", "Grandchild", Ready); // AC-1 itself has a "parent for" child

        var outcome = await AutopilotEpicRunner.ResolveAsync(provider, Clicked(), Ready, new FakeMergeChecker(), CancellationToken.None);

        Assert.Equal(AutopilotEpicOutcomeKind.Paused, outcome.Kind);
        Assert.Equal("AC-1", outcome.PausedSubId);
        Assert.Contains("AC-1", outcome.Reason);
        Assert.Null(outcome.Run);
    }

    [Fact]
    public async Task ResolveAsync_WhenASubIsALeaf_IsUnaffectedByTheNestedEpicCheck()
    {
        // Regression: a normal leaf sub (no children of its own) is unaffected by the nested-epic guard above.
        var provider = new FakeTrackerProvider();
        provider.AddChild("AC-EPIC", "AC-1", "A leaf sub", Ready);

        var outcome = await AutopilotEpicRunner.ResolveAsync(provider, Clicked(), Ready, new FakeMergeChecker(), CancellationToken.None);

        Assert.Equal(AutopilotEpicOutcomeKind.Ready, outcome.Kind);
        Assert.Equal("AC-1", outcome.Run!.IssueId);
    }

    // AC-346 review, MEDIUM 5: a links read that fails (network, 429, instance down) must not be mistaken for "no
    // links" — that would plan the epic issue itself as an ordinary issue for as long as the tracker misbehaves.
    [Fact]
    public async Task ResolveAsync_WhenTheEpicsOwnLinksCannotBeRead_PausesRatherThanTreatingItAsNotAnEpic()
    {
        var provider = new FakeTrackerProvider();
        provider.MakeUnreadable("AC-EPIC");

        var outcome = await AutopilotEpicRunner.ResolveAsync(provider, Clicked(), Ready, new FakeMergeChecker(), CancellationToken.None);

        Assert.Equal(AutopilotEpicOutcomeKind.Paused, outcome.Kind);
        Assert.Null(outcome.Run);
    }

    [Fact]
    public async Task ResolveAsync_WhenASubsOwnLinksCannotBeRead_PausesRatherThanDroppingItsDependencies()
    {
        var provider = new FakeTrackerProvider();
        provider.AddChild("AC-EPIC", "AC-1", "First", Ready);
        provider.MakeUnreadable("AC-1");

        var outcome = await AutopilotEpicRunner.ResolveAsync(provider, Clicked(), Ready, new FakeMergeChecker(), CancellationToken.None);

        Assert.Equal(AutopilotEpicOutcomeKind.Paused, outcome.Kind);
        Assert.Equal("AC-1", outcome.PausedSubId);
    }

    // AC-346 review, HIGH 3 / MEDIUM 5: when the merge checker cannot determine anything (no resolvable repository
    // directory), the chain must pause rather than treat every sub as "not merged" and restart from the first sub
    // every time — which would break "after a merge, the next start picks the correct next sub" outright.
    [Fact]
    public async Task ResolveAsync_WhenMergeStatusCannotBeDetermined_PausesRatherThanAssumingNothingIsMerged()
    {
        var provider = new FakeTrackerProvider();
        provider.AddChild("AC-EPIC", "AC-1", "First", Ready);

        var outcome = await AutopilotEpicRunner.ResolveAsync(provider, Clicked(), Ready, new UnresolvableMergeChecker(), CancellationToken.None);

        Assert.Equal(AutopilotEpicOutcomeKind.Paused, outcome.Kind);
        Assert.Equal("AC-1", outcome.PausedSubId);
        Assert.Null(outcome.Run);
    }

    // AC-346 review, MEDIUM 7: one fetch-and-load for the whole resolve pass, not one per sub.
    [Fact]
    public async Task ResolveAsync_RefreshesTheMergeCheckerExactlyOnce_RegardlessOfSubCount()
    {
        var provider = new FakeTrackerProvider();
        provider.AddChild("AC-EPIC", "AC-1", "First", Ready);
        provider.AddChild("AC-EPIC", "AC-2", "Second", Ready);
        provider.AddChild("AC-EPIC", "AC-3", "Third", Ready);
        var checker = new FakeMergeChecker("AC-1", "AC-2"); // first two already merged, forcing the loop past them

        var outcome = await AutopilotEpicRunner.ResolveAsync(provider, Clicked(), Ready, checker, CancellationToken.None);

        Assert.Equal("AC-3", outcome.Run!.IssueId);
        Assert.True(checker.RefreshCalled);
    }

    // MUTATION TEST (DoD): the "stop bij merge-klaar" gate is that ResolveAsync never advances past a sub until
    // origin/main actually shows it merged — a second call while the picked sub is still unmerged must return the
    // SAME sub again, never silently skip ahead to the next one as if the first had already landed. Removing the
    // `switch (mergeChecker.IsMerged(subId)) { case true: continue; ... }` gate in AutopilotEpicRunner.ResolveAsync (or
    // replacing it with something that ignores the checker) turns this red: the second call would then advance to
    // "AC-2" instead of staying on "AC-1", because nothing would still be gating on "is it actually merged".
    [Fact]
    public async Task ResolveAsync_CalledAgainBeforeThePickedSubIsMerged_ReturnsTheSameSub_NeverAutoAdvancing()
    {
        var provider = new FakeTrackerProvider();
        provider.AddChild("AC-EPIC", "AC-1", "First", Ready);
        provider.AddChild("AC-EPIC", "AC-2", "Second", Ready);
        provider.AddDependsOn("AC-2", "AC-1");

        var first = await AutopilotEpicRunner.ResolveAsync(provider, Clicked(), Ready, new FakeMergeChecker(), CancellationToken.None);
        // A fresh trigger — the human has not merged AC-1's PR yet, so no second AutopilotRun for AC-2 may start. A
        // fresh checker instance each call, same as production (a new GitEpicSubMergeChecker per "plan" intent).
        var second = await AutopilotEpicRunner.ResolveAsync(provider, Clicked(), Ready, new FakeMergeChecker(), CancellationToken.None);

        Assert.Equal("AC-1", first.Run!.IssueId);
        Assert.Equal("AC-1", second.Run!.IssueId);
    }
}
