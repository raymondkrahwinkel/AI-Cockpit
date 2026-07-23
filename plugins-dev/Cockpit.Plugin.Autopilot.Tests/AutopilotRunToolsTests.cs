using System.ComponentModel;
using System.Reflection;
using FluentAssertions;

namespace Cockpit.Plugin.Autopilot.Tests;

/// <summary>
/// The step agent's MCP tools (AC-193): the wording of the <c>autopilot_blocked</c> tool description, which the model
/// reads to decide when to stop. It is asserted here rather than through a live session so the last-resort framing —
/// prefer a documented assumption, block only on a genuine hard blocker — is pinned without a running run.
/// </summary>
public class AutopilotRunToolsTests
{
    private static string BlockedDescription()
    {
        var method = typeof(AutopilotRunTools).GetMethod(nameof(AutopilotRunTools.Blocked), BindingFlags.Instance | BindingFlags.Public);
        method.Should().NotBeNull();
        return method!.GetCustomAttribute<DescriptionAttribute>()!.Description;
    }

    [Fact]
    public void BlockedDescription_IsLastResort_AndNoLongerTellsTheAgentToUseItInsteadOfGuessing()
    {
        var description = BlockedDescription();

        // AC-193: the old "Use this instead of guessing" steered the agent to block on any uncertainty. It is gone.
        description.Should().NotContain("instead of guessing");
        // The tool is now framed as a last resort that prefers a documented assumption + carrying on.
        description.Should().Contain("Last resort");
        description.Should().Contain("documented, reasonable assumption");
        description.Should().Contain("genuine hard blocker");
        description.Should().Contain("irreversible or destructive");
        description.Should().Contain("missing credential");
    }
}
