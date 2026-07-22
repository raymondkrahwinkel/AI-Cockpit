using FluentAssertions;

namespace Cockpit.Plugin.Autopilot.Tests;

/// <summary>
/// The plan's operator-facing label (Raymond 2026-07-22): a run carries a name the operator recognises it by, falling
/// back to the goal until one is set, and <see cref="AutopilotPlan.WithName"/> is a value-copy so the living plan stays
/// immutable.
/// </summary>
public class AutopilotPlanTests
{
    private static AutopilotPlan _Plan(string goal, string name = "") =>
        new(goal, null, [new AutopilotStep("1", "Step", "desc", "work", "opus", "brief", "compiles", GateMode.Hard)]) { Name = name };

    [Fact]
    public void Label_FallsBackToGoal_WhenNoNameSet()
    {
        _Plan("Add a helper class").Label.Should().Be("Add a helper class");
    }

    [Fact]
    public void Label_IsTheName_WhenSet()
    {
        _Plan("Add a helper class", "HelperTwo").Label.Should().Be("HelperTwo");
    }

    [Fact]
    public void SuggestedName_FallsThroughNameThenGoalThenFirstStepTitle()
    {
        // Name wins when set.
        new AutopilotPlan("the goal", null, [new AutopilotStep("1", "First step", "d", "work", null, "b", null)]) { Name = "Chosen" }
            .SuggestedName.Should().Be("Chosen");

        // No name → the goal.
        new AutopilotPlan("the goal", null, [new AutopilotStep("1", "First step", "d", "work", null, "b", null)])
            .SuggestedName.Should().Be("the goal");

        // No name and no goal (the CEO passed neither) → the first step's title, so the field is never left empty.
        new AutopilotPlan("", null, [new AutopilotStep("1", "First step", "d", "work", null, "b", null)])
            .SuggestedName.Should().Be("First step");
    }

    [Fact]
    public void WithName_ReturnsANamedCopy_LeavingTheOriginalUnchanged()
    {
        var plan = _Plan("Add a helper class");
        var named = plan.WithName("HelperTwo");

        named.Name.Should().Be("HelperTwo");
        named.Steps.Should().BeEquivalentTo(plan.Steps);
        plan.Name.Should().BeEmpty();
    }

    [Fact]
    public void WithWorkingDirectory_ReturnsACopyWithTheFolder_LeavingTheOriginalUnchanged()
    {
        var plan = _Plan("Add a helper class");
        var located = plan.WithWorkingDirectory("/home/ray/proj");

        located.WorkingDirectory.Should().Be("/home/ray/proj");
        located.Steps.Should().BeEquivalentTo(plan.Steps);
        plan.WorkingDirectory.Should().BeEmpty();
    }
}
