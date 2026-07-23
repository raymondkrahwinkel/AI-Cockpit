using FluentAssertions;

namespace Cockpit.Plugin.Autopilot.Tests;

/// <summary>
/// The turn the CEO is handed when a step worker consults it mid-step (AC-201): a pure builder, so its wording — the
/// step title, the worker's question, and the two tools it hands the CEO (answer / escalate) — is tested without a live
/// session. The tools must be named on the CEO endpoint or the call the CEO makes hits a tool that does not exist.
/// </summary>
public class AutopilotConsultBriefTests
{
    private static AutopilotStep _Step() => new("1", "Wire the API", "d", "Claude", "opus", "b", "compiles");

    [Fact]
    public void ConsultTurn_CarriesTheStepTitle_TheQuestion_AndTheAnswerAndEscalateTools()
    {
        var turn = AutopilotConsultBrief.ConsultTurn(_Step(), "Which auth scheme should the endpoint use?");

        turn.Should().Contain("Wire the API");
        turn.Should().Contain("Which auth scheme should the endpoint use?");
        turn.Should().Contain("mcp__cockpit-autopilot-ceo__autopilot_answer_worker");
        turn.Should().Contain("mcp__cockpit-autopilot-ceo__autopilot_escalate_to_operator");
    }

    [Fact]
    public void ConsultTurn_WithNoActiveStep_StillReadsAndNamesBothTools()
    {
        var turn = AutopilotConsultBrief.ConsultTurn(null, "a question");

        turn.Should().Contain("a question");
        turn.Should().Contain("autopilot_answer_worker");
        turn.Should().Contain("autopilot_escalate_to_operator");
    }
}
