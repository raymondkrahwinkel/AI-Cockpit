using Cockpit.Plugins.Abstractions.Sessions;
using Cockpit.Plugins.Abstractions.Workspaces;
using NSubstitute;

namespace Cockpit.Plugin.Autopilot.Tests;

// Where a run works (AC-174): the operator's chosen folder wins, else the active session's directory, else the
// cockpit's own — so a run planned from a tracker issue (no session in view) still resolves a folder to work in.
public class AutopilotWorkingDirectoryTests
{
    // Resolve ends in Path.GetFullPath, so a bare "/chosen" comes back drive-rooted on Windows. These are
    // already-absolute paths on either platform, so normalising is a no-op and the expectation stays a literal
    // rather than a second call to the method under test.
    private static readonly string ChosenFolder = Path.Combine(Path.GetTempPath(), "chosen");
    private static readonly string ActiveSession = Path.Combine(Path.GetTempPath(), "active", "session");

    public static IEnumerable<object[]> Preferences() =>
    [
        [ActiveSession, ChosenFolder, ChosenFolder],
        [ActiveSession, null!, ActiveSession],
        [ActiveSession, "   ", ActiveSession],
        [null!, null!, Directory.GetCurrentDirectory()],
    ];

    [Theory]
    [MemberData(nameof(Preferences))]
    public void Resolve_PrefersTheChosenFolder_ThenTheActiveSession_ThenTheCockpitsOwn(
        string? activeSession, string? chosen, string expected) =>
        Assert.Equal(expected, AutopilotWorkingDirectory.Resolve(_Context(activeSession), chosen));

    [Fact]
    public void Resolve_AnswersAnAbsolutePath_EvenWhenItWasGivenARelativeOne()
    {
        // The normalising is the point of this method: the git-status check, the worktree and the confinement each
        // resolve what comes out, and a relative path would let them resolve against different working directories
        // — dropping Path.GetFullPath left all 231 tests green. Both sources are covered because they are separate branches.
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
