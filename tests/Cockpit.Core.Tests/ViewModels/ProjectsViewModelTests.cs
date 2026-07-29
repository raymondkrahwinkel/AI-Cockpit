using NSubstitute;
using Cockpit.App.Services;
using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Projects;
using Cockpit.Core.Projects;

namespace Cockpit.Core.Tests.ViewModels;

/// <summary>
/// The projects manager (AC-161). It owns the persisting the editor deliberately does not, so what it writes
/// after each add/edit/remove is the whole feature's source of truth.
/// </summary>
public class ProjectsViewModelTests
{
    private static (ProjectsViewModel ViewModel, IProjectStore Store, ISessionDialogService Dialogs) Build(
        params Project[] saved)
    {
        var store = Substitute.For<IProjectStore>();
        store.LoadAsync(Arg.Any<CancellationToken>()).Returns(new ProjectSettings { Projects = saved });

        var dialogs = Substitute.For<ISessionDialogService>();
        return (new ProjectsViewModel(store, dialogs), store, dialogs);
    }

    [Fact]
    public async Task LoadAsync_PublishesTheSavedProjects()
    {
        var (viewModel, _, _) = Build(Project.Create("Cockpit"), Project.Create("Depot"));

        await viewModel.LoadAsync();

        Assert.Equal(new[] { "Cockpit", "Depot" }, viewModel.Projects.Select(project => project.Name));
        Assert.True(viewModel.HasProjects);
    }

    [Fact]
    public async Task RecentProjects_LeadWithWhatWasWorkedOnLast()
    {
        var never = Project.Create("Archive");
        var older = Project.Create("Depot") with { LastOpenedAt = DateTimeOffset.Now.AddDays(-3) };
        var newest = Project.Create("Cockpit") with { LastOpenedAt = DateTimeOffset.Now.AddMinutes(-5) };
        var (viewModel, _, _) = Build(never, older, newest);

        await viewModel.LoadAsync();

        Assert.Equal(new[] { "Cockpit", "Depot", "Archive" }, viewModel.RecentProjects.Select(project => project.Name));
        // The manager keeps the operator's own order — re-sorting it under them on every start is its own chaos.
        Assert.Equal(new[] { "Archive", "Depot", "Cockpit" }, viewModel.Projects.Select(project => project.Name));
        Assert.Equal("Cockpit", viewModel.MostRecentProject?.Name);
        Assert.Equal(2, viewModel.OpenedProjectCount);
    }

    [Fact]
    public async Task SidebarProjects_AreTheRecentFew_WithTheRestOneClickAway()
    {
        // The sidebar strip is for reaching what you are busy with; a list that grows with every project turns it
        // back into a menu, so it holds the recent handful and says where the others are.
        var saved = Enumerable.Range(1, 7)
            .Select(index => Project.Create($"Project {index}") with { LastOpenedAt = DateTimeOffset.Now.AddMinutes(-index) })
            .ToArray();
        var (viewModel, _, _) = Build(saved);

        await viewModel.LoadAsync();

        Assert.Equal(5, System.Linq.Enumerable.Count(viewModel.SidebarProjects));
        Assert.Equal(new[] { "Project 1", "Project 2", "Project 3", "Project 4", "Project 5" }, viewModel.SidebarProjects.Select(project => project.Name));
        Assert.True(viewModel.HasMoreThanSidebarShows);
    }

    [Fact]
    public async Task WithFewProjects_TheSidebarShowsThemAll_AndSaysNothingAboutMore()
    {
        var (viewModel, _, _) = Build(Project.Create("Cockpit"), Project.Create("Depot"));

        await viewModel.LoadAsync();

        Assert.Equal(2, System.Linq.Enumerable.Count(viewModel.SidebarProjects));
        Assert.False(viewModel.HasMoreThanSidebarShows);
    }

