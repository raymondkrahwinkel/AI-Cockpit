using Cockpit.Plugins.Abstractions.Sessions;
using Cockpit.Plugins.Abstractions.Workspaces;
using NSubstitute;

namespace Cockpit.Plugin.Autopilot.Tests;

// Where a run works (AC-174): the operator's chosen folder wins, else the active session's directory, else the
// cockpit's own — so a run planned from a tracker issue (no session in view) still resolves a folder to work in.
public class AutopilotWorkingDirectoryTests
{
    // Resolve ends in Path.GetFullPath, so a bare "/chosen" comes back drive-rooted on Windows and the test read
    // as a failure of code that was doing exactly what it promises. These are already-absolute paths on either
    // platform, so normalising them is a no-op and the expectation stays a literal rather than a second call to
    // the method under test.
    private static readonly string ChosenFolder = Path.Combine(Path.GetTempPath(), "chosen");
    private static readonly string ActiveSession = Path.Combine(Path.GetTempPath(), "active", "session");

    [Fact]
    public void Resolve_PrefersTheChosenFolder_OverTheActiveSession()
    {
        Assert.Equal(ChosenFolder, AutopilotWorkingDirectory.Resolve(_Context(ActiveSession), ChosenFolder));
    }

    [Fact]
    public void Resolve_FallsBackToTheActiveSession_WhenNoFolderChosen()
    {
        var context = _Context(ActiveSession);
        Assert.Equal(ActiveSession, AutopilotWorkingDirectory.Resolve(context, null));
        Assert.Equal(ActiveSession, AutopilotWorkingDirectory.Resolve(context, "   "));
    }

    [Fact]
    public void Resolve_FallsBackToTheCurrentDirectory_WhenNeitherIsSet()
    {
        Assert.Equal(Directory.GetCurrentDirectory(), AutopilotWorkingDirectory.Resolve(_Context(null), null));
    }

    [Fact]
    public void Resolve_AnswersAnAbsolutePath_EvenWhenItWasGivenARelativeOne()
    {
        // The normalising is the point of this method, not a detail of it: the git-status check, the worktree and
        // the confinement each resolve what comes out, and a relative path would let them resolve against different
        // working directories. Nothing held that — dropping the Path.GetFullPath left all 231 tests green.
        //
        // Both sources, because they are separate branches: normalising only the one the operator types would still
        // leave the session's directory going through raw, and that mutant passed everything.
        var relative = Path.Combine(".", "a", "..", "b");
        var expected = Path.Combine(Directory.GetCurrentDirectory(), "b");

        var chosen = AutopilotWorkingDirectory.Resolve(_Context(null), relative);
        var session = AutopilotWorkingDirectory.Resolve(_Context(relative), null);

        Assert.True(Path.IsPathFullyQualified(chosen));
        Assert.Equal(expected, chosen);
        Assert.Equal(expected, session);
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
