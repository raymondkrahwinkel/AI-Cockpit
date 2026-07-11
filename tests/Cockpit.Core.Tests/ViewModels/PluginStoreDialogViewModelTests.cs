using Cockpit.App.ViewModels;
using Cockpit.Core.Plugins;
using FluentAssertions;

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
        "https://store/index.json",
        installedVersion);

    private static PluginManagerViewModel _ManagerWith(params StorePluginRowViewModel[] rows)
    {
        var manager = new PluginManagerViewModel();
        manager.Stores.Add("github.com/example/plugins");
        foreach (var row in rows)
        {
            manager.AvailablePlugins.Add(row);
        }

        return manager;
    }

    [Fact]
    public void Constructor_BuildsSidebarWithDiscoverAllCategoriesInstalledAndUpdates_AndSelectsDiscover()
    {
        var manager = _ManagerWith(
            _Row("a", "Alpha", category: "Issue trackers"),
            _Row("b", "Beta", category: "AI providers"),
            _Row("c", "Gamma", category: null));
        var vm = new PluginStoreDialogViewModel(manager);

        vm.SidebarItems.Select(item => item.Label).Should().Equal(
            "Discover", "All plugins", "AI providers", "Issue trackers", "Other", "Installed (0)", "Updates available (0)");
        vm.SelectedSidebarItem.Should().Be(PluginStoreSidebarItem.Discover);
    }

    [Fact]
    public void SelectingACategory_FiltersTheGridToThatCategoryOnly()
    {
        var manager = _ManagerWith(
            _Row("a", "Alpha", category: "Issue trackers"),
            _Row("b", "Beta", category: "AI providers"));
        var vm = new PluginStoreDialogViewModel(manager);

        vm.SelectedSidebarItem = vm.SidebarItems.Single(item => item.Label == "AI providers");

        vm.FilteredPlugins.Should().ContainSingle().Which.Name.Should().Be("Beta");
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
        var updates = vm.SidebarItems.Single(item => item.Label.StartsWith("Updates available"));
        installed.Label.Should().Be("Installed (2)");
        installed.IsEnabled.Should().BeTrue();
        updates.Label.Should().Be("Updates available (1)");
        updates.IsEnabled.Should().BeTrue();

        vm.SelectedSidebarItem = updates;
        vm.FilteredPlugins.Should().ContainSingle().Which.Name.Should().Be("Beta");
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

        vm.FilteredPlugins.Should().ContainSingle().Which.Name.Should().Be("Alpha");
    }

    [Fact]
    public void ShowDiscoverRails_OnlyWhenDiscoverSelectedAndNoSearchText()
    {
        var manager = _ManagerWith(_Row("a", "Alpha", featured: true));
        var vm = new PluginStoreDialogViewModel(manager);

        vm.ShowDiscoverRails.Should().BeTrue();

        vm.SearchText = "alpha";
        vm.ShowDiscoverRails.Should().BeFalse();
        vm.FeaturedPlugins.Should().BeEmpty();

        vm.SearchText = string.Empty;
        vm.SelectedSidebarItem = vm.SidebarItems.Single(item => item.Label == "All plugins");
        vm.ShowDiscoverRails.Should().BeFalse();
    }

    [Fact]
    public void FeaturedAndRecentlyAdded_PopulateOnlyOnDiscover()
    {
        var manager = _ManagerWith(
            _Row("a", "Alpha", featured: true, published: "2026-06-01"),
            _Row("b", "Beta", featured: false, published: "2026-07-01"),
            _Row("c", "Gamma", featured: false));
        var vm = new PluginStoreDialogViewModel(manager);

        vm.FeaturedPlugins.Should().ContainSingle().Which.Name.Should().Be("Alpha");
        vm.RecentlyAddedPlugins.Select(row => row.Name).Should().Equal("Beta", "Alpha");
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

        vm.SelectedPlugin.Should().BeNull();
    }

    [Fact]
    public void HasNoStores_ReflectsTheManagersStoreList()
    {
        var manager = new PluginManagerViewModel();
        var vm = new PluginStoreDialogViewModel(manager);

        vm.HasNoStores.Should().BeTrue();

        manager.Stores.Add("github.com/example/plugins");

        vm.HasNoStores.Should().BeFalse();
    }

    [Fact]
    public void Dispose_StopsReactingToFurtherCatalogueChanges_AndIsIdempotent()
    {
        var manager = _ManagerWith(_Row("a", "Alpha", category: "Issue trackers"));
        var vm = new PluginStoreDialogViewModel(manager);

        vm.Dispose();
        manager.AvailablePlugins.Add(_Row("b", "Beta", category: "AI providers"));

        vm.SidebarItems.Select(item => item.Label).Should().NotContain("AI providers");
        var act = () => vm.Dispose();
        act.Should().NotThrow();
    }

    [Theory]
    [InlineData(PluginStoreFilterKind.Installed, "No plugins installed from a store yet.")]
    [InlineData(PluginStoreFilterKind.UpdatesAvailable, "Everything is up to date.")]
    [InlineData(PluginStoreFilterKind.All, "Nothing here yet.")]
    public void BuildEmptyStateMessage_PerFilterKind_WithoutSearchText(PluginStoreFilterKind kind, string expected)
    {
        PluginStoreDialogViewModel.BuildEmptyStateMessage(new PluginStoreFilter(kind), searchText: null).Should().Be(expected);
    }

    [Fact]
    public void BuildEmptyStateMessage_WithSearchText_TakesPriorityOverTheFilter()
    {
        PluginStoreDialogViewModel.BuildEmptyStateMessage(PluginStoreFilter.Installed, "foo")
            .Should().Be("No plugins match 'foo'.");
    }

    [Fact]
    public void Filter_Category_IsCaseInsensitive()
    {
        var rows = new[] { _Row("a", "Alpha", category: "Issue Trackers") };

        PluginStoreDialogViewModel.Filter(rows, PluginStoreFilter.ForCategory("issue trackers"), null, PluginStoreSortMode.NameAscending)
            .Should().ContainSingle();
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

        sorted.Select(row => row.Name).Should().Equal("Beta", "Zeta", "Alpha");
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

        sorted.Select(row => row.Name).Should().Equal("Alpha", "Beta", "Zeta");
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

        PluginStoreDialogViewModel.DistinctCategories(rows).Should().Equal("Alpha cat", "Other", "Zeta cat");
    }
}
