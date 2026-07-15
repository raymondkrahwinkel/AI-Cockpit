using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Cockpit.App.ViewModels;

/// <summary>
/// The plugin store dialog (#62): a browsing/presentation layer over the existing
/// <see cref="PluginManagerViewModel"/> rather than a second catalogue or install path. It wraps the
/// same shared instance the Options→Plugins tab uses (<c>CockpitViewModel.Plugins</c>) and adds search,
/// sort, sidebar filtering (Discover/All/category/Installed/Available updates) and a
/// selected-plugin detail panel over its <see cref="PluginManagerViewModel.AvailablePlugins"/>. Every
/// install/update, the consent step and the restart banner all still go through <see cref="Manager"/>'s
/// own commands/properties unchanged — this view model never downloads or installs anything itself.
/// </summary>
public sealed partial class PluginStoreDialogViewModel : ViewModelBase, IDisposable
{
    private readonly PluginManagerViewModel _manager;
    private readonly NotifyCollectionChangedEventHandler _onAvailablePluginsChanged;
    private readonly NotifyCollectionChangedEventHandler _onAvailableTemplatesChanged;
    private readonly NotifyCollectionChangedEventHandler _onStoresChanged;
    private readonly NotifyCollectionChangedEventHandler _onInstalledPluginsChanged;
    private readonly PropertyChangedEventHandler _onManagerPropertyChanged;
    private bool _isDisposed;

    /// <summary>The wrapped manager — the store dialog's XAML binds installs/updates, the restart banner and store-URL management straight to its commands/properties (e.g. <c>Manager.InstallFromStoreCommand</c>, <c>Manager.NeedsRestart</c>).</summary>
    public PluginManagerViewModel Manager => _manager;

    public IReadOnlyList<PluginStoreSortModeOption> SortModes { get; } = PluginStoreSortModeOption.All;

    public ObservableCollection<PluginStoreSidebarItem> SidebarItems { get; } = [];

    public ObservableCollection<StorePluginRowViewModel> FilteredPlugins { get; } = [];

    public ObservableCollection<StorePluginRowViewModel> FeaturedPlugins { get; } = [];

    public ObservableCollection<StorePluginRowViewModel> RecentlyAddedPlugins { get; } = [];

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private PluginStoreSortMode _selectedSortMode = PluginStoreSortMode.NameAscending;

    [ObservableProperty]
    private PluginStoreSidebarItem? _selectedSidebarItem;

    [ObservableProperty]
    private StorePluginRowViewModel? _selectedPlugin;

    [ObservableProperty]
    private bool _isManageStoresOpen;

    /// <summary>Design-time constructor for the previewer.</summary>
    public PluginStoreDialogViewModel() : this(new PluginManagerViewModel())
    {
    }

    /// <param name="manager">The shared plugin manager instance (see <see cref="Manager"/>).</param>
    /// <param name="initialFilter">
    /// The sidebar scope preselected when the dialog opens (#65) — e.g. a plugin-update toast opening
    /// straight onto <see cref="PluginStoreFilter.UpdatesAvailable"/> instead of the default Discover
    /// page. Falls back to <see cref="SidebarItems"/>'s first entry (Discover) when null or when no
    /// sidebar item matches it yet (the catalogue may still be empty at construction time — once it
    /// loads, <see cref="_RebuildSidebarItems"/> re-selects the same filter by value).
    /// </param>
    public PluginStoreDialogViewModel(PluginManagerViewModel manager, PluginStoreFilter? initialFilter = null)
    {
        _manager = manager;
        _onAvailablePluginsChanged = (_, _) => _OnCatalogueChanged();
        // The templates arrive after the plugins do, so counting them only when the plugin list changes left the
        // sidebar saying "Workflow templates (0)" — greyed out and unclickable — while the store was offering ten.
        _onAvailableTemplatesChanged = (_, _) => _OnCatalogueChanged();
        _onStoresChanged = (_, _) => _OnCatalogueChanged();
        // The Installed list is the local plugins, not the catalogue — so installing from a zip, or removing one,
        // changes it without the catalogue moving at all. Without this the heading count and the groups would be
        // whatever they were when the dialog opened.
        _onInstalledPluginsChanged = (_, _) => _OnInstalledChanged();
        _onManagerPropertyChanged = _OnManagerPropertyChanged;
        _manager.AvailablePlugins.CollectionChanged += _onAvailablePluginsChanged;
        _manager.AvailableTemplates.CollectionChanged += _onAvailableTemplatesChanged;
        _manager.Plugins.CollectionChanged += _onInstalledPluginsChanged;
        _manager.Stores.CollectionChanged += _onStoresChanged;
        _manager.PropertyChanged += _onManagerPropertyChanged;

        _RebuildSidebarItems();
        SelectedSidebarItem = initialFilter is null
            ? SidebarItems[0]
            : SidebarItems.FirstOrDefault(item => item.Filter == initialFilter) ?? SidebarItems[0];
        _RecomputeFiltered();
    }

