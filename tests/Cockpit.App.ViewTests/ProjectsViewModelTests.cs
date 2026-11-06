using Cockpit.App.Services;
using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Projects;
using Cockpit.Core.Projects;
using Cockpit.Infrastructure.Projects;
using Cockpit.Plugins.Abstractions.Projects;
using NSubstitute;

namespace Cockpit.App.ViewTests;

// AC-1797: catalog notifications need the same owned, pumped dispatcher as the Projects page.
[Collection("avalonia")]
public class ProjectsViewModelTests
{
    private static (ProjectsViewModel ViewModel, IProjectStore Store, ISessionDialogService Dialogs) Build(
        params Project[] saved)
    {
        var store = Substitute.For<IProjectStore>();
        store.LoadAsync(Arg.Any<CancellationToken>()).Returns(new ProjectSettings { Projects = saved });
        var dialogs = Substitute.For<ISessionDialogService>();
        return (TestProjects.ViewModel(store, dialogs), store, dialogs);
    }

    [Fact]
    public Task MarkOpened_ForAProjectRemovedMeanwhile_WritesNothing() => HeadlessAvalonia.RunAsync(async () =>
    {
        var (viewModel, store, _) = Build();
        await viewModel.LoadAsync();

        await viewModel.MarkOpenedAsync(Project.Create("Gone"), DateTimeOffset.Now);

        await store.DidNotReceive().SaveAsync(Arg.Any<ProjectSettings>(), Arg.Any<CancellationToken>());
    });

    [Fact]
    public Task RemoveProject_OnlyAfterConfirmation() => HeadlessAvalonia.RunAsync(async () =>
    {
        var project = Project.Create("Cockpit");
        var (viewModel, store, dialogs) = Build(project);
        dialogs.ShowConfirmationDialogAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>()).Returns(false);
        await viewModel.LoadAsync();
        viewModel.SelectedProject = viewModel.Projects[0];

        await viewModel.RemoveProjectCommand.ExecuteAsync(null);

        await store.DidNotReceive().SaveAsync(Arg.Any<ProjectSettings>(), Arg.Any<CancellationToken>());
    });

    [Fact]
    public Task ToggleSharingAsync_AlreadySharedProject_ConfirmsThenRemovesOnlyTheBindingRow() => HeadlessAvalonia.RunAsync(async () =>
    {
        var extraMemoryRow = new ProjectResource("~/Notes/payroll.md", ProjectResourceRole.Memory) { Label = "Personal notes" };
        var bound = Project.Create("PayrollProcessor") with
        {
            Resources = [new ProjectResource("Depot — Work:payroll-processor", ProjectResourceRole.Memory), extraMemoryRow],
        };
        var source = Substitute.For<ISharedProjectSource>();
        source.Key.Returns("Depot — Work");
        source.SourceName.Returns("Depot — Work");
        source.ListAsync(Arg.Any<CancellationToken>()).Returns(
            SharedProjectListResult.Success([new SharedProject("Depot — Work:payroll-processor", "PayrollProcessor")]));
        var registry = Substitute.For<ISharedProjectSourceRegistry>();
        registry.Sources.Returns([source]);
        var store = Substitute.For<IProjectStore>();
        store.LoadAsync(Arg.Any<CancellationToken>()).Returns(new ProjectSettings { Projects = [bound] });
        var dialogs = Substitute.For<ISessionDialogService>();
        dialogs.ShowConfirmationDialogAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>()).Returns(true);
        var catalog = TestProjects.Catalog(store, registry);
        var viewModel = TestProjects.ViewModel(catalog, dialogs);
        await viewModel.LoadAsync();
        await viewModel.SharedProjectsLoadTask;
        viewModel.SelectedProject = bound;

        Assert.Equal("Stop sharing…", viewModel.ShareToggleLabel);

        await viewModel.ToggleSharingAsync(bound);

        await store.Received(1).SaveAsync(Arg.Is<ProjectSettings>(settings =>
            !settings.Projects.Single().Resources.Any(resource => resource.Reference == "Depot — Work:payroll-processor")
            && settings.Projects.Single().Resources.Contains(extraMemoryRow)), Arg.Any<CancellationToken>());

        var external = Project.Create("Added outside the page");
        await Task.Run(() => catalog.AddNewProjectAsync(external));
        Assert.Contains(viewModel.Projects, project => project.Id == external.Id);
    });
}
