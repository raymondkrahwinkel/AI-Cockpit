using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Configuration;
using Cockpit.Core.Sessions;
using Cockpit.Infrastructure.Sessions;
using Cockpit.Infrastructure.Sessions.Tty;
using FluentAssertions;
using NSubstitute;

namespace Cockpit.Core.Tests.Claude;

/// <summary>
/// Resuming an earlier conversation (#: resume): both spawn paths hand the CLI the flag that picks the
/// conversation up — <c>--continue</c> for the most recent one in the working directory, <c>--resume &lt;id&gt;</c>
/// for a named one — and a fresh session passes neither. The CLI resolves both against its own history, so the
/// cockpit never parses a transcript to hand the work back.
/// </summary>
public class ClaudeResumeArgumentsTests
{
    private static readonly ClaudeCliOptions Cli = new();

    private static IPermissionServerState PermissionState()
    {
        var state = Substitute.For<IPermissionServerState>();
        state.McpConfigPath.Returns((string?)null);
        state.PermissionPromptToolName.Returns((string?)null);
        return state;
    }

    [Fact]
    public void SdkSpawn_NewConversation_PassesNoResumeFlag()
    {
        var arguments = ClaudeCliProcess.BuildArguments(Cli, null, null, PermissionState(), resume: SessionResume.New);

        arguments.Should().NotContain("--continue").And.NotContain("--resume");
    }

    [Fact]
    public void SdkSpawn_MostRecent_PassesContinue()
    {
        var arguments = ClaudeCliProcess.BuildArguments(Cli, null, null, PermissionState(), resume: SessionResume.MostRecent);

        arguments.Should().Contain("--continue").And.NotContain("--resume");
    }

    [Fact]
    public void SdkSpawn_BySessionId_PassesResumeWithThatId()
    {
        var arguments = ClaudeCliProcess.BuildArguments(Cli, null, null, PermissionState(), resume: SessionResume.BySessionId("abc-123"));

        arguments.Should().ContainInOrder("--resume", "abc-123");
    }

    // A blank id would make the CLI open its own interactive session picker, which a headless spawn cannot
    // answer — so it degrades to a fresh conversation instead of hanging on a prompt nobody can see.
    [Fact]
    public void SdkSpawn_BySessionIdWithoutAnId_FallsBackToAFreshConversation()
    {
        var arguments = ClaudeCliProcess.BuildArguments(Cli, null, null, PermissionState(), resume: new SessionResume(SessionResumeMode.BySessionId, "  "));

        arguments.Should().NotContain("--resume").And.NotContain("--continue");
    }

    [Fact]
    public void TtySpawn_MostRecent_PassesContinue()
    {
        var arguments = ClaudeTtyLauncher.BuildArguments(null, null, null, resume: SessionResume.MostRecent);

        arguments.Should().Contain("--continue");
    }

    [Fact]
    public void TtySpawn_BySessionId_PassesResumeWithThatId()
    {
        var arguments = ClaudeTtyLauncher.BuildArguments(null, null, null, resume: SessionResume.BySessionId("abc-123"));

        arguments.Should().ContainInOrder("--resume", "abc-123");
    }

    [Fact]
    public void TtySpawn_NewConversation_PassesNoResumeFlag()
    {
        var arguments = ClaudeTtyLauncher.BuildArguments(null, null, null, resume: SessionResume.New);

        arguments.Should().NotContain("--continue").And.NotContain("--resume");
    }
}
