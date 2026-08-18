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
    public void For_TheProjectsOwnFolder_IsThatProject()
    {
        var match = ProjectDirectoryMatch.For([_At("Cockpit", "/repos/cockpit")], "/repos/cockpit");

        Assert.NotNull(match);
        Assert.Equal("Cockpit", match.Name);
    }

    [Fact]
    public void For_AFolderInside_IsStillThatProject() =>
        Assert.NotNull(ProjectDirectoryMatch.For([_At("Cockpit", "/repos/cockpit")], "/repos/cockpit/src/Core"));

    [Fact]
    public void For_ASiblingSharingAPrefix_IsNotInsideIt() =>
        Assert.Null(ProjectDirectoryMatch.For([_At("Cockpit", "/repos/cockpit")], "/repos/cockpit-plugins"));

    [Fact]
    public void For_TrailingSeparatorsAndRelativeSegments_AreTheSameFolder() =>
        Assert.NotNull(ProjectDirectoryMatch.For([_At("Cockpit", "/repos/cockpit/")], "/repos/cockpit/src/.."));

    [Fact]
    public void For_NestedProjects_TheMostSpecificClaimWins()
    {
        var match = ProjectDirectoryMatch.For(
            [_At("Monorepo", "/repos/mono"), _At("Web", "/repos/mono/apps/web")],
            "/repos/mono/apps/web/src");

        Assert.NotNull(match);
        Assert.Equal("Web", match.Name);
    }

    [Fact]
    public void For_NestedProjects_OrderDoesNotDecide()
    {
        var match = ProjectDirectoryMatch.For(
            [_At("Web", "/repos/mono/apps/web"), _At("Monorepo", "/repos/mono")],
            "/repos/mono/apps/web/src");

        Assert.NotNull(match);
        Assert.Equal("Web", match.Name);
    }

    [Fact]
    public void For_TwoProjectsOnOneFolder_AnswersNeither() =>
        // Storage order must not decide whose environment a run carries: no project beats the wrong project.
        Assert.Null(ProjectDirectoryMatch.For([_At("First", "/repos/shared"), _At("Second", "/repos/shared")], "/repos/shared"));

    [Fact]
    public void For_TwoProjectsOnOneFolder_ADeeperClaimStillWins()
    {
        var match = ProjectDirectoryMatch.For(
            [_At("First", "/repos/shared"), _At("Second", "/repos/shared"), _At("Inner", "/repos/shared/inner")],
            "/repos/shared/inner");

        Assert.NotNull(match);
        Assert.Equal("Inner", match.Name);
    }

    // AC-938: a Waymark-shaped project (a web repo and an android repo, spread across the disk, neither nested in
    // the other) needs a run in *either* declared repository to match the same project.

    [Fact]
    public void For_ASpreadOutSecondRepository_IsStillTheSameProject()
    {
        var waymark = new Project("waymark", "Waymark")
        {
            SourceDirectories = [new("/repos/waymark-web"), new("/home/dev/waymark-android")],
        };

        var match = ProjectDirectoryMatch.For([waymark], "/home/dev/waymark-android/src");

        Assert.NotNull(match);
        Assert.Equal("Waymark", match.Name);
    }

    [Fact]
    public void For_TwoOfTheSameProjectsOwnRepositoriesClaimingTheSameFolder_IsStillThatProject()
    {
        // Not the ambiguity For_TwoProjectsOnOneFolder_AnswersNeither guards against — that is two *different*
        // projects claiming the same folder. Two rows of the *same* project agreeing on an answer is not a
        // conflict to refuse.
        var project = new Project("dup", "Duplicated")
        {
            SourceDirectories = [new("/repos/one"), new("/repos/one")],
        };

        var match = ProjectDirectoryMatch.For([project], "/repos/one/src");

        Assert.NotNull(match);
        Assert.Equal("Duplicated", match.Name);
    }

    [Fact]
    public void For_AFolderNoProjectClaims_IsNoProject() =>
        Assert.Null(ProjectDirectoryMatch.For([_At("Cockpit", "/repos/cockpit")], "/tmp/scratch"));

    [Fact]
    public void For_AProjectWithoutAFolder_ClaimsNothing() =>
        Assert.Null(ProjectDirectoryMatch.For([_At("Admin", null), _At("Blank", "   ")], "/repos/cockpit"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void For_WithoutADirectory_IsNoProject(string? directory) =>
        Assert.Null(ProjectDirectoryMatch.For([_At("Cockpit", "/repos/cockpit")], directory));
}
