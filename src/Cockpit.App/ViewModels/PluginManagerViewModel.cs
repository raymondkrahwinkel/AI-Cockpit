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
using Cockpit.Plugins.Abstractions;

namespace Cockpit.App.ViewModels;

/// <summary>
/// The "Plugins" Options tab (#14): the installed plugins (install-from-zip, enable with first-load
/// consent, disable, remove) and the plugin stores (add/remove a public-repo store, browse its catalogue,
/// install or update from it). It is a config editor over discovery + the registration store; a store
/// download always goes through the same installer + consent, never bypassing them. Enable/disable/remove
/// and installs take effect on the next restart (a non-collectible plugin cannot load or unload live).
/// </summary>
public partial class PluginManagerViewModel : ViewModelBase
{
    private readonly IPluginRegistrationStore? _registrationStore;
    private readonly IPluginInstaller? _installer;
    private readonly PluginBootstrap? _bootstrap;
    private readonly ISessionDialogService? _dialogService;
    private readonly IPluginStoreConfigStore? _storeConfigStore;
    private readonly IPluginStoreClient? _storeClient;
    private readonly IReadOnlyDictionary<string, PluginSettingsRegistration>? _settingsRegistry;
    private readonly PluginDiagnostics? _diagnostics;
    private readonly IPluginContributionSink? _contributionSink;
    private readonly IAppRestartService? _restartService;
    private readonly IWorkflowTemplateLibrary? _templateLibrary;

    public ObservableCollection<PluginRowViewModel> Plugins { get; } = [];

    public ObservableCollection<PluginStoreConfig> Stores { get; } = [];

    /// <summary>
    /// The configured stores as display rows for the Manage-stores dialog (#62): the same URLs as
    /// <see cref="Stores"/>, each wrapped with a name/icon/plugin-count. Rebuilt from <see cref="Stores"/> on
    /// load, then enriched from each store's <c>index.json</c> on <see cref="BrowseStoresAsync"/>.
    /// </summary>
    public ObservableCollection<PluginStoreInfo> StoreInfos { get; } = [];

    public ObservableCollection<StorePluginRowViewModel> AvailablePlugins { get; } = [];

    /// <summary>
    /// The workflow templates the stores offer (#69) — flows somebody already drew. Browsed with the plugins, from the
    /// same index: a store that publishes both is one store, and asking the operator to visit two places to find out
    /// what it has would be an implementation detail leaking into the app.
    /// </summary>
    public ObservableCollection<StoreTemplateRowViewModel> AvailableTemplates { get; } = [];

    /// <summary>Whether any store offers a template at all — no templates, no section.</summary>
    public bool HasAvailableTemplates => AvailableTemplates.Count > 0;

    // Plugins updated this session, keyed by plugin id → the version just staged. An update is only swapped live
    // on restart, so the live manifest still reports the old version; treating the staged version as installed
    // keeps a just-updated plugin from lingering in "Available updates" until the restart.
    // Concurrent because the background PluginUpdateChecker reads it (via IsUpdateStaged) off the UI thread while the
    // install commands mutate it on the UI thread (AC-76).
    private readonly ConcurrentDictionary<string, string> _pendingUpdateVersions = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// How many plugin updates the background checker found waiting (AC-76) — bound by the sidebar "Plugin store"
    /// button's badge, so an update is a persistent indicator in the main window rather than only a transient toast.
    /// Fed by <see cref="SetUpdateBadgeCount"/> from the checker, and counted down as the operator stages each update.
    /// </summary>
    [ObservableProperty]
    private int _updateBadgeCount;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private bool _hasPlugins;

    [ObservableProperty]
    private string _newStoreUrl = string.Empty;

    /// <summary>Whether the add-store form is set to a local folder rather than a remote URL (AC-7) — flips which fields the Manage-stores dialog shows.</summary>
    [ObservableProperty]
    private bool _newStoreIsLocal;

    /// <summary>An optional bearer token for a private remote store (AC-7) — sent as an Authorization header, and encrypted at rest when secret protection is on.</summary>
    [ObservableProperty]
    private string _newStoreToken = string.Empty;

