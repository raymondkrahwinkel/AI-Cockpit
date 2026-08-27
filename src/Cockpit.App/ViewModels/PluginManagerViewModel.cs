using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cockpit.App.Plugins;
using Cockpit.App.Services;
using Cockpit.Core.Abstractions.Plugins;
using Cockpit.Core.Plugins;
using Cockpit.Infrastructure.Plugins;
using Cockpit.Infrastructure.Svg;
using Cockpit.Plugins.Abstractions;

namespace Cockpit.App.ViewModels;

// It is a config editor over discovery + the registration store; a store download always goes through the same
// installer + consent, never bypassing them (#14).
public partial class PluginManagerViewModel : ViewModelBase
{
    private readonly IPluginRegistrationStore? _registrationStore;
    private readonly IPluginInstaller? _installer;
    private readonly PluginBootstrap? _bootstrap;
    private readonly ISessionDialogService? _dialogService;
    private readonly IPluginStoreConfigStore? _storeConfigStore;
    private readonly IPluginStoreClient? _storeClient;

    // Falls back to wrapping `_storeClient`/`_installer` itself only when nothing was handed in, so the dozens of
    // existing tests that construct this view model by hand and stub `IPluginStoreClient`/`IPluginInstaller` directly
    // keep observing every call without also having to stub this interface (AC-510[b]).
    private readonly IPluginProvisioningService? _provisioningService;

    private readonly IReadOnlyDictionary<string, PluginSettingsRegistration>? _settingsRegistry;
    private readonly PluginDiagnostics? _diagnostics;
    private readonly IPluginContributionSink? _contributionSink;
    private readonly IAppRestartService? _restartService;
    private readonly IWorkflowTemplateLibrary? _templateLibrary;

    public ObservableCollection<PluginRowViewModel> Plugins { get; } = [];

    public ObservableCollection<PluginStoreConfig> Stores { get; } = [];

    // The configured stores as display rows for the Manage-stores dialog (#62): the same URLs as
    // `Stores`, each wrapped with a name/icon/plugin-count. Rebuilt from `Stores` on
    // load, then enriched from each store's `index.json` on `BrowseStoresAsync`.
    public ObservableCollection<PluginStoreInfo> StoreInfos { get; } = [];

    public ObservableCollection<StorePluginRowViewModel> AvailablePlugins { get; } = [];

    // The workflow templates the stores offer (#69) — flows somebody already drew. Browsed with the plugins, from the
    // same index: a store that publishes both is one store, and asking the operator to visit two places to find out
    // what it has would be an implementation detail leaking into the app.
    public ObservableCollection<StoreTemplateRowViewModel> AvailableTemplates { get; } = [];

    // Whether any store offers a template at all — no templates, no section.
    public bool HasAvailableTemplates => AvailableTemplates.Count > 0;

    // An update is only swapped live on restart, so the live manifest still reports the old version; treating the
    // staged version as installed keeps a just-updated plugin from lingering in "Available updates" until the restart
    // (AC-76).
    private readonly ConcurrentDictionary<string, string> _pendingUpdateVersions = new(StringComparer.OrdinalIgnoreCase);

    // How many plugin updates the background checker found waiting (AC-76) — bound by the sidebar "Plugin store"
    // button's badge, so an update is a persistent indicator in the main window rather than only a transient toast.
    // Fed by `SetUpdateBadgeCount` from the checker, and counted down as the operator stages each update.
    [ObservableProperty]
    private int _updateBadgeCount;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private bool _hasPlugins;

    [ObservableProperty]
    private string _newStoreUrl = string.Empty;

    // Whether the add-store form is set to a local folder rather than a remote URL (AC-7) — flips which fields the Manage-stores dialog shows.
    [ObservableProperty]
    private bool _newStoreIsLocal;

    // An optional bearer token for a private remote store (AC-7) — sent as an Authorization header, and encrypted at rest when secret protection is on.
    [ObservableProperty]
    private string _newStoreToken = string.Empty;

    // The chosen local store folder (AC-7), set by the folder picker.
    [ObservableProperty]
    private string _newStoreFolder = string.Empty;

    [ObservableProperty]
    private bool _isBusy;

    // A single install is one step and has no fraction — the download arrives in one piece — so only "Update all"
    // leaves it, fed by the same counter it already writes into `StatusMessage` (AC-420).
    [ObservableProperty]
    private bool _busyProgressIndeterminate = true;

    [ObservableProperty]
    private double _busyProgressValue;

    // "The store is working" is a depth, not a flag (AC-420).
    private int _busyDepth;

    private void _EnterBusy()
    {
        _busyDepth++;
        IsBusy = true;
    }

    private void _ExitBusy()
    {
        _busyDepth = Math.Max(0, _busyDepth - 1);
        if (_busyDepth == 0)
        {
            IsBusy = false;
        }
    }

    // Three per-route guards each closed the seam they were aimed at and left the next one, so the routes no longer
    // decide this for themselves — _InstallExclusivelyAsync does, once (AC-456).
    private bool _installInFlight;

    // A plain bool carries it because these are dispatcher-thread commands: they interleave at their awaits, never in
    // parallel, so there is no read-modify-write to lose (AC-456).
    private async Task _InstallExclusivelyAsync(Func<Task> install)
    {
        if (_installInFlight)
        {
            // Refused without a word, deliberately.
            return;
        }

        _installInFlight = true;
        _EnterBusy();
        try
        {
            await install();
        }
        finally
        {
            _ExitBusy();
            _installInFlight = false;
        }
    }

