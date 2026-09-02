using System.Text.Json;
using Cockpit.Plugins.Abstractions;
namespace Cockpit.Plugin.Autopilot.Tests;

// The pure decision logic a run context carries: the edge guard that fires the "needs you" toast exactly once when
// a run enters AwaitingOperator (AC-194), and the settled-outcome classification that records a run in history
// rather than silently dropping it (AC-196). The phases are internal enums (CS0051), so the rows box them.
public class AutopilotRunContextTests
{
    public static IEnumerable<object[]> ToastEdges() =>
    [
        [AutopilotPlanPhase.Running, AutopilotPlanPhase.AwaitingOperator, true],
        // Any phase other than AwaitingOperator is not a "needs you" edge, regardless of where it came from.
        [AutopilotPlanPhase.Running, AutopilotPlanPhase.Planning, false],
        [AutopilotPlanPhase.Running, AutopilotPlanPhase.Running, false],
        [AutopilotPlanPhase.Running, AutopilotPlanPhase.Blocked, false],
        [AutopilotPlanPhase.Running, AutopilotPlanPhase.MergeReady, false],
        [AutopilotPlanPhase.Running, AutopilotPlanPhase.Stopped, false],
        // The guard's whole point: OnControllerChanged re-renders many times while the run waits, but only the first
        // transition into the wait should toast — a same-phase render must not fire another.
        [AutopilotPlanPhase.AwaitingOperator, AutopilotPlanPhase.AwaitingOperator, false],
    ];

    [Theory]
    [MemberData(nameof(ToastEdges))]
    public void ShouldToastAwaiting_FiresOnlyOnTheEdgeIntoAwaitingOperator(object previous, object current, bool toasts) =>
        Assert.Equal(toasts, AutopilotRunContext.ShouldToastAwaiting((AutopilotPlanPhase)previous, (AutopilotPlanPhase)current));

    // A run is recorded in history once it has settled — including an operator-stopped one (AC-196), which was the
    // case that used to be dropped silently.
    public static IEnumerable<object[]> Phases() =>
    [
        [AutopilotPlanPhase.MergeReady, true],
        [AutopilotPlanPhase.Blocked, true],
        [AutopilotPlanPhase.Stopped, true],
        [AutopilotPlanPhase.Planning, false],
        [AutopilotPlanPhase.Running, false],
        [AutopilotPlanPhase.AwaitingOperator, false],
    ];

    [Theory]
    [MemberData(nameof(Phases))]
    public void IsSettledOutcome_RecordsExactlyTheRunsThatEnded(object phase, bool settled) =>
        Assert.Equal(settled, AutopilotPlanWorkspaceBody.IsSettledOutcome((AutopilotPlanPhase)phase));

    // The persistent "needs you" marker (AC-203): raised while any active run is in AwaitingOperator, cleared the
    // moment it leaves — answered or settled — so it never outlives the wait it signals. A CEO consult (spoor 2,
    // AC-201) keeps the run Running and must not raise it; only an operator escalation (spoor 3) does.
    public static IEnumerable<object[]> ActiveRunPhases() =>
    [
        [new[] { AutopilotPlanPhase.AwaitingOperator }, true],
        [new[] { AutopilotPlanPhase.Running, AutopilotPlanPhase.AwaitingOperator }, true],
        [Array.Empty<AutopilotPlanPhase>(), false],
        [new[] { AutopilotPlanPhase.Planning }, false],
        [new[] { AutopilotPlanPhase.Running }, false],
        [new[] { AutopilotPlanPhase.Blocked }, false],
        [new[] { AutopilotPlanPhase.MergeReady }, false],
        [new[] { AutopilotPlanPhase.Stopped }, false],
        [new[] { AutopilotPlanPhase.Running, AutopilotPlanPhase.Running }, false],
    ];

