using Cockpit.Core.Projects;

namespace Cockpit.Core.Tests.Projects;

/// <summary>
/// Placing a folder on the project that owns it (AC-320) — the answer a session gets when nobody told it which
/// project it works on, which is every session a plugin embeds.
/// </summary>
public class ProjectDirectoryMatchTests
{
    private static Project _At(string name, string? source) =>
        new(name.ToLowerInvariant(), name) { SourceDirectories = source is null ? [] : [new(source)] };

    [Fact]
    public void For_ASiblingSharingAPrefix_OrTwoProjectsOnOneFolder_AnswersNone()
    {
        Assert.Null(ProjectDirectoryMatch.For([_At("Cockpit", "/repos/cockpit")], "/repos/cockpit-plugins"));

        // Storage order must not decide whose environment a run carries: no project beats the wrong project.
        Assert.Null(ProjectDirectoryMatch.For([_At("First", "/repos/shared"), _At("Second", "/repos/shared")], "/repos/shared"));
    }

    [Fact]
    public void For_NestedProjects_TheMostSpecificClaimWins_AndASpreadOutSecondRepositoryIsStillTheSameProject()
    {
        var match = ProjectDirectoryMatch.For(
            [_At("Monorepo", "/repos/mono"), _At("Web", "/repos/mono/apps/web")],
            "/repos/mono/apps/web/src");

        Assert.NotNull(match);
        Assert.Equal("Web", match.Name);

        // AC-938: a Waymark-shaped project (a web repo and an android repo, spread across the disk, neither nested
        // in the other) needs a run in *either* declared repository to match the same project.
        var waymark = new Project("waymark", "Waymark")
        {
            SourceDirectories = [new("/repos/waymark-web"), new("/home/dev/waymark-android")],
        };

        Assert.Equal("Waymark", ProjectDirectoryMatch.For([waymark], "/home/dev/waymark-android/src")?.Name);
    }
}