    // A restart and every route that unpacks are closed while work is in flight (AC-420), gated by their command's
    // CanExecute — which is what a bound Button consults, so the affordance goes dead rather than only looking dead
    // (AC-456, AC-455).
    partial void OnIsBusyChanged(bool value)
    {
        RestartNowCommand.NotifyCanExecuteChanged();
        InstallFromStoreCommand.NotifyCanExecuteChanged();
        InstallFromZipCommand.NotifyCanExecuteChanged();
        UpdateAllCommand.NotifyCanExecuteChanged();
        BrowseStoresCommand.NotifyCanExecuteChanged();

        AddStoreCommand.NotifyCanExecuteChanged();
        RemoveStoreCommand.NotifyCanExecuteChanged();
        EnablePluginCommand.NotifyCanExecuteChanged();
        DisablePluginCommand.NotifyCanExecuteChanged();
        RemovePluginCommand.NotifyCanExecuteChanged();
        MovePluginUpCommand.NotifyCanExecuteChanged();
        MovePluginDownCommand.NotifyCanExecuteChanged();
        TogglePluginMenuVisibilityCommand.NotifyCanExecuteChanged();
        ToggleStorePluginCommand.NotifyCanExecuteChanged();
        InstallTemplateCommand.NotifyCanExecuteChanged();
        RemoveTemplateCommand.NotifyCanExecuteChanged();
    }

    // True once an install/enable/disable/remove has actually changed plugin state this session (#53) — the manager
    // shows a "Restart now" button once this flips, instead of the operator having to remember to close and relaunch
    // the app by hand.
    [ObservableProperty]
    private bool _needsRestart;

    // "Update all" raises `NeedsRestart` after the *first* plugin of the batch, so the button appeared while the other
    // nine were still downloading; restarting there left them silently un-updated with a banner saying the update was
    // done (AC-420).
    public bool CanRestart => _restartService is not null && !IsBusy;

    // Closed while the store is working, and read by every one of those commands rather than by the overlay drawn over
    // their buttons: an overlay stops a pointer and leaves the focus underneath it, so a Tab and a space bar walk
    // straight past it (AC-455).
    public bool CanChangePlugins => !IsBusy;

    // Design-time constructor for the previewer.
    public PluginManagerViewModel()
    {
        _WatchAvailablePluginsForUpdateGate();
        _WatchPluginsForPendingApprovalBadge();
    }

    public PluginManagerViewModel(
        IPluginRegistrationStore registrationStore,
        IPluginInstaller installer,
        PluginBootstrap bootstrap,
        ISessionDialogService dialogService,
        IPluginStoreConfigStore storeConfigStore,
        IPluginStoreClient storeClient,
        IReadOnlyDictionary<string, PluginSettingsRegistration> settingsRegistry,
        PluginDiagnostics diagnostics,
        IPluginContributionSink? contributionSink = null,
        IAppRestartService? restartService = null,
        IWorkflowTemplateLibrary? templateLibrary = null,
        IPluginProvisioningService? provisioningService = null)
    {
        _registrationStore = registrationStore;
        _installer = installer;
        _bootstrap = bootstrap;
        _dialogService = dialogService;
        _storeConfigStore = storeConfigStore;
        _storeClient = storeClient;
        // Prefer the DI-resolved singleton (AC-510[b]: one install path, not two); only build a private one when
        // nothing was handed in, which is every existing test that constructs this view model directly.
        _provisioningService = provisioningService ?? new PluginProvisioningService(storeClient, installer);
        _settingsRegistry = settingsRegistry;
        _diagnostics = diagnostics;
        _contributionSink = contributionSink;
        _restartService = restartService;
        _templateLibrary = templateLibrary;
        _WatchAvailablePluginsForUpdateGate();
        _WatchPluginsForPendingApprovalBadge();
    }

