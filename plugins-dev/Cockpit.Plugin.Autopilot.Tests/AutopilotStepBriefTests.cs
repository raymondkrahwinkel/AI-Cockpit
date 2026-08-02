
namespace Cockpit.Plugin.Autopilot.Tests;

// The turns the autonomous run hands its sessions (AC-174): a step agent's visible opening instruction (its work plus
// how to report done) and the CEO's validation turn. Kept pure builders off the coordinator so the wording — the tool
// to call, what to include — is tested without a live session.
public class AutopilotStepBriefTests
{
    [Fact]
    public void For_IncludesTheWork_AcceptanceAndTheStepDoneTool()
    {
        var step = new AutopilotStep("1", "Code", "desc", "Claude", "opus", "do the code", "compiles and tests green");

        var brief = AutopilotStepBrief.For(step, agentCount: 1, agentNumber: 1);

        Assert.Contains("do the code", brief);
        Assert.Contains("compiles and tests green", brief);
        Assert.Contains("mcp__cockpit-autopilot-run__autopilot_step_done", brief);
    }

    [Fact]
    public void For_FallsBackToTheDescription_WhenNoBriefWasWritten()
    {
        var step = new AutopilotStep("1", "Code", "the description", "Claude", "opus", "  ", "acc");

        Assert.Contains("the description", AutopilotStepBrief.For(step, 1, 1));
    }

    [Fact]
    public void For_ParallelAgent_NamesItsShareOfTheWork()
    {
        var step = new AutopilotStep("1", "Code", "d", "Claude", "opus", "b", "a");

        Assert.Contains("agent 2 of 3", AutopilotStepBrief.For(step, agentCount: 3, agentNumber: 2));
    }

    [Fact]
    public void For_CarriesAGenericBrainSkip_SoAnEmbeddedAgentDoesNotStallOnASetupQuestion()
    {
        var step = new AutopilotStep("1", "Code", "d", "Claude", "opus", "do the work", "a");

        var brief = AutopilotStepBrief.For(step, 1, 1);

        // The autonomy preamble tells the agent to step past a persona/brain/config prompt instead of waiting for a
        // human — and it names no specific persona, so it stays generic across profiles.
        Assert.Contains("autonomous agent", brief);
        Assert.Contains("persona, brain, or", brief);
        Assert.Contains("do not stop to ask", brief);
        Assert.DoesNotContain("Zyra", brief);
        Assert.DoesNotContain("Aura", brief);
        // The task itself still comes through.
        Assert.Contains("do the work", brief);
    }

    [Fact]
    public void For_TellsTheAgentToAssumeAndFollowConventions_ForATaskAmbiguity_NotStopToAsk()
    {
        var step = new AutopilotStep("1", "Code", "d", "Claude", "opus", "do the work", "compiles");

        var brief = AutopilotStepBrief.For(step, 1, 1);

        // AC-193: a task ambiguity the brief did not spell out is not a mid-run question — the agent makes the most
        // reasonable assumption, follows the codebase's existing conventions, and records it in its done-summary.
        Assert.Contains("Task ambiguity", brief);
        Assert.Contains("most reasonable assumption", brief);
        Assert.Contains("FOLLOW THE EXISTING CONVENTIONS", brief);
        Assert.Contains("note the assumption in your autopilot_step_done summary", brief);
    }

    [Fact]
    public void For_FramesAutopilotBlockedAsConsultingTheManager_NotReachingTheOperatorDirectly()
    {
        var step = new AutopilotStep("1", "Code", "d", "Claude", "opus", "do the work", "compiles");

        var brief = AutopilotStepBrief.For(step, 1, 1);

        // AC-201: when a reasonable assumption is not enough, the agent consults its MANAGER (the CEO) via
        // autopilot_blocked — which answers or escalates to the operator — rather than reaching the operator itself.
        Assert.Contains("Your manager (the CEO) is reachable", brief);
        Assert.Contains("autopilot_blocked to consult your manager", brief);
        Assert.Contains("escalates to the operator", brief);
        Assert.Contains("Never stop for an ordinary judgement call", brief);
    }

    [Fact]
    public void For_DirectsTheAgentToExecuteAndCommit_NotAnalyseOrPlan()
    {
        var step = new AutopilotStep("1", "Code", "d", "Qwen (local)", null, "do the work", "compiles");

        var brief = AutopilotStepBrief.For(step, 1, 1);

        // The execution mandate: every model, however light, is told to BUILD — write the code, run
        // the tests, commit in the worktree — and explicitly not to analyse, summarise, ask, or reply with a plan, which
        // is the failure that strands a step on a lighter model.
        Assert.Contains("execution task, not an analysis or planning task", brief);
        Assert.Contains("COMMIT your work in this worktree", brief);
        Assert.Contains("Do NOT instead describe the repository", brief);
        Assert.Contains("verify it builds and its tests pass, commit it, and only then report", brief);
        // Provider-neutral — the mandate names no brand or model.
        Assert.DoesNotContain("Claude", brief);
        Assert.DoesNotContain("opus", brief);
    }

    [Fact]
    public void For_KeepsTheAssumptionAndConsultFlow_AlongsideTheExecutionMandate()
    {
        var step = new AutopilotStep("1", "Code", "d", "Claude", "opus", "do the work", "compiles");

        var brief = AutopilotStepBrief.For(step, 1, 1);

        // The new execution mandate must not have displaced AC-193 (assume + follow conventions) or AC-201 (consult the
        // manager, do not stop for an ordinary judgement call).
        Assert.Contains("most reasonable assumption", brief);
        Assert.Contains("autopilot_blocked to consult your manager", brief);
        Assert.Contains("Never stop for an ordinary judgement call", brief);
    }

    [Fact]
    public void ValidationTurn_AsksTheCeoToJudgeAgainstAcceptance_ViaTheTool()
    {
        var step = new AutopilotStep("1", "Code", "d", "Claude", "opus", "b", "compiles");

        var turn = AutopilotStepBrief.ValidationTurn(step, ["opened PR #1"]);

        Assert.Contains("compiles", turn);
        Assert.Contains("opened PR #1", turn);
        Assert.Contains("mcp__cockpit-autopilot-ceo__autopilot_validate", turn);
    }

    [Fact]
    public void ValidationTurn_WithAWhitespaceOnlySingleSummary_UsesTheNoSummaryFallback()
    {
        var step = new AutopilotStep("1", "Code", "d", "Claude", "opus", "b", "compiles");

        var turn = AutopilotStepBrief.ValidationTurn(step, ["   "]);

        // AC-206: a single whitespace-only summary is treated as no summary — the CEO gets the clear fallback rather than
        // a blank "What the agent(s) reported:" block, like the zero-summary case already does.
        Assert.Contains("(the agent reported no summary)", turn);
    }

    [Fact]
    public void ValidationTurn_ListsEveryAgentsReport_ForAParallelStep()
    {
        var step = new AutopilotStep("1", "Code", "d", "Claude", "opus", "b", "a");

        var turn = AutopilotStepBrief.ValidationTurn(step, ["did part A", "did part B"]);

        Assert.Contains("did part A", turn);
        Assert.Contains("did part B", turn);
    }
}