    /// <summary>The chosen local store folder (AC-7), set by the folder picker.</summary>
    [ObservableProperty]
    private string _newStoreFolder = string.Empty;

    [ObservableProperty]
    private bool _isBusy;

    /// <summary>
    /// True once an install/enable/disable/remove has actually changed plugin state this session (#53) — the
    /// manager shows a "Restart now" button once this flips, instead of the operator having to remember to
    /// close and relaunch the app by hand. Sticky for the session: it never resets to false, since an
    /// earlier change still needs that restart even after a later one.
    /// </summary>
    [ObservableProperty]
    private bool _needsRestart;

    /// <summary>Whether a "Restart now" affordance can do anything — false in the design-time/no-op constructor, where there is no real app to restart.</summary>
    public bool CanRestart => _restartService is not null;

    /// <summary>Design-time constructor for the previewer.</summary>
    public PluginManagerViewModel()
    {
        _WatchAvailablePluginsForUpdateGate();
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
        IWorkflowTemplateLibrary? templateLibrary = null)
    {
        _registrationStore = registrationStore;
        _installer = installer;
        _bootstrap = bootstrap;
        _dialogService = dialogService;
        _storeConfigStore = storeConfigStore;
        _storeClient = storeClient;
        _settingsRegistry = settingsRegistry;
        _diagnostics = diagnostics;
        _contributionSink = contributionSink;
        _restartService = restartService;
        _templateLibrary = templateLibrary;
        _WatchAvailablePluginsForUpdateGate();
    }

