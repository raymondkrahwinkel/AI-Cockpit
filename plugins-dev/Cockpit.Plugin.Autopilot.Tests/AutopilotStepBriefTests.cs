using FluentAssertions;

namespace Cockpit.Plugin.Autopilot.Tests;

/// <summary>
/// The turns the autonomous run hands its sessions (AC-174): a step agent's visible opening instruction (its work plus
/// how to report done) and the CEO's validation turn. Kept pure builders off the coordinator so the wording — the tool
/// to call, what to include — is tested without a live session.
/// </summary>
public class AutopilotStepBriefTests
{
    [Fact]
    public void For_IncludesTheWork_AcceptanceAndTheStepDoneTool()
    {
        var step = new AutopilotStep("1", "Code", "desc", "Claude", "opus", "do the code", "compiles and tests green");

        var brief = AutopilotStepBrief.For(step, agentCount: 1, agentNumber: 1);

        brief.Should().Contain("do the code");
        brief.Should().Contain("compiles and tests green");
        brief.Should().Contain("mcp__cockpit-autopilot-run__autopilot_step_done");
    }

    [Fact]
    public void For_FallsBackToTheDescription_WhenNoBriefWasWritten()
    {
        var step = new AutopilotStep("1", "Code", "the description", "Claude", "opus", "  ", "acc");

        AutopilotStepBrief.For(step, 1, 1).Should().Contain("the description");
    }

    [Fact]
    public void For_ParallelAgent_NamesItsShareOfTheWork()
    {
        var step = new AutopilotStep("1", "Code", "d", "Claude", "opus", "b", "a");

        AutopilotStepBrief.For(step, agentCount: 3, agentNumber: 2).Should().Contain("agent 2 of 3");
    }

    [Fact]
    public void For_CarriesAGenericBrainSkip_SoAnEmbeddedAgentDoesNotStallOnASetupQuestion()
    {
        var step = new AutopilotStep("1", "Code", "d", "Claude", "opus", "do the work", "a");

        var brief = AutopilotStepBrief.For(step, 1, 1);

        // The autonomy preamble tells the agent to step past a persona/brain/config prompt instead of waiting for a
        // human — and it names no specific persona, so it stays generic across profiles.
        brief.Should().Contain("autonomous agent");
        brief.Should().Contain("persona, brain, or");
        brief.Should().Contain("do not stop to ask");
        brief.Should().NotContain("Zyra");
        brief.Should().NotContain("Aura");
        // The task itself still comes through.
        brief.Should().Contain("do the work");
    }

    [Fact]
    public void For_TellsTheAgentToAssumeAndFollowConventions_ForATaskAmbiguity_NotStopToAsk()
    {
        var step = new AutopilotStep("1", "Code", "d", "Claude", "opus", "do the work", "compiles");

        var brief = AutopilotStepBrief.For(step, 1, 1);

        // AC-193: a task ambiguity the brief did not spell out is not a mid-run question — the agent makes the most
        // reasonable assumption, follows the codebase's existing conventions, and records it in its done-summary.
        brief.Should().Contain("Task ambiguity");
        brief.Should().Contain("most reasonable assumption");
        brief.Should().Contain("FOLLOW THE EXISTING CONVENTIONS");
        brief.Should().Contain("note the assumption in your autopilot_step_done summary");
        // autopilot_blocked is scoped to a genuine hard blocker only, not an ordinary judgement call.
        brief.Should().Contain("Only use autopilot_blocked for a genuine hard blocker");
    }

    [Fact]
    public void ValidationTurn_AsksTheCeoToJudgeAgainstAcceptance_ViaTheTool()
    {
        var step = new AutopilotStep("1", "Code", "d", "Claude", "opus", "b", "compiles");

        var turn = AutopilotStepBrief.ValidationTurn(step, ["opened PR #1"]);

        turn.Should().Contain("compiles");
        turn.Should().Contain("opened PR #1");
        turn.Should().Contain("mcp__cockpit-autopilot-ceo__autopilot_validate");
    }

    [Fact]
    public void ValidationTurn_ListsEveryAgentsReport_ForAParallelStep()
    {
        var step = new AutopilotStep("1", "Code", "d", "Claude", "opus", "b", "a");

        var turn = AutopilotStepBrief.ValidationTurn(step, ["did part A", "did part B"]);

        turn.Should().Contain("did part A");
        turn.Should().Contain("did part B");
    }
}
