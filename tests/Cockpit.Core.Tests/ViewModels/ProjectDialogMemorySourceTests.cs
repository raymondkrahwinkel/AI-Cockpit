using NSubstitute;
using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Abstractions.Profiles;
using Cockpit.Core.Profiles;
using Cockpit.Core.Projects;
using Cockpit.Plugins.Abstractions.Projects;

namespace Cockpit.Core.Tests.ViewModels;

/// <summary>
/// A Memory row's picker (AC-165/166): "Folder" plus one entry per contributed source. What is picked and what is
/// stored are different strings too, exactly the shape <see cref="ProjectDialogPluginFieldTests"/> already covers
/// for a plugin field — the operator sees "cockpit", the project stores "depot:cockpit" — with the same worry about
/// a plugin that is not installed: it must not lose or garble a reference just because this dialog was opened.
/// <para>
/// AC-485 moved this picker from a single dialog-wide field onto <see cref="ProjectResourceRowViewModel"/> itself —
/// <see cref="ProjectDialogViewModel.MemorySourceChoices"/> is still the one shared list of choices, but which one
/// is picked, and the reference typed beside it, now live on each row.
/// </para>
/// </summary>
public class ProjectDialogMemorySourceTests
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

    private static ProjectMemorySourceRegistration DepotSource() =>
        new("depot", "Depot project", "Read it through the Depot MCP.");

    /// <summary>
    /// The Depot plugin is not installed on this machine. Opening and saving the editor for an unrelated reason
    /// (renaming the project, say) must not lose or corrupt what it already pointed at.
    /// </summary>
    [Fact]
    public async Task CreateAsync_AnUninstalledSchemesReference_SelectsFolderAndKeepsTheRawTextUntouchedOnSave()
    {
        var project = Project.Create("Cockpit") with { MemoryRef = "depot:cockpit" };

        // No memorySources passed at all here — as if the Depot plugin were not installed.
        var viewModel = await ProjectDialogViewModel.CreateAsync(project, ProfileStore(), Catalog());

        var row = Assert.Single(viewModel.ResourceRows);
        Assert.True(row.IsMemoryFolderMode);
        Assert.Equal("depot:cockpit", row.Reference);

        Assert.Equal("depot:cockpit", viewModel.ToProject().MemoryRef);
    }

    [Fact]
    public async Task RoundTrip_ADepotReference_SurvivesLoadAndSaveUnchanged()
    {
        var project = Project.Create("Cockpit") with { MemoryRef = "depot:cockpit" };

        var viewModel = await ProjectDialogViewModel.CreateAsync(
            project, ProfileStore(), Catalog(), memorySources: [DepotSource()]);

        // Both checked so this is not merely "the string happens to match": the picker must actually have selected
        // the Depot source (not fallen back to Folder with the raw text carried through, which would print the same
        // saved string by coincidence).
        Assert.Equal("depot", viewModel.ResourceRows.Single().SelectedMemorySourceChoice?.Scheme);
        Assert.Equal("depot:cockpit", viewModel.ToProject().MemoryRef);
    }

}
