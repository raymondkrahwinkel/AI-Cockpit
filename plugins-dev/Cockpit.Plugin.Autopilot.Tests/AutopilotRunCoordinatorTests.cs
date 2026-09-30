using Avalonia.Controls;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Sessions;
using Cockpit.Plugins.Abstractions.Tracking;
using Cockpit.Plugins.Abstractions.Workspaces;
using NSubstitute;

namespace Cockpit.Plugin.Autopilot.Tests;

// The executeStep adapter behind the run-driver (AC-174): per step it embeds an agent session, awaits its
// done-report, has the still-live CEO validate the result, and returns pass/fail. The driver's own loop is tested
// separately; here it is the coordination and the pane gates (which pane may report done, validate, or raise a blockade).
[Collection("avalonia")]
public class AutopilotRunCoordinatorTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Fact]
    public void ReportStepDone_FromAPaneThatIsNotAnActiveStep_IsRejected()
    {
        var coordinator = new AutopilotRunCoordinator(Substitute.For<ICockpitHost>(), new AutopilotPlanController());

        Assert.False(coordinator.ReportStepDone("nobody", "done"));
    }

    [Fact]
    public void ReportValidation_WithNoValidationPending_OrTheWrongPane_IsRejected()
    {
        var plan = new AutopilotPlanController();
        plan.BindSession("ceo-pane");
        var coordinator = new AutopilotRunCoordinator(Substitute.For<ICockpitHost>(), plan);

        Assert.False(coordinator.ReportValidation("ceo-pane", passed: true, reason: null));
        Assert.False(coordinator.ReportValidation("intruder", passed: true, reason: null));
    }

    // AC-1337: an epic run's collection branch (environment.CollectionBranch) becomes the PR's base — unset opens
    // against the default branch, exactly as v1 did, since AutopilotPrRequest.Base then stays null.

    // Records what it was asked to publish, so a test can assert on the request instead of on what a real gh/git
    // process would have done with it.
    private sealed class CapturingPrPublisher : IAutopilotPrPublisher
    {
        public AutopilotPrRequest? LastRequest { get; private set; }

        public Task<AutopilotPrProbe> ProbeAsync(string worktreePath, CancellationToken cancellationToken = default) =>
            Task.FromResult(new AutopilotPrProbe(IsGitRun: true, HasRemote: true, GhAvailable: true));

        public Task<AutopilotPrPublishResult> PublishAsync(AutopilotPrRequest request, bool createPullRequest, CancellationToken cancellationToken = default)
        {
            LastRequest = request;
            return Task.FromResult(new AutopilotPrPublishResult(Pushed: true, PrUrl: "https://example/pr/1", Error: null));
        }

        public Task<bool> EnsureCommittedAsync(string worktreePath, string message, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public Task<AutopilotStrayCommits> RecoverStrayCommitsAsync(string runWorktreePath, string runBranch, string stepWorktreePath, CancellationToken cancellationToken = default) =>
            Task.FromResult(AutopilotStrayCommits.None);
    }

    [Fact]
    public async Task ReportTrackerStageAsync_FromANonCeoPane_IsRejected_AndDoesNotTouchTheTracker()
    {
        var plan = _RunningPlanWithSource(new AutopilotPlanSource("youtrack", "AC-1", "t"), _HardStep("1"));
        var provider = Substitute.For<ITrackerProvider>();
        provider.TrackerId.Returns("youtrack");
        var host = _Host();
        host.TrackerProviders.Returns(new[] { provider });
        var coordinator = new AutopilotRunCoordinator(host, plan);

        Assert.False((await coordinator.ReportTrackerStageAsync("intruder", "Review")));
        await provider.DidNotReceive().SetStageAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // AC-202 automatic phase→stage mapping: the coordinator itself moves the source issue as the run crosses a lifecycle
    // edge (start → in-progress, merge-ready → review), so the stage no longer hangs on the CEO calling autopilot_tracker_stage.

    // A concrete tracker provider that records SetStageAsync calls (a substitute cannot intercept SuggestStageName — it
    // is a default interface method), mapping the neutral stages to the AC board's own vocabulary like YouTrack does.
    private sealed class FakeTrackerProvider(string trackerId, bool throwOnSet = false) : ITrackerProvider
    {
        public string TrackerId => trackerId;

        public List<(string IssueId, string Stage)> StageCalls { get; } = [];

        public string? SuggestStageName(TrackerWorkStage stage) => stage switch
        {
            TrackerWorkStage.InProgress => "Develop",
            TrackerWorkStage.InReview => "Review",
            TrackerWorkStage.InTest => "Test",
            TrackerWorkStage.Done => "Done",
            _ => null,
        };

        public List<(string IssueId, string Comment)> Comments { get; } = [];

        public Task<bool> SetStageAsync(string issueId, string stage, CancellationToken cancellationToken = default)
        {
            if (throwOnSet)
            {
                throw new InvalidOperationException("tracker down");
            }

            lock (StageCalls)
            {
                StageCalls.Add((issueId, stage));
            }

            return Task.FromResult(true);
        }

        public Task<bool> PostCommentAsync(string issueId, string comment, CancellationToken cancellationToken = default)
        {
            lock (Comments)
            {
                Comments.Add((issueId, comment));
            }

            return Task.FromResult(true);
        }

        public Task<bool> AttachAsync(string issueId, string fileName, byte[] content, string mediaType, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task<IReadOnlyList<TrackerComment>> ReadCommentsAsync(string issueId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<TrackerComment>>([]);
    }

    // AC-1338: the merge gate. An epic run's sub whose review gate passed and whose PR was pushed stands at the gate:
    // the evidence package goes to the assistant, and only a go (explicit mode) or the mode itself (automatic) lands
    // it. The counter-proofs are the rows: no go → nothing merged; refused → nothing merged; red build → nothing merged.

    [Theory]
    [InlineData(true, 0, 1, 1, "Test")]
    [InlineData(true, 1, 1, 1, "Review")]
    [InlineData(false, 0, 0, 0, "Review")]
    public async Task RunAsync_AtTheMergeGate_InExplicitMode_MergesOnlyOnAGo(bool go, int mergeBuildExit, int expectedMerges, int expectedLedgerWrites, string expectedLastStage)
    {
        var (plan, provider, host, storage, executor, coordinator, environment) = _AtTheGate(automatic: false, buildExit: 0, mergeBuildExit);
        var context = _Context(_Session("step-pane"));
        var shown = new TaskCompletionSource();
        var validationSent = new TaskCompletionSource();
        host.When(h => h.SendToSessionAsync("ceo-pane", Arg.Any<string>())).Do(_ => validationSent.TrySetResult());

        var run = coordinator.RunAsync(context.EmbedSession, _Session("ceo-pane"), _Settings(), _ => shown.TrySetResult(), _ => { }, environment, _DirectUi, CancellationToken.None);
        await shown.Task.WaitAsync(Timeout);
        Assert.True(coordinator.ReportStepDone("step-pane", "reviewed"));
        await validationSent.Task.WaitAsync(Timeout);
        Assert.True(coordinator.ReportValidation("ceo-pane", passed: true, reason: "clean"));

        await _Until(() => coordinator.AwaitingMergeGo);
        // The package the assistant judges carries the PR, the HEAD, the gate's verdict and the three-dot diff-stat —
        // with the deletion of a file another sub added, which is the failure this gate exists to catch.
        await host.Received(1).NotifyAssistantAsync("merge-gate", Arg.Is<string>(body =>
            body.Contains("https://example/pr/1") && body.Contains("abc1234") && body.Contains("Code review: Passed") && body.Contains("src/OtherSub.cs | 12 ------------")));
        Assert.Equal(0, executor.MergeCalls);

        // A go for another sub never lands on this one.
        Assert.False(coordinator.ReportMergeGo("AC-2", go: true, reason: null, by: "a stranger"));
        Assert.True(coordinator.ReportMergeGo("AC-1", go, reason: "read the diff", by: "the test"));

        await run.WaitAsync(Timeout);
        Assert.False(coordinator.AwaitingMergeGo);
        Assert.Equal(expectedMerges, executor.MergeCalls);
        // The ledger takes the tip's build as it came out — a red one is what holds the epic's next sub (see the epic runner's Theory).
        storage.Received(expectedLedgerWrites).Set("mergeGate:lastBuild:epic/ac-epic", Arg.Is<AutopilotMergeBuildRecord>(record => record.Sha == "tip9999" && record.ExitCode == mergeBuildExit));
        Assert.Equal(("AC-1", expectedLastStage), provider.StageCalls.Last());
        // The trail on the epic (EpicWorkflow §3 step 6) names the sub and who answered, whichever way it went.
        var epicComment = Assert.Single(provider.Comments);
        Assert.Equal("AC-EPIC", epicComment.IssueId);
        Assert.Contains("AC-1", epicComment.Comment);
        Assert.Contains("the test", epicComment.Comment);
    }

    [Theory]
    [InlineData(false, 0, true, 0, "Review")]
    [InlineData(false, 1, false, 0, "Review")]
    [InlineData(true, 0, false, 1, "Test")]
    [InlineData(true, 1, false, 0, "Review")]
    public async Task RunAsync_AtTheMergeGate_WithoutAGo_MergesOnlyInAutomaticMode_AndNeverARedBranch(bool automatic, int buildExit, bool expectedWaits, int expectedMerges, string expectedLastStage)
    {
        var (_, provider, host, _, executor, coordinator, environment) = _AtTheGate(automatic, buildExit);
        var context = _Context(_Session("step-pane"));
        var shown = new TaskCompletionSource();
        var validationSent = new TaskCompletionSource();
        host.When(h => h.SendToSessionAsync("ceo-pane", Arg.Any<string>())).Do(_ => validationSent.TrySetResult());
        using var cancel = new CancellationTokenSource();

        var run = coordinator.RunAsync(context.EmbedSession, _Session("ceo-pane"), _Settings(), _ => shown.TrySetResult(), _ => { }, environment, _DirectUi, cancel.Token);
        await shown.Task.WaitAsync(Timeout);
        Assert.True(coordinator.ReportStepDone("step-pane", "reviewed"));
        await validationSent.Task.WaitAsync(Timeout);
        Assert.True(coordinator.ReportValidation("ceo-pane", passed: true, reason: "clean"));

        await _Until(() => coordinator.AwaitingMergeGo || run.IsCompleted);
        Assert.Equal(expectedWaits, coordinator.AwaitingMergeGo);

        // The operator closes the run while it stands at the gate: nothing has moved, and nothing moves now.
        cancel.Cancel();
        await run.WaitAsync(Timeout);
        Assert.Equal(expectedMerges, executor.MergeCalls);
        await host.Received(1).NotifyAssistantAsync("merge-gate", Arg.Any<string>());
        Assert.Equal(("AC-1", expectedLastStage), provider.StageCalls.Last());
    }

    // An epic run standing one passed review gate away from its merge gate: a source with an epic, a PR-delivering plan
    // in explicit or automatic mode, a publisher that pushes and opens a PR, and an executor whose branch build exits `buildExit`.
    private static (AutopilotPlanController Plan, FakeTrackerProvider Provider, ICockpitHost Host, IPluginStorage Storage, RecordingMergeExecutor Executor, AutopilotRunCoordinator Coordinator, AutopilotRunEnvironment Environment) _AtTheGate(bool automatic, int buildExit, int mergeBuildExit = 0)
    {
        var mode = automatic ? AutopilotMergeMode.Automatic : AutopilotMergeMode.Explicit;
        var gate = _HardStep("1") with { Title = "Code review", IsReviewGate = true };
        var plan = new AutopilotPlanController();
        plan.BeginPlanning(new AutopilotPlan("goal", new AutopilotPlanSource("youtrack", "AC-1", "Do it", EpicId: "AC-EPIC"), [gate]) { DeliversPullRequest = true, MergeMode = mode });
        plan.BindSession("ceo-pane");
        Assert.True(plan.Approve());

        var provider = new FakeTrackerProvider("youtrack");
        var host = _Host();
        host.TrackerProviders.Returns(new ITrackerProvider[] { provider });
        var storage = Substitute.For<IPluginStorage>();
        var executor = new RecordingMergeExecutor(buildExit, mergeBuildExit);
        var coordinator = new AutopilotRunCoordinator(host, plan, prPublisher: new CapturingPrPublisher(), mergeExecutor: executor, mergeBuildLedger: new AutopilotMergeBuildLedger(storage));
        var environment = new AutopilotRunEnvironment("/repo", "/repo/.worktrees/run", IsolateSteps: true, RunWorktreeBranch: "autopilot/run", CollectionBranch: "epic/ac-epic");
        return (plan, provider, host, storage, executor, coordinator, environment);
    }

    // Measures a fixed picture — a branch that deletes a file another sub added — and counts the merges it is asked
    // for, so a test asserts on whether the gate reached for the merge rather than on git.
    private sealed class RecordingMergeExecutor(int buildExit, int mergeBuildExit) : IAutopilotMergeExecutor
    {
        private int _mergeCalls;

        public int MergeCalls => Volatile.Read(ref _mergeCalls);

        public Task<AutopilotMergeEvidence> DescribeAsync(string worktreePath, string collectionBranch, string buildCommand, TimeSpan buildTimeout, CancellationToken cancellationToken = default) =>
            Task.FromResult(new AutopilotMergeEvidence("abc1234", " src/OtherSub.cs | 12 ------------\n 1 file changed, 12 deletions(-)", buildExit, "error CS0001" , null));

        public Task<AutopilotMergeResult> MergeAsync(AutopilotMergeRequest request, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _mergeCalls);
            return Task.FromResult(new AutopilotMergeResult(true, "merged the pull request", "tip9999", mergeBuildExit, string.Empty, null));
        }
    }

    // AC-201 tiered blocker escalation: a worker's autopilot_blocked routes to ReportConsultAsync, which consults the run's
    // CEO first (spoor 2) instead of the operator; the CEO answers (spoor 2 done) or escalates to the operator (spoor 3).

    [Fact]
    public async Task ReportConsult_WithTheCeoSessionEnded_FailsClosedToTheOperator_WithoutRelayingToTheCeo()
    {
        var plan = _RunningPlan(_HardStep("1"));
        var host = _Host();
        var context = _Context(_Session("step-pane"));
        var coordinator = new AutopilotRunCoordinator(host, plan);

        var shown = new TaskCompletionSource();
        using var cts = new CancellationTokenSource();
        // The CEO session has already ended (its Completion has fired) — there is no live manager to consult.
        var endedCeo = _Session("ceo-pane", Task.FromResult<string?>("the CEO session ended"));
        var run = coordinator.RunAsync(context.EmbedSession, endedCeo, _Settings(), _ => shown.TrySetResult(), _ => { }, _Env(), _DirectUi, cts.Token);
        await shown.Task.WaitAsync(Timeout);

        // Fail-closed: with no live CEO the consult goes straight to the operator instead of being dropped.
        Assert.True((await coordinator.ReportConsultAsync("step-pane", "Which db?")));
        Assert.Equal(AutopilotPlanPhase.AwaitingOperator, plan.Phase);
        Assert.Equal("Which db?", plan.PendingQuestion);
        // Nothing was relayed to the (ended) CEO session.
        await host.DidNotReceive().SendToSessionAsync("ceo-pane", Arg.Any<string>());

        cts.Cancel();
        await run.WaitAsync(Timeout);
    }

    private static AutopilotPlanController _RunningPlan(AutopilotStep step)
    {
        var plan = new AutopilotPlanController();
        plan.BeginPlanning(new AutopilotPlan("goal", null, [step]));
        plan.BindSession("ceo-pane");
        Assert.True(plan.Approve());
        return plan;
    }

    private static AutopilotPlanController _RunningPlanWithSource(AutopilotPlanSource source, AutopilotStep step)
    {
        var plan = new AutopilotPlanController();
        plan.BeginPlanning(new AutopilotPlan("goal", source, [step]));
        plan.BindSession("ceo-pane");
        Assert.True(plan.Approve());
        return plan;
    }

    private static AutopilotStep _HardStep(string id) =>
        new(id, "Code", "do the work", "Claude", "opus", "brief", "compiles", GateMode.Hard);

    private static ICockpitHost _Host()
    {
        var host = Substitute.For<ICockpitHost>();
        host.SendToSessionAsync(Arg.Any<string>(), Arg.Any<string>()).Returns(Task.CompletedTask);
        return host;
    }

    private static IWorkspaceContext _Context(IEmbeddedSession stepSession)
    {
        var context = Substitute.For<IWorkspaceContext>();
        context.EmbedSession(Arg.Any<EmbeddedSessionRequest>()).Returns(stepSession);
        context.Sessions.Returns(Substitute.For<ICockpitSessionObserver>());
        return context;
    }

    // A fake publisher that probes as a fully capable git+gh run but fails to open the pull request itself — the AC-347
    // FIX B scenario: the branch pushes, but the PR never lands, so the run must not read back as clean.
    private sealed class FailingPrPublisher : IAutopilotPrPublisher
    {
        public Task<AutopilotPrProbe> ProbeAsync(string worktreePath, CancellationToken cancellationToken = default) =>
            Task.FromResult(new AutopilotPrProbe(IsGitRun: true, HasRemote: true, GhAvailable: true));

        public Task<AutopilotPrPublishResult> PublishAsync(AutopilotPrRequest request, bool createPullRequest, CancellationToken cancellationToken = default) =>
            Task.FromResult(new AutopilotPrPublishResult(Pushed: true, PrUrl: null, Error: "gh failed to open the pull request"));

        public Task<bool> EnsureCommittedAsync(string worktreePath, string message, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public Task<AutopilotStrayCommits> RecoverStrayCommitsAsync(string runWorktreePath, string runBranch, string stepWorktreePath, CancellationToken cancellationToken = default) =>
            Task.FromResult(AutopilotStrayCommits.None);
    }

    // A hand-rolled step session whose Activity event can be raised on demand — NSubstitute cannot reliably raise an
    // interface event that carries a default (no-op) add/remove body, which IEmbeddedSession.Activity does.
    private sealed class ProgressingSession : IEmbeddedSession
    {
        private readonly TaskCompletionSource<string?> _completion = new();

        public ProgressingSession(string paneId) => PaneId = paneId;

        // AC-1398: the run backend never reads a view; reading one fails the test.
        public Control View => throw new InvalidOperationException("AC-1398: the run backend holds a session by pane id, never by its view.");

        public string PaneId { get; }

        public Task<string?> Completion => _completion.Task;

        public event Action? Activity;

        public void RaiseActivity() => Activity?.Invoke();

        public Task CloseAsync()
        {
            _completion.TrySetResult(null);
            return Task.CompletedTask;
        }

        public void SetInputEnabled(bool enabled)
        {
        }
    }

    private static IEmbeddedSession _Session(string paneId, Task<string?>? completion = null)
    {
        var session = Substitute.For<IEmbeddedSession>();
        session.PaneId.Returns(paneId);
        session.CloseAsync().Returns(Task.CompletedTask);
        // A live session's Completion has not fired; a never-completing task models that, so the coordinator waits on
        // the step's done-report as usual. A test that wants to model a session ending early passes its own task.
        session.Completion.Returns(completion ?? new TaskCompletionSource<string?>().Task);
        return session;
    }

    private static AutopilotSettings _Settings(int? maxAttempts = null, int? maxConsults = null)
    {
        var storage = Substitute.For<IPluginStorage>();
        if (maxAttempts is { } cap)
        {
            storage.Get<int?>("maxSelfFixAttempts").Returns(cap);
        }

        if (maxConsults is { } consultCap)
        {
            storage.Get<int?>("maxConsultsPerStep").Returns(consultCap);
        }

        return new AutopilotSettings(storage);
    }

    private static async Task _Until(Func<bool> condition)
    {
        for (var i = 0; i < 500 && !condition(); i++)
        {
            await Task.Delay(10);
        }

        Assert.True(condition(), "the condition should hold within the timeout");
    }

    private static AutopilotRunEnvironment _Env(bool isolate = true) => new("/repo", null, isolate);

    private static Task _DirectUi(Action action)
    {
        action();
        return Task.CompletedTask;
    }
}
