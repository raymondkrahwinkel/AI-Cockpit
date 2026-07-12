using FluentAssertions;
using Cockpit.Core.Configuration;
using Cockpit.Infrastructure.Sessions;

namespace Cockpit.Core.Tests.Claude;

/// <summary>
/// Exercises <see cref="ClaudeCliProcess.BuildArguments"/> — the spawn flag construction, in
/// particular the <c>--permission-prompt-tool</c>/<c>--mcp-config</c>/<c>--strict-mcp-config</c>
/// wiring — without spawning a real process.
/// </summary>
public class ClaudeCliProcessArgumentsTests
{
    [Fact]
    public void BuildArguments_AlwaysIncludesTheRequiredStreamJsonFlags()
    {
        var args = ClaudeCliProcess.BuildArguments(new ClaudeCliOptions(), permissionMode: null, model: null, new StubPermissionServerState());

        args.Should().ContainInOrder("-p", "--input-format", "stream-json");
        args.Should().Contain("--output-format").And.Contain("--verbose").And.Contain("--include-partial-messages");
    }

    [Fact]
    public void BuildArguments_WhenPermissionServerReady_AppendsPermissionPromptToolAndStrictMcpConfig()
    {
        var state = new StubPermissionServerState
        {
            McpConfigPath = @"C:\Users\x\AppData\Roaming\Cockpit\mcp-permission.json",
            PermissionPromptToolName = "mcp__cockpit__permission_prompt",
        };

        var args = ClaudeCliProcess.BuildArguments(new ClaudeCliOptions(), permissionMode: null, model: null, state);

        args.Should().ContainInOrder(
            "--permission-prompt-tool",
            "mcp__cockpit__permission_prompt",
            "--mcp-config",
            @"C:\Users\x\AppData\Roaming\Cockpit\mcp-permission.json",
            "--strict-mcp-config");
    }

    [Fact]
    public void BuildArguments_WhenPermissionServerNotReady_OmitsThePermissionFlags()
    {
        var args = ClaudeCliProcess.BuildArguments(new ClaudeCliOptions(), permissionMode: null, model: null, new StubPermissionServerState());

        args.Should().NotContain("--permission-prompt-tool");
        args.Should().NotContain("--mcp-config");
        args.Should().NotContain("--strict-mcp-config");
    }

    [Fact]
    public void BuildArguments_InBypassPermissionsMode_OmitsThePromptToolEvenWhenTheServerIsReady()
    {
        // bypassPermissions means "no prompts" — wiring the prompt tool would re-introduce the very
        // prompts the operator bypassed, so bypass must actually bypass (bug #15).
        var state = new StubPermissionServerState
        {
            McpConfigPath = @"C:\Users\x\AppData\Roaming\Cockpit\mcp-permission.json",
            PermissionPromptToolName = "mcp__cockpit__permission_prompt",
        };

        var args = ClaudeCliProcess.BuildArguments(new ClaudeCliOptions(), permissionMode: "bypassPermissions", model: null, state);

        args.Should().ContainInOrder("--permission-mode", "bypassPermissions");
        args.Should().NotContain("--permission-prompt-tool");
        args.Should().NotContain("--mcp-config");
        args.Should().NotContain("--strict-mcp-config");
    }

    [Fact]
    public void BuildArguments_InAcceptEditsMode_KeepsThePromptTool_SinceNonEditToolsAreStillGated()
    {
        var state = new StubPermissionServerState
        {
            McpConfigPath = @"C:\Users\x\AppData\Roaming\Cockpit\mcp-permission.json",
            PermissionPromptToolName = "mcp__cockpit__permission_prompt",
        };

        var args = ClaudeCliProcess.BuildArguments(new ClaudeCliOptions(), permissionMode: "acceptEdits", model: null, state);

        args.Should().Contain("--permission-prompt-tool");
    }

    [Fact]
    public void BuildArguments_UsesExplicitPermissionMode_OverTheOptionsDefault()
    {
        var options = new ClaudeCliOptions { PermissionMode = "default" };

        var args = ClaudeCliProcess.BuildArguments(options, permissionMode: "plan", model: null, new StubPermissionServerState());

        var permissionModeIndex = args.IndexOf("--permission-mode");
        permissionModeIndex.Should().BeGreaterThanOrEqualTo(0);
        args[permissionModeIndex + 1].Should().Be("plan");
    }

    [Fact]
    public void BuildArguments_WithModel_AppendsModelFlag()
    {
        var args = ClaudeCliProcess.BuildArguments(new ClaudeCliOptions(), permissionMode: null, model: "opus", new StubPermissionServerState());

        var modelIndex = args.IndexOf("--model");
        modelIndex.Should().BeGreaterThanOrEqualTo(0);
        args[modelIndex + 1].Should().Be("opus");
    }
}
