using Cockpit.Core.Projects;
using FluentAssertions;

namespace Cockpit.Core.Tests.Projects;

/// <summary>
/// Finding the project behind a link a plugin holds (AC-419) — what the New-session dialog is preselected on when a
/// tracker plugin starts a session from an issue, and what it deliberately is not.
/// </summary>
public class ProjectLinkMatchTests
{
    private const string Tracker = "youtrack.project";

    private static Project _Linked(string name, string key, string value) =>
        new(name.ToLowerInvariant(), name) { PluginFields = new Dictionary<string, string> { [key] = value } };

    [Fact]
    public void For_TheProjectCarryingTheLink_IsThatProject() =>
        ProjectLinkMatch.For([_Linked("Cockpit", Tracker, "AC")], Tracker, "AC")
            .Should().NotBeNull().And.Subject.As<Project>().Name.Should().Be("Cockpit");

    [Fact]
    public void For_AmongProjectsLinkedElsewhere_PicksTheOneThatMatches() =>
        ProjectLinkMatch.For(
                [_Linked("Depot", Tracker, "DEP"), _Linked("Cockpit", Tracker, "AC")],
                Tracker,
                "AC")
            .Should().NotBeNull().And.Subject.As<Project>().Name.Should().Be("Cockpit");

    [Fact]
    public void For_ALinkNoProjectDeclares_IsNoProject() =>
        ProjectLinkMatch.For([_Linked("Depot", Tracker, "DEP")], Tracker, "AC").Should().BeNull();

    [Fact]
    public void For_TheSameValueUnderAnotherKey_IsNotTheSameLink() =>
        ProjectLinkMatch.For([_Linked("Cockpit", "github.repository", "AC")], Tracker, "AC").Should().BeNull();

    [Fact]
    public void For_TwoProjectsCarryingTheLink_IsNoProject() =>
        ProjectLinkMatch.For(
                [_Linked("Cockpit", Tracker, "AC"), _Linked("Cockpit fork", Tracker, "AC")],
                Tracker,
                "AC")
            .Should().BeNull("preselecting one of them would pick by storage order, and a wrong preselection is read as right");

    [Fact]
    public void For_ValuesDifferingOnlyInCase_AreTheSameLinkTwice() =>
        ProjectLinkMatch.For(
                [_Linked("Cockpit", Tracker, "AC"), _Linked("Cockpit fork", Tracker, "ac")],
                Tracker,
                "AC")
            .Should().BeNull();

    [Fact]
    public void For_AValueInAnotherCase_StillMatches() =>
        ProjectLinkMatch.For([_Linked("Cockpit", Tracker, "AC")], Tracker, "ac")
            .Should().NotBeNull("a tracker short name is not a case-sensitive identifier");

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void For_NoValueToMatchOn_IsNoProject(string? value) =>
        ProjectLinkMatch.For([_Linked("Cockpit", Tracker, "AC")], Tracker, value).Should().BeNull();

    [Fact]
    public void For_NoProjectsAtAll_IsNoProject() =>
        ProjectLinkMatch.For([], Tracker, "AC").Should().BeNull();
}
