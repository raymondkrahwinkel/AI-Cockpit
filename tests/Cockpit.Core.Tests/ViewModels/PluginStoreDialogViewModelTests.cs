using Cockpit.App.ViewModels;
using Cockpit.Core.Plugins;

namespace Cockpit.Core.Tests.ViewModels;

/// <summary>
/// The plugin store dialog's (#62) presentation logic: sidebar derivation (categories/counts), search +
/// sort + filter over the shared <see cref="PluginManagerViewModel.AvailablePlugins"/>, the Discover
/// rails, and the empty-state message — all pure projections/static helpers, since the dialog never
/// duplicates the manager's fetch/install/consent behaviour (that stays exercised by
/// <c>PluginManagerViewModel</c>'s own tests).
/// </summary>
public class PluginStoreDialogViewModelTests
{
    private static StorePluginRowViewModel _Row(
        string id,
        string name,
        string? category = null,
        bool featured = false,
        string? published = null,
        string? author = null,
        string? installedVersion = null,
        string latestVersion = "1.0.0") => new(
        new PluginStoreEntry(
            Id: id,
            Name: name,
            Description: $"{name} description",
            Author: author,
            LatestVersion: latestVersion,
            Versions: [new PluginStoreVersion(latestVersion, $"{id}/{latestVersion}.zip", 1, "1.0.0", "sha", null)],
            Category: category,
            Featured: featured,
            Published: published),
        PluginStoreConfig.Remote("https://store/index.json"),
        installedVersion);

    /// <summary>
    /// The Installed list is grouped by the category the store gives each plugin (Raymond, 2026-07-15): one flat
    /// list stopped being readable once widgets, providers, issue trackers and a workflow engine all lived in it.
    /// </summary>
    [Fact]
    public void InstalledGroups_AreTheStoresCategories_WithOtherLast()
    {
        var manager = _ManagerWith(
            _Row("clock", "Clock", category: "Widgets", installedVersion: "1.0.0"),
            _Row("youtrack", "YouTrack", category: "Issue trackers", installedVersion: "1.0.0"),
            _Row("git", "Git status", category: "Productivity", installedVersion: "1.0.0"));
        // Installed, in no store's catalogue — Raymond's own machine had exactly this: an old reference-widgets
        // left behind by the clock/system-monitor split, doing nothing and listed nowhere.
        manager.Plugins.Add(LocalPlugin("widgets", "Reference widgets"));
        var vm = new PluginStoreDialogViewModel(manager);

        Assert.Equal(
            new[] { "Issue trackers", "Productivity", "Widgets", "Other" },
            vm.InstalledGroups.Select(group => group.Header));
        Assert.True(vm.ShowInstalledGroupHeaders);
    }

    /// <summary>A plugin.json carries no category — it is a store-index field — so with no catalogue there is nothing to group by, and the flat list is the honest answer.</summary>
    [Fact]
    public void WithNoCatalogue_EverythingIsOneGroup_AndTheHeadingsStayOut()
    {
        var manager = new PluginManagerViewModel();
        manager.Plugins.Add(LocalPlugin("git", "Git status"));
        manager.Plugins.Add(LocalPlugin("clock", "Clock"));
        var vm = new PluginStoreDialogViewModel(manager);

        Assert.Equal(2, System.Linq.Enumerable.Count(Assert.Single(vm.InstalledGroups).Plugins));
        Assert.False(vm.ShowInstalledGroupHeaders, "one heading says nothing the list does not");
    }

    /// <summary>
    /// The arrows move a plugin through the left menu, which is one flat sequence — and this list is no longer
    /// shown as one. On the manager's plain ±1 they lie: ↑ on the first widget moves it past a provider, so the
    /// menu shifts while nothing visibly moves. Within the heading is the reading that survives.
    /// </summary>
    [Fact]
    public async Task MovingAPluginUp_MovesItPastItsOwnHeadingsPrevious_NotWhateverSitsAboveItInTheMenu()
    {
        var manager = _ManagerWith(
            _Row("git", "Git status", category: "Productivity", installedVersion: "1.0.0"),
            _Row("clock", "Clock", category: "Widgets", installedVersion: "1.0.0"),
            _Row("transcripts", "Transcript search", category: "Productivity", installedVersion: "1.0.0"));
        var vm = new PluginStoreDialogViewModel(manager);
        var transcripts = manager.Plugins.Single(plugin => plugin.FolderId == "transcripts");

        await vm.MoveInstalledPluginUpCommand.ExecuteAsync(transcripts);

        Assert.Equal(
            new[] { "transcripts", "git" },
            vm.InstalledGroups.Single(group => group.Header == "Productivity").Plugins.Select(plugin => plugin.FolderId));
    }