    [Fact]
    public async Task MarkOpened_PersistsWhenItWasWorkedOn()
    {
        var project = Project.Create("Cockpit");
        var (viewModel, store, _) = Build(project);
        await viewModel.LoadAsync();
        var openedAt = DateTimeOffset.Now;

        await viewModel.MarkOpenedAsync(project, openedAt);

        await store.Received(1).SaveAsync(
            Arg.Is<ProjectSettings>(settings => settings.Projects[0].LastOpenedAt == openedAt),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MarkOpened_ForAProjectRemovedMeanwhile_WritesNothing()
    {
        var (viewModel, store, _) = Build();
        await viewModel.LoadAsync();

        await viewModel.MarkOpenedAsync(Project.Create("Gone"), DateTimeOffset.Now);

        await store.DidNotReceive().SaveAsync(Arg.Any<ProjectSettings>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AddProject_WhenTheEditorReturnsOne_PersistsAndSelectsIt()
    {
        var (viewModel, store, dialogs) = Build();
        var created = Project.Create("Cockpit");
        dialogs.ShowProjectDialogAsync(null).Returns(created);
        await viewModel.LoadAsync();

        await viewModel.AddProjectCommand.ExecuteAsync(null);

        await store.Received(1).SaveAsync(
            Arg.Is<ProjectSettings>(settings => settings.Projects.Count == 1 && settings.Projects[0].Id == created.Id),
            Arg.Any<CancellationToken>());
        Assert.Equal(created.Id, viewModel.SelectedProject?.Id);
    }

    [Fact]
    public async Task AddProject_WhenTheEditorIsCancelled_WritesNothing()
    {
        var (viewModel, store, dialogs) = Build();
        dialogs.ShowProjectDialogAsync(null).Returns((Project?)null);
        await viewModel.LoadAsync();

        await viewModel.AddProjectCommand.ExecuteAsync(null);

        await store.DidNotReceive().SaveAsync(Arg.Any<ProjectSettings>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EditProject_PersistsTheEditedProjectUnderTheSameId()
    {
        var project = Project.Create("Cockpit");
        var (viewModel, store, dialogs) = Build(project);
        dialogs.ShowProjectDialogAsync(Arg.Any<Project?>()).Returns(project with { Name = "AI-Cockpit" });
        await viewModel.LoadAsync();
        viewModel.SelectedProject = viewModel.Projects[0];

        await viewModel.EditProjectCommand.ExecuteAsync(null);

        await store.Received(1).SaveAsync(
            Arg.Is<ProjectSettings>(settings => settings.Projects.Count == 1 && settings.Projects[0].Name == "AI-Cockpit"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RemoveProject_OnlyAfterConfirmation()
    {
        var project = Project.Create("Cockpit");
        var (viewModel, store, dialogs) = Build(project);
        dialogs.ShowConfirmationDialogAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>()).Returns(false);
        await viewModel.LoadAsync();
        viewModel.SelectedProject = viewModel.Projects[0];

        await viewModel.RemoveProjectCommand.ExecuteAsync(null);

        await store.DidNotReceive().SaveAsync(Arg.Any<ProjectSettings>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RemoveProject_Confirmed_PersistsTheShorterList()
    {
        var removed = Project.Create("Cockpit");
        var kept = Project.Create("Depot");
        var (viewModel, store, dialogs) = Build(removed, kept);
        dialogs.ShowConfirmationDialogAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>()).Returns(true);
        await viewModel.LoadAsync();
        viewModel.SelectedProject = viewModel.Projects[0];

        await viewModel.RemoveProjectCommand.ExecuteAsync(null);

        await store.Received(1).SaveAsync(
            Arg.Is<ProjectSettings>(settings => settings.Projects.Count == 1 && settings.Projects[0].Id == kept.Id),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EditAndRemove_AreUnavailableWithoutASelection()
    {
        var (viewModel, _, _) = Build(Project.Create("Cockpit"));
        await viewModel.LoadAsync();

        Assert.False(viewModel.EditProjectCommand.CanExecute(null));
        Assert.False(viewModel.RemoveProjectCommand.CanExecute(null));

        viewModel.SelectedProject = viewModel.Projects[0];

        Assert.True(viewModel.EditProjectCommand.CanExecute(null));
        Assert.True(viewModel.RemoveProjectCommand.CanExecute(null));
    }

    /// <summary>Reloading must not silently move the operator's selection to a different project.</summary>
    [Fact]
    public async Task LoadAsync_KeepsTheSelectionWhenTheProjectIsStillThere()
    {
        var project = Project.Create("Cockpit");
        var (viewModel, _, _) = Build(project, Project.Create("Depot"));
        await viewModel.LoadAsync();
        viewModel.SelectedProject = viewModel.Projects[1];

        await viewModel.LoadAsync();

        Assert.Equal("Depot", viewModel.SelectedProject?.Name);
    }
}
