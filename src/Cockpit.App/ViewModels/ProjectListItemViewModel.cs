using Cockpit.Core.Projects;

namespace Cockpit.App.ViewModels;

public sealed class ProjectListItemViewModel
{
    public ProjectListItemViewModel(Project project, int sessionsRunning)
    {
        Project = project;
        SessionsRunning = sessionsRunning;
    }

    public ProjectListItemViewModel(SharedProjectOffer sharedProject)
    {
        SharedProject = sharedProject;
    }

    public Project? Project { get; }

    public SharedProjectOffer? SharedProject { get; }

    public string Id => Project?.Id ?? SharedProject!.Id;

    public string Name => Project?.Name ?? SharedProject!.Name;

    public string? Description => Project?.Description ?? SharedProject!.Description;

    public string? Category => Project?.Category;

    public int SessionsRunning { get; }

    public bool IsOnThisMachine => Project is not null;
}
