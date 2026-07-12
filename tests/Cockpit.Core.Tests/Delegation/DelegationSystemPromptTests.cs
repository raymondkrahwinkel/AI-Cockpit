using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Configuration;
using Cockpit.Core.Delegation;
using Cockpit.Infrastructure.Sessions;
using Cockpit.Infrastructure.Sessions.Tty;
using FluentAssertions;
using NSubstitute;

namespace Cockpit.Core.Tests.Delegation;

/// <summary>
/// A session that can delegate is told so (#67). Tool descriptions explain what the orchestrator's tools do but
/// not when using them is worthwhile, so without this an agent that could hand bulk work to a local model simply
/// does it itself and the whole feature sits unused. Equally, a session that cannot delegate must not be told it
/// can — a nudge toward tools that are not there is just noise in the prompt.
/// </summary>
public class DelegationSystemPromptTests
{
    [Fact]
    public void TheSdkSpawn_TellsASessionItCanDelegate_OnlyWhenItActuallyGetsTheTools()
    {
        var withTools = ClaudeCliProcess.BuildArguments(new ClaudeCliOptions(), "acceptEdits", "sonnet", _PermissionServer(), canDelegate: true);
        var without = ClaudeCliProcess.BuildArguments(new ClaudeCliOptions(), "acceptEdits", "sonnet", _PermissionServer(), canDelegate: false);

        withTools.Should().Contain("--append-system-prompt");
        withTools.Should().Contain(DelegationSystemPrompt.Default);
        without.Should().NotContain("--append-system-prompt");
    }

    [Fact]
    public void TheTtySpawn_TellsASessionItCanDelegate_OnlyWhenItActuallyGetsTheTools()
    {
        var withTools = ClaudeTtyLauncher.BuildArguments("acceptEdits", "sonnet", "medium", "/tmp/mcp.json", canDelegate: true);
        var without = ClaudeTtyLauncher.BuildArguments("acceptEdits", "sonnet", "medium", "/tmp/mcp.json", canDelegate: false);

        withTools.Should().Contain("--append-system-prompt");
        without.Should().NotContain("--append-system-prompt");
    }

    [Fact]
    public void TheInstruction_PointsAtListProfiles_RatherThanNamingProfilesItself()
    {
        // The profiles and what they are good for live in the cockpit's settings and change there; restating them
        // in the prompt would hand the model a second copy that goes stale the moment a profile is edited.
        DelegationSystemPrompt.Default.Should().Contain("list_profiles");
        DelegationSystemPrompt.Default.Should().Contain("delegate_task");
        DelegationSystemPrompt.Default.Should().Contain("get_task_result");
    }

    private static IPermissionServerState _PermissionServer()
    {
        var state = Substitute.For<IPermissionServerState>();
        state.McpConfigPath.Returns("/tmp/permission-mcp.json");
        state.PermissionPromptToolName.Returns("mcp__cockpit__permission_prompt");
        state.PermissionMcpUrl.Returns("http://127.0.0.1:1234/mcp");
        return state;
    }
}