    [Fact]
    public async Task APluginAlreadyFirstUnderItsHeading_DoesNotMove()
    {
        var manager = _ManagerWith(
            _Row("clock", "Clock", category: "Widgets", installedVersion: "1.0.0"),
            _Row("git", "Git status", category: "Productivity", installedVersion: "1.0.0"));
        var vm = new PluginStoreDialogViewModel(manager);
        var git = manager.Plugins.Single(plugin => plugin.FolderId == "git");

        await vm.MoveInstalledPluginUpCommand.ExecuteAsync(git);

        Assert.Equal(
            new[] { "clock", "git" },
            manager.Plugins.Select(plugin => plugin.FolderId));
    }

    /// <summary>
    /// A manager holding a catalogue — and, for every row the catalogue calls installed, the local plugin that
    /// makes it so. Those are one fact in the real app: a row reports an installed version because the folder is
    /// on disk. Filling only the catalogue built a state that cannot happen — the Installed pane empty while its
    /// heading counted two — and a test written against it agreed with whatever the code did.
    /// </summary>
    private static PluginManagerViewModel _ManagerWith(params StorePluginRowViewModel[] rows)
    {
        var manager = new PluginManagerViewModel();
        manager.Stores.Add(PluginStoreConfig.Remote("github.com/example/plugins"));
        foreach (var row in rows)
        {
            manager.AvailablePlugins.Add(row);
            if (row.IsInstalled)
            {
                manager.Plugins.Add(LocalPlugin(row.Id, row.Name));
            }
        }

        return manager;
    }

    /// <summary>The locally discovered half of an installed plugin — what the Installed pane lists and its heading counts.</summary>
    private static PluginRowViewModel LocalPlugin(string id, string name) =>
        new(new DiscoveredPlugin(
            FolderPath: $"/plugins/{id}",
            FolderId: id,
            Manifest: new PluginManifest(id, name, "1.0.0", $"{name}.dll", 1, null, null, null, null),
            Sha256: "sha",
            Decision: PluginLoadDecision.Load));

    [Fact]
    public void FillingTheCatalogue_RaisesTheUpdateAllGate_SoTheButtonAppearsWhenTheStoreLoads()
    {
        // The values themselves are computed, so reading them is never wrong — the bug was that nothing told
        // the binding to re-read them. Browsing the stores rebuilds AvailablePlugins, and the change used to be
        // announced only from the install/update paths, leaving "Update all" hidden right after the store
        // loaded: exactly when the sidebar already said "Available updates (n)". So assert the notification.
        var manager = new PluginManagerViewModel();
        var raised = new List<string?>();
        manager.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        manager.AvailablePlugins.Add(_Row("a", "Alpha", installedVersion: "1.0.0", latestVersion: "2.0.0"));

        Assert.Contains(nameof(PluginManagerViewModel.HasAvailableUpdates), raised);
        Assert.Contains(nameof(PluginManagerViewModel.AvailableUpdateCount), raised);
        Assert.True(manager.HasAvailableUpdates);
        Assert.Equal(1, manager.AvailableUpdateCount);
    }

    [Fact]
    public void ClearingTheCatalogue_RaisesTheUpdateAllGate_SoTheButtonHidesAgain()
    {
        var manager = _ManagerWith(_Row("a", "Alpha", installedVersion: "1.0.0", latestVersion: "2.0.0"));
        var raised = new List<string?>();
        manager.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        manager.AvailablePlugins.Clear();

        Assert.Contains(nameof(PluginManagerViewModel.HasAvailableUpdates), raised);
        Assert.False(manager.HasAvailableUpdates);
    }

    [Fact]
    public void Constructor_BuildsSidebarWithDiscoverAllCategoriesInstalledAndUpdates_AndSelectsDiscover()
    {
        var manager = _ManagerWith(
            _Row("a", "Alpha", category: "Issue trackers"),
            _Row("b", "Beta", category: "AI providers"),
            _Row("c", "Gamma", category: null));
        var vm = new PluginStoreDialogViewModel(manager);

        Assert.Equal(
            new[] { "Discover", "All plugins", "AI providers", "Issue trackers", "Other", "Workflow templates (0)", "Installed (0)", "Available updates (0)" },
            vm.SidebarItems.Select(item => item.Label));
        Assert.Equal(PluginStoreSidebarItem.Discover, vm.SelectedSidebarItem);
    }

