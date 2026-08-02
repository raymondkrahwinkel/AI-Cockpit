using Cockpit.Plugins.Abstractions.Projects;

namespace Cockpit.App.ViewModels;

/// <summary>
/// One <see cref="ISharedProjectSource"/>'s rows in the Projects workspace (AC-245) — "Shared via Depot — Work",
/// already bound and hidden projects filtered out (<see cref="ProjectsViewModel.LoadSharedProjectsAsync"/>). Carries
/// its own error rather than throwing one source's failure into every other group: a connection that is not signed
/// in shows its own row under its own heading, the rest of the workspace unaffected.
/// </summary>
public sealed record SharedProjectGroupViewModel(string SourceName, IReadOnlyList<SharedProject> Projects, string? Error)
{
    public bool HasError => Error is { Length: > 0 };

    public bool HasProjects => Projects.Count > 0;
}
