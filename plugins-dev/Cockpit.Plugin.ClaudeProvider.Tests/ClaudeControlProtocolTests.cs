using System.Text.Json;

namespace Cockpit.Plugin.ClaudeProvider.Tests;

// `ClaudeControlProtocol` (Fase 4) — the in-band stdio permission channel's wire format, anchored to the
// exact shapes the official Agent SDK transport uses (`claude-agent-sdk-python query.py`): an inbound
// `can_use_tool` control_request, and the `control_response` allow/deny answer echoing its `request_id`.
// These tests are the single place the wire assumptions live, so a live field-name drift is a one-line fix.
public class ClaudeControlProtocolTests
{

    [Fact]
    public void BuildDecisionResponse_Allow_WithAnswers_MergesThemIntoUpdatedInputBesideTheQuestions()
    {
        // AC-715: the AskUserQuestion contract — the SDK reads `updatedInput.answers`, keyed by question text,
        // alongside the `questions` it sent. An allow that echoes only the input approves the question and never
        // answers it, which is what the cockpit did before this.
        const string questions = """{"questions":[{"question":"Which tests?","header":"Tests","options":[{"label":"Both"}],"multiSelect":false}]}""";

        var line = ClaudeControlProtocol.BuildDecisionResponse(
            "req-4", allow: true, originalInputJson: questions, denyMessage: "unused", answersJson: """{"Which tests?":"Both"}""");

        using var document = JsonDocument.Parse(line);
        var updatedInput = document.RootElement.GetProperty("response").GetProperty("response").GetProperty("updatedInput");

        Assert.Equal("Both", updatedInput.GetProperty("answers").GetProperty("Which tests?").GetString());
        Assert.Equal("Which tests?", updatedInput.GetProperty("questions")[0].GetProperty("question").GetString());
    }
}
