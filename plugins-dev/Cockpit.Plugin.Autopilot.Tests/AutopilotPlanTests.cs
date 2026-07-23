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

    private static AutopilotPlan _SourcePlan(string goal, string name = "", string issueId = "AC-191") =>
        new(goal, new AutopilotPlanSource("YouTrack", issueId, "A title"),
            [new AutopilotStep("1", "Step", "desc", "work", "opus", "brief", "compiles", GateMode.Hard)]) { Name = name };

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
    public void Label_PrefixesTheSourceIssueKey_ForATrackerRun()
    {
        // A tracker-triggered run reads as "AC-191 - …" in the queue and history (AC-199), on both the name and the
        // goal fallback.
        _SourcePlan("Autopilot: enforce the rule").Label.Should().Be("AC-191 - Autopilot: enforce the rule");
        _SourcePlan("the goal", "Chosen name").Label.Should().Be("AC-191 - Chosen name");
    }

    [Fact]
    public void SuggestedName_PrefixesTheSourceIssueKey_ForATrackerRun()
    {
        _SourcePlan("Autopilot: enforce the rule").SuggestedName.Should().Be("AC-191 - Autopilot: enforce the rule");
    }

    [Fact]
    public void SourcePrefix_IsNotAppliedTwice_WhenTheNameAlreadyOpensWithTheIssueKey()
    {
        // The CEO may already have proposed a prefixed name (or the prefix was applied once already, e.g. an approved
        // Name carried it in) — it must not become "AC-191 - AC-191 - …".
        _SourcePlan("AC-191 - Autopilot: enforce", "AC-191 - Autopilot: enforce")
            .Label.Should().Be("AC-191 - Autopilot: enforce");
        _SourcePlan("AC-191 - Autopilot: enforce").SuggestedName.Should().Be("AC-191 - Autopilot: enforce");
    }

    [Fact]
    public void SourcePrefix_IsNotApplied_ForACeoFirstPlan()
    {
        // No source → no issue key to prefix with; the name is left exactly as-is.
        _Plan("Autopilot: enforce the rule").Label.Should().Be("Autopilot: enforce the rule");
        _Plan("Autopilot: enforce the rule").SuggestedName.Should().Be("Autopilot: enforce the rule");
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
