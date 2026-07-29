using System.Text.Json;
using Cockpit.Plugins.Abstractions;
namespace Cockpit.Plugin.Autopilot.Tests;

/// <summary>
/// The pure decision logic a run context carries: the edge guard that fires the "needs you" toast exactly once when a
/// run enters the AwaitingOperator wait (AC-194), and the settled-outcome classification that decides a run is recorded
/// in history rather than silently dropped — including a run the operator stopped (AC-196). Both are extracted as pure
/// statics precisely so they can be exercised here without a host or a UI thread.
/// </summary>
public class AutopilotRunContextTests
{
    [Fact]
    public void ShouldToastAwaiting_FiresOnTheEdgeIntoAwaitingOperator()
    {
        Assert.True(AutopilotRunContext.ShouldToastAwaiting(AutopilotPlanPhase.Running, AutopilotPlanPhase.AwaitingOperator));
    }

    [Fact]
    public void ShouldToastAwaiting_DoesNotFire_WhenTheTargetIsNotAwaitingOperator()
    {
        // Any phase other than AwaitingOperator is not a "needs you" edge, regardless of where it came from.
        foreach (var current in new[]
        {
            AutopilotPlanPhase.Planning,
            AutopilotPlanPhase.Running,
            AutopilotPlanPhase.Blocked,
            AutopilotPlanPhase.MergeReady,
            AutopilotPlanPhase.Stopped,
        })
        {
            Assert.False(AutopilotRunContext.ShouldToastAwaiting(AutopilotPlanPhase.Running, current));
        }
    }

    [Fact]
    public void ShouldToastAwaiting_DoesNotRepeat_WhileAlreadyAwaiting()
    {
        // The guard's whole point: OnControllerChanged re-renders many times while the run waits, but only the first
        // transition into the wait should toast — a same-phase render must not fire another.
        Assert.False(AutopilotRunContext.ShouldToastAwaiting(AutopilotPlanPhase.AwaitingOperator, AutopilotPlanPhase.AwaitingOperator));
    }

    [Fact]
    public void IsSettledOutcome_RecordsMergeReadyBlockedAndStopped()
    {
        Assert.True(AutopilotPlanWorkspaceBody.IsSettledOutcome(AutopilotPlanPhase.MergeReady));
        Assert.True(AutopilotPlanWorkspaceBody.IsSettledOutcome(AutopilotPlanPhase.Blocked));
        Assert.True(AutopilotPlanWorkspaceBody.IsSettledOutcome(AutopilotPlanPhase.Stopped));
    }

    [Fact]
    public void IsSettledOutcome_DoesNotRecordAnUnsettledRun()
    {
        Assert.False(AutopilotPlanWorkspaceBody.IsSettledOutcome(AutopilotPlanPhase.Planning));
        Assert.False(AutopilotPlanWorkspaceBody.IsSettledOutcome(AutopilotPlanPhase.Running));
        Assert.False(AutopilotPlanWorkspaceBody.IsSettledOutcome(AutopilotPlanPhase.AwaitingOperator));
    }

    [Fact]
    public void NeedsOperatorAttention_IsTrue_WhileAnyRunAwaitsTheOperator()
    {
        // The persistent "needs you" marker's condition (AC-203): a run in AwaitingOperator raises the standing signal,
        // and it stays raised as long as any active run is in that phase — regardless of what the others are doing.
        Assert.True(AutopilotPlanWorkspaceBody.NeedsOperatorAttention([AutopilotPlanPhase.AwaitingOperator]));
        Assert.True(AutopilotPlanWorkspaceBody.NeedsOperatorAttention(
            [AutopilotPlanPhase.Running, AutopilotPlanPhase.AwaitingOperator]));
    }

    [Fact]
    public void NeedsOperatorAttention_IsFalse_OnceTheRunLeavesTheWait()
    {
        // The marker clears the moment the run leaves AwaitingOperator — answered (→ Running) or settled — so it never
        // outlives the wait it signals. No active run at all is likewise nothing to flag.
        Assert.False(AutopilotPlanWorkspaceBody.NeedsOperatorAttention([]));
        foreach (var phase in new[]
        {
            AutopilotPlanPhase.Planning,
            AutopilotPlanPhase.Running,
            AutopilotPlanPhase.Blocked,
            AutopilotPlanPhase.MergeReady,
            AutopilotPlanPhase.Stopped,
        })
        {
            Assert.False(AutopilotPlanWorkspaceBody.NeedsOperatorAttention([phase]));
        }
    }

    [Fact]
    public void NeedsOperatorAttention_IsNotTrippedByACeoConsult()
    {
        // A CEO consult (spoor 2, AC-201) keeps the run Running — only an operator escalation (spoor 3) turns it
        // AwaitingOperator. A run that is merely Running, however many, must not raise the marker.
        Assert.False(AutopilotPlanWorkspaceBody.NeedsOperatorAttention(
            [AutopilotPlanPhase.Running, AutopilotPlanPhase.Running]));
    }

