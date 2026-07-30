using NSubstitute;
using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Abstractions.Profiles;
using Cockpit.Core.Profiles;
using Cockpit.Plugins.Abstractions.Projects;

namespace Cockpit.Core.Tests.ViewModels;

/// <summary>
/// AC-523: the project screen's Memory row keeps its family-instance dropdown ("Depot", say) in step with the
/// registry after the operator uses that row's own "Servers…" button — creating, removing or leaving a connection
/// alone in the plugin's settings screen must be reflected back onto the row the instant that call returns, not
/// only once the whole project dialog is closed and reopened.
/// <para>
/// Route A (per the ticket): <see cref="ProjectDialogViewModel.ConfigureMemorySourceAsync"/> rebuilds
/// <see cref="ProjectDialogViewModel.MemorySourceFamilyInstances"/> from the same live registry
/// <see cref="ProjectDialogViewModel.CreateAsync"/> itself reads (via the <c>refreshMemorySources</c> callback), and
/// pushes it onto every row with <see cref="ProjectResourceRowViewModel.UpdateFamilyInstanceChoices"/>. This suite
/// simulates "the registry changed while the settings screen was open" with a fake registry a test's own
/// <see cref="ProjectMemorySourceFamily.ConfigureAsync"/> callback mutates directly — standing in for whatever the
/// plugin's real settings screen would have done to the real <c>IProjectMemorySourceRegistry</c>.
/// </para>
/// </summary>
public class ProjectDialogMemorySourceRefreshTests
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

    private static ProjectMemorySourceRegistration DepotInstance(string scheme, string instanceTitle) =>
        new(scheme, "Depot project", "Read it through the Depot MCP.") { FamilyKey = "depot", InstanceTitle = instanceTitle };

    /// <summary>
    /// Stands in for the real <c>IProjectMemorySourceRegistry</c> (AC-523's own doc-comment note: "reuse how
    /// CreateAsync gets at the registry, don't inject a new dependency") — a plain mutable holder a test's own
    /// <see cref="ProjectMemorySourceFamily.ConfigureAsync"/> callback can add to or remove from, exactly the shape
    /// a plugin's settings screen changes the real registry while it is open.
    /// </summary>
    private sealed class FakeRegistry
    {
        public List<ProjectMemorySourceRegistration> Sources { get; } = [];

        public List<ProjectMemorySourceFamily> Families { get; } = [];

        public (IReadOnlyList<ProjectMemorySourceRegistration> Sources, IReadOnlyList<ProjectMemorySourceFamily> Families) Snapshot() =>
            ([.. Sources], [.. Families]);
    }

    // --- AC1: zero servers -> "Servers…" creates one -> it is selectable without closing the dialog ----------------

    [Fact]
    public async Task ConfigureMemorySourceAsync_ZeroInstancesBecomeOne_TheNewOneIsSelectableImmediately()
    {
        var registry = new FakeRegistry();
        Task configureAsync(CancellationToken _)
        {
            // Standing in for the operator creating a server and saving in the plugin's own settings screen.
            registry.Sources.Add(DepotInstance("depot", "Depot (krahwinkel-it)"));
            return Task.CompletedTask;
        }

        registry.Families.Add(new ProjectMemorySourceFamily("depot", "Depot")
        {
            EmptyHint = "No Depot server configured yet",
            ConfigureAsync = configureAsync,
        });

        var viewModel = await ProjectDialogViewModel.CreateAsync(
            project: null, ProfileStore(), Catalog(),
            memorySources: [], memorySourceFamilies: registry.Families,
            refreshMemorySources: registry.Snapshot);

        viewModel.AddResourceRowCommand.Execute(null);
        var row = viewModel.ResourceRows[0];
        row.SelectedMemorySourceChoice = viewModel.MemorySourceChoices.Single(choice => choice.FamilyKey == "depot");

        // Baseline: nothing registered yet, the empty state is what shows.
        Assert.False(row.HasFamilyInstances);
        Assert.Empty(row.FamilyInstanceChoices);

        await viewModel.ConfigureMemorySourceCommand.ExecuteAsync(row);

        Assert.True(row.HasFamilyInstances);
        var instance = Assert.Single(row.FamilyInstanceChoices);
        Assert.Equal("Depot (krahwinkel-it)", instance.Label);
    }

    // --- AC2: a server already existed and was picked; a second is added; the first stays picked --------------------

    [Fact]
    public async Task ConfigureMemorySourceAsync_ASecondInstanceIsAdded_TheFirstStaysSelectedAndTheSecondAppears()
    {
        var registry = new FakeRegistry();
        registry.Sources.Add(DepotInstance("depot", "Depot (krahwinkel-it)"));

        Task configureAsync(CancellationToken _)
        {
            registry.Sources.Add(DepotInstance("depot.synvolution", "Depot (synvolution)"));
            return Task.CompletedTask;
        }

        registry.Families.Add(new ProjectMemorySourceFamily("depot", "Depot") { ConfigureAsync = configureAsync });

        var viewModel = await ProjectDialogViewModel.CreateAsync(
            project: null, ProfileStore(), Catalog(),
            memorySources: registry.Sources, memorySourceFamilies: registry.Families,
            refreshMemorySources: registry.Snapshot);

        viewModel.AddResourceRowCommand.Execute(null);
        var row = viewModel.ResourceRows[0];
        row.SelectedMemorySourceChoice = viewModel.MemorySourceChoices.Single(choice => choice.FamilyKey == "depot");
        var originalInstance = Assert.Single(row.FamilyInstanceChoices);
        row.SelectedFamilyInstance = originalInstance;

        await viewModel.ConfigureMemorySourceCommand.ExecuteAsync(row);

        Assert.Equal(2, row.FamilyInstanceChoices.Count);
        Assert.Contains(row.FamilyInstanceChoices, choice => choice.Scheme == "depot.synvolution");
        // The pre-existing selection is not lost by the rebuild (AC-523 criterion 2) — it may be a fresh
        // MemorySourceChoice instance, but it names the same scheme the operator had picked before the rebuild.
        Assert.NotNull(row.SelectedFamilyInstance);
        Assert.Equal("depot", row.SelectedFamilyInstance!.Scheme);
        Assert.Equal("Depot (krahwinkel-it)", row.SelectedFamilyInstance!.Label);
    }

    // --- AC3: the selected server is removed elsewhere while the settings screen was open -----------------------------

    [Fact]
    public async Task ConfigureMemorySourceAsync_TheSelectedInstanceIsRemovedElsewhere_SelectionVisiblyFallsBackToNone()
    {
        var registry = new FakeRegistry();
        registry.Sources.Add(DepotInstance("depot", "Depot (krahwinkel-it)"));
        registry.Sources.Add(DepotInstance("depot.synvolution", "Depot (synvolution)"));

        Task configureAsync(CancellationToken _)
        {
            // Standing in for the operator deleting this exact connection in the settings screen.
            registry.Sources.RemoveAll(source => source.Scheme == "depot.synvolution");
            return Task.CompletedTask;
        }

        registry.Families.Add(new ProjectMemorySourceFamily("depot", "Depot") { ConfigureAsync = configureAsync });

        var viewModel = await ProjectDialogViewModel.CreateAsync(
            project: null, ProfileStore(), Catalog(),
            memorySources: registry.Sources, memorySourceFamilies: registry.Families,
            refreshMemorySources: registry.Snapshot);

        viewModel.AddResourceRowCommand.Execute(null);
        var row = viewModel.ResourceRows[0];
        row.SelectedMemorySourceChoice = viewModel.MemorySourceChoices.Single(choice => choice.FamilyKey == "depot");
        row.SelectedFamilyInstance = row.FamilyInstanceChoices.Single(choice => choice.Scheme == "depot.synvolution");

        await viewModel.ConfigureMemorySourceCommand.ExecuteAsync(row);

        // The removed connection is gone from the offered choices...
        Assert.DoesNotContain(row.FamilyInstanceChoices, choice => choice.Scheme == "depot.synvolution");
        Assert.Single(row.FamilyInstanceChoices);
        // ...and the row does not keep silently pointing at it: the selection visibly falls back to none rather
        // than pretending nothing changed (AC-523 criterion 3).
        Assert.Null(row.SelectedFamilyInstance);
    }

    // --- AC4: the last instance disappears -> the empty-state hint returns instead of a blank dropdown ---------------

    [Fact]
    public async Task ConfigureMemorySourceAsync_TheLastInstanceIsRemoved_TheEmptyHintReturnsInsteadOfABlankDropdown()
    {
        var registry = new FakeRegistry();
        registry.Sources.Add(DepotInstance("depot", "Depot (krahwinkel-it)"));

        Task configureAsync(CancellationToken _)
        {
            registry.Sources.Clear();
            return Task.CompletedTask;
        }

        registry.Families.Add(new ProjectMemorySourceFamily("depot", "Depot")
        {
            EmptyHint = "No Depot server configured yet",
            ConfigureAsync = configureAsync,
        });

        var viewModel = await ProjectDialogViewModel.CreateAsync(
            project: null, ProfileStore(), Catalog(),
            memorySources: registry.Sources, memorySourceFamilies: registry.Families,
            refreshMemorySources: registry.Snapshot);

        viewModel.AddResourceRowCommand.Execute(null);
        var row = viewModel.ResourceRows[0];
        row.SelectedMemorySourceChoice = viewModel.MemorySourceChoices.Single(choice => choice.FamilyKey == "depot");
        row.SelectedFamilyInstance = Assert.Single(row.FamilyInstanceChoices);
        Assert.True(row.HasFamilyInstances);

        await viewModel.ConfigureMemorySourceCommand.ExecuteAsync(row);

        Assert.False(row.HasFamilyInstances);
        Assert.Empty(row.FamilyInstanceChoices);
        Assert.Null(row.SelectedFamilyInstance);
        Assert.Equal("No Depot server configured yet", row.MemorySourceInstanceEmptyHint);
    }

    // --- Every row sharing the family sees the same rebuild, not only the row the button was clicked on --------------

    [Fact]
    public async Task ConfigureMemorySourceAsync_AnotherRowPickedTheSameFamily_ThatRowIsRefreshedToo()
    {
        var registry = new FakeRegistry();
        Task configureAsync(CancellationToken _)
        {
            registry.Sources.Add(DepotInstance("depot", "Depot (krahwinkel-it)"));
            return Task.CompletedTask;
        }

        registry.Families.Add(new ProjectMemorySourceFamily("depot", "Depot") { ConfigureAsync = configureAsync });

        var viewModel = await ProjectDialogViewModel.CreateAsync(
            project: null, ProfileStore(), Catalog(),
            memorySources: [], memorySourceFamilies: registry.Families,
            refreshMemorySources: registry.Snapshot);

        viewModel.AddResourceRowCommand.Execute(null);
        viewModel.AddResourceRowCommand.Execute(null);
        var clickedRow = viewModel.ResourceRows[0];
        var otherRow = viewModel.ResourceRows[1];
        clickedRow.SelectedMemorySourceChoice = viewModel.MemorySourceChoices.Single(choice => choice.FamilyKey == "depot");
        otherRow.SelectedMemorySourceChoice = viewModel.MemorySourceChoices.Single(choice => choice.FamilyKey == "depot");

        await viewModel.ConfigureMemorySourceCommand.ExecuteAsync(clickedRow);

        Assert.True(otherRow.HasFamilyInstances);
        Assert.Single(otherRow.FamilyInstanceChoices);
    }
}
