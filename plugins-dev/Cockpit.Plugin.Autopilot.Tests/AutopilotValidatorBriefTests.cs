
namespace Cockpit.Plugin.Autopilot.Tests;

// The hidden brief a run's CEO validator session is started with (AC-174): a pure builder, so its wording — and the
// tool names it hands the CEO — is tested without a live session. The tracker and validate tools live on the CEO
// endpoint (`AutopilotCeoTools`), not the step-agent run endpoint (AC-198), so the brief must name them
// there or the stage/note/validate call the CEO makes hits a tool that does not exist.
public class AutopilotValidatorBriefTests
{
    private static AutopilotPlan _SourcePlan() =>
        new("Do the work", new AutopilotPlanSource("YouTrack", "AC-198", "A title"), []);

    [Fact]
    public void For_NamesTheTrackerAndValidateTools_OnTheCeoEndpoint()
    {
        var brief = AutopilotValidatorBrief.For(_SourcePlan());

        Assert.Contains("mcp__cockpit-autopilot-ceo__autopilot_tracker_stage", brief);
        Assert.Contains("mcp__cockpit-autopilot-ceo__autopilot_tracker_note", brief);
        Assert.Contains("mcp__cockpit-autopilot-ceo__autopilot_validate", brief);
    }

    [Fact]
    public void For_DoesNotNameTheTrackerOrValidateTools_OnTheRunEndpoint()
    {
        var brief = AutopilotValidatorBrief.For(_SourcePlan());

        // The step-agent run endpoint hosts only step_done/blocked — the tracker and validate tools are not there, so
        // the brief must never point the CEO at cockpit-autopilot-run for them.
        Assert.DoesNotContain("cockpit-autopilot-run__autopilot_tracker_stage", brief);
        Assert.DoesNotContain("cockpit-autopilot-run__autopilot_tracker_note", brief);
        Assert.DoesNotContain("cockpit-autopilot-run__autopilot_validate", brief);
    }

    [Fact]
    public void For_TellsTheCeoItManagesWorkerConsults_ViaAnswerAndEscalateTools()
    {
        var brief = AutopilotValidatorBrief.For(_SourcePlan());

        // AC-201: the CEO is also the workers' manager — a mid-step consult is answered (relayed to the worker) or, only
        // when it is genuinely an operator call, escalated. Both tools live on the CEO endpoint.
        Assert.Contains("consult you before it continues", brief);
        Assert.Contains("mcp__cockpit-autopilot-ceo__autopilot_answer_worker", brief);
        Assert.Contains("mcp__cockpit-autopilot-ceo__autopilot_escalate_to_operator", brief);
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
