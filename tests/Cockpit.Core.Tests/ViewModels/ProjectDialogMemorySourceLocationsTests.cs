using NSubstitute;
using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Abstractions.Profiles;
using Cockpit.Core.Profiles;
using Cockpit.Plugins.Abstractions.Projects;

namespace Cockpit.Core.Tests.ViewModels;

/// <summary>
/// AC-502: "Choose…" on a Memory row goes from always disabled for a non-Folder source (AC-166's original rule) to
/// enabled exactly when the picked source can enumerate its own locations — <see cref="ProjectResourceRowViewModel.CanBrowse"/>
/// and the <see cref="MemorySourceChoice.ListLocationsAsync"/>/<see cref="MemorySourceChoice.SignInAsync"/> carried
/// from <see cref="ProjectMemorySourceRegistration"/> through <see cref="ProjectDialogViewModel.CreateAsync"/> is
/// what that decision reads.
/// </summary>
public class ProjectDialogMemorySourceLocationsTests
{
    private static ISessionProfileStore ProfileStore()
    {
        var store = Substitute.For<ISessionProfileStore>();
        store.LoadAsync(Arg.Any<CancellationToken>()).Returns([new SessionProfile("personal", new ClaudeConfig("~/.claude"))]);
        return store;
    }

    private static IMcpServerCatalog Catalog()
    {
        var catalog = Substitute.For<IMcpServerCatalog>();
        catalog.GetServersAsync(Arg.Any<CancellationToken>()).Returns([]);
        return catalog;
    }

    private static Task<ProjectMemorySourceLocationsResult> ListLocations(CancellationToken _) =>
        Task.FromResult(ProjectMemorySourceLocationsResult.Success([new ProjectMemorySourceLocation("cockpit", "Cockpit")]));

    [Fact]
    public async Task CreateAsync_ASourceThatCannotList_LeavesChooseDisabled_TheSameAsBeforeAC502()
    {
        var source = new ProjectMemorySourceRegistration("depot", "Depot project", "Read it there.");
        var viewModel = await ProjectDialogViewModel.CreateAsync(project: null, ProfileStore(), Catalog(), memorySources: [source]);
        viewModel.AddResourceRowCommand.Execute(null);
        var row = viewModel.ResourceRows.Single();

        row.SelectedMemorySourceChoice = viewModel.MemorySourceChoices.Single(choice => choice.Scheme == "depot");

        Assert.False(row.CanBrowse);
        Assert.Null(row.SelectedMemorySourceChoice.ListLocationsAsync);
    }

    [Fact]
    public async Task CreateAsync_ASourceThatCanList_EnablesChoose()
    {
        var source = new ProjectMemorySourceRegistration("depot", "Depot project", "Read it there.") { ListLocationsAsync = ListLocations };
        var viewModel = await ProjectDialogViewModel.CreateAsync(project: null, ProfileStore(), Catalog(), memorySources: [source]);
        viewModel.AddResourceRowCommand.Execute(null);
        var row = viewModel.ResourceRows.Single();

        row.SelectedMemorySourceChoice = viewModel.MemorySourceChoices.Single(choice => choice.Scheme == "depot");

        Assert.True(row.CanBrowse);
        Assert.NotNull(row.SelectedMemorySourceChoice.ListLocationsAsync);
    }

    [Fact]
    public async Task CreateAsync_FolderSelected_ChooseStaysEnabledRegardlessOfListing()
    {
        var source = new ProjectMemorySourceRegistration("depot", "Depot project", "Read it there.") { ListLocationsAsync = ListLocations };
        var viewModel = await ProjectDialogViewModel.CreateAsync(project: null, ProfileStore(), Catalog(), memorySources: [source]);
        viewModel.AddResourceRowCommand.Execute(null);
        var row = viewModel.ResourceRows.Single();

        row.SelectedMemorySourceChoice = viewModel.MemorySourceChoices.Single(choice => choice.Scheme is null);

        Assert.True(row.IsMemoryFolderMode);
        Assert.True(row.CanBrowse);
    }

    [Fact]
    public async Task CreateAsync_CarriesTheRegistrationsSignInDelegateThroughToTheChoice()
    {
        Task<bool> SignIn(CancellationToken _) => Task.FromResult(true);
        var source = new ProjectMemorySourceRegistration("depot", "Depot project", "Read it there.")
        {
            ListLocationsAsync = ListLocations,
            SignInAsync = SignIn,
        };

        var viewModel = await ProjectDialogViewModel.CreateAsync(project: null, ProfileStore(), Catalog(), memorySources: [source]);

        var choice = viewModel.MemorySourceChoices.Single(choice => choice.Scheme == "depot");
        Assert.NotNull(choice.SignInAsync);
        Assert.True(await choice.SignInAsync!(CancellationToken.None));
    }
}
