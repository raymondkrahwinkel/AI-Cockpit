using Cockpit.Core.Projects;

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
    public void For_TheProjectCarryingTheLink_IsThatProject()
    {
        var match = ProjectLinkMatch.For([_Linked("Cockpit", Tracker, "AC")], Tracker, "AC");

        Assert.NotNull(match);
        Assert.Equal("Cockpit", match.Name);
    }

    [Fact]
    public void For_AmongProjectsLinkedElsewhere_PicksTheOneThatMatches()
    {
        var match = ProjectLinkMatch.For(
            [_Linked("Depot", Tracker, "DEP"), _Linked("Cockpit", Tracker, "AC")],
            Tracker,
            "AC");

        Assert.NotNull(match);
        Assert.Equal("Cockpit", match.Name);
    }

    [Fact]
    public void For_ALinkNoProjectDeclares_IsNoProject() =>
        Assert.Null(ProjectLinkMatch.For([_Linked("Depot", Tracker, "DEP")], Tracker, "AC"));

    [Fact]
    public void For_TheSameValueUnderAnotherKey_IsNotTheSameLink() =>
        Assert.Null(ProjectLinkMatch.For([_Linked("Cockpit", "github.repository", "AC")], Tracker, "AC"));

    [Fact]
    public void For_TwoProjectsCarryingTheLink_IsNoProject() =>
        Assert.Null(ProjectLinkMatch.For(
            [_Linked("Cockpit", Tracker, "AC"), _Linked("Cockpit fork", Tracker, "AC")],
            Tracker,
            "AC"));

    [Fact]
    public void For_ValuesDifferingOnlyInCase_AreTheSameLinkTwice() =>
        Assert.Null(ProjectLinkMatch.For(
            [_Linked("Cockpit", Tracker, "AC"), _Linked("Cockpit fork", Tracker, "ac")],
            Tracker,
            "AC"));

    [Fact]
    public void For_AValueInAnotherCase_StillMatches() =>
        Assert.NotNull(ProjectLinkMatch.For([_Linked("Cockpit", Tracker, "AC")], Tracker, "ac"));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void For_NoValueToMatchOn_IsNoProject(string? value) =>
        Assert.Null(ProjectLinkMatch.For([_Linked("Cockpit", Tracker, "AC")], Tracker, value));

    [Fact]
    public void For_NoProjectsAtAll_IsNoProject() =>
        Assert.Null(ProjectLinkMatch.For([], Tracker, "AC"));

    [Fact]
    public void For_AProjectLinkedToSeveralPrefixes_MatchesAnyOfThem()
    {
        var project = _Linked("EVE Workbench", Tracker, "EWB, AT, EJ, AUTH");

        Assert.Equal("EVE Workbench", ProjectLinkMatch.For([project], Tracker, "AT")?.Name);
        Assert.Equal("EVE Workbench", ProjectLinkMatch.For([project], Tracker, "EWB")?.Name);
    }

    [Fact]
    public void For_TwoProjectsWhoseListsShareOnePrefix_IsNoProject() =>
        Assert.Null(ProjectLinkMatch.For(
            [_Linked("EVE Workbench", Tracker, "EWB, AT, EJ"), _Linked("Auth service", Tracker, "AT, AUTH")],
            Tracker,
            "AT"));
}