    /// <summary>Unsubscribes from the shared manager's collections/property-changed — call when the dialog closes; the manager itself outlives the dialog (it is the Options tab's own instance), so a dialog that forgot this would leak one subscription per open.</summary>
    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _manager.AvailablePlugins.CollectionChanged -= _onAvailablePluginsChanged;
        _manager.AvailableTemplates.CollectionChanged -= _onAvailableTemplatesChanged;
        _manager.Plugins.CollectionChanged -= _onInstalledPluginsChanged;
        _manager.Stores.CollectionChanged -= _onStoresChanged;
        _manager.PropertyChanged -= _onManagerPropertyChanged;
        _isDisposed = true;
    }

    private void _OnManagerPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PluginManagerViewModel.IsBusy))
        {
            OnPropertyChanged(nameof(IsLoadingCatalogue));
        }
    }

    /// <summary>True when no store is configured yet — the grid column is replaced by the "no stores" panel (§1.8).</summary>
    public bool HasNoStores => _manager.Stores.Count == 0;

    /// <summary>True during the first catalogue fetch (stores configured, nothing loaded yet, a browse in flight) — the grid shows the "Loading plugin catalogue…" message instead of an empty state (§1.8).</summary>
    public bool IsLoadingCatalogue => _manager.IsBusy && !HasNoStores && _manager.AvailablePlugins.Count == 0;

    /// <summary>The Discover page's Featured/Recently-added rails only show above the "All plugins" grid when Discover is selected and there is no active search (§1.4).</summary>
    public bool ShowDiscoverRails => SelectedSidebarItem?.Filter.Kind == PluginStoreFilterKind.Discover && string.IsNullOrWhiteSpace(SearchText);

    /// <summary>
    /// True when the <b>Installed</b> filter is selected, so the main pane swaps the catalogue grid for the
    /// local plugin-management view (install-from-zip + per-plugin enable/disable/remove/settings) — moved
    /// here from the old Options → Plugins tab so all plugin control lives in one place. Unlike catalogue
    /// browsing, this view works even with no store configured (installing from a zip needs no store).
    /// </summary>
    public bool IsInstalledView => SelectedSidebarItem?.Filter.Kind == PluginStoreFilterKind.Installed;

    /// <summary>True while the sidebar's "Workflow templates" is selected — the flows the stores offer, not the plugins.</summary>
    public bool IsTemplatesView => SelectedSidebarItem?.Filter.Kind == PluginStoreFilterKind.Templates;

    /// <summary>
    /// The installed plugins under their category headings (Raymond, 2026-07-15) — one flat list stopped being
    /// readable once widgets, providers, issue trackers and a workflow engine all lived in it.
    /// </summary>
    /// <remarks>
    /// The heading comes from the catalogue, matched by plugin id, because a <c>plugin.json</c> carries no
    /// category — it is a store-index field. That is also why the grouping degrades rather than breaks: with no
    /// store configured (and this view is explicitly built to work without one) nothing matches, everything lands
    /// under one heading, and what is left is the flat list it replaced.
    /// </remarks>
    public IReadOnlyList<InstalledPluginGroup> InstalledGroups =>
        [.. _manager.Plugins
            .GroupBy(_CategoryOf, StringComparer.OrdinalIgnoreCase)
            // Alphabetical, the order the sidebar's own categories take — except "Other", which is a fallback
            // bucket rather than a peer and reads as filler if it lands between two real categories.
            .OrderBy(group => group.Key == StorePluginRowViewModel.OtherCategory ? 1 : 0)
            .ThenBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => new InstalledPluginGroup(group.Key, [.. group]))];

    /// <summary>True once there is more than one heading — with a single group the headings say nothing the list does not.</summary>
    public bool ShowInstalledGroupHeaders => InstalledGroups.Count > 1;

    /// <summary>
    /// Moves a plugin up the left menu, past the previous one <b>under its own heading</b> (Raymond, 2026-07-15).
    /// </summary>
    /// <remarks>
    /// The arrows move a plugin through the left menu, which is one flat sequence — and this list is no longer
    /// shown as one. Left on the manager's plain ±1 they would have started lying: press ↑ on the first widget
    /// and the row above it in the menu order is some provider, so the menu shifts while nothing visibly moves.
    /// <para>
    /// Within the heading is the reading that survives both: with one heading — no store configured, nothing
    /// matched — every plugin shares it and this is the old behaviour exactly. The menu order itself is never
    /// re-sorted by category; you simply cannot move a widget above an issue tracker, which was never a thing
    /// worth doing.
    /// </para>
    /// </remarks>
    [RelayCommand]
    private Task MoveInstalledPluginUpAsync(PluginRowViewModel row) => _MoveWithinGroupAsync(row, -1);

    /// <inheritdoc cref="MoveInstalledPluginUpAsync"/>
    [RelayCommand]
    private Task MoveInstalledPluginDownAsync(PluginRowViewModel row) => _MoveWithinGroupAsync(row, +1);

    private Task _MoveWithinGroupAsync(PluginRowViewModel row, int offset)
    {
        if (InstalledGroups.FirstOrDefault(group => group.Plugins.Contains(row)) is not { } group)
        {
            return Task.CompletedTask;
        }

        var within = group.Plugins.ToList().IndexOf(row) + offset;
        if (within < 0 || within >= group.Plugins.Count)
        {
            return Task.CompletedTask;
        }

        // Its neighbour's seat in the menu order, which is what the manager writes. Under one heading that seat
        // is the next one along and this is the plain ±1 it always was.
        return _manager.MovePluginToAsync(row, _manager.Plugins.IndexOf(group.Plugins[within]));
    }

    // The catalogue is the only thing that knows what a plugin is. One no store lists — sideloaded from a zip, or
    // superseded and left behind — has no category to find, and lands in "Other".
    private string _CategoryOf(PluginRowViewModel plugin) =>
        _manager.AvailablePlugins.FirstOrDefault(row =>
            string.Equals(row.Id, plugin.Discovered.Manifest.Id, StringComparison.OrdinalIgnoreCase))?.Category
        ?? StorePluginRowViewModel.OtherCategory;


    /// <summary>The templates a search narrows to: name, description and author, because whoever searches for "review" has that word in the description more often than in the name.</summary>
    public IEnumerable<StoreTemplateRowViewModel> FilteredTemplates => string.IsNullOrWhiteSpace(SearchText)
        ? _manager.AvailableTemplates
        : _manager.AvailableTemplates.Where(template =>
            template.Name.Contains(SearchText, StringComparison.OrdinalIgnoreCase)
            || template.Description.Contains(SearchText, StringComparison.OrdinalIgnoreCase)
            || template.Author.Contains(SearchText, StringComparison.OrdinalIgnoreCase));

    /// <summary>What the templates view says when a store offers none, or a search matches none.</summary>
    public string TemplatesEmptyMessage => _manager.AvailableTemplates.Count == 0
        ? "The configured stores offer no workflow templates."
        : "No templates match that.";

    /// <summary>The empty-state message for the current filter/search combination (§1.8) — search takes priority over the filter-specific message.</summary>
    public string EmptyStateMessage => BuildEmptyStateMessage(SelectedSidebarItem?.Filter ?? PluginStoreFilter.Discover, SearchText);

    public bool HasFeaturedPlugins => FeaturedPlugins.Count > 0;

    public bool HasRecentlyAddedPlugins => RecentlyAddedPlugins.Count > 0;

    public bool HasFilteredResults => FilteredPlugins.Count > 0;

    /// <summary>The sort combobox's bound item — a thin wrapper so the picker binds to a <see cref="PluginStoreSortModeOption"/> object while <see cref="SelectedSortMode"/> (the enum) stays the plain, test-friendly source of truth.</summary>
    public PluginStoreSortModeOption SelectedSortModeOption
    {
        get => SortModes.First(option => option.Mode == SelectedSortMode);
        set => SelectedSortMode = value.Mode;
    }

    /// <summary>Reloads the catalogue through the shared manager (its own fetch/problems handling, unchanged) — called when the dialog opens and by the Refresh button.</summary>
    public async Task LoadAsync()
    {
        if (_manager.Stores.Count > 0)
        {
            await _manager.BrowseStoresCommand.ExecuteAsync(null);
        }
    }

    [RelayCommand]
    private void ShowDetails(StorePluginRowViewModel? row)
    {
        if (row is not null)
        {
            SelectedPlugin = row;
        }
    }

    [RelayCommand]
    private void CloseDetails() => SelectedPlugin = null;

    // Cached (not recomputed per get) so the ComboBox's items are stable instances — the SelectedVersion we set
    // is one of these exact instances, so the dropdown shows it selected.
    private IReadOnlyList<StoreVersionOption> _selectedPluginVersions = [];

    /// <summary>The versions of the plugin shown in the detail panel, for the version-picker dropdown (the installed one is marked).</summary>
    public IReadOnlyList<StoreVersionOption> SelectedPluginVersions => _selectedPluginVersions;

    /// <summary>The version chosen in the picker dropdown; defaults to the installed version (or the latest when not installed).</summary>
    [ObservableProperty]
    private StoreVersionOption? _selectedVersion;

    /// <summary>"Reinstall" when the picked version is the one already installed, else "Install" — so re-installing the current version reads correctly instead of a plain "Install".</summary>
    public string InstallVersionLabel => SelectedVersion?.IsInstalled == true ? "Reinstall" : "Install";

    /// <summary>Release notes of the picked version, shown under the dropdown.</summary>
    public string? SelectedVersionNotes => SelectedVersion?.Version.Notes;

    public bool HasSelectedVersionNotes => !string.IsNullOrWhiteSpace(SelectedVersionNotes);

    partial void OnSelectedPluginChanged(StorePluginRowViewModel? value)
    {
        _selectedPluginVersions = value?.Entry.Versions?
            .Select(version => new StoreVersionOption(version, string.Equals(version.Version, value.InstalledVersion, StringComparison.Ordinal)))
            .ToList() ?? [];
        OnPropertyChanged(nameof(SelectedPluginVersions));

        // Default the picker to the installed version (so "Reinstall" is the obvious action), else the latest.
        SelectedVersion = _selectedPluginVersions.FirstOrDefault(option => option.IsInstalled)
                          ?? _selectedPluginVersions.FirstOrDefault();
    }

    partial void OnSelectedVersionChanged(StoreVersionOption? value)
    {
        OnPropertyChanged(nameof(InstallVersionLabel));
        OnPropertyChanged(nameof(SelectedVersionNotes));
        OnPropertyChanged(nameof(HasSelectedVersionNotes));
    }

    /// <summary>Installs the version picked in the detail panel — a rollback to an older build, a re-install of the current one, or an upgrade.</summary>
    [RelayCommand]
    private async Task InstallSelectedVersionAsync()
    {
        if (SelectedVersion is { } option && SelectedPlugin is { } row)
        {
            await Manager.InstallStoreVersionAsync(row, option.Version);
        }
    }

    [RelayCommand]
    private void ToggleManageStores() => IsManageStoresOpen = !IsManageStoresOpen;

    [RelayCommand]
    private Task RefreshAsync() => LoadAsync();

    [RelayCommand]
    private void ClearSearch() => SearchText = string.Empty;

    partial void OnSearchTextChanged(string value) => _RecomputeFiltered();

    partial void OnSelectedSortModeChanged(PluginStoreSortMode value)
    {
        OnPropertyChanged(nameof(SelectedSortModeOption));
        _RecomputeFiltered();
    }

    partial void OnSelectedSidebarItemChanged(PluginStoreSidebarItem? value)
    {
        OnPropertyChanged(nameof(IsInstalledView));
        OnPropertyChanged(nameof(IsTemplatesView));
        OnPropertyChanged(nameof(FilteredTemplates));
        OnPropertyChanged(nameof(TemplatesEmptyMessage));
        _RecomputeFiltered();
    }

    private void _OnCatalogueChanged()
    {
        _RebuildSidebarItems();
        _RecomputeFiltered();
        OnPropertyChanged(nameof(HasNoStores));
        OnPropertyChanged(nameof(IsLoadingCatalogue));

        // The Installed list is local, but its headings are not: a catalogue arriving late is what turns "Other"
        // into "Widgets", so the groups have to be re-read when it lands.
        _NotifyInstalledGroups();
    }

    // The local plugin list changed (a zip install, a removal) without the catalogue moving at all. The Installed
    // heading counts that list, and the count lives on a sidebar item — which is rebuilt whole.
    private void _OnInstalledChanged()
    {
        _RebuildSidebarItems();
        _NotifyInstalledGroups();
    }

    private void _NotifyInstalledGroups()
    {
        OnPropertyChanged(nameof(InstalledGroups));
        OnPropertyChanged(nameof(ShowInstalledGroupHeaders));
    }

    // Rebuilds the sidebar from the manager's current catalogue (categories + Installed/Updates counts
    // are derived, never hardcoded — a store that adds a new category shows up on its own). Re-selects
    // the same filter by value if it still exists, so browsing does not lose the selection on every
    // catalogue refresh; falls back to Discover if the previously selected category/filter disappeared.
    private void _RebuildSidebarItems()
    {
        var previousFilter = SelectedSidebarItem?.Filter;
        var plugins = _manager.AvailablePlugins;

        // What the Installed view lists, not what the catalogue happens to know is installed. Counting the
        // catalogue meant the label and the list disagreed about a plugin no store offers: it appeared in the
        // list and not in the number, which is how an operator ends up counting rows to see who is lying.
        var installedCount = _manager.Plugins.Count;
        var updatesCount = plugins.Count(row => row.UpdateAvailable);

        SidebarItems.Clear();
        SidebarItems.Add(PluginStoreSidebarItem.Discover);
        SidebarItems.Add(PluginStoreSidebarItem.All);
        foreach (var category in DistinctCategories(plugins))
        {
            SidebarItems.Add(PluginStoreSidebarItem.ForCategory(category));
        }

        SidebarItems.Add(PluginStoreSidebarItem.Templates(_manager.AvailableTemplates.Count));
        SidebarItems.Add(PluginStoreSidebarItem.Installed(installedCount));
        SidebarItems.Add(PluginStoreSidebarItem.UpdatesAvailable(updatesCount));

        var match = previousFilter is null ? null : SidebarItems.FirstOrDefault(item => item.Filter == previousFilter);
        SelectedSidebarItem = match ?? SidebarItems[0];
    }

    private void _RecomputeFiltered()
    {
        var filter = SelectedSidebarItem?.Filter ?? PluginStoreFilter.Discover;
        var filtered = Filter(_manager.AvailablePlugins, filter, SearchText, SelectedSortMode);
        _Replace(FilteredPlugins, filtered);

        if (SelectedPlugin is not null && !filtered.Contains(SelectedPlugin))
        {
            SelectedPlugin = null;
        }

        if (ShowDiscoverRails)
        {
            _Replace(FeaturedPlugins, Featured(_manager.AvailablePlugins));
            _Replace(RecentlyAddedPlugins, RecentlyAdded(_manager.AvailablePlugins));
        }
        else
        {
            _Replace(FeaturedPlugins, []);
            _Replace(RecentlyAddedPlugins, []);
        }

        OnPropertyChanged(nameof(ShowDiscoverRails));
        OnPropertyChanged(nameof(EmptyStateMessage));
        OnPropertyChanged(nameof(HasFeaturedPlugins));
        OnPropertyChanged(nameof(HasRecentlyAddedPlugins));
        OnPropertyChanged(nameof(HasFilteredResults));
    }

    private static void _Replace(ObservableCollection<StorePluginRowViewModel> target, IReadOnlyList<StorePluginRowViewModel> source)
    {
        // Skip the reset when nothing actually changed, so a re-filter that yields the same set does not
        // needlessly collapse the grid's scroll position / card focus.
        if (target.SequenceEqual(source))
        {
            return;
        }

        target.Clear();
        foreach (var row in source)
        {
            target.Add(row);
        }
    }

    /// <summary>
    /// Pure filter+search+sort over a catalogue — a static helper (no view-model construction needed) so
    /// it is directly unit-testable. Discover and All share the same scope (the full catalogue): Discover
    /// only adds the Featured/Recently-added rails on top of the same grid.
    /// </summary>
    public static IReadOnlyList<StorePluginRowViewModel> Filter(
        IEnumerable<StorePluginRowViewModel> plugins,
        PluginStoreFilter filter,
        string? searchText,
        PluginStoreSortMode sortMode)
    {
        ArgumentNullException.ThrowIfNull(plugins);
        ArgumentNullException.ThrowIfNull(filter);

        IEnumerable<StorePluginRowViewModel> scoped = filter.Kind switch
        {
            PluginStoreFilterKind.Category => plugins.Where(row => string.Equals(row.Category, filter.Category, StringComparison.OrdinalIgnoreCase)),
            PluginStoreFilterKind.Installed => plugins.Where(row => row.IsInstalled),
            PluginStoreFilterKind.UpdatesAvailable => plugins.Where(row => row.UpdateAvailable),
            _ => plugins,
        };

        if (!string.IsNullOrWhiteSpace(searchText))
        {
            var needle = searchText.Trim();
            scoped = scoped.Where(row =>
                _Contains(row.Name, needle) || _Contains(row.Description, needle) || _Contains(row.Author, needle));
        }

        return Sort(scoped, sortMode);
    }

    public static IReadOnlyList<StorePluginRowViewModel> Sort(IEnumerable<StorePluginRowViewModel> plugins, PluginStoreSortMode sortMode)
    {
        ArgumentNullException.ThrowIfNull(plugins);

        return sortMode switch
        {
            PluginStoreSortMode.RecentlyUpdated => plugins
                .OrderByDescending(row => row.PublishedDate.HasValue)
                .ThenByDescending(row => row.PublishedDate ?? DateOnly.MinValue)
                .ThenBy(row => row.Name, StringComparer.OrdinalIgnoreCase)
                .ToList(),
            PluginStoreSortMode.Author => plugins
                .OrderBy(row => row.Author ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .ThenBy(row => row.Name, StringComparer.OrdinalIgnoreCase)
                .ToList(),
            _ => plugins.OrderBy(row => row.Name, StringComparer.OrdinalIgnoreCase).ToList(),
        };
    }

    /// <summary>The number of newest-first entries shown in the Discover page's "Recently added" rail.</summary>
    public const int RecentlyAddedCount = 5;

    public static IReadOnlyList<StorePluginRowViewModel> Featured(IEnumerable<StorePluginRowViewModel> plugins) =>
        plugins.Where(row => row.IsFeatured).OrderBy(row => row.Name, StringComparer.OrdinalIgnoreCase).ToList();

    public static IReadOnlyList<StorePluginRowViewModel> RecentlyAdded(IEnumerable<StorePluginRowViewModel> plugins) =>
        plugins
            .Where(row => row.PublishedDate.HasValue)
            .OrderByDescending(row => row.PublishedDate)
            .Take(RecentlyAddedCount)
            .ToList();

    public static IReadOnlyList<string> DistinctCategories(IEnumerable<StorePluginRowViewModel> plugins) =>
        plugins
            .Select(row => row.Category)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(category => category, StringComparer.OrdinalIgnoreCase)
            .ToList();

    public static string BuildEmptyStateMessage(PluginStoreFilter filter, string? searchText)
    {
        ArgumentNullException.ThrowIfNull(filter);

        if (!string.IsNullOrWhiteSpace(searchText))
        {
            return $"No plugins match '{searchText.Trim()}'.";
        }

        return filter.Kind switch
        {
            PluginStoreFilterKind.Installed => "No plugins installed from a store yet.",
            PluginStoreFilterKind.UpdatesAvailable => "Everything is up to date.",
            _ => "Nothing here yet.",
        };
    }

    private static bool _Contains(string? haystack, string needle) =>
        !string.IsNullOrEmpty(haystack) && haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);
}
