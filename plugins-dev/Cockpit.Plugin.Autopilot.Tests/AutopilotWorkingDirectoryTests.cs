using Cockpit.Plugins.Abstractions.Sessions;
using Cockpit.Plugins.Abstractions.Workspaces;
using FluentAssertions;
using NSubstitute;

namespace Cockpit.Plugin.Autopilot.Tests;

/// <summary>
/// Where a run works (AC-174): the operator's chosen folder wins, else the active session's directory, else the
/// cockpit's own — so a run planned from a tracker issue (no session in view) still resolves a folder to work in.
/// </summary>
public class AutopilotWorkingDirectoryTests
{
    [Fact]
    public void Resolve_PrefersTheChosenFolder_OverTheActiveSession()
    {
        AutopilotWorkingDirectory.Resolve(_Context("/active/session"), "/chosen").Should().Be("/chosen");
    }

    [Fact]
    public void Resolve_FallsBackToTheActiveSession_WhenNoFolderChosen()
    {
        var context = _Context("/active/session");
        AutopilotWorkingDirectory.Resolve(context, null).Should().Be("/active/session");
        AutopilotWorkingDirectory.Resolve(context, "   ").Should().Be("/active/session");
    }

    [Fact]
    public void Resolve_FallsBackToTheCurrentDirectory_WhenNeitherIsSet()
    {
        AutopilotWorkingDirectory.Resolve(_Context(null), null).Should().Be(Directory.GetCurrentDirectory());
    }

    private static IWorkspaceContext _Context(string? activeSessionDirectory)
    {
        var sessions = Substitute.For<ICockpitSessionObserver>();
        sessions.ActiveSessionWorkingDirectory.Returns(activeSessionDirectory);
        var context = Substitute.For<IWorkspaceContext>();
        context.Sessions.Returns(sessions);
        return context;
    }
}
