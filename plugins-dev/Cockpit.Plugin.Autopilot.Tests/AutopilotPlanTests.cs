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
    [InlineData("Add a helper class", "", "Add a helper class")]
    [InlineData("Add a helper class", "HelperTwo", "HelperTwo")]
    public void ForACeoFirstPlan_BothLabels_AreTheNameFallingBackToTheGoal_WithNoPrefix(string goal, string name, string expected)
    {
        // No source → no issue key to prefix with; the name is left exactly as-is on both spellings.
        var plan = _Plan(goal, name);

        Assert.Equal(expected, plan.Label);
        Assert.Equal(expected, plan.SuggestedName);
    }

    [Fact]
    public void SuggestedName_WithNeitherNameNorGoal_FallsBackToTheFirstStepTitle() =>
        // The CEO does not always pass a name or a goal, and the approval field must never be left empty. `Label` has
        // no such duty and stops at the goal, which is why only this spelling is asserted.
        Assert.Equal("Step", _Plan(string.Empty).SuggestedName);

    // A tracker-triggered run reads as "AC-191 - …" in the queue and history (AC-199) on both spellings — and exactly
    // once: the CEO may already have proposed a prefixed name, or an approved Name carried one in, and neither may
    // become "AC-191 - AC-191 - …".
    [Theory]
    [InlineData("Autopilot: enforce the rule", "", "AC-191 - Autopilot: enforce the rule")]
    [InlineData("the goal", "Chosen name", "AC-191 - Chosen name")]
    [InlineData("AC-191 - Autopilot: enforce", "", "AC-191 - Autopilot: enforce")]
    [InlineData("AC-191 - Autopilot: enforce", "AC-191 - Autopilot: enforce", "AC-191 - Autopilot: enforce")]
    public void ForATrackerRun_BothLabels_CarryTheIssueKeyExactlyOnce(string goal, string name, string expected)
    {
        var plan = _SourcePlan(goal, name);

        Assert.Equal(expected, plan.Label);
        Assert.Equal(expected, plan.SuggestedName);
    }

    [Fact]
    public void TheWithHelpers_ReturnACopy_LeavingTheOriginalUnchanged()
    {
        var plan = _Plan("Add a helper class");

        var named = plan.WithName("HelperTwo");
        var located = plan.WithWorkingDirectory("/home/ray/proj");

        Assert.Equal("HelperTwo", named.Name);
        Assert.Equal("/home/ray/proj", located.WorkingDirectory);
        Assert.Equivalent(plan.Steps, named.Steps);
        Assert.Equivalent(plan.Steps, located.Steps);
        Assert.Empty(plan.Name);
        Assert.Empty(plan.WorkingDirectory);
    }

    // AC-434. Plain xUnit Assert here (not FluentAssertions, which the codebase is moving off — CSharp.md
    // §FluentAssertions) — new assertions, not new usage of the file's existing style.
    private static AutopilotStep _Step(string id, bool reviewGate = false, AutopilotStepStatus status = AutopilotStepStatus.Pending) =>
        new(id, $"Step {id}", "d", "work", null, "b", null, GateMode.Hard, status) { IsReviewGate = reviewGate };

    // The steps are an internal record, so the rows box the array and the test casts it back — one cast, no mapping.
    public static IEnumerable<object[]> PlanShapes() =>
    [
        // An ordinary step goes alone.
        [new[] { _Step("1"), _Step("2") }, new[] { "1" }],
        // Adjacent review gates go together, in plan order.
        [
            new[] { _Step("code-review", reviewGate: true), _Step("security-review", reviewGate: true), _Step("pr") },
            new[] { "code-review", "security-review" },
        ],
        // A gate that already settled is not pulled back in.
        [
            new[] { _Step("code-review", reviewGate: true, status: AutopilotStepStatus.Passed), _Step("security-review", reviewGate: true) },
            new[] { "security-review" },
        ],
        // Adversarial-review fix: a CEO plan that does not keep its review gates adjacent (an implement step sitting
        // between them) must not let the second gate run before that step has even started.
        [
            new[] { _Step("code-review", reviewGate: true), _Step("implement"), _Step("security-review", reviewGate: true) },
            new[] { "code-review" },
        ],
        // Nothing left to run.
        [new[] { _Step("1", status: AutopilotStepStatus.Passed) }, Array.Empty<string>()],
    ];

    [Theory]
    [MemberData(nameof(PlanShapes))]
    public void NextPendingGroup_TakesTheNextStep_OrTheWholeAdjacentReviewGate(object steps, string[] expected)
    {
        var plan = new AutopilotPlan("goal", null, (AutopilotStep[])steps);

        Assert.Equal(expected, plan.NextPendingGroup.Select(step => step.Id));
    }
}