    [Fact]
    public void PreferredContextIndex_PicksTheFirstRun_WhenNoneAwaitsTheOperator()
    {
        // The unbugged single-run case, and the multi-run case where nothing needs the operator yet: the first run
        // stays the default, exactly what the surface showed before AC-440.
        Assert.Equal(0, AutopilotPlanWorkspaceBody.PreferredContextIndex([AutopilotPlanPhase.Running]));
        Assert.Equal(0, AutopilotPlanWorkspaceBody.PreferredContextIndex(
            [AutopilotPlanPhase.Running, AutopilotPlanPhase.Planning]));
    }

    [Fact]
    public void PreferredContextIndex_PicksTheAwaitingRun_EvenWhenItIsNotFirst()
    {
        // AC-440's bug: the pane always rendered _activeContexts[0] while the "Needs you" badge lit up for any active
        // run in AwaitingOperator — so a second, later run's blockade could sit behind the first run's still-running
        // step surface with no way to reach it. The awaiting run must win regardless of its position in the list.
        Assert.Equal(1, AutopilotPlanWorkspaceBody.PreferredContextIndex(
            [AutopilotPlanPhase.Running, AutopilotPlanPhase.AwaitingOperator]));
        Assert.Equal(2, AutopilotPlanWorkspaceBody.PreferredContextIndex(
            [AutopilotPlanPhase.Running, AutopilotPlanPhase.Running, AutopilotPlanPhase.AwaitingOperator]));
    }

    [Fact]
    public void PreferredContextIndex_PicksTheFirstAwaitingRun_WhenSeveralAreAwaiting()
    {
        Assert.Equal(0, AutopilotPlanWorkspaceBody.PreferredContextIndex(
            [AutopilotPlanPhase.AwaitingOperator, AutopilotPlanPhase.AwaitingOperator]));
    }

    [Fact]
    public void NextAwaitingIndex_IsNull_WhenNothingAwaits()
    {
        // The badge is never visible with nothing awaiting, but the click handler guards it anyway rather than
        // trusting that invariant blindly.
        Assert.Null(AutopilotPlanWorkspaceBody.NextAwaitingIndex(awaitingCount: 0, currentIndex: -1));
    }

    [Fact]
    public void NextAwaitingIndex_StepsToTheNextRun_AndWrapsAfterTheLast()
    {
        // Repeated clicks must reach every awaiting run in turn, not stick on the first (the exact bug a review caught
        // before this shipped: deriving "next" from the badge's own click history rather than the run actually shown
        // made the first click on a second awaiting run land back on the one already displayed).
        Assert.Equal(0, AutopilotPlanWorkspaceBody.NextAwaitingIndex(awaitingCount: 2, currentIndex: -1));
        Assert.Equal(1, AutopilotPlanWorkspaceBody.NextAwaitingIndex(awaitingCount: 2, currentIndex: 0));
        Assert.Equal(0, AutopilotPlanWorkspaceBody.NextAwaitingIndex(awaitingCount: 2, currentIndex: 1));
    }

    [Fact]
    public void NextAwaitingIndex_ReturnsTheOnlyRun_WhenOnlyOneAwaits()
    {
        // With one awaiting run already shown (currentIndex 0), the "next" is itself — the caller's own
        // ReferenceEquals guard is what turns this into a no-op click rather than a same-run re-render that would
        // rebuild the answer TextBox and drop whatever the operator had typed.
        Assert.Equal(0, AutopilotPlanWorkspaceBody.NextAwaitingIndex(awaitingCount: 1, currentIndex: 0));
        Assert.Equal(0, AutopilotPlanWorkspaceBody.NextAwaitingIndex(awaitingCount: 1, currentIndex: -1));
    }

    /// <summary>Round-trips through JSON the way the host's real storage does, so an unset key reads back as "not set".</summary>
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
        // The confinement the request above asks for is only granted if the provider vouches for it, and a
        // permission-based provider stops vouching in a bypass mode. Naming a mode here is what drops whatever the CEO
        // profile has stored (the host keeps the profile's default when a request names none) — without it, a profile
        // saved on bypassPermissions makes the host's fail-closed gate refuse the validator, and the run waits on a CEO
        // that never starts (AC-191).
        var storage = new FakeStorage();
        var settings = new AutopilotSettings(storage);
        settings.SetAutonomyMode("bypassPermissions");

        var request = AutopilotRunContext.ValidatorCeoRequest(settings, "/runs/worktree", _SourcePlan(), "run-1");

        Assert.False(string.IsNullOrWhiteSpace(request.PermissionMode));
        // Coerced away from bypass by AutopilotSettings (AC-209), so even a stored bypass cannot reach the driver here.
        Assert.Equal(AutopilotSettings.DefaultAutonomyMode, request.PermissionMode);
        Assert.NotEqual("bypassPermissions", request.PermissionMode);
    }
}
