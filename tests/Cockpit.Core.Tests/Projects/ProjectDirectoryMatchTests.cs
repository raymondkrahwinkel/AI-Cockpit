using Cockpit.Core.Projects;
using FluentAssertions;

namespace Cockpit.Core.Tests.Projects;

/// <summary>
/// Placing a folder on the project that owns it (AC-320) — the answer a session gets when nobody told it which
/// project it works on, which is every session a plugin embeds.
/// </summary>
public class ProjectDirectoryMatchTests
{
    private static Project _At(string name, string? source) =>
        new(name.ToLowerInvariant(), name) { SourceDirectory = source };

    [Fact]
    public void For_TheProjectsOwnFolder_IsThatProject() =>
        ProjectDirectoryMatch.For([_At("Cockpit", "/repos/cockpit")], "/repos/cockpit")
            .Should().NotBeNull().And.Subject.As<Project>().Name.Should().Be("Cockpit");

    [Fact]
    public void For_AFolderInside_IsStillThatProject() =>
        ProjectDirectoryMatch.For([_At("Cockpit", "/repos/cockpit")], "/repos/cockpit/src/Core")
            .Should().NotBeNull();

    [Fact]
    public void For_ASiblingSharingAPrefix_IsNotInsideIt() =>
        ProjectDirectoryMatch.For([_At("Cockpit", "/repos/cockpit")], "/repos/cockpit-plugins")
            .Should().BeNull();

    [Fact]
    public void For_TrailingSeparatorsAndRelativeSegments_AreTheSameFolder() =>
        ProjectDirectoryMatch.For([_At("Cockpit", "/repos/cockpit/")], "/repos/cockpit/src/..")
            .Should().NotBeNull();

    [Fact]
    public void For_NestedProjects_TheMostSpecificClaimWins() =>
        ProjectDirectoryMatch.For(
                [_At("Monorepo", "/repos/mono"), _At("Web", "/repos/mono/apps/web")],
                "/repos/mono/apps/web/src")
            .Should().NotBeNull().And.Subject.As<Project>().Name.Should().Be("Web");

    [Fact]
    public void For_NestedProjects_OrderDoesNotDecide() =>
        ProjectDirectoryMatch.For(
                [_At("Web", "/repos/mono/apps/web"), _At("Monorepo", "/repos/mono")],
                "/repos/mono/apps/web/src")
            .Should().NotBeNull().And.Subject.As<Project>().Name.Should().Be("Web");

    [Fact]
    public void For_TwoProjectsOnOneFolder_AnswersNeither() =>
        // Storage order must not decide whose environment a run carries: no project beats the wrong project.
        ProjectDirectoryMatch.For([_At("First", "/repos/shared"), _At("Second", "/repos/shared")], "/repos/shared")
            .Should().BeNull();

    [Fact]
    public void For_TwoProjectsOnOneFolder_ADeeperClaimStillWins() =>
        ProjectDirectoryMatch.For(
                [_At("First", "/repos/shared"), _At("Second", "/repos/shared"), _At("Inner", "/repos/shared/inner")],
                "/repos/shared/inner")
            .Should().NotBeNull().And.Subject.As<Project>().Name.Should().Be("Inner");

    [Fact]
    public void For_AFolderNoProjectClaims_IsNoProject() =>
        ProjectDirectoryMatch.For([_At("Cockpit", "/repos/cockpit")], "/tmp/scratch").Should().BeNull();

    [Fact]
    public void For_AProjectWithoutAFolder_ClaimsNothing() =>
        ProjectDirectoryMatch.For([_At("Admin", null), _At("Blank", "   ")], "/repos/cockpit").Should().BeNull();

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void For_WithoutADirectory_IsNoProject(string? directory) =>
        ProjectDirectoryMatch.For([_At("Cockpit", "/repos/cockpit")], directory).Should().BeNull();
}
