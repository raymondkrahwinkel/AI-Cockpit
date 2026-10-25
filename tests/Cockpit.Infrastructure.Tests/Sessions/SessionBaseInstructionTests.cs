using Cockpit.Core.Sessions;
using Cockpit.Infrastructure.Sessions;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Infrastructure.Tests.Sessions;

// AC-1705 (L2c): a session started with no briefing still knows how a turn ends and where its mail arrives — after
// the profile's and project's own words, ahead of the statusline line, on the one option every driver reads.
public class SessionBaseInstructionTests
{
    [Fact]
    public void EveryLaunch_CarriesTheBaseInstruction_AfterTheProfilesWords_AheadOfTheStatusline()
    {
        var options = SessionStartComposer.WithInstructions(options: null, systemPrompt: "Be terse.", isJobRun: false);

        Assert.Equal(
            "Be terse.\n\n" + SessionBaseInstruction.Text + "\n\n" + AgentStatusSystemPrompt.Default,
            options[WellKnownPluginSessionOptions.AppendSystemPrompt]);
        Assert.Contains("announced next step", SessionBaseInstruction.Text, StringComparison.Ordinal);
        Assert.Contains("read_inbox", SessionBaseInstruction.Text, StringComparison.Ordinal);
    }
}
