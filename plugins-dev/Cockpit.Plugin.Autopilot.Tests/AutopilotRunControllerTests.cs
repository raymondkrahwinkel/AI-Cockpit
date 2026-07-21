using FluentAssertions;

namespace Cockpit.Plugin.Autopilot.Tests;

/// <summary>
/// <see cref="AutopilotRunController"/> — the shared run state between the "start" intent, the scoping judgment and the
/// workspace body (AC-150/AC-151): scoping → refused or running, each raising the change the body re-renders on.
/// </summary>
public class AutopilotRunControllerTests
{
    private static AutopilotRun Run(string issue) =>
        new("youtrack", issue, issue, new Dictionary<string, string>());

    [Fact]
    public void BeginScoping_SetsTheRunScoping_AndRaisesChanged()
    {
        var controller = new AutopilotRunController();
        var fired = 0;
        controller.Changed += (_, _) => fired++;

        controller.Current.Should().BeNull();
        controller.BeginScoping(Run("AC-151"));

        controller.Current.Should().BeEquivalentTo(new { IssueId = "AC-151" });
        controller.Phase.Should().Be(AutopilotRunPhase.Scoping);
        fired.Should().Be(1);
    }

    [Fact]
    public void Refuse_ParksTheRun_WithItsReason()
    {
        var controller = new AutopilotRunController();
        controller.BeginScoping(Run("AC-151"));

        controller.Refuse("no acceptance criteria");

        controller.Phase.Should().Be(AutopilotRunPhase.Refused);
        controller.RefusalReason.Should().Be("no acceptance criteria");
    }

    [Fact]
    public void MarkRunning_AdvancesTheRun_AndClearsAnyRefusalReason()
    {
        var controller = new AutopilotRunController();
        controller.BeginScoping(Run("AC-151"));

        controller.MarkRunning();

        controller.Phase.Should().Be(AutopilotRunPhase.Running);
        controller.RefusalReason.Should().BeNull();
    }

    [Fact]
    public void BeginScoping_Twice_ReplacesTheRun_AndResetsToScoping()
    {
        var controller = new AutopilotRunController();
        controller.BeginScoping(Run("AC-1"));
        controller.Refuse("too big");

        controller.BeginScoping(Run("AC-2"));

        controller.Current.Should().BeEquivalentTo(new { IssueId = "AC-2" });
        controller.Phase.Should().Be(AutopilotRunPhase.Scoping);
        controller.RefusalReason.Should().BeNull();
    }
}