    // Browsing the stores rebuilds that collection, so the gate has to be re-raised from the collection itself —
    // notifying only from the install/update paths left the button hidden right after the store loaded, the one moment
    // there is definitely something to update.
    private void _WatchAvailablePluginsForUpdateGate() =>
        AvailablePlugins.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasAvailableUpdates));
            OnPropertyChanged(nameof(AvailableUpdateCount));
        };

    [RelayCommand(CanExecute = nameof(CanRestart))]
    private void RestartNow() => _restartService?.Restart();

    // Loads the installed plugins + stores first: unlike the old Options→Plugins tab, the store can be opened without
    // ever opening Options, and its Installed view needs `Plugins` populated (#62).
    [RelayCommand]
    private async Task OpenStoreDialogAsync()
    {
        if (_dialogService is null)
        {
            return;
        }

        await LoadAsync();
        await _dialogService.ShowPluginStoreDialogAsync(this);
    }

    // Rediscovers the installed plugins and loads the configured stores; called when the Options dialog opens and after
    // every change (AC-455).
    public async Task LoadAsync()
    {
        if (_bootstrap is not null)
        {
            var discovered = await _bootstrap.DiscoverAsync(AbstractionsContract.Version);
            var registrations = _registrationStore is null
                ? new Dictionary<string, PluginRegistration>()
                : (await _registrationStore.LoadAllAsync()).ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal);

            // AC-208: from here on PendingApprovalCount reads live off Plugins rather than the startup seed — a
            // real discovery just ran, so it is the fresher, and only source that drops to 0 as the operator acts.
            _hasDiscoveredPluginsOnce = true;

            Plugins.Clear();
            // The manager lists plugins in the order they appear in the left menu (#72), so moving one up here
            // moves it up there — a list ordered differently from the thing it reorders is a puzzle, not a tool.
            foreach (var plugin in discovered.OrderBy(plugin => registrations.TryGetValue(plugin.FolderId, out var registration) ? registration.MenuOrder : 0))
            {
                registrations.TryGetValue(plugin.FolderId, out var menuRegistration);
                Plugins.Add(new PluginRowViewModel(
                    plugin,
                    _settingsRegistry?.ContainsKey(plugin.FolderId) ?? false,
                    _diagnostics?.AllForFolder(plugin.FolderId),
                    menuRegistration?.HiddenInMenu ?? false,
                    menuRegistration?.PinnedToSidebar ?? false));
            }

            HasPlugins = Plugins.Count > 0;
            // Belt-and-braces alongside the CollectionChanged watcher: an empty-to-empty discovery (no plugins at
            // all, or none of them at NeedsConsent) never raises Add/Reset, which would otherwise leave a nonzero
            // startup seed on screen after the switch to the live (zero) count above.
            OnPropertyChanged(nameof(PendingApprovalCount));
            OnPropertyChanged(nameof(HasPendingApproval));
        }

        await _LoadStoresAsync();
    }

    private async Task _LoadStoresAsync()
    {
        if (_storeConfigStore is null)
        {
            return;
        }

        Stores.Clear();
        StoreInfos.Clear();
        foreach (var store in await _storeConfigStore.LoadAsync())
        {
            Stores.Add(store);
            // Name/icon/count start URL-derived and fill in on the next browse; keeping the same URL as the
            // key means the browse can find and enrich this exact row.
            StoreInfos.Add(new PluginStoreInfo(store));
        }
    }

    // Whether a zip install can be started — it reaches the same installer as a store install, so it is closed while the store is working (AC-420). Nothing queues: the button goes dead and the operator presses it again afterwards.
    public bool CanInstallFromZip => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanInstallFromZip))]
    private async Task InstallFromZipAsync()
    {
        if (_dialogService is null || _installer is null)
        {
            return;
        }

        var zipPath = await _dialogService.PickPluginZipAsync();
        if (zipPath is null)
        {
            return;
        }

        // Claimed only now, not around the picker: the operator choosing a file is their time, and covering the dialog
        // behind a busy overlay while a file picker is open would be covering nothing that is working (AC-456).
        await _InstallExclusivelyAsync(async () =>
        {
            StatusMessage = $"Installing '{Path.GetFileName(zipPath)}'…";
            var result = await _installer.InstallFromZipAsync(zipPath, AbstractionsContract.Version);
            await _AfterInstallAsync(result, "Plugin installed. Restart the cockpit to activate it.");
        });
    }

    [RelayCommand(CanExecute = nameof(CanChangePlugins))]
    private async Task EnablePluginAsync(PluginRowViewModel row)
    {
        if (_registrationStore is null || _dialogService is null)
        {
            return;
        }

        // Enabling always requires consent to the current bytes: the operator sees what they are trusting
        // and the shown SHA-256 is what gets pinned.
        var consented = await _dialogService.ShowPluginConsentAsync(row.ToConsentInfo());
        if (!consented)
        {
            return;
        }

        await _registrationStore.SaveAsync(row.FolderId, new PluginRegistration(Enabled: true, PinnedSha256: row.Discovered.Sha256));
        await LoadAsync();
        StatusMessage = $"'{row.DisplayName}' enabled. Restart the cockpit to load it.";
        NeedsRestart = true;
    }

    // The manager's gear is now one of several ways into a plugin's settings, so it opens them the same way the others do.
    [RelayCommand]
    private async Task OpenPluginSettingsAsync(PluginRowViewModel row)
    {
        if (_contributionSink is null)
        {
            return;
        }

        await _contributionSink.OpenPluginSettingsAsync(row.FolderId);
    }

    // Moves the plugin up the left menu (#72) — and up this list, which is ordered the same way.
    [RelayCommand(CanExecute = nameof(CanChangePlugins))]
    private Task MovePluginUpAsync(PluginRowViewModel row) => MovePluginToAsync(row, Plugins.IndexOf(row) - 1);

    // Moves the plugin down the left menu (#72).
    [RelayCommand(CanExecute = nameof(CanChangePlugins))]
    private Task MovePluginDownAsync(PluginRowViewModel row) => MovePluginToAsync(row, Plugins.IndexOf(row) + 1);

    // The neighbour is the caller's to choose because it is not always the next one along: the store dialog lists these
    // under category headings, and "up" there means past the previous plugin *under the same heading*, which the flat
    // list may have several rows away.
    public async Task MovePluginToAsync(PluginRowViewModel row, int target)
    {
        var index = Plugins.IndexOf(row);
        if (index < 0 || target < 0 || target >= Plugins.Count || target == index)
        {
            return;
        }

        var offset = target - index;
        Plugins.Move(index, target);

        // The move itself is this list, which is the menu order; persisting is what follows from it. Without a
        // store (design time, the previewer) there is nowhere to write and nothing else to do — but the row still
        // moves, because an arrow that quietly does nothing is worse than one that is not there.
        if (_registrationStore is null)
        {
            return;
        }

        for (var position = 0; position < Plugins.Count; position++)
        {
            var plugin = Plugins[position];
            await _registrationStore.SaveMenuPreferenceAsync(plugin.FolderId, position, plugin.HiddenInMenu);
            _contributionSink?.ApplyPluginMenuPreference(plugin.FolderId, position, plugin.HiddenInMenu);
        }

        StatusMessage = $"'{row.DisplayName}' moved {(offset < 0 ? "up" : "down")} in the left menu.";
    }

    // Hides or shows the plugin's left-menu contributions (#72). The plugin keeps running either way — its
    // shortcut and command-palette entry still work — so this is emphatically not a quieter way to disable it.
    [RelayCommand(CanExecute = nameof(CanChangePlugins))]
    private async Task TogglePluginMenuVisibilityAsync(PluginRowViewModel row)
    {
        if (_registrationStore is null)
        {
            return;
        }

        var hidden = !row.HiddenInMenu;
        var order = Math.Max(Plugins.IndexOf(row), 0);

        await _registrationStore.SaveMenuPreferenceAsync(row.FolderId, order, hidden);
        _contributionSink?.ApplyPluginMenuPreference(row.FolderId, order, hidden);
        await LoadAsync();

        StatusMessage = hidden
            ? $"'{row.DisplayName}' hidden from the left menu — it still runs, and its shortcut still works."
            : $"'{row.DisplayName}' shown in the left menu again.";
    }

    // Pins or unpins the plugin's contributions top-level in the sidebar, out of the collapsed "Plugins ›" menu
    // (AC-937) — a separate axis from #72's order/hide, so neither is touched here.
    [RelayCommand(CanExecute = nameof(CanChangePlugins))]
    private async Task TogglePluginPinnedAsync(PluginRowViewModel row)
    {
        if (_registrationStore is null)
        {
            return;
        }

        var pinned = !row.PinnedToSidebar;
        var order = Math.Max(Plugins.IndexOf(row), 0);

        await _registrationStore.SaveMenuPreferenceAsync(row.FolderId, order, row.HiddenInMenu, pinned);
        _contributionSink?.ApplyPluginMenuPreference(row.FolderId, order, row.HiddenInMenu, pinned);
        await LoadAsync();

        StatusMessage = pinned
            ? $"'{row.DisplayName}' pinned to the sidebar."
            : $"'{row.DisplayName}' moved into 'Plugins ›'.";
    }

    [RelayCommand(CanExecute = nameof(CanChangePlugins))]
    private async Task DisablePluginAsync(PluginRowViewModel row)
    {
        if (_registrationStore is null)
        {
            return;
        }

        await _registrationStore.SaveAsync(row.FolderId, new PluginRegistration(Enabled: false, PinnedSha256: row.Discovered.Sha256));
        await LoadAsync();
        StatusMessage = $"'{row.DisplayName}' disabled. Restart the cockpit to unload it.";
        NeedsRestart = true;
    }

    [RelayCommand(CanExecute = nameof(CanChangePlugins))]
    private async Task RemovePluginAsync(PluginRowViewModel row)
    {
        if (_registrationStore is null || _installer is null)
        {
            return;
        }

        if (_dialogService is not null &&
            !await _dialogService.ShowConfirmationDialogAsync(
                "Remove plugin",
                $"Remove '{row.DisplayName}'? It will be uninstalled on the next restart. You can install it again from the store.",
                "Remove"))
        {
            return;
        }

        await _installer.MarkForRemovalAsync(row.FolderId);
        await _registrationStore.RemoveAsync(row.FolderId);
        await LoadAsync();
        StatusMessage = $"'{row.DisplayName}' will be removed on the next restart.";
        NeedsRestart = true;
    }

    // Opens a folder picker for a local store (AC-7) and puts the chosen path in the add-store form.
    [RelayCommand]
    private async Task PickStoreFolderAsync()
    {
        if (_dialogService is null)
        {
            return;
        }

        if (await _dialogService.PickPluginStoreFolderAsync() is { } folder)
        {
            NewStoreFolder = folder;
        }
    }

    [RelayCommand(CanExecute = nameof(CanChangePlugins))]
    private async Task AddStoreAsync()
    {
        if (_storeConfigStore is null)
        {
            return;
        }

        if (!_TryBuildNewStore(out var store, out var error))
        {
            StatusMessage = error;
            return;
        }

        await _storeConfigStore.AddAsync(store);
        NewStoreUrl = string.Empty;
        NewStoreToken = string.Empty;
        NewStoreFolder = string.Empty;
        await _LoadStoresAsync();
        StatusMessage = "Store added. Use Browse to see its plugins.";
    }

    // Builds a store from the add-store form — a local folder, or a remote URL with an optional token. Validation
    // matches how the client resolves it: a local folder must exist, a remote URL must parse to an index.
    private bool _TryBuildNewStore(out PluginStoreConfig store, out string error)
    {
        store = null!;
        error = string.Empty;

        if (NewStoreIsLocal)
        {
            var folder = NewStoreFolder.Trim();
            if (folder.Length == 0)
            {
                error = "Choose a folder for the local store.";
                return false;
            }

            if (!Directory.Exists(folder))
            {
                error = "That folder does not exist.";
                return false;
            }

            store = PluginStoreConfig.Local(folder);
            return true;
        }

        var url = NewStoreUrl.Trim();
        if (!PluginStoreUrl.TryResolveIndexUrl(url, out _, out var urlError))
        {
            error = urlError ?? "That store URL is not valid.";
            return false;
        }

        var token = NewStoreToken.Trim();
        store = PluginStoreConfig.Remote(url, token.Length == 0 ? null : token);
        return true;
    }

    [RelayCommand(CanExecute = nameof(CanChangePlugins))]
    private async Task RemoveStoreAsync(PluginStoreInfo info)
    {
        if (_storeConfigStore is null)
        {
            return;
        }

        if (_dialogService is not null &&
            !await _dialogService.ShowConfirmationDialogAsync(
                "Remove store",
                $"Remove the plugin store '{info.Name}'? Its plugins will no longer appear in the catalogue. Already-installed plugins stay installed.",
                "Remove"))
        {
            return;
        }

        await _storeConfigStore.RemoveAsync(info.Store);
        await _LoadStoresAsync();
    }

    // Public because the install paths call it directly on their way out and must not be refused: the command is gated
    // for the operator (a refresh mid-install clears and refills the collection that install is walking), and the
    // method is the way in for the code that owns the install around it.
    [RelayCommand(CanExecute = nameof(CanChangePlugins))]
    public async Task BrowseStoresAsync()
    {
        if (_storeClient is null)
        {
            return;
        }

        _EnterBusy();
        // Said before the fetch, not only after it (AC-420): the busy overlay shows StatusMessage, and this
        // method raises it — including for a plain Refresh — so without a line of its own the overlay would
        // sit there repeating whatever the last install said while it is actually reloading the catalogue.
        StatusMessage = "Loading the plugin catalogue…";
        AvailablePlugins.Clear();
        AvailableTemplates.Clear();
        try
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var seenTemplates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var problems = new List<string>();
            // Store logos are fetched after the catalogue is in — plugins show at once, the logos pop in when
            // they arrive, and a slow or broken image never delays the list.
            var logoLoads = new List<Task>();
            // Over a snapshot, for the reason "Update all" takes one: this loop awaits a fetch per store, and LoadAsync
            // clears that list.
            foreach (var store in Stores.ToList())
            {
                var info = StoreInfos.FirstOrDefault(candidate => candidate.Store.SameStoreAs(store));

                var fetch = await _storeClient.FetchIndexAsync(store);
                if (!fetch.IsSuccess || fetch.Index is null || fetch.IndexUrl is null)
                {
                    problems.Add(fetch.Error ?? "unreachable store");
                    if (info is not null)
                    {
                        info.IsReachable = false;
                        info.IsBrowsed = true;
                    }

                    continue;
                }

                if (info is not null)
                {
                    // The store's own advertised name/icon/count for the Manage-stores dialog; the name falls
                    // back to the URL-derived one when the index sets none.
                    info.Name = string.IsNullOrWhiteSpace(fetch.Index.Name) ? info.Name : fetch.Index.Name!;
                    info.Icon = fetch.Index.Icon;
                    info.PluginCount = fetch.Index.Plugins.Count;
                    info.IsReachable = true;
                    info.IsBrowsed = true;

                    if (!string.IsNullOrWhiteSpace(fetch.Index.IconUrl))
                    {
                        logoLoads.Add(_LoadStoreLogoAsync(info, store, fetch.Index.IconUrl!));
                    }
                }

                foreach (var entry in fetch.Index.Plugins)
                {
                    // First store wins for a given id, so a plugin listed in several stores shows once.
                    if (!seen.Add(entry.Id))
                    {
                        continue;
                    }

                    // AC-815: a reference-only plugin (e.g. Example Workspace) stays installable via
                    // install-from-zip but drops out of every browsable list.
                    if (entry.Hidden)
                    {
                        continue;
                    }

                    var installedRow = Plugins.FirstOrDefault(row => row.FolderId == PluginFolderName.Normalize(entry.Id));
                    // A staged update reports its new version even before the restart, so it drops out of the
                    // updates list once updated instead of lingering.
                    var installedVersion = _pendingUpdateVersions.TryGetValue(entry.Id, out var pending)
                        ? pending
                        : installedRow?.Discovered.Manifest.Version;
                    var row = new StorePluginRowViewModel(
                        entry,
                        store,
                        installedVersion,
                        isEnabled: installedRow?.CanDisable ?? false,
                        hasSettings: installedRow?.HasSettings ?? false);
                    AvailablePlugins.Add(row);

                    // AC-553 option A: a provider plugin's `LogoAsset` naming its vendor's own CDN — fetched the
                    // same way a store's own IconUrl already is (below), never redistributed as a file in this repo.
                    if (row.IsRemoteLogoAsset)
                    {
                        logoLoads.Add(_LoadPluginLogoAsync(row, store, entry.LogoAsset!));
                    }
                }

                foreach (var template in fetch.Index.Templates ?? [])
                {
                    // First store wins for an id, same as for plugins: a template listed twice shows once.
                    if (!seenTemplates.Add(template.Id))
                    {
                        continue;
                    }

                    AvailableTemplates.Add(new StoreTemplateRowViewModel(
                        template,
                        store,
                        isInstalled: _templateLibrary?.IsInstalled(template.Id) ?? false));
                }
            }

            await Task.WhenAll(logoLoads);

            OnPropertyChanged(nameof(HasAvailableTemplates));

            StatusMessage = AvailablePlugins.Count == 0
                ? (problems.Count > 0 ? $"No plugins found ({problems[0]})." : "No plugins found in the configured stores.")
                : $"{AvailablePlugins.Count} plugin(s) available." + (problems.Count > 0 ? $" ({problems.Count} store(s) unreachable.)" : string.Empty);

            // Recompute the update badge from browse results so installs and rollbacks cannot leave it stale (AC-76).
            UpdateBadgeCount = AvailableUpdateCount;
        }
        finally
        {
            _ExitBusy();
        }
    }

    // Fetches a store's logo image and hands it to its row as a Bitmap. Best-effort: an http error, an oversize
    // image or an undecodable one leaves Logo null, and the row keeps its emoji/default glyph — a store's logo is
    // never allowed to break browsing.
    private async Task _LoadStoreLogoAsync(PluginStoreInfo info, PluginStoreConfig store, string iconUrl)
    {
        if (_storeClient is null)
        {
            return;
        }

        try
        {
            var image = await _storeClient.DownloadImageAsync(store, iconUrl);
            if (image.IsSuccess && image.Bytes is { Length: > 0 } bytes)
            {
                using var stream = new MemoryStream(bytes);
                info.Logo = new Bitmap(stream);
            }
        }
        catch
        {
            // Decoding failed (a non-image, a corrupt file) — fall back to the glyph, silently.
        }
    }

    // Fetches a provider plugin's vendor-hosted logo (AC-553 option A), best-effort like `_LoadStoreLogoAsync`
    // above: any failure leaves `RemoteLogo` null and the row falls to its glyph/monogram. Rasterises SVG bytes
    // first (SvgRasterizer) since Avalonia's `Bitmap` only decodes raster images.
    private async Task _LoadPluginLogoAsync(StorePluginRowViewModel row, PluginStoreConfig store, string logoUrl)
    {
        if (_storeClient is null)
        {
            return;
        }

        try
        {
            var image = await _storeClient.DownloadImageAsync(store, logoUrl);
            if (!image.IsSuccess || image.Bytes is not { Length: > 0 } bytes)
            {
                return;
            }

            if (SvgRasterizer.LooksLikeSvg(bytes) && SvgRasterizer.Rasterize(bytes, 256f) is { } raster)
            {
                bytes = raster;
            }

            using var stream = new MemoryStream(bytes);
            row.RemoteLogo = new Bitmap(stream);
        }
        catch
        {
            // Decoding failed (a non-image, a corrupt file) — fall back to the glyph/monogram, silently.
        }
    }

    // Installs a workflow template (#69): fetches its flow, checks it against the store's checksum, and writes it into
    // the library the editor's picker reads. Nothing is loaded and no code runs — a template is a flow as text, and it
    // arrives switched off, for the operator to read before arming it.
    [RelayCommand(CanExecute = nameof(CanChangePlugins))]
    private async Task InstallTemplateAsync(StoreTemplateRowViewModel row)
    {
        if (_storeClient is null || _templateLibrary is null)
        {
            return;
        }

        _EnterBusy();
        try
        {
            var download = await _storeClient.DownloadTemplateAsync(row.Store, row.Entry.Path, row.Entry.Sha256);
            if (!download.IsSuccess || download.Json is null)
            {
                StatusMessage = download.Error ?? $"Could not install '{row.Name}'.";
                return;
            }

            _templateLibrary.Install(new InstalledWorkflowTemplate(
                row.Entry.Id,
                row.Entry.Name,
                row.Entry.Description,
                download.Json,
                row.Entry.Author,
                row.Entry.Version,
                row.Entry.Category,
                row.Entry.Requires));

            row.IsInstalled = true;

            // Templates are read into the editor's picker at startup, so this one is there next time — said plainly
            // rather than left for the operator to wonder why the flow they just installed is not in the list.
            NeedsRestart = true;
            var installedMessage = $"'{row.Name}' installed. Restart the cockpit and it is in the flow editor's templates.";
            StatusMessage = download.Warning is { } warning ? $"⚠ {warning} {installedMessage}" : installedMessage;
        }
        finally
        {
            _ExitBusy();
        }
    }

    // Takes an installed template out of the library. The flows already made from it are yours and stay.
    [RelayCommand(CanExecute = nameof(CanChangePlugins))]
    private void RemoveTemplate(StoreTemplateRowViewModel row)
    {
        if (_templateLibrary is null)
        {
            return;
        }

        _templateLibrary.Remove(row.Entry.Id);
        row.IsInstalled = false;
        NeedsRestart = true;
        StatusMessage = $"'{row.Name}' removed. Flows you already made from it are unaffected.";
    }

    // Whether a store install can be started — nothing else may already be in flight (AC-420).
    public bool CanInstallFromStore => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanInstallFromStore))]
    private async Task InstallFromStoreAsync(StorePluginRowViewModel row)
    {
        if (_storeClient is null || _installer is null)
        {
            return;
        }

        await _InstallExclusivelyAsync(async () =>
        {
            try
            {
                await _DownloadAndInstallRowAsync(row);

                // Refresh the catalogue rows to their new installed/up-to-date state, but keep the install
                // (or consent) message the operator just saw rather than the browse summary.
                var installMessage = StatusMessage;
                await BrowseStoresAsync();
                StatusMessage = installMessage;
            }
            finally
            {
                // The catalogue was rebuilt (or cleared) — refresh the "Update all" button's gate and count.
                // Inside the scope, so a refused start leaves the running install's catalogue alone.
                OnPropertyChanged(nameof(HasAvailableUpdates));
                OnPropertyChanged(nameof(AvailableUpdateCount));
            }
        });
    }

    // True when at least one installed plugin has a newer, installable version in a store — gates the "Update all"
    // button (AC-181).
    public bool HasAvailableUpdates => AvailablePlugins.Any(row => row.CanUpdate);

    // How many installed plugins have a newer, installable version available — shown on the "Update all" button.
    public int AvailableUpdateCount => AvailablePlugins.Count(row => row.CanUpdate);

    // Whether the sidebar "Plugin store" badge shows — true while the background checker's last count found updates (AC-76).
    public bool HasUpdateBadge => UpdateBadgeCount > 0;

    partial void OnUpdateBadgeCountChanged(int value) => OnPropertyChanged(nameof(HasUpdateBadge));

    // AC-208: Plugins is only ever populated by LoadAsync, which runs when the operator opens the Options/store dialog
    // — not at startup.
    private int _seededPendingApprovalCount;
    private bool _hasDiscoveredPluginsOnce;

    // How many installed plugins are sitting at awaiting-approval (AC-208) — new, or their bytes changed since
    // last approved. Before the first `LoadAsync` this is the startup snapshot seeded via
    // `SeedPendingApprovalCount`; after, it reads straight off `Plugins`, live.
    public int PendingApprovalCount => _hasDiscoveredPluginsOnce
        ? Plugins.Count(row => row.Discovered.Decision is PluginLoadDecision.NeedsConsent)
        : _seededPendingApprovalCount;

    // Whether the sidebar "Plugin store" badge should show the pending-approval count (AC-208).
    public bool HasPendingApproval => PendingApprovalCount > 0;

    // A no-op once a real discovery (`LoadAsync`) has run — the live count owns the badge from then on, so a stale seed
    // can never keep it showing after the operator has dealt with everything (AC-208).
    public void SeedPendingApprovalCount(int count)
    {
        _seededPendingApprovalCount = Math.Max(0, count);
        if (_hasDiscoveredPluginsOnce)
        {
            return;
        }

        OnPropertyChanged(nameof(PendingApprovalCount));
        OnPropertyChanged(nameof(HasPendingApproval));
    }

    // Raise approval counts after every wholesale plugin reload, not only after commands, so fresh browse results
    // cannot leave the gate stale.
    private void _WatchPluginsForPendingApprovalBadge() =>
        Plugins.CollectionChanged += (_, _) =>
        {
            _hasDiscoveredPluginsOnce = true;
            OnPropertyChanged(nameof(PendingApprovalCount));
            OnPropertyChanged(nameof(HasPendingApproval));
        };

    // Sets the sidebar badge count from the background update checker (AC-76); marshaled to the UI thread since the checker runs off it, or set directly when already on it.
    public void SetUpdateBadgeCount(int count)
    {
        var clamped = Math.Max(0, count);
        if (Dispatcher.UIThread.CheckAccess())
        {
            UpdateBadgeCount = clamped;
        }
        else
        {
            Dispatcher.UIThread.Post(() => UpdateBadgeCount = clamped);
        }
    }

    // The background checker compares store versions against the on-disk manifest, which does not change until restart,
    // so without this a just-installed update would re-inflate the badge on the next 15-minute pass — a staged update
    // is up to date until the restart applies it (AC-76).
    public bool IsUpdateStaged(string entryId, string latestVersion) =>
        _pendingUpdateVersions.TryGetValue(entryId, out var staged) && !PluginVersion.IsNewer(latestVersion, staged);

    // Whether the batch update can be started — it unpacks into the same folder as every other route, so it is closed while the store is working (AC-456).
    public bool CanUpdateAll => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanUpdateAll))]
    private async Task UpdateAllAsync()
    {
        if (_storeClient is null || _installer is null)
        {
            return;
        }

        // Snapshot before installing: each install triggers a reload that rebuilds AvailablePlugins, which
        // would otherwise mutate the collection we are iterating. CanUpdate rather than the raw UpdateAvailable
        // (AC-181): a batch must not download an update this host cannot run and have the installer refuse it.
        var updates = AvailablePlugins.Where(row => row.CanUpdate).ToList();
        if (updates.Count == 0)
        {
            StatusMessage = "Everything is up to date.";
            return;
        }

        await _InstallExclusivelyAsync(async () =>
        {
            BusyProgressValue = 0;
            BusyProgressIndeterminate = false;
            try
            {
                var updated = 0;
                // Kept rather than only shown as they happen (AC-455): StatusMessage is one line, so each
                // failure was overwritten by the next plugin, then by the catalogue reload, then by a summary
                // telling the operator to read a message that no longer existed anywhere.
                var failed = new List<string>();
                for (var i = 0; i < updates.Count; i++)
                {
                    var row = updates[i];
                    StatusMessage = $"Updating '{row.Name}' ({i + 1} of {updates.Count})…";
                    try
                    {
                        // Isolate each plugin: one failing update must not abort the whole batch.
                        if (await _DownloadAndInstallRowAsync(row))
                        {
                            updated++;
                        }
                        else
                        {
                            failed.Add(row.Name);
                        }
                    }
                    catch (Exception exception)
                    {
                        failed.Add(row.Name);
                        StatusMessage = $"'{row.Name}' failed to update: {exception.Message}";
                    }

                    // Counted whether it worked or not: the bar tracks how far through the batch we are, and a
                    // failed plugin is behind us too. Whether it installed is what `updated` answers.
                    BusyProgressValue = (i + 1) * 100.0 / updates.Count;
                }

                await BrowseStoresAsync();
                StatusMessage = failed.Count == 0
                    ? $"Updated {updated} plugin(s). Restart the cockpit to activate."
                    : $"Updated {updated} of {updates.Count} plugin(s). {string.Join(", ", failed.Select(name => $"'{name}'"))} failed."
                      + (updated > 0 ? " Restart the cockpit to activate the rest." : string.Empty);
                NeedsRestart = updated > 0;
            }
            finally
            {
                // Back to indeterminate here rather than only on the next batch: a single install that follows
                // has no fraction to show, and a bar left at 100% behind its overlay would be showing other
                // work. Inside the scope, so a refused start leaves the running batch's bar alone.
                BusyProgressIndeterminate = true;
                BusyProgressValue = 0;
                OnPropertyChanged(nameof(HasAvailableUpdates));
                OnPropertyChanged(nameof(AvailableUpdateCount));
            }
        });
    }

    // Opens the settings of the installed plugin behind a store row (the card's ⚙). No-op when it isn't installed or has no settings.
    [RelayCommand]
    private async Task OpenStorePluginSettingsAsync(StorePluginRowViewModel row)
    {
        if (_InstalledRowFor(row) is { HasSettings: true } installed)
        {
            await OpenPluginSettingsAsync(installed);
        }
    }

    // Enables or disables the installed plugin behind a store row (the card's power toggle), then refreshes the catalogue so the toggle reflects the new state.
    [RelayCommand(CanExecute = nameof(CanChangePlugins))]
    private async Task ToggleStorePluginAsync(StorePluginRowViewModel row)
    {
        if (_InstalledRowFor(row) is not { } installed)
        {
            return;
        }

        if (installed.CanDisable)
        {
            await DisablePluginAsync(installed);
        }
        else
        {
            await EnablePluginAsync(installed);
        }

        var message = StatusMessage;
        await BrowseStoresAsync();
        StatusMessage = message;
    }

    // Installs a specific advertised version of a plugin (the detail panel's per-version Install), so a newer install can be rolled back to an older one.
    public async Task InstallStoreVersionAsync(StorePluginRowViewModel row, PluginStoreVersion version)
    {
        if (_storeClient is null || _installer is null)
        {
            return;
        }

        await _InstallExclusivelyAsync(async () =>
        {
            await _DownloadAndInstallRowAsync(row, version);
            var message = StatusMessage;
            await BrowseStoresAsync();
            StatusMessage = message;
        });
    }

    private PluginRowViewModel? _InstalledRowFor(StorePluginRowViewModel row) =>
        Plugins.FirstOrDefault(installed => installed.FolderId == PluginFolderName.Normalize(row.Id));

    // Install one store row's version — its advertised latest, or an explicit one for a rollback — through the
    // provisioning service (AC-510[b]), then run the same UI-side aftercare every install path always has: the consent
    // walk for a fresh install, the registration re-pin for a staged update.
    private async Task<bool> _DownloadAndInstallRowAsync(StorePluginRowViewModel row, PluginStoreVersion? explicitVersion = null)
    {
        if ((explicitVersion ?? row.LatestVersionEntry) is not { } version)
        {
            StatusMessage = $"'{row.Name}' has no downloadable version in the store.";
            return false;
        }

        StatusMessage = $"Downloading '{row.Name}' v{version.Version}…";
        var provision = await _provisioningService!.InstallAsync(
            new PluginProvisionRequest(row.Id, row.Name, row.Store, version), AbstractionsContract.Version);

        // Surface an unverified-checksum advisory ahead of the installed message (AC-46): a store that publishes
        // no per-artifact hash still installs, but the operator is told the download could not be verified.
        var installedMessage = provision.Warning is { } warning
            ? $"⚠ {warning} '{row.Name}' installed. Restart the cockpit to activate it."
            : $"'{row.Name}' installed. Restart the cockpit to activate it.";

        // Translated back to the installer's own result shape so the shared aftercare below — consent walk,
        // registration re-pin — stays the one place that logic lives, unchanged by where the bytes came from.
        var installResult = provision.IsSuccess
            ? PluginInstallResult.Success(provision.FolderId!, provision.Sha256, staged: provision.Outcome == PluginProvisionOutcome.Staged)
            : PluginInstallResult.Failure(provision.Error ?? "Install failed.");

        await _AfterInstallAsync(installResult, installedMessage);

        // A staged update is live only after restart, so remember the version it now effectively is, so the
        // store stops offering the same update (and drops it out of the updates list) until the restart.
        if (provision.Outcome == PluginProvisionOutcome.Staged)
        {
            _pendingUpdateVersions[row.Id] = version.Version;
        }

        return installResult.IsSuccess;
    }

    // A fresh install walks a needs-consent plugin into the consent step; an update (staged over an existing install)
    // never re-prompts consent — it re-pins the new bytes' hash and preserves the plugin's enabled state, so after the
    // restart swap it comes back exactly as it was.
    private async Task _AfterInstallAsync(PluginInstallResult result, string installedMessage)
    {
        if (!result.IsSuccess)
        {
            StatusMessage = result.Error ?? "Install failed.";
            return;
        }

        if (result.Staged)
        {
            // An update: the new bytes are live only after the restart, so re-pin their hash now (matching the
            // swap) and keep the current enabled/disabled state. No rediscovery, no consent — the restart
            // applies it cleanly.
            if (_registrationStore is not null && result.FolderId is { } folderId && result.Sha256 is { } newSha256)
            {
                var registrations = await _registrationStore.LoadAllAsync();
                if (registrations.TryGetValue(folderId, out var prior))
                {
                    await _registrationStore.SaveAsync(folderId, new PluginRegistration(Enabled: prior.Enabled, PinnedSha256: newSha256));
                }

                // No registration at all means this is not an update: the operator removed this plugin and has now
                // installed it again, so the folder is still on disk (the removal is applied at the next start) and the
                // installer staged over it (AC-455).
            }

            StatusMessage = installedMessage;
            NeedsRestart = true;
            return;
        }

        // Fresh install: reload and walk a needs-consent plugin straight into the consent step.
        await LoadAsync();
        var installed = Plugins.FirstOrDefault(row => row.FolderId == result.FolderId);
        if (installed is not null && installed.CanEnable)
        {
            // The method, not EnablePluginCommand: the command is closed while the store is working (AC-455)
            // and this runs inside the install that made it so. Routing it through the command would drop the
            // consent step of every fresh install without a word.
            await EnablePluginAsync(installed);
        }
        else
        {
            StatusMessage = installedMessage;
            NeedsRestart = true;
        }
    }

}
