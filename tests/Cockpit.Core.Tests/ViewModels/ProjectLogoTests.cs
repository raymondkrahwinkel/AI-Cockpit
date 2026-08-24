using NSubstitute;
using Cockpit.App.Services;
using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Projects;
using Cockpit.Core.Projects;

namespace Cockpit.Core.Tests.ViewModels;

/// <summary>
/// A project's logo (AC-162): the operator points at a file or a link, and the cockpit keeps a copy of its own so
/// the card still has its picture when the original moves. The manager owns that copy, the way it owns the saving.
/// </summary>
public class ProjectLogoTests
{
    private static (ProjectsViewModel ViewModel, IProjectStore Store, ISessionDialogService Dialogs, IProjectLogoStore Logos) Build(
        params Project[] saved)
    {
        var store = Substitute.For<IProjectStore>();
        store.LoadAsync(Arg.Any<CancellationToken>()).Returns(new ProjectSettings { Projects = saved });

        var dialogs = Substitute.For<ISessionDialogService>();
        var logos = Substitute.For<IProjectLogoStore>();
        return (new ProjectsViewModel(store, dialogs, logos), store, dialogs, logos);
    }

    [Fact]
    public async Task APickedFile_IsStoredAsACopy_AndTheProjectKeepsThatPath()
    {
        var (viewModel, store, dialogs, logos) = Build();
        var created = Project.Create("Invoices") with { LogoPath = "/home/raymond/Pictures/invoices.png" };
        dialogs.ShowProjectDialogAsync(null).Returns(created);
        logos.SaveAsync(created.Id, "/home/raymond/Pictures/invoices.png").Returns("/cockpit/project-logos/x.png");
        await viewModel.LoadAsync();

        await viewModel.AddProjectCommand.ExecuteAsync(null);

        await store.Received(1).SaveAsync(
            Arg.Is<ProjectSettings>(settings => settings.Projects[0].LogoPath == "/cockpit/project-logos/x.png"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ALogoLeftAlone_IsNotStoredAgain()
    {
        var project = Project.Create("Invoices") with { LogoPath = "/cockpit/project-logos/x.png" };
        var (viewModel, _, dialogs, logos) = Build(project);
        logos.IsStoredCopy("/cockpit/project-logos/x.png").Returns(true);
        dialogs.ShowProjectDialogAsync(Arg.Any<Project?>()).Returns(project with { Name = "Invoices 2026" });
        await viewModel.LoadAsync();
        viewModel.SelectedProject = viewModel.Projects[0];

        await viewModel.EditProjectCommand.ExecuteAsync(null);

        // Re-storing the copy would read the very file it is about to overwrite.
        await logos.DidNotReceive().SaveAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ClearingTheField_RemovesTheStoredCopy()
    {
        var project = Project.Create("Invoices") with { LogoPath = "/cockpit/project-logos/x.png" };
        var (viewModel, store, dialogs, logos) = Build(project);
        dialogs.ShowProjectDialogAsync(Arg.Any<Project?>()).Returns(project with { LogoPath = null });
        await viewModel.LoadAsync();
        viewModel.SelectedProject = viewModel.Projects[0];

        await viewModel.EditProjectCommand.ExecuteAsync(null);

        logos.Received(1).Remove(project.Id);
        await store.Received(1).SaveAsync(
            Arg.Is<ProjectSettings>(settings => settings.Projects[0].LogoPath == null),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ASourceThatCannotBeRead_CostsThePictureAndNotTheProject()
    {
        var (viewModel, store, dialogs, logos) = Build();
        var created = Project.Create("Invoices") with { LogoPath = "https://example.invalid/logo.png" };
        dialogs.ShowProjectDialogAsync(null).Returns(created);
        logos.SaveAsync(created.Id, "https://example.invalid/logo.png").Returns((string?)null);
        await viewModel.LoadAsync();

        await viewModel.AddProjectCommand.ExecuteAsync(null);

        await store.Received(1).SaveAsync(
            Arg.Is<ProjectSettings>(settings => settings.Projects.Count == 1 && settings.Projects[0].LogoPath == null),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RemovingAProject_TakesItsLogoWithIt()
    {
        var project = Project.Create("Invoices") with { LogoPath = "/cockpit/project-logos/x.png" };
        var (viewModel, _, dialogs, logos) = Build(project);
        dialogs.ShowConfirmationDialogAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>()).Returns(true);
        await viewModel.LoadAsync();
        viewModel.SelectedProject = viewModel.Projects[0];

        await viewModel.RemoveProjectCommand.ExecuteAsync(null);

        logos.Received(1).Remove(project.Id);
    }

    // AC-1054: a bind is a one-time copy — a logo added to the shared definition afterward never arrives on its
    // own. `DepotSyncWatcher` already re-downloads it on every check; this adopts it once the project has none yet.
    [Fact]
    public async Task ASyncCheckWithLogoBytes_WritesTheLogoWhenTheProjectHasNone()
    {
        var project = Project.Create("EVE Together");
        var (viewModel, store, _, logos) = Build(project);
        logos.SaveAsync(project.Id, Arg.Any<string>()).Returns("/cockpit/project-logos/eve.png");
        await viewModel.LoadAsync();

        await viewModel.SetRemoteChangeState(project.Id, hasRemoteChange: false, [1, 2, 3]);

        await store.Received(1).SaveAsync(
            Arg.Is<ProjectSettings>(settings => settings.Projects[0].LogoPath == "/cockpit/project-logos/eve.png"),
            Arg.Any<CancellationToken>());
    }

    // Criterion 2: a project that already has its own logo must never have it swapped out from under the operator
    // by a background check.
    [Fact]
    public async Task ASyncCheckWithLogoBytes_NeverOverwritesAnExistingLogo()
    {
        var project = Project.Create("EVE Together") with { LogoPath = "/cockpit/project-logos/x.png" };
        var (viewModel, store, _, logos) = Build(project);
        await viewModel.LoadAsync();

        await viewModel.SetRemoteChangeState(project.Id, hasRemoteChange: false, [1, 2, 3]);

        await logos.DidNotReceiveWithAnyArgs().SaveAsync(default!, default!, default);
        await store.DidNotReceive().SaveAsync(Arg.Any<ProjectSettings>(), Arg.Any<CancellationToken>());
    }
}
