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

        Assert.Equal("Cockpit", Assert.Single(settings.Normalized().Projects).Name);
    }

    [Fact]
    public void Normalized_KeepsTheFirstOfARepeatedId()
    {
        var settings = new ProjectSettings
        {
            Projects = [new Project("same", "first"), new Project("same", "second")],
        };

        Assert.Equal("first", Assert.Single(settings.Normalized().Projects).Name);
    }

    [Fact]
    public void Normalized_DropsBlankInformationRowsAndTidiesTheRest()
    {
        var settings = new ProjectSettings
        {
            Projects =
            [
                Project.Create("Cockpit") with
                {
                    AdditionalInfo =
                    [
                        new ProjectInfoField("  Repository ", " https://github.com/example/repo "),
                        new ProjectInfoField("  ", "   "),
                    ],
                },
            ],
        };

        var info = Assert.Single(settings.Normalized().Projects).AdditionalInfo;

        Assert.Single(info);
        Assert.Equal("Repository", info[0].Label);
        Assert.Equal("https://github.com/example/repo", info[0].Value);
    }

    [Fact]
    public void Normalized_ProjectWithNothingToTidy_IsTheSameInstance()
    {
        var project = Project.Create("Cockpit") with
        {
            AdditionalInfo = [new ProjectInfoField("Repository", "https://github.com/example/repo")],
        };
        var settings = ProjectSettings.Empty.WithProject(project);

        Assert.Same(settings, settings.Normalized());
    }

    [Fact]
    public void Find_UnknownOrMissingId_ReturnsNull()
    {
        var settings = ProjectSettings.Empty.WithProject(Project.Create("Cockpit"));

        Assert.Null(settings.Find("gone"));
        Assert.Null(settings.Find(null));
    }

    [Fact]
    public void WithUpdated_SwapsTheProjectCarryingThatId()
    {
        var project = Project.Create("Cockpit");
        var settings = ProjectSettings.Empty.WithProject(project);

        var renamed = settings.WithUpdated(project with { Name = "AI-Cockpit" });

        Assert.Equal("AI-Cockpit", Assert.Single(renamed.Projects).Name);
    }

    [Fact]
    public void WithoutProject_RemovesItAndLeavesTheRest()
    {
        var kept = Project.Create("Cockpit");
        var removed = Project.Create("Depot");
        var settings = ProjectSettings.Empty.WithProject(kept).WithProject(removed);

        Assert.Equal(kept.Id, Assert.Single(settings.WithoutProject(removed.Id).Projects).Id);
    }
}
