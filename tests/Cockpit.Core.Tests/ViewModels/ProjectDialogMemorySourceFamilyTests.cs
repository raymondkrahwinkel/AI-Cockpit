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

    [Fact]
    public async Task CreateAsync_AFamilyDeclaredButNoInstancesRegistered_StillOffersFolderFirst()
    {
        var viewModel = await ProjectDialogViewModel.CreateAsync(
            project: null, ProfileStore(), Catalog(), memorySourceFamilies: [DepotFamily()]);

        Assert.Equal(new[] { "Folder", "Depot" }, viewModel.MemorySourceChoices.Select(choice => choice.Label));
        Assert.Null(viewModel.MemorySourceChoices[0].Scheme);
        Assert.Null(viewModel.MemorySourceChoices[0].FamilyKey);
    }

    [Fact]
    public async Task CreateAsync_ZeroSourcesAndZeroFamilies_StillOffersExactlyFolder()
    {
        // The explicit regression case: an empty list passed for both, rather than null for either — proving the
        // "Folder is unconditional" fix does not depend on which of the two optional parameters was simply omitted.
        var viewModel = await ProjectDialogViewModel.CreateAsync(
            project: null, ProfileStore(), Catalog(), memorySources: [], memorySourceFamilies: []);

        Assert.Equal(new[] { "Folder" }, viewModel.MemorySourceChoices.Select(choice => choice.Label));
    }

    // --- Dropdown order: Folder, then families (declaration order), then ungrouped sources (registration order) --

    [Fact]
    public async Task CreateAsync_DropdownOrder_IsFolderThenFamiliesThenUngroupedSources()
    {
        var notesFamily = new ProjectMemorySourceFamily("notes-family", "Notes family");
        var ungrouped = new ProjectMemorySourceRegistration("scratch", "Scratchpad", "Read it there.");

        var viewModel = await ProjectDialogViewModel.CreateAsync(
            project: null, ProfileStore(), Catalog(),
            memorySources: [ungrouped],
            memorySourceFamilies: [DepotFamily(), notesFamily]);

        Assert.Equal(
            new[] { "Folder", "Depot", "Notes family", "Scratchpad" },
            viewModel.MemorySourceChoices.Select(choice => choice.Label));
    }

    // --- A registration under a declared family becomes an instance, not its own row ----------------------------

    [Fact]
    public async Task CreateAsync_ARegistrationUnderADeclaredFamily_BecomesAnInstanceRatherThanItsOwnRow()
    {
        var viewModel = await ProjectDialogViewModel.CreateAsync(
            project: null, ProfileStore(), Catalog(),
            memorySources: [DepotInstance("depot", "Depot (krahwinkel-it)")],
            memorySourceFamilies: [DepotFamily()]);

        // Exactly "Folder" and "Depot" at the top level — the instance itself never gets its own top-level row.
        Assert.Equal(new[] { "Folder", "Depot" }, viewModel.MemorySourceChoices.Select(choice => choice.Label));

        var instances = Assert.Single(viewModel.MemorySourceFamilyInstances);
        Assert.Equal("depot", instances.Key);
        var instance = Assert.Single(instances.Value);
        Assert.Equal("Depot (krahwinkel-it)", instance.Label);
        Assert.Equal("depot", instance.Scheme);
    }

    [Fact]
    public async Task CreateAsync_InstanceTitleLeftBlank_FallsBackToTheRegistrationsOwnTitle()
    {
        var blankTitled = new ProjectMemorySourceRegistration("depot", "Depot project", "Read it there.") { FamilyKey = "depot" };

        var viewModel = await ProjectDialogViewModel.CreateAsync(
            project: null, ProfileStore(), Catalog(), memorySources: [blankTitled], memorySourceFamilies: [DepotFamily()]);

        var instance = Assert.Single(viewModel.MemorySourceFamilyInstances["depot"]);
        Assert.Equal("Depot project", instance.Label);
    }

    /// <summary>
    /// AC-499 review, defect found by the harness: <see cref="ProjectMemorySourceRegistration.InstanceTitle"/>'s own
    /// doc comment promises "blank or null falls back to Title" — a whitespace-only value is blank in every other
    /// sense this codebase uses the word, but <c>CreateAsync</c> originally tested only <c>Length: > 0</c>, which a
    /// whitespace-only string satisfies. That produced a blank-looking row in the instance dropdown instead of
    /// falling back, breaking the doc comment's own promise. Fixed to <c>string.IsNullOrWhiteSpace</c>; pinned here.
    /// </summary>
    [Fact]
    public async Task CreateAsync_InstanceTitleIsWhitespaceOnly_FallsBackToTheRegistrationsOwnTitle()
    {
        var whitespaceTitled = new ProjectMemorySourceRegistration("depot", "Depot project", "Read it there.")
        {
            FamilyKey = "depot",
            InstanceTitle = "   ",
        };

        var viewModel = await ProjectDialogViewModel.CreateAsync(
            project: null, ProfileStore(), Catalog(), memorySources: [whitespaceTitled], memorySourceFamilies: [DepotFamily()]);

        var instance = Assert.Single(viewModel.MemorySourceFamilyInstances["depot"]);
        Assert.Equal("Depot project", instance.Label);
    }

    // --- A registration whose FamilyKey names no declared family falls back to its own ungrouped row --------------

    [Fact]
    public async Task CreateAsync_ARegistrationWithAnUndeclaredFamilyKey_FallsBackToItsOwnUngroupedRow()
    {
        // No AddProjectMemorySourceFamily call ever declared "ghost" — per ProjectMemorySourceRegistration.FamilyKey's
        // own doc comment, this must simply never be grouped, exactly as if FamilyKey had been left null.
        var orphaned = new ProjectMemorySourceRegistration("depot", "Depot project", "Read it there.") { FamilyKey = "ghost" };

        var viewModel = await ProjectDialogViewModel.CreateAsync(
            project: null, ProfileStore(), Catalog(), memorySources: [orphaned]);

        Assert.Equal(new[] { "Folder", "Depot project" }, viewModel.MemorySourceChoices.Select(choice => choice.Label));
        Assert.Empty(viewModel.MemorySourceFamilyInstances);
    }

    // --- A saved "{scheme}:{value}" selects the family and its instance, and shows the bare value ------------------

    [Fact]
    public async Task CreateAsync_ASavedFamilyInstanceReference_SelectsTheFamilyAndItsInstanceAndShowsTheBareValue()
    {
        var project = Project.Create("Cockpit") with { MemoryRef = "depot:cockpit" };

        var viewModel = await ProjectDialogViewModel.CreateAsync(
            project, ProfileStore(), Catalog(),
            memorySources: [DepotInstance("depot", "Depot (krahwinkel-it)")],
            memorySourceFamilies: [DepotFamily()]);

        var row = Assert.Single(viewModel.ResourceRows);
        Assert.Equal("Depot", row.SelectedMemorySourceChoice?.Label);
        Assert.Equal("depot", row.SelectedMemorySourceChoice?.FamilyKey);
        Assert.Equal("Depot (krahwinkel-it)", row.SelectedFamilyInstance?.Label);
        Assert.Equal("cockpit", row.Reference);
        Assert.False(row.IsMemoryFolderMode);
    }

    [Fact]
    public async Task CreateAsync_ASavedReferenceMatchingASiblingInstance_SelectsThatInstanceNotTheFirstOne()
    {
        var project = Project.Create("Cockpit") with { MemoryRef = "depot.synvolution:handbook" };

        var viewModel = await ProjectDialogViewModel.CreateAsync(
            project, ProfileStore(), Catalog(),
            memorySources:
            [
                DepotInstance("depot", "Depot (krahwinkel-it)"),
                DepotInstance("depot.synvolution", "Depot (synvolution)"),
            ],
            memorySourceFamilies: [DepotFamily()]);

        var row = Assert.Single(viewModel.ResourceRows);
        Assert.Equal("Depot (synvolution)", row.SelectedFamilyInstance?.Label);
        Assert.Equal("handbook", row.Reference);
    }

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

    [Fact]
    public async Task CreateAsync_PickingAFamilyWithNoInstances_ShowsTheEmptyHintAndNoInstanceSelected()
    {
        var viewModel = await ProjectDialogViewModel.CreateAsync(
            project: null, ProfileStore(), Catalog(), memorySourceFamilies: [DepotFamily()]);
        viewModel.Name = "Cockpit";
        viewModel.AddResourceRowCommand.Execute(null);
        var row = viewModel.ResourceRows[0];

        row.SelectedMemorySourceChoice = viewModel.MemorySourceChoices.Single(choice => choice.FamilyKey == "depot");

        Assert.True(row.ShowsMemorySourceServerRow);
        Assert.False(row.HasFamilyInstances);
        Assert.Empty(row.FamilyInstanceChoices);
        Assert.Null(row.SelectedFamilyInstance);
        Assert.Equal("No Depot server configured yet", row.MemorySourceInstanceEmptyHint);
        // No scheme to fold at all — a family with nothing picked under it is Folder-shaped for saving purposes.
        Assert.Null(row.SelectedMemorySourceLeaf);
    }

    // --- ToDomain folds the scheme from the picked instance, never from the family (the family has none) ----------

    [Fact]
    public async Task ToDomain_AFamilyInstancePicked_FoldsTheInstancesSchemeNotTheFamilys()
    {
        var viewModel = await ProjectDialogViewModel.CreateAsync(
            project: null, ProfileStore(), Catalog(),
            memorySources: [DepotInstance("depot", "Depot (krahwinkel-it)")],
            memorySourceFamilies: [DepotFamily()]);
        viewModel.Name = "Cockpit";
        viewModel.AddResourceRowCommand.Execute(null);
        var row = viewModel.ResourceRows[0];
        row.SelectedMemorySourceChoice = viewModel.MemorySourceChoices.Single(choice => choice.FamilyKey == "depot");
        row.Reference = "cockpit";

        var domain = row.ToDomain();

        Assert.Equal("depot:cockpit", domain.Reference);
    }

    [Fact]
    public async Task ToDomain_AFamilyPickedWithNoInstanceChosen_SavesTheTypedTextUnprefixed()
    {
        // The family itself carries no Scheme (only its instances do) — nothing to fold, so this must behave like
        // Folder rather than invent a scheme that names nothing.
        var viewModel = await ProjectDialogViewModel.CreateAsync(
            project: null, ProfileStore(), Catalog(), memorySourceFamilies: [DepotFamily()]);
        viewModel.Name = "Cockpit";
        viewModel.AddResourceRowCommand.Execute(null);
        var row = viewModel.ResourceRows[0];
        row.SelectedMemorySourceChoice = viewModel.MemorySourceChoices.Single(choice => choice.FamilyKey == "depot");
        row.Reference = "cockpit";

        var domain = row.ToDomain();

        Assert.Equal("cockpit", domain.Reference);
    }
}