    [Theory]
    [MemberData(nameof(ActiveRunPhases))]
    public void NeedsOperatorAttention_IsRaised_ExactlyWhileARunAwaitsTheOperator(object phases, bool needed) =>
        Assert.Equal(needed, AutopilotPlanWorkspaceBody.NeedsOperatorAttention((AutopilotPlanPhase[])phases));

    // AC-440's bug: the pane always rendered _activeContexts[0] while the badge lit up for any awaiting run, so a
    // second run's blockade could sit behind the first run's step surface unreachable. The awaiting run now wins
    // regardless of position; with none awaiting the first run stays the default, as before AC-440.
    public static IEnumerable<object[]> ContextPreferences() =>
    [
        [new[] { AutopilotPlanPhase.Running }, 0],
        [new[] { AutopilotPlanPhase.Running, AutopilotPlanPhase.Planning }, 0],
        [new[] { AutopilotPlanPhase.Running, AutopilotPlanPhase.AwaitingOperator }, 1],
        [new[] { AutopilotPlanPhase.Running, AutopilotPlanPhase.Running, AutopilotPlanPhase.AwaitingOperator }, 2],
        [new[] { AutopilotPlanPhase.AwaitingOperator, AutopilotPlanPhase.AwaitingOperator }, 0],
    ];

    [Theory]
    [MemberData(nameof(ContextPreferences))]
    public void PreferredContextIndex_PicksTheFirstAwaitingRun_ElseTheFirstRun(object phases, int expected) =>
        Assert.Equal(expected, AutopilotPlanWorkspaceBody.PreferredContextIndex((AutopilotPlanPhase[])phases));

    [Theory]
    // The badge is never visible with nothing awaiting, but the click handler guards it anyway rather than trusting
    // that invariant blindly.
    [InlineData(0, -1, null)]
    // Repeated clicks must reach every awaiting run in turn, not stick on the first (the exact bug a review caught
    // before this shipped: deriving "next" from the badge's own click history rather than the run actually shown
    // made the first click on a second awaiting run land back on the one already displayed).
    [InlineData(2, -1, 0)]
    [InlineData(2, 0, 1)]
    [InlineData(2, 1, 0)]
    // With one awaiting run already shown, the "next" is itself — the caller's own ReferenceEquals guard is what
    // turns this into a no-op click rather than a same-run re-render that would rebuild the answer TextBox and drop
    // whatever the operator had typed.
    [InlineData(1, 0, 0)]
    [InlineData(1, -1, 0)]
    public void NextAwaitingIndex_StepsThroughTheAwaitingRuns_AndWrapsAfterTheLast(int awaitingCount, int currentIndex, int? expected) =>
        Assert.Equal(expected, AutopilotPlanWorkspaceBody.NextAwaitingIndex(awaitingCount, currentIndex));

    // Round-trips through JSON the way the host's real storage does, so an unset key reads back as "not set".
    private sealed class FakeStorage : IPluginStorage
    {
        private readonly Dictionary<string, string> _data = new(StringComparer.Ordinal);

        public T? Get<T>(string key) => _data.TryGetValue(key, out var json) ? JsonSerializer.Deserialize<T>(json) : default;

        public void Set<T>(string key, T value) => _data[key] = JsonSerializer.Serialize(value);

        public void SetSecret(string key, string value) => Set(key, value);

        public string? GetSecret(string key) => Get<string>(key);
    }

    private static AutopilotPlan _SourcePlan() =>
        new("Do the work", new AutopilotPlanSource("YouTrack", "AC-191", "A title"), []);

    [Fact]
    public void ValidatorCeoRequest_AsksToBeConfined_ToTheDirectoryItValidates()
    {
        var request = AutopilotRunContext.ValidatorCeoRequest(new AutopilotSettings(new FakeStorage()), "/runs/worktree", _SourcePlan(), "run-1");

        Assert.True(request.ConfineFileToolsToWorkingDirectory);
        Assert.Equal("/runs/worktree", request.WorkingDirectory);
        // The validator never cuts its own worktree — it reads the one the run already has.
        Assert.False(request.IsolateInWorktree);
    }

