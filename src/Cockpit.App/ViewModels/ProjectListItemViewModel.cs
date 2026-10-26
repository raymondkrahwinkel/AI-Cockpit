using Cockpit.Core.Projects;

namespace Cockpit.App.ViewModels;

public sealed class ProjectListItemViewModel
{
    public ProjectListItemViewModel(Project project, int sessionsRunning)
    {
        Project = project;
        Id = project.Id;
        Name = project.Name;
        Description = project.Description;
        SessionsRunning = sessionsRunning;
    }

    public ProjectListItemViewModel(SharedProjectOffer sharedProject)
    {
        SharedProject = sharedProject;
        Id = sharedProject.Id;
        Name = sharedProject.Name;
        Description = sharedProject.Description;
    }

    public Project? Project { get; }

    public SharedProjectOffer? SharedProject { get; }

    public string Id { get; }

    public string Name { get; }

    public string? Description { get; }

    public string? Category => Project?.Category;

    public int SessionsRunning { get; }

    public bool IsOnThisMachine => Project is not null;
}
