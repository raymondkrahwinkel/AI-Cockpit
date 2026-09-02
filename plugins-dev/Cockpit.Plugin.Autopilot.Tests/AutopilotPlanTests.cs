
namespace Cockpit.Plugin.Autopilot.Tests;

// The plan's operator-facing label: a run carries a name the operator recognises it by, falling
// back to the goal until one is set, and `AutopilotPlan.WithName` is a value-copy so the living plan stays
// immutable.
public class AutopilotPlanTests
{
    private static AutopilotPlan _Plan(string goal, string name = "") =>
        new(goal, null, [new AutopilotStep("1", "Step", "desc", "work", "opus", "brief", "compiles", GateMode.Hard)]) { Name = name };

    private static AutopilotPlan _SourcePlan(string goal, string name = "", string issueId = "AC-191") =>
        new(goal, new AutopilotPlanSource("YouTrack", issueId, "A title"),
            [new AutopilotStep("1", "Step", "desc", "work", "opus", "brief", "compiles", GateMode.Hard)]) { Name = name };

    [Theory]
    [InlineData("", "Add a helper class")]
    [InlineData("HelperTwo", "HelperTwo")]
    public void Label_IsTheName_FallingBackToTheGoal(string name, string expected) =>
        Assert.Equal(expected, _Plan("Add a helper class", name).Label);

    [Fact]
    public void SuggestedName_FallsThroughNameThenGoalThenFirstStepTitle()
    {
        // Name wins when set.
        Assert.Equal("Chosen", new AutopilotPlan("the goal", null, [new AutopilotStep("1", "First step", "d", "work", null, "b", null)]) { Name = "Chosen" }
            .SuggestedName);

        // No name → the goal.
        Assert.Equal("the goal", new AutopilotPlan("the goal", null, [new AutopilotStep("1", "First step", "d", "work", null, "b", null)])
            .SuggestedName);

        // No name and no goal (the CEO passed neither) → the first step's title, so the field is never left empty.
        Assert.Equal("First step", new AutopilotPlan("", null, [new AutopilotStep("1", "First step", "d", "work", null, "b", null)])
            .SuggestedName);
    }

    [Fact]
    public void Label_PrefixesTheSourceIssueKey_ForATrackerRun()
    {
        // A tracker-triggered run reads as "AC-191 - …" in the queue and history (AC-199), on both the name and the
        // goal fallback.
        Assert.Equal("AC-191 - Autopilot: enforce the rule", _SourcePlan("Autopilot: enforce the rule").Label);
        Assert.Equal("AC-191 - Chosen name", _SourcePlan("the goal", "Chosen name").Label);
    }

    [Fact]
    public void SuggestedName_PrefixesTheSourceIssueKey_ForATrackerRun()
    {
        Assert.Equal("AC-191 - Autopilot: enforce the rule", _SourcePlan("Autopilot: enforce the rule").SuggestedName);
    }

    [Fact]
    public void SourcePrefix_IsNotAppliedTwice_WhenTheNameAlreadyOpensWithTheIssueKey()
    {
        // The CEO may already have proposed a prefixed name (or the prefix was applied once already, e.g. an approved
        // Name carried it in) — it must not become "AC-191 - AC-191 - …".
        Assert.Equal("AC-191 - Autopilot: enforce", _SourcePlan("AC-191 - Autopilot: enforce", "AC-191 - Autopilot: enforce")
            .Label);
        Assert.Equal("AC-191 - Autopilot: enforce", _SourcePlan("AC-191 - Autopilot: enforce").SuggestedName);
    }

    [Fact]
    public void SourcePrefix_IsNotApplied_ForACeoFirstPlan()
    {
        // No source → no issue key to prefix with; the name is left exactly as-is.
        Assert.Equal("Autopilot: enforce the rule", _Plan("Autopilot: enforce the rule").Label);
        Assert.Equal("Autopilot: enforce the rule", _Plan("Autopilot: enforce the rule").SuggestedName);
    }

    [Fact]
    public void WithName_ReturnsANamedCopy_LeavingTheOriginalUnchanged()
    {
        var plan = _Plan("Add a helper class");
        var named = plan.WithName("HelperTwo");

        Assert.Equal("HelperTwo", named.Name);
        Assert.Equivalent(plan.Steps, named.Steps);
        Assert.Empty(plan.Name);
    }

    [Fact]
    public void WithWorkingDirectory_ReturnsACopyWithTheFolder_LeavingTheOriginalUnchanged()
    {
        var plan = _Plan("Add a helper class");
        var located = plan.WithWorkingDirectory("/home/ray/proj");

        Assert.Equal("/home/ray/proj", located.WorkingDirectory);
        Assert.Equivalent(plan.Steps, located.Steps);
        Assert.Empty(plan.WorkingDirectory);
    }

    // AC-434. Plain xUnit Assert here (not FluentAssertions, which the codebase is moving off — CSharp.md
    // §FluentAssertions) — new assertions, not new usage of the file's existing style.
    private static AutopilotStep _Step(string id, bool reviewGate = false, AutopilotStepStatus status = AutopilotStepStatus.Pending) =>
        new(id, $"Step {id}", "d", "work", null, "b", null, GateMode.Hard, status) { IsReviewGate = reviewGate };

    [Fact]
    public void NextPendingGroup_ForAnOrdinaryStep_IsJustThatOneStep()
    {
        var plan = new AutopilotPlan("goal", null, [_Step("1"), _Step("2")]);

        Assert.Equal(["1"], plan.NextPendingGroup.Select(step => step.Id));
    }

    [Fact]
    public void NextPendingGroup_ForAReviewGate_IsEveryPendingStepSharingTheFlag_InPlanOrder()
    {
        var plan = new AutopilotPlan("goal", null,
            [_Step("code-review", reviewGate: true), _Step("security-review", reviewGate: true), _Step("pr")]);

        Assert.Equal(["code-review", "security-review"], plan.NextPendingGroup.Select(step => step.Id));
    }

    [Fact]
    public void NextPendingGroup_ExcludesAReviewGateStepThatAlreadySettled()
    {
        var plan = new AutopilotPlan("goal", null,
        [
            _Step("code-review", reviewGate: true, status: AutopilotStepStatus.Passed),
            _Step("security-review", reviewGate: true),
        ]);

        Assert.Equal(["security-review"], plan.NextPendingGroup.Select(step => step.Id));
    }

    [Fact]
    public void NextPendingGroup_DoesNotPullANonAdjacentReviewGateForward_PastAnUnfinishedOrdinaryStep()
    {
        // Adversarial-review fix: a CEO plan that does not keep its review gates adjacent (an implement step sitting
        // between them) must not let the second gate run before that step has even started.
        var plan = new AutopilotPlan("goal", null,
            [_Step("code-review", reviewGate: true), _Step("implement"), _Step("security-review", reviewGate: true)]);

        Assert.Equal(["code-review"], plan.NextPendingGroup.Select(step => step.Id));
    }

    [Fact]
    public void NextPendingGroup_IsEmpty_WhenEveryStepHasSettled()
    {
        var plan = new AutopilotPlan("goal", null, [_Step("1", status: AutopilotStepStatus.Passed)]);

        Assert.Empty(plan.NextPendingGroup);
    }
}
