using NSubstitute;
using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Abstractions.Profiles;
using Cockpit.Core.Profiles;
using Cockpit.Core.Projects;
using Cockpit.Plugins.Abstractions.Projects;

namespace Cockpit.Core.Tests.ViewModels;

/// <summary>
/// AC-499's second axis: a family groups however many instances (Depot connections, say) under one top-level
/// picker entry, so the operator's first choice is "what kind of place" and the second (only shown once the first
/// names a family) is "which one". Covers <see cref="ProjectDialogViewModel.CreateAsync"/>'s own building of
/// <see cref="ProjectDialogViewModel.MemorySourceChoices"/>/<see cref="ProjectDialogViewModel.MemorySourceFamilyInstances"/>
/// and how a saved <c>"{scheme}:{value}"</c> reference resolves against them — <see cref="ProjectDialogResourceRowTests"/>
/// and <see cref="ProjectDialogMemorySourceTests"/> already cover the single-axis (Folder/ungrouped-source) shape
/// this widens rather than replaces.
/// </summary>
public class ProjectDialogMemorySourceFamilyTests
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

    private static ProjectMemorySourceFamily DepotFamily() => new("depot", "Depot") { EmptyHint = "No Depot server configured yet" };

    private static ProjectMemorySourceRegistration DepotInstance(string scheme, string instanceTitle) =>
        new(scheme, "Depot project", "Read it through the Depot MCP.") { FamilyKey = "depot", InstanceTitle = instanceTitle };

    // --- Folder is unconditional (the bug Raymond reported) ---------------------------------------------------

    // --- Dropdown order: Folder, then families (declaration order), then ungrouped sources (registration order) --

    // --- A registration under a declared family becomes an instance, not its own row ----------------------------

    // --- A registration whose FamilyKey names no declared family falls back to its own ungrouped row --------------

    // --- A saved "{scheme}:{value}" selects the family and its instance, and shows the bare value ------------------

    // --- AC-485/:136-141's existing rule, unaffected by families: an unregistered scheme is left completely alone --

    [Fact]
    public async Task CreateAsync_ASavedReferenceForAnUnregisteredInstanceUnderADeclaredFamily_LeavesFolderSelectedAndReferenceUntouched()
    {
        // "depot.wispslate" names no instance this dialog was given, even though the "depot" family itself is
        // declared (and does have a different instance registered) — the existing :136-141 rule says this must fall
        // back to Folder with the raw text carried through, the same as a scheme no plugin registered at all.
        var project = Project.Create("Cockpit") with { MemoryRef = "depot.wispslate:cockpit" };

        var viewModel = await ProjectDialogViewModel.CreateAsync(
            project, ProfileStore(), Catalog(),
            memorySources: [DepotInstance("depot", "Depot (krahwinkel-it)")],
            memorySourceFamilies: [DepotFamily()]);

        var row = Assert.Single(viewModel.ResourceRows);
        Assert.True(row.IsMemoryFolderMode);
        Assert.Equal(viewModel.MemorySourceChoices[0], row.SelectedMemorySourceChoice);
        Assert.Null(row.SelectedFamilyInstance);
        Assert.Equal("depot.wispslate:cockpit", row.Reference);
        Assert.Equal("depot.wispslate:cockpit", viewModel.ToProject().MemoryRef);
    }

    // --- The empty state itself is reachable from the picker, per ProjectMemorySourceFamily's own doc comment ------

    // --- ToDomain folds the scheme from the picked instance, never from the family (the family has none) ----------

}
