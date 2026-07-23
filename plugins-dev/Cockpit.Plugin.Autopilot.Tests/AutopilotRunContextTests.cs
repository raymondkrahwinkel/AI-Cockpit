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
}
