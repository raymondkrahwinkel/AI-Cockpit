using FluentAssertions;
using Cockpit.Infrastructure.Claude.Tty;

namespace Cockpit.Core.Tests.Claude;

/// <summary>
/// Covers <see cref="ClaudeTtyLauncher.BuildArguments"/> in isolation — mirrors the teststyle used for
/// <c>ClaudeCliProcess.BuildArguments</c> (see <c>ClaudeCliProcessArgumentsTests</c>): one behaviour
/// per test, no real spawn involved.
/// </summary>
public class ClaudeTtyLauncherBuildArgumentsTests
{
    private static readonly Guid SessionId = Guid.Parse("11111111-2222-3333-4444-555555555555");

    [Fact]
    public void BuildArguments_WithNothingSelected_OnlyForcesTheSessionId()
    {
        var arguments = ClaudeTtyLauncher.BuildArguments(SessionId, permissionMode: null, model: null, effort: null);

        arguments.Should().Equal("--session-id", SessionId.ToString());
    }

    [Fact]
    public void BuildArguments_WithARegularMode_AddsPermissionModeFlag()
    {
        var arguments = ClaudeTtyLauncher.BuildArguments(SessionId, permissionMode: "acceptEdits", model: null, effort: null);

        arguments.Should().Equal("--session-id", SessionId.ToString(), "--permission-mode", "acceptEdits");
    }

    [Fact]
    public void BuildArguments_WithBypassPermissions_AddsDangerouslySkipPermissionsWithoutPermissionModeFlag()
    {
        var arguments = ClaudeTtyLauncher.BuildArguments(SessionId, permissionMode: "bypassPermissions", model: null, effort: null);

        arguments.Should().Equal("--session-id", SessionId.ToString(), "--dangerously-skip-permissions");
    }

    [Fact]
    public void BuildArguments_WithAModel_AddsModelFlag()
    {
        var arguments = ClaudeTtyLauncher.BuildArguments(SessionId, permissionMode: null, model: "opus", effort: null);

        arguments.Should().Equal("--session-id", SessionId.ToString(), "--model", "opus");
    }

    [Fact]
    public void BuildArguments_WithAnEffort_AddsEffortFlag()
    {
        var arguments = ClaudeTtyLauncher.BuildArguments(SessionId, permissionMode: null, model: null, effort: "high");

        arguments.Should().Equal("--session-id", SessionId.ToString(), "--effort", "high");
    }

    [Fact]
    public void BuildArguments_WithEverythingSelected_CombinesAllFlagsInOrder()
    {
        var arguments = ClaudeTtyLauncher.BuildArguments(SessionId, permissionMode: "default", model: "sonnet", effort: "max");

        arguments.Should().Equal(
            "--session-id", SessionId.ToString(), "--permission-mode", "default", "--model", "sonnet", "--effort", "max");
    }
}
