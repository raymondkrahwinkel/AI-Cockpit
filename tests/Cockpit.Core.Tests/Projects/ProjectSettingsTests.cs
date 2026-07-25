using FluentAssertions;
using Cockpit.Core.Projects;

namespace Cockpit.Core.Tests.Projects;

/// <summary>The project list's own rules: what survives a load, and how the manager's add/edit/remove behave.</summary>
public class ProjectSettingsTests
{
    [Fact]
    public void Normalized_DropsEntriesWithoutAnIdOrAName()
    {
        var settings = new ProjectSettings
        {
            Projects =
            [
                Project.Create("Cockpit"),
                new Project(string.Empty, "no id"),
                new Project("no-name", "  "),
            ],
        };

        settings.Normalized().Projects.Should().ContainSingle().Which.Name.Should().Be("Cockpit");
    }

    [Fact]
    public void Normalized_KeepsTheFirstOfARepeatedId()
    {
        var settings = new ProjectSettings
        {
            Projects = [new Project("same", "first"), new Project("same", "second")],
        };

        settings.Normalized().Projects.Should().ContainSingle().Which.Name.Should().Be("first");
    }

    [Fact]
    public void Find_UnknownOrMissingId_ReturnsNull()
    {
        var settings = ProjectSettings.Empty.WithProject(Project.Create("Cockpit"));

        settings.Find("gone").Should().BeNull("a session can outlive the project it was started under");
        settings.Find(null).Should().BeNull();
    }

    [Fact]
    public void WithUpdated_SwapsTheProjectCarryingThatId()
    {
        var project = Project.Create("Cockpit");
        var settings = ProjectSettings.Empty.WithProject(project);

        var renamed = settings.WithUpdated(project with { Name = "AI-Cockpit" });

        renamed.Projects.Should().ContainSingle().Which.Name.Should().Be("AI-Cockpit");
    }

    [Fact]
    public void WithoutProject_RemovesItAndLeavesTheRest()
    {
        var kept = Project.Create("Cockpit");
        var removed = Project.Create("Depot");
        var settings = ProjectSettings.Empty.WithProject(kept).WithProject(removed);

        settings.WithoutProject(removed.Id).Projects.Should().ContainSingle().Which.Id.Should().Be(kept.Id);
    }
}
