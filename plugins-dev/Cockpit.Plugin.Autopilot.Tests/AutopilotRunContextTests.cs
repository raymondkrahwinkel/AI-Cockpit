using System.Text.Json;
using Cockpit.Plugins.Abstractions;
using FluentAssertions;

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
        AutopilotRunContext.ShouldToastAwaiting(AutopilotPlanPhase.Running, AutopilotPlanPhase.AwaitingOperator)
            .Should().BeTrue();
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
            AutopilotRunContext.ShouldToastAwaiting(AutopilotPlanPhase.Running, current).Should().BeFalse();
        }
    }

    [Fact]
    public void ShouldToastAwaiting_DoesNotRepeat_WhileAlreadyAwaiting()
    {
        // The guard's whole point: OnControllerChanged re-renders many times while the run waits, but only the first
        // transition into the wait should toast — a same-phase render must not fire another.
        AutopilotRunContext.ShouldToastAwaiting(AutopilotPlanPhase.AwaitingOperator, AutopilotPlanPhase.AwaitingOperator)
            .Should().BeFalse();
    }

    [Fact]
    public void IsSettledOutcome_RecordsMergeReadyBlockedAndStopped()
    {
        AutopilotPlanWorkspaceBody.IsSettledOutcome(AutopilotPlanPhase.MergeReady).Should().BeTrue();
        AutopilotPlanWorkspaceBody.IsSettledOutcome(AutopilotPlanPhase.Blocked).Should().BeTrue();
        AutopilotPlanWorkspaceBody.IsSettledOutcome(AutopilotPlanPhase.Stopped).Should().BeTrue();
    }

    [Fact]
    public void IsSettledOutcome_DoesNotRecordAnUnsettledRun()
    {
        AutopilotPlanWorkspaceBody.IsSettledOutcome(AutopilotPlanPhase.Planning).Should().BeFalse();
        AutopilotPlanWorkspaceBody.IsSettledOutcome(AutopilotPlanPhase.Running).Should().BeFalse();
        AutopilotPlanWorkspaceBody.IsSettledOutcome(AutopilotPlanPhase.AwaitingOperator).Should().BeFalse();
    }

    [Fact]
    public void NeedsOperatorAttention_IsTrue_WhileAnyRunAwaitsTheOperator()
    {
        // The persistent "needs you" marker's condition (AC-203): a run in AwaitingOperator raises the standing signal,
        // and it stays raised as long as any active run is in that phase — regardless of what the others are doing.
        AutopilotPlanWorkspaceBody.NeedsOperatorAttention([AutopilotPlanPhase.AwaitingOperator]).Should().BeTrue();
        AutopilotPlanWorkspaceBody.NeedsOperatorAttention(
            [AutopilotPlanPhase.Running, AutopilotPlanPhase.AwaitingOperator]).Should().BeTrue();
    }

    [Fact]
    public void NeedsOperatorAttention_IsFalse_OnceTheRunLeavesTheWait()
    {
        // The marker clears the moment the run leaves AwaitingOperator — answered (→ Running) or settled — so it never
        // outlives the wait it signals. No active run at all is likewise nothing to flag.
        AutopilotPlanWorkspaceBody.NeedsOperatorAttention([]).Should().BeFalse();
        foreach (var phase in new[]
        {
            AutopilotPlanPhase.Planning,
            AutopilotPlanPhase.Running,
            AutopilotPlanPhase.Blocked,
            AutopilotPlanPhase.MergeReady,
            AutopilotPlanPhase.Stopped,
        })
        {
            AutopilotPlanWorkspaceBody.NeedsOperatorAttention([phase]).Should().BeFalse();
        }
    }

    [Fact]
    public void NeedsOperatorAttention_IsNotTrippedByACeoConsult()
    {
        // A CEO consult (spoor 2, AC-201) keeps the run Running — only an operator escalation (spoor 3) turns it
        // AwaitingOperator. A run that is merely Running, however many, must not raise the marker.
        AutopilotPlanWorkspaceBody.NeedsOperatorAttention(
            [AutopilotPlanPhase.Running, AutopilotPlanPhase.Running]).Should().BeFalse();
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

        request.ConfineFileToolsToWorkingDirectory.Should().BeTrue();
        request.WorkingDirectory.Should().Be("/runs/worktree");
        // The validator never cuts its own worktree — it reads the one the run already has.
        request.IsolateInWorktree.Should().BeFalse();
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

        request.PermissionMode.Should().NotBeNullOrWhiteSpace();
        // Coerced away from bypass by AutopilotSettings (AC-209), so even a stored bypass cannot reach the driver here.
        request.PermissionMode.Should().Be(AutopilotSettings.DefaultAutonomyMode).And.NotBe("bypassPermissions");
    }
}
