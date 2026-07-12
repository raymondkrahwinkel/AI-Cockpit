using FluentAssertions;
using Cockpit.Infrastructure.Sessions.Tty;

namespace Cockpit.Core.Tests.Claude;

/// <summary>
/// Covers <see cref="ClaudeTtyLauncher.BuildArguments"/> in isolation — mirrors the teststyle used for
/// <c>ClaudeCliProcess.BuildArguments</c> (see <c>ClaudeCliProcessArgumentsTests</c>): one behaviour
/// per test, no real spawn involved. The session id is not forced (the transcript is located as the new
/// file after launch), so nothing is passed unless a start default was selected.
/// </summary>
public class ClaudeTtyLauncherBuildArgumentsTests
{
    [Fact]
    public void BuildArguments_WithNothingSelected_IsEmpty()
    {
        var arguments = ClaudeTtyLauncher.BuildArguments(permissionMode: null, model: null, effort: null);

        arguments.Should().BeEmpty();
    }

    [Fact]
    public void BuildArguments_WithARegularMode_AddsPermissionModeFlag()
    {
        var arguments = ClaudeTtyLauncher.BuildArguments(permissionMode: "acceptEdits", model: null, effort: null);

        arguments.Should().Equal("--permission-mode", "acceptEdits");
    }

    [Fact]
    public void BuildArguments_WithBypassPermissions_AddsDangerouslySkipPermissionsWithoutPermissionModeFlag()
    {
        var arguments = ClaudeTtyLauncher.BuildArguments(permissionMode: "bypassPermissions", model: null, effort: null);

        arguments.Should().Equal("--dangerously-skip-permissions");
    }

    [Fact]
    public void BuildArguments_WithAModel_AddsModelFlag()
    {
        var arguments = ClaudeTtyLauncher.BuildArguments(permissionMode: null, model: "opus", effort: null);

        arguments.Should().Equal("--model", "opus");
    }

    [Fact]
    public void BuildArguments_WithAnEffort_AddsEffortFlag()
    {
        var arguments = ClaudeTtyLauncher.BuildArguments(permissionMode: null, model: null, effort: "high");

        arguments.Should().Equal("--effort", "high");
    }

    [Fact]
    public void BuildArguments_WithEverythingSelected_CombinesAllFlagsInOrder()
    {
        var arguments = ClaudeTtyLauncher.BuildArguments(permissionMode: "default", model: "sonnet", effort: "max");

        arguments.Should().Equal("--permission-mode", "default", "--model", "sonnet", "--effort", "max");
    }

    [Fact]
    public void BuildArguments_WithAnMcpConfigPath_AddsMcpConfigWithoutStrictFlag()
    {
        // The registry is fanned into the TUI additively (#26): --mcp-config, never --strict-mcp-config, so the
        // user's own .mcp.json survives alongside the cockpit-configured servers.
        var arguments = ClaudeTtyLauncher.BuildArguments(permissionMode: null, model: null, effort: null, mcpConfigPath: "/tmp/mcp.json");

        arguments.Should().Equal("--mcp-config", "/tmp/mcp.json");
        arguments.Should().NotContain("--strict-mcp-config");
    }

    [Fact]
    public void BuildArguments_WithModeAndMcpConfig_AppendsMcpConfigAfterTheStartDefaults()
    {
        var arguments = ClaudeTtyLauncher.BuildArguments(permissionMode: "default", model: null, effort: null, mcpConfigPath: "/tmp/mcp.json");

        arguments.Should().Equal("--permission-mode", "default", "--mcp-config", "/tmp/mcp.json");
    }
}