    [Fact]
    public void Constructor_WithInitialFilter_PreselectsThatSidebarItem()
    {
        var manager = _ManagerWith(
            _Row("a", "Alpha", installedVersion: "1.0.0", latestVersion: "1.0.0"),
            _Row("b", "Beta", installedVersion: "1.0.0", latestVersion: "2.0.0"));

        var vm = new PluginStoreDialogViewModel(manager, PluginStoreFilter.UpdatesAvailable);

        Assert.NotNull(vm.SelectedSidebarItem);
        Assert.Equal(PluginStoreFilter.UpdatesAvailable, vm.SelectedSidebarItem!.Filter);
        Assert.Equal("Beta", Assert.Single(vm.FilteredPlugins).Name);
    }

    [Fact]
    public void Constructor_WithNoInitialFilter_DefaultsToDiscover()
    {
        var manager = _ManagerWith(_Row("a", "Alpha"));

        var vm = new PluginStoreDialogViewModel(manager);

        Assert.Equal(PluginStoreSidebarItem.Discover, vm.SelectedSidebarItem);
    }

    [Fact]
    public void SelectingACategory_FiltersTheGridToThatCategoryOnly()
    {
        var manager = _ManagerWith(
            _Row("a", "Alpha", category: "Issue trackers"),
            _Row("b", "Beta", category: "AI providers"));
        var vm = new PluginStoreDialogViewModel(manager);

        vm.SelectedSidebarItem = vm.SidebarItems.Single(item => item.Label == "AI providers");

        Assert.Equal("Beta", Assert.Single(vm.FilteredPlugins).Name);
    }

    [Fact]
    public void InstalledAndUpdatesAvailable_ReflectManagerState_AndAreDisabledWhenEmpty()
    {
        var manager = _ManagerWith(
            _Row("a", "Alpha", installedVersion: "1.0.0", latestVersion: "1.0.0"),
            _Row("b", "Beta", installedVersion: "1.0.0", latestVersion: "2.0.0"),
            _Row("c", "Gamma"));
        var vm = new PluginStoreDialogViewModel(manager);

        var installed = vm.SidebarItems.Single(item => item.Label.StartsWith("Installed"));
        var updates = vm.SidebarItems.Single(item => item.Label.StartsWith("Available updates"));
        Assert.Equal("Installed (2)", installed.Label);
        Assert.True(installed.IsEnabled);
        Assert.Equal("Available updates (1)", updates.Label);
        Assert.True(updates.IsEnabled);

        vm.SelectedSidebarItem = updates;
        Assert.Equal("Beta", Assert.Single(vm.FilteredPlugins).Name);
    }

    [Fact]
    public void SearchText_FiltersWithinTheCurrentSidebarScope_CaseInsensitiveAcrossNameDescriptionAuthor()
    {
        var manager = _ManagerWith(
            _Row("a", "Alpha", category: "Issue trackers", author: "Cockpit"),
            _Row("b", "Beta", category: "Issue trackers", author: "SomeoneElse"));
        var vm = new PluginStoreDialogViewModel(manager);
        vm.SelectedSidebarItem = vm.SidebarItems.Single(item => item.Label == "Issue trackers");

        vm.SearchText = "cockpit";

        Assert.Equal("Alpha", Assert.Single(vm.FilteredPlugins).Name);
    }

    [Fact]
    public void ShowDiscoverRails_OnlyWhenDiscoverSelectedAndNoSearchText()
    {
        var manager = _ManagerWith(_Row("a", "Alpha", featured: true));
        var vm = new PluginStoreDialogViewModel(manager);

        Assert.True(vm.ShowDiscoverRails);

        vm.SearchText = "alpha";
        Assert.False(vm.ShowDiscoverRails);
        Assert.Empty(vm.FeaturedPlugins);

        vm.SearchText = string.Empty;
        vm.SelectedSidebarItem = vm.SidebarItems.Single(item => item.Label == "All plugins");
        Assert.False(vm.ShowDiscoverRails);
    }

    [Fact]
    public void FeaturedAndRecentlyAdded_PopulateOnlyOnDiscover()
    {
        var manager = _ManagerWith(
            _Row("a", "Alpha", featured: true, published: "2026-06-01"),
            _Row("b", "Beta", featured: false, published: "2026-07-01"),
            _Row("c", "Gamma", featured: false));
        var vm = new PluginStoreDialogViewModel(manager);

        Assert.Equal("Alpha", Assert.Single(vm.FeaturedPlugins).Name);
        Assert.Equal(new[] { "Beta", "Alpha" }, vm.RecentlyAddedPlugins.Select(row => row.Name));
    }