    [Fact]
    public void ValidatorCeoRequest_CarriesTheRun_SoTheCeosOwnSpendIsCountedAgainstIt()
    {
        // AC-251: the validating CEO is one of the three things a run spends on, and the one whose context grows
        // as the run goes. Leaving it off the run would under-report exactly the cost the reduction work targets.
        var plan = _SourcePlan();

        var request = AutopilotRunContext.ValidatorCeoRequest(new AutopilotSettings(new FakeStorage()), "/runs/worktree", plan, "run-1");

        Assert.Equal("run-1", request.RunId);
        Assert.Equal(plan.Label, request.RunLabel);
    }

    [Fact]
    public void ValidatorCeoRequest_NamesItsOwnPermissionMode_SoAProfileSavedInBypassCannotDecideIt()
    {
        // Confinement is only granted if the provider vouches for it, and a permission-based provider stops
        // vouching in a bypass mode. Naming a mode here drops whatever the CEO profile has stored — without it, a
        // profile saved on bypassPermissions makes the host refuse the validator and the run waits on a CEO that never starts (AC-191).
        var storage = new FakeStorage();
        var settings = new AutopilotSettings(storage);
        settings.SetAutonomyMode("bypassPermissions");

        var request = AutopilotRunContext.ValidatorCeoRequest(settings, "/runs/worktree", _SourcePlan(), "run-1");

        Assert.False(string.IsNullOrWhiteSpace(request.PermissionMode));
        // Coerced away from bypass by AutopilotSettings (AC-209), so even a stored bypass cannot reach the driver here.
        Assert.Equal(AutopilotSettings.DefaultAutonomyMode, request.PermissionMode);
        Assert.NotEqual("bypassPermissions", request.PermissionMode);
    }

    [Theory]
    // AC-254: unset, the validator behaves exactly as before the split — one shared pair.
    [InlineData(null, "opus")]
    // Set, only the model diverges; the profile still follows planning because no profile override was set. That
    // setting the one does not move the other is proven on the settings themselves, in AutopilotSettingsTests.
    [InlineData("sonnet", "sonnet")]
    public void ValidatorCeoRequest_TakesTheValidationPair_FallingBackToPlanning(string? validationModel, string expectedModel)
    {
        var settings = new AutopilotSettings(new FakeStorage());
        settings.SetCeoProfileLabel("work");
        settings.SetCeoModel("opus");
        settings.SetCeoValidationModel(validationModel);

        var request = AutopilotRunContext.ValidatorCeoRequest(settings, "/runs/worktree", _SourcePlan(), "run-1");

        Assert.Equal("work", request.ProfileId);
        Assert.Equal(expectedModel, request.Model);
    }

    [Fact]
    public void ValidatorCeoRequest_WithACarryOver_StaysTheSameValidatorOnTheSameRun()
    {
        // AC-253: the replacement carries the ledger in its hidden brief, and is otherwise the validator the run
        // already had — same profile/model (AC-254) and same run (AC-251), or the very context this measures would
        // drop out of usage-history.jsonl the moment the checkpoint fires.
        var settings = new AutopilotSettings(new FakeStorage());
        settings.SetCeoProfileLabel("work");
        settings.SetCeoValidationModel("sonnet");
        var plan = _SourcePlan();

        var request = AutopilotRunContext.ValidatorCeoRequest(settings, "/runs/worktree", plan, "run-1", "- Code it: done, verified");

        Assert.Equal("work", request.ProfileId);
        Assert.Equal("sonnet", request.Model);
        Assert.Equal("run-1", request.RunId);
        Assert.Equal(plan.Label, request.RunLabel);
        Assert.Contains("- Code it: done, verified", request.AppendSystemPrompt);
        // The ledger is added to the validator brief, never instead of it.
        Assert.Contains(AutopilotValidatorBrief.For(plan), request.AppendSystemPrompt);
    }
}
