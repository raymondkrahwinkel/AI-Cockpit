using FluentAssertions;

namespace Cockpit.Plugin.ClaudeProvider.Tests;

/// <summary>
/// <see cref="ClaudeSdkArguments.BuildArguments"/> (Fase 4, SDK route) — the headless stream-json invocation, and the
/// one thing it deliberately does NOT do that the host's in-tree spawn did: wire a <c>--permission-prompt-tool</c>/MCP
/// permission server. Approvals ride the control protocol instead, so that flag must never appear.
/// </summary>
public class ClaudeSdkArgumentsTests
{
    [Fact]
    public void BuildArguments_IsHeadlessStreamJson()
    {
        var arguments = ClaudeSdkArguments.BuildArguments(permissionMode: "default", model: null, resumeSessionId: null, continueMostRecent: false);

        arguments.Should().ContainInOrder("-p", "--input-format", "stream-json", "--output-format", "stream-json");
        arguments.Should().Contain("--verbose");
        arguments.Should().Contain("--include-partial-messages");
        arguments.Should().ContainInOrder("--permission-mode", "default");
    }

    [Fact]
    public void BuildArguments_NeverWiresAPermissionPromptTool()
    {
        // The whole point of the control-protocol route: no HTTP MCP permission server, so none of its flags.
        var arguments = ClaudeSdkArguments.BuildArguments("default", "opus", null, false);

        arguments.Should().NotContain("--permission-prompt-tool");
        arguments.Should().NotContain("--mcp-config");
        arguments.Should().NotContain("--strict-mcp-config");
    }

    [Fact]
    public void BuildArguments_ModelAndNamedResume_AreFlags()
    {
        var arguments = ClaudeSdkArguments.BuildArguments("plan", "sonnet", resumeSessionId: "sess-123", continueMostRecent: true);

        arguments.Should().ContainInOrder("--model", "sonnet");
        // A named resume wins over "most recent": --resume with the id, never --continue.
        arguments.Should().ContainInOrder("--resume", "sess-123");
        arguments.Should().NotContain("--continue");
    }

    [Fact]
    public void BuildArguments_ContinueMostRecent_WhenNoNamedResume()
    {
        var arguments = ClaudeSdkArguments.BuildArguments("default", null, resumeSessionId: null, continueMostRecent: true);

        arguments.Should().Contain("--continue");
        arguments.Should().NotContain("--resume");
    }

    [Fact]
    public void BuildArguments_BlankPermissionMode_DefaultsToDefault()
    {
        var arguments = ClaudeSdkArguments.BuildArguments(permissionMode: "  ", model: null, resumeSessionId: null, continueMostRecent: false);

        arguments.Should().ContainInOrder("--permission-mode", "default");
    }
}
