namespace Cockpit.Plugin.Autopilot.Tests;

// The hidden brief a run's CEO validator session is started with (AC-174): a pure builder, so its wording and the
// tool names it hands the CEO are tested without a live session. The tracker/validate tools live on the CEO
// endpoint, not the step-agent run endpoint (AC-198), so the brief must name them there or the CEO's calls miss.
public class AutopilotValidatorBriefTests
{
    private static AutopilotPlan _SourcePlan() =>
        new("Do the work", new AutopilotPlanSource("YouTrack", "AC-198", "A title"), []);

    public static IEnumerable<object[]> ToolsOnTheCeoEndpoint() =>
    [
        // Naming them on the CEO endpoint and *not* on the run endpoint is one guarantee seen from two sides: the
        // step-agent run endpoint hosts only step_done/blocked, so a brief that points the CEO at
        // cockpit-autopilot-run for these makes every one of its calls miss.
        [
            new[]
            {
                "mcp__cockpit-autopilot-ceo__autopilot_tracker_stage",
                "mcp__cockpit-autopilot-ceo__autopilot_tracker_note",
                "mcp__cockpit-autopilot-ceo__autopilot_validate",
            },
            new[]
            {
                "cockpit-autopilot-run__autopilot_tracker_stage",
                "cockpit-autopilot-run__autopilot_tracker_note",
                "cockpit-autopilot-run__autopilot_validate",
            },
        ],
        // AC-201: the CEO is also the workers' manager — a mid-step consult is answered (relayed to the worker) or, only
        // when it is genuinely an operator call, escalated. Both tools live on the CEO endpoint.
        [
            new[]
            {
                "consult you before it continues",
                "mcp__cockpit-autopilot-ceo__autopilot_answer_worker",
                "mcp__cockpit-autopilot-ceo__autopilot_escalate_to_operator",
            },
            Array.Empty<string>(),
        ],
    ];

    [Theory]
    [MemberData(nameof(ToolsOnTheCeoEndpoint))]
    public void For_NamesEveryToolItHandsTheCeo_OnTheCeoEndpointOnly(string[] present, string[] absent)
    {
        var brief = AutopilotValidatorBrief.For(_SourcePlan());

        Assert.All(present, name => Assert.Contains(name, brief));
        Assert.All(absent, name => Assert.DoesNotContain(name, brief));
    }

    [Fact]
    public void For_CeoFirstPlan_StillNamesValidateOnTheCeoEndpoint_AndCarriesNoTrackerSentence()
    {
        var brief = AutopilotValidatorBrief.For(new AutopilotPlan("Do the work", null, []));

        Assert.Contains("mcp__cockpit-autopilot-ceo__autopilot_validate", brief);
        // A CEO-first run has no source issue to keep in sync, so no tracker tools are offered.
        Assert.DoesNotContain("autopilot_tracker_stage", brief);
        Assert.DoesNotContain("autopilot_tracker_note", brief);
    }
}