    [Fact]
    public void SelectedPlugin_ClearsWhenItFallsOutOfTheFilteredSet()
    {
        var manager = _ManagerWith(
            _Row("a", "Alpha", category: "Issue trackers"),
            _Row("b", "Beta", category: "AI providers"));
        var vm = new PluginStoreDialogViewModel(manager);
        var beta = manager.AvailablePlugins.Single(row => row.Name == "Beta");
        vm.SelectedPlugin = beta;

        vm.SelectedSidebarItem = vm.SidebarItems.Single(item => item.Label == "Issue trackers");

        Assert.Null(vm.SelectedPlugin);
    }

    [Fact]
    public void HasNoStores_ReflectsTheManagersStoreList()
    {
        var manager = new PluginManagerViewModel();
        var vm = new PluginStoreDialogViewModel(manager);

        Assert.True(vm.HasNoStores);

        manager.Stores.Add(PluginStoreConfig.Remote("github.com/example/plugins"));

        Assert.False(vm.HasNoStores);
    }

    [Fact]
    public void Dispose_StopsReactingToFurtherCatalogueChanges_AndIsIdempotent()
    {
        var manager = _ManagerWith(_Row("a", "Alpha", category: "Issue trackers"));
        var vm = new PluginStoreDialogViewModel(manager);

        vm.Dispose();
        manager.AvailablePlugins.Add(_Row("b", "Beta", category: "AI providers"));

        Assert.DoesNotContain("AI providers", vm.SidebarItems.Select(item => item.Label));
        var act = () => vm.Dispose();
        act();
    }

    [Theory]
    [InlineData(PluginStoreFilterKind.Installed, "No plugins installed from a store yet.")]
    [InlineData(PluginStoreFilterKind.UpdatesAvailable, "Everything is up to date.")]
    [InlineData(PluginStoreFilterKind.All, "Nothing here yet.")]
    public void BuildEmptyStateMessage_PerFilterKind_WithoutSearchText(PluginStoreFilterKind kind, string expected)
    {
        Assert.Equal(expected, PluginStoreDialogViewModel.BuildEmptyStateMessage(new PluginStoreFilter(kind), searchText: null));
    }

    [Fact]
    public void BuildEmptyStateMessage_WithSearchText_TakesPriorityOverTheFilter()
    {
        Assert.Equal("No plugins match 'foo'.", PluginStoreDialogViewModel.BuildEmptyStateMessage(PluginStoreFilter.Installed, "foo"));
    }

    [Fact]
    public void Filter_Category_IsCaseInsensitive()
    {
        var rows = new[] { _Row("a", "Alpha", category: "Issue Trackers") };

        Assert.Single(PluginStoreDialogViewModel.Filter(rows, PluginStoreFilter.ForCategory("issue trackers"), null, PluginStoreSortMode.NameAscending));
    }

    [Fact]
    public void Sort_RecentlyUpdated_NewestFirst_UndatedEntriesLast()
    {
        var rows = new[]
        {
            _Row("a", "Zeta", published: "2026-01-01"),
            _Row("b", "Alpha", published: null),
            _Row("c", "Beta", published: "2026-06-01"),
        };

        var sorted = PluginStoreDialogViewModel.Sort(rows, PluginStoreSortMode.RecentlyUpdated);

        Assert.Equal(new[] { "Beta", "Zeta", "Alpha" }, sorted.Select(row => row.Name));
    }

    [Fact]
    public void Sort_Author_OrdersByAuthorThenName()
    {
        var rows = new[]
        {
            _Row("a", "Zeta", author: "Zed"),
            _Row("b", "Alpha", author: "Ann"),
            _Row("c", "Beta", author: "Ann"),
        };

        var sorted = PluginStoreDialogViewModel.Sort(rows, PluginStoreSortMode.Author);

        Assert.Equal(new[] { "Alpha", "Beta", "Zeta" }, sorted.Select(row => row.Name));
    }

    [Fact]
    public void DistinctCategories_UsesOtherFallback_AndIsSortedAlphabetically()
    {
        var rows = new[]
        {
            _Row("a", "Alpha", category: "Zeta cat"),
            _Row("b", "Beta", category: null),
            _Row("c", "Gamma", category: "Alpha cat"),
        };

        Assert.Equal(new[] { "Alpha cat", "Other", "Zeta cat" }, PluginStoreDialogViewModel.DistinctCategories(rows));
    }
}
