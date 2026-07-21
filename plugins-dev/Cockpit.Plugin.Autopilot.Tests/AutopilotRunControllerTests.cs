using Cockpit.Plugins.Abstractions;
using FluentAssertions;
using NSubstitute;

namespace Cockpit.Plugin.Autopilot.Tests;

/// <summary>
/// <see cref="AutopilotRunController"/> — the run state between the trigger, the scoping judgment, the session and the
/// done-gate (AC-150–AC-153): scoping → refused or running, gate reports accumulate, and MarkReady settles to
/// merge-ready or blocked by the per-gate hard/skip policy (default: Security hard, the rest skip).
/// </summary>
public class AutopilotRunControllerTests
{
    private static AutopilotRunController _Controller() =>
        new(new AutopilotSettings(Substitute.For<IPluginStorage>()));

    private static AutopilotRun Run(string issue) =>
        new("youtrack", issue, issue, new Dictionary<string, string>());

    [Fact]
    public void BeginScoping_SetsTheRunScoping_AndRaisesChanged()
    {
        var controller = _Controller();
        var fired = 0;
        controller.Changed += (_, _) => fired++;

        controller.Current.Should().BeNull();
        controller.BeginScoping(Run("AC-153"));

        controller.Current.Should().BeEquivalentTo(new { IssueId = "AC-153" });
        controller.Phase.Should().Be(AutopilotRunPhase.Scoping);
        fired.Should().Be(1);
    }

    [Fact]
    public void Refuse_ParksTheRun_WithItsReason()
    {
        var controller = _Controller();
        controller.BeginScoping(Run("AC-153"));

        controller.Refuse("no acceptance criteria");

        controller.Phase.Should().Be(AutopilotRunPhase.Refused);
        controller.BlockReason.Should().Be("no acceptance criteria");
    }

    [Fact]
    public void BeginScoping_Twice_ReplacesTheRun_ResetsToScoping_AndClearsGates()
    {
        var controller = _Controller();
        controller.BeginScoping(Run("AC-1"));
        controller.MarkRunning();
        controller.ReportGate(GateKind.Security, AutopilotGateOutcome.Passed);

        controller.BeginScoping(Run("AC-2"));

        controller.Current.Should().BeEquivalentTo(new { IssueId = "AC-2" });
        controller.Phase.Should().Be(AutopilotRunPhase.Scoping);
        controller.Gates.Should().BeEmpty();
    }

    [Fact]
    public void MarkRunning_ClearsAnyBlockReason()
    {
        var controller = _Controller();
        controller.BeginScoping(Run("AC-153"));
        controller.Refuse("declined");

        controller.MarkRunning();

        controller.Phase.Should().Be(AutopilotRunPhase.Running);
        controller.BlockReason.Should().BeNull();
    }

    [Fact]
    public void Block_ThenResume_MovesAwaitingOperator_AndBack()
    {
        var controller = _Controller();
        controller.BeginScoping(Run("AC-155"));
        controller.MarkRunning();

        controller.Block("Which database should it use?");
        controller.Phase.Should().Be(AutopilotRunPhase.AwaitingOperator);
        controller.PendingQuestion.Should().Be("Which database should it use?");

        controller.ResumeRunning();
        controller.Phase.Should().Be(AutopilotRunPhase.Running);
        controller.PendingQuestion.Should().BeNull();
    }

    [Fact]
    public void Park_BlocksTheRun_WithItsReason_AndClearsTheQuestion()
    {
        var controller = _Controller();
        controller.BeginScoping(Run("AC-155"));
        controller.MarkRunning();
        controller.Block("?");

        controller.Park("No operator answer in time.");

        controller.Phase.Should().Be(AutopilotRunPhase.Blocked);
        controller.BlockReason.Should().Be("No operator answer in time.");
        controller.PendingQuestion.Should().BeNull();
    }

    [Fact]
    public void MarkReady_WhenEveryHardGatePassed_IsMergeReady()
    {
        var controller = _Controller();
        controller.BeginScoping(Run("AC-153"));
        controller.MarkRunning();
        controller.ReportGate(GateKind.Security, AutopilotGateOutcome.Passed);

        controller.MarkReady();

        controller.Phase.Should().Be(AutopilotRunPhase.MergeReady);
    }

    [Fact]
    public void MarkReady_WithAPrUrl_KeepsItForEvidence()
    {
        var controller = _Controller();
        controller.BeginScoping(Run("AC-154"));
        controller.MarkRunning();
        controller.ReportGate(GateKind.Security, AutopilotGateOutcome.Passed);

        controller.MarkReady("https://example/pr/1");

        controller.PrUrl.Should().Be("https://example/pr/1");
    }

    [Fact]
    public void MarkReady_WhenAHardGateFailed_IsBlocked_NamingIt()
    {
        var controller = _Controller();
        controller.BeginScoping(Run("AC-153"));
        controller.MarkRunning();
        controller.ReportGate(GateKind.Security, AutopilotGateOutcome.Failed);

        controller.MarkReady();

        controller.Phase.Should().Be(AutopilotRunPhase.Blocked);
        controller.BlockReason.Should().Contain("Security");
    }

    [Fact]
    public void MarkReady_WhenAHardGateWasNotReported_IsBlocked()
    {
        var controller = _Controller();
        controller.BeginScoping(Run("AC-153"));
        controller.MarkRunning();

        controller.MarkReady();

        controller.Phase.Should().Be(AutopilotRunPhase.Blocked);
    }

    [Fact]
    public void MarkReady_WhenOnlyASkipGateFailed_IsStillMergeReady()
    {
        var controller = _Controller();
        controller.BeginScoping(Run("AC-153"));
        controller.MarkRunning();
        controller.ReportGate(GateKind.Security, AutopilotGateOutcome.Passed);
        controller.ReportGate(GateKind.Verify, AutopilotGateOutcome.Failed);

        controller.MarkReady();

        controller.Phase.Should().Be(AutopilotRunPhase.MergeReady);
    }
}
