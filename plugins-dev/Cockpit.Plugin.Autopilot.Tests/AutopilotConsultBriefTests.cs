namespace Cockpit.Plugin.Autopilot.Tests;

// The turn the CEO is handed when a step worker consults it mid-step (AC-201): a pure builder, so its wording — the
// step title, the worker's question, and the two tools it hands the CEO (answer / escalate) — is tested without a live
// session. The tools must be named on the CEO endpoint or the call the CEO makes hits a tool that does not exist.
public class AutopilotConsultBriefTests
{
    // The step is an internal record, so the rows box it and the test casts back once.
    public static IEnumerable<object[]> Consults() =>
    [
        [
            new AutopilotStep("1", "Wire the API", "d", "Claude", "opus", "b", "compiles"),
            "Which auth scheme should the endpoint use?",
            new[]
            {
                "Wire the API",
                "Which auth scheme should the endpoint use?",
                "mcp__cockpit-autopilot-ceo__autopilot_answer_worker",
                "mcp__cockpit-autopilot-ceo__autopilot_escalate_to_operator",
            },
        ],
        // With no active step the turn still reads, and still names both tools on the CEO endpoint.
        [
            null!, "a question",
            new[]
            {
                "a question",
                "mcp__cockpit-autopilot-ceo__autopilot_answer_worker",
                "mcp__cockpit-autopilot-ceo__autopilot_escalate_to_operator",
            },
        ],
    ];

    [Theory]
    [MemberData(nameof(Consults))]
    public void ConsultTurn_CarriesTheStepTitle_TheQuestion_AndBothTools(object? step, string question, string[] present)
    {
        var turn = AutopilotConsultBrief.ConsultTurn((AutopilotStep?)step, question);

        Assert.All(present, fragment => Assert.Contains(fragment, turn));
    }
}