    // The "Update all" button binds to HasAvailableUpdates/AvailableUpdateCount, which are computed from
    // AvailablePlugins. Browsing the stores rebuilds that collection, so the gate has to be re-raised from the
    // collection itself — notifying only from the install/update paths left the button hidden right after the
    // store loaded, the one moment there is definitely something to update.
    private void _WatchAvailablePluginsForUpdateGate() =>
        AvailablePlugins.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasAvailableUpdates));
            OnPropertyChanged(nameof(AvailableUpdateCount));
        };

    [RelayCommand(CanExecute = nameof(CanRestart))]
    private void RestartNow() => _restartService?.Restart();

    /// <summary>
    /// Opens the plugin store dialog (#62) — now the single home for all plugin control (from the main menu's
    /// "Plugin store" and the plugin-update toast) — over this same manager instance, so installs/updates/
    /// consent/restart stay on the one shared flow. Loads the installed plugins + stores first: unlike the
    /// old Options→Plugins tab, the store can be opened without ever opening Options, and its Installed view
    /// needs <see cref="Plugins"/> populated.
    /// </summary>
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

    /// <summary>Rediscovers the installed plugins and loads the configured stores; called when the Options dialog opens and after every change.</summary>
    public async Task LoadAsync()
    {
        if (_bootstrap is not null)
        {
            var discovered = await _bootstrap.DiscoverAsync(AbstractionsContract.Version);
            var registrations = _registrationStore is null
                ? new Dictionary<string, PluginRegistration>()
                : (await _registrationStore.LoadAllAsync()).ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal);

            Plugins.Clear();
            // The manager lists plugins in the order they appear in the left menu (#72), so moving one up here
            // moves it up there — a list ordered differently from the thing it reorders is a puzzle, not a tool.
            foreach (var plugin in discovered.OrderBy(plugin => registrations.TryGetValue(plugin.FolderId, out var registration) ? registration.MenuOrder : 0))
            {
                Plugins.Add(new PluginRowViewModel(
                    plugin,
                    _settingsRegistry?.ContainsKey(plugin.FolderId) ?? false,
                    _diagnostics?.ForFolder(plugin.FolderId)?.Error,
                    registrations.TryGetValue(plugin.FolderId, out var menuRegistration) && menuRegistration.HiddenInMenu));
            }

            HasPlugins = Plugins.Count > 0;
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

    [RelayCommand]
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

        var result = await _installer.InstallFromZipAsync(zipPath, AbstractionsContract.Version);
        await _AfterInstallAsync(result, "Plugin installed. Restart AI-Cockpit to activate it.");
    }

    [RelayCommand]
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
        StatusMessage = $"'{row.DisplayName}' enabled. Restart AI-Cockpit to load it.";
        NeedsRestart = true;
    }

    /// <summary>The manager's gear is now one of several ways into a plugin's settings, so it opens them the same way the others do.</summary>
    [RelayCommand]
    private async Task OpenPluginSettingsAsync(PluginRowViewModel row)
    {
        if (_contributionSink is null)
        {
            return;
        }

        await _contributionSink.OpenPluginSettingsAsync(row.FolderId);
    }

    /// <summary>Moves the plugin up the left menu (#72) — and up this list, which is ordered the same way.</summary>
    [RelayCommand]
    private Task MovePluginUpAsync(PluginRowViewModel row) => MovePluginToAsync(row, Plugins.IndexOf(row) - 1);

    /// <summary>Moves the plugin down the left menu (#72).</summary>
    [RelayCommand]
    private Task MovePluginDownAsync(PluginRowViewModel row) => MovePluginToAsync(row, Plugins.IndexOf(row) + 1);

    /// <summary>
    /// Moves a plugin to an absolute position in the menu order. The neighbour is the caller's to choose because
    /// it is not always the next one along: the store dialog lists these under category headings, and "up" there
    /// means past the previous plugin <em>under the same heading</em>, which the flat list may have several rows
    /// away. This list stays the menu order either way — that is the one thing being written.
    /// </summary>
    /// <remarks>
    /// Reordering writes every plugin's position, not just the ones that moved: the stored order is only
    /// meaningful as a whole, and a plugin that was never moved has no position of its own yet.
    /// </remarks>
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

    /// <summary>
    /// Hides or shows the plugin's left-menu contributions (#72). The plugin keeps running either way — its
    /// shortcut and command-palette entry still work — so this is emphatically not a quieter way to disable it.
    /// </summary>
    [RelayCommand]
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

    [RelayCommand]
    private async Task DisablePluginAsync(PluginRowViewModel row)
    {
        if (_registrationStore is null)
        {
            return;
        }

        await _registrationStore.SaveAsync(row.FolderId, new PluginRegistration(Enabled: false, PinnedSha256: row.Discovered.Sha256));
        await LoadAsync();
        StatusMessage = $"'{row.DisplayName}' disabled. Restart AI-Cockpit to unload it.";
        NeedsRestart = true;
    }

    [RelayCommand]
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

    /// <summary>Opens a folder picker for a local store (AC-7) and puts the chosen path in the add-store form.</summary>
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

    [RelayCommand]
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

    [RelayCommand]
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

    [RelayCommand]
    private async Task BrowseStoresAsync()
    {
        if (_storeClient is null)
        {
            return;
        }

        IsBusy = true;
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
            foreach (var store in Stores)
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

                    var installedRow = Plugins.FirstOrDefault(row => row.FolderId == PluginFolderName.Normalize(entry.Id));
                    // A staged update reports its new version even before the restart, so it drops out of the
                    // updates list once updated instead of lingering.
                    var installedVersion = _pendingUpdateVersions.TryGetValue(entry.Id, out var pending)
                        ? pending
                        : installedRow?.Discovered.Manifest.Version;
                    AvailablePlugins.Add(new StorePluginRowViewModel(
                        entry,
                        store,
                        installedVersion,
                        isEnabled: installedRow?.CanDisable ?? false,
                        hasSettings: installedRow?.HasSettings ?? false));
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

            // Reconcile the sidebar badge to the just-browsed truth (AC-76): browsing (opening the store, or the
            // refresh after an install/update/rollback) recomputes the real available-update count — staged updates
            // already excluded — so the badge counts down on a consumed update and up on a rollback, without an
            // ad-hoc per-install decrement that could not tell a fresh install or a rollback apart (review).
            UpdateBadgeCount = AvailableUpdateCount;
        }
        finally
        {
            IsBusy = false;
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

    /// <summary>
    /// Installs a workflow template (#69): fetches its flow, checks it against the store's checksum, and writes it into
    /// the library the editor's picker reads. Nothing is loaded and no code runs — a template is a flow as text, and it
    /// arrives switched off, for the operator to read before arming it.
    /// </summary>
    [RelayCommand]
    private async Task InstallTemplateAsync(StoreTemplateRowViewModel row)
    {
        if (_storeClient is null || _templateLibrary is null)
        {
            return;
        }

        IsBusy = true;
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
            var installedMessage = $"'{row.Name}' installed. Restart AI-Cockpit and it is in the flow editor's templates.";
            StatusMessage = download.Warning is { } warning ? $"⚠ {warning} {installedMessage}" : installedMessage;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Takes an installed template out of the library. The flows already made from it are yours and stay.</summary>
    [RelayCommand]
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

    [RelayCommand]
    private async Task InstallFromStoreAsync(StorePluginRowViewModel row)
    {
        if (_storeClient is null || _installer is null)
        {
            return;
        }

        IsBusy = true;
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
            IsBusy = false;
            // The catalogue was rebuilt (or cleared) — refresh the "Update all" button's gate and count.
            OnPropertyChanged(nameof(HasAvailableUpdates));
            OnPropertyChanged(nameof(AvailableUpdateCount));
        }
    }

    /// <summary>True when at least one installed plugin has a newer version in a store — gates the "Update all" button.</summary>
    public bool HasAvailableUpdates => AvailablePlugins.Any(row => row.UpdateAvailable);

    /// <summary>How many installed plugins have a newer version available — shown on the "Update all" button.</summary>
    public int AvailableUpdateCount => AvailablePlugins.Count(row => row.UpdateAvailable);

    /// <summary>Whether the sidebar "Plugin store" badge shows — true while the background checker's last count found updates (AC-76).</summary>
    public bool HasUpdateBadge => UpdateBadgeCount > 0;

    partial void OnUpdateBadgeCountChanged(int value) => OnPropertyChanged(nameof(HasUpdateBadge));

    /// <summary>Sets the sidebar badge count from the background update checker (AC-76); marshaled to the UI thread since the checker runs off it, or set directly when already on it.</summary>
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

    /// <summary>
    /// Whether an update to <paramref name="latestVersion"/> for the plugin id <paramref name="entryId"/> has already
    /// been staged this session (AC-76). The background checker compares store versions against the on-disk manifest,
    /// which does not change until restart, so without this a just-installed update would re-inflate the badge on the
    /// next 15-minute pass — a staged update is up to date until the restart applies it.
    /// </summary>
    public bool IsUpdateStaged(string entryId, string latestVersion) =>
        _pendingUpdateVersions.TryGetValue(entryId, out var staged) && !PluginVersion.IsNewer(latestVersion, staged);

    [RelayCommand]
    private async Task UpdateAllAsync()
    {
        if (_storeClient is null || _installer is null)
        {
            return;
        }

        // Snapshot before installing: each install triggers a reload that rebuilds AvailablePlugins, which
        // would otherwise mutate the collection we are iterating.
        var updates = AvailablePlugins.Where(row => row.UpdateAvailable).ToList();
        if (updates.Count == 0)
        {
            StatusMessage = "Everything is up to date.";
            return;
        }

        IsBusy = true;
        try
        {
            var updated = 0;
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
                }
                catch (Exception exception)
                {
                    StatusMessage = $"'{row.Name}' failed to update: {exception.Message}";
                }
            }

            await BrowseStoresAsync();
            StatusMessage = updated == updates.Count
                ? $"Updated {updated} plugin(s). Restart AI-Cockpit to activate."
                : $"Updated {updated} of {updates.Count} plugin(s); the rest failed — see the message above. Restart to activate.";
            NeedsRestart = updated > 0;
        }
        finally
        {
            IsBusy = false;
            OnPropertyChanged(nameof(HasAvailableUpdates));
            OnPropertyChanged(nameof(AvailableUpdateCount));
        }
    }

    /// <summary>Opens the settings of the installed plugin behind a store row (the card's ⚙). No-op when it isn't installed or has no settings.</summary>
    [RelayCommand]
    private async Task OpenStorePluginSettingsAsync(StorePluginRowViewModel row)
    {
        if (_InstalledRowFor(row) is { HasSettings: true } installed)
        {
            await OpenPluginSettingsAsync(installed);
        }
    }

    /// <summary>Enables or disables the installed plugin behind a store row (the card's power toggle), then refreshes the catalogue so the toggle reflects the new state.</summary>
    [RelayCommand]
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

    /// <summary>Installs a specific advertised version of a plugin (the detail panel's per-version Install), so a newer install can be rolled back to an older one.</summary>
    public async Task InstallStoreVersionAsync(StorePluginRowViewModel row, PluginStoreVersion version)
    {
        if (_storeClient is null || _installer is null)
        {
            return;
        }

        IsBusy = true;
        try
        {
            await _DownloadAndInstallRowAsync(row, version);
            var message = StatusMessage;
            await BrowseStoresAsync();
            StatusMessage = message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private PluginRowViewModel? _InstalledRowFor(StorePluginRowViewModel row) =>
        Plugins.FirstOrDefault(installed => installed.FolderId == PluginFolderName.Normalize(row.Id));

    // Download + install one store row's version — its advertised latest, or an explicit one for a rollback.
    // Reports through StatusMessage. No IsBusy/browse of its own so it composes into the single-row install, the
    // batch "Update all" and the per-version install. Returns whether the install succeeded.
    private async Task<bool> _DownloadAndInstallRowAsync(StorePluginRowViewModel row, PluginStoreVersion? explicitVersion = null)
    {
        if ((explicitVersion ?? row.LatestVersionEntry) is not { } version)
        {
            StatusMessage = $"'{row.Name}' has no downloadable version in the store.";
            return false;
        }

        StatusMessage = $"Downloading '{row.Name}' v{version.Version}…";
        var download = await _storeClient!.DownloadZipAsync(row.Store, version.Path, version.Sha256);
        if (!download.IsSuccess || download.ZipPath is null)
        {
            StatusMessage = download.Error ?? "Download failed.";
            return false;
        }

        try
        {
            // Surface an unverified-checksum advisory ahead of the installed message (AC-46): a store that publishes
            // no per-artifact hash still installs, but the operator is told the download could not be verified.
            var installedMessage = download.Warning is { } warning
                ? $"⚠ {warning} '{row.Name}' installed. Restart AI-Cockpit to activate it."
                : $"'{row.Name}' installed. Restart AI-Cockpit to activate it.";

            var result = await _installer!.InstallFromZipAsync(download.ZipPath, AbstractionsContract.Version);
            await _AfterInstallAsync(result, installedMessage);

            // A staged update is live only after restart, so remember the version it now effectively is, so the
            // store stops offering the same update (and drops it out of the updates list) until the restart.
            if (result.IsSuccess && result.Staged)
            {
                _pendingUpdateVersions[row.Id] = version.Version;
            }

            return result.IsSuccess;
        }
        finally
        {
            _TryDelete(download.ZipPath);
        }
    }

    // Shared tail of every install path. A fresh install walks a needs-consent plugin into the consent step;
    // an update (staged over an existing install) never re-prompts consent — it re-pins the new bytes' hash and
    // preserves the plugin's enabled state, so after the restart swap it comes back exactly as it was. That is
    // also what keeps a batch "Update all" from popping a consent modal per plugin.
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
                var wasEnabled = registrations.TryGetValue(folderId, out var prior) && prior.Enabled;
                await _registrationStore.SaveAsync(folderId, new PluginRegistration(Enabled: wasEnabled, PinnedSha256: newSha256));
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
            await EnablePluginAsync(installed);
        }
        else
        {
            StatusMessage = installedMessage;
            NeedsRestart = true;
        }
    }

    private static void _TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
            // A leftover temp download is harmless; the OS temp cleaner reclaims it.
        }
    }
}
