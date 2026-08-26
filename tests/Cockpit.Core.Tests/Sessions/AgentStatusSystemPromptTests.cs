using Cockpit.Core.Sessions;

namespace Cockpit.Core.Tests.Sessions;

/// <summary>
/// Every ordinary spun-up agent is told to keep its own statusline current (AC-544). The set_status tool's own
/// description already says to update it "as you move on" once; this covers the standing instruction that carries
/// that as a habit through the whole session, not only at the moment the model first considers the tool.
/// </summary>
public class AgentStatusSystemPromptTests
{
    [Fact]
    public void TheInstruction_NamesTheTool_AndTellsTheAgentToKeepItCurrent()
    {
        Assert.Contains("set_status", AgentStatusSystemPrompt.Default);

        // Not just "call the tool once" — the whole point is the standing habit: pick up, update as the phase
        // changes, and clear when done. A prompt that only named the tool would be no better than its description.
        Assert.Contains("update", AgentStatusSystemPrompt.Default, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("clear", AgentStatusSystemPrompt.Default, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheInstruction_ExplainsThatARunningCiCheckIsSilent() =>
        Assert.Contains("silence is not a green result", AgentStatusSystemPrompt.Default, StringComparison.OrdinalIgnoreCase);
}
