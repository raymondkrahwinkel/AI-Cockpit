using System.Collections.ObjectModel;
using Avalonia.Controls;
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
    private readonly IReadOnlyDictionary<string, Func<Control>>? _settingsRegistry;
    private readonly IPluginDialogHost? _dialogHost;
    private readonly PluginDiagnostics? _diagnostics;
    private readonly IPluginContributionSink? _contributionSink;
    private readonly IAppRestartService? _restartService;

    public ObservableCollection<PluginRowViewModel> Plugins { get; } = [];

    public ObservableCollection<string> Stores { get; } = [];

    public ObservableCollection<StorePluginRowViewModel> AvailablePlugins { get; } = [];

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private bool _hasPlugins;

    [ObservableProperty]
    private string _newStoreUrl = string.Empty;

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
    }

    public PluginManagerViewModel(
        IPluginRegistrationStore registrationStore,
        IPluginInstaller installer,
        PluginBootstrap bootstrap,
        ISessionDialogService dialogService,
        IPluginStoreConfigStore storeConfigStore,
        IPluginStoreClient storeClient,
        IReadOnlyDictionary<string, Func<Control>> settingsRegistry,
        IPluginDialogHost dialogHost,
        PluginDiagnostics diagnostics,
        IPluginContributionSink? contributionSink = null,
        IAppRestartService? restartService = null)
    {
        _registrationStore = registrationStore;
        _installer = installer;
        _bootstrap = bootstrap;
        _dialogService = dialogService;
        _storeConfigStore = storeConfigStore;
        _storeClient = storeClient;
        _settingsRegistry = settingsRegistry;
        _dialogHost = dialogHost;
        _diagnostics = diagnostics;
        _contributionSink = contributionSink;
        _restartService = restartService;
    }

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
            Plugins.Clear();
            foreach (var plugin in discovered)
            {
                Plugins.Add(new PluginRowViewModel(
                    plugin,
                    _settingsRegistry?.ContainsKey(plugin.FolderId) ?? false,
                    _diagnostics?.ForFolder(plugin.FolderId)?.Error));
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
        foreach (var store in await _storeConfigStore.LoadAsync())
        {
            Stores.Add(store);
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
        await _AfterInstallAsync(result, "Plugin installed. Restart the cockpit to activate it.");
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
        StatusMessage = $"'{row.DisplayName}' enabled. Restart the cockpit to load it.";
        NeedsRestart = true;
    }

    [RelayCommand]
    private async Task OpenPluginSettingsAsync(PluginRowViewModel row)
    {
        if (_dialogHost is null || _settingsRegistry is null || !_settingsRegistry.TryGetValue(row.FolderId, out var createView))
        {
            return;
        }

        await _dialogHost.ShowSettingsDialogAsync(
            $"{row.DisplayName} settings",
            createView,
            640,
            560,
            onSaved: () => _contributionSink?.NotifySettingsSaved(row.FolderId));
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
        StatusMessage = $"'{row.DisplayName}' disabled. Restart the cockpit to unload it.";
        NeedsRestart = true;
    }

    [RelayCommand]
    private async Task RemovePluginAsync(PluginRowViewModel row)
    {
        if (_registrationStore is null || _installer is null)
        {
            return;
        }

        await _installer.MarkForRemovalAsync(row.FolderId);
        await _registrationStore.RemoveAsync(row.FolderId);
        await LoadAsync();
        StatusMessage = $"'{row.DisplayName}' will be removed on the next restart.";
        NeedsRestart = true;
    }

    [RelayCommand]
    private async Task AddStoreAsync()
    {
        if (_storeConfigStore is null)
        {
            return;
        }

        var url = NewStoreUrl.Trim();
        if (!PluginStoreUrl.TryResolveIndexUrl(url, out _, out var error))
        {
            StatusMessage = error ?? "That store URL is not valid.";
            return;
        }

        await _storeConfigStore.AddAsync(url);
        NewStoreUrl = string.Empty;
        await _LoadStoresAsync();
        StatusMessage = "Store added. Use Browse to see its plugins.";
    }

    [RelayCommand]
    private async Task RemoveStoreAsync(string storeUrl)
    {
        if (_storeConfigStore is null)
        {
            return;
        }

        await _storeConfigStore.RemoveAsync(storeUrl);
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
        try
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var problems = new List<string>();
            foreach (var storeUrl in Stores)
            {
                var fetch = await _storeClient.FetchIndexAsync(storeUrl);
                if (!fetch.IsSuccess || fetch.Index is null || fetch.IndexUrl is null)
                {
                    problems.Add(fetch.Error ?? "unreachable store");
                    continue;
                }

                foreach (var entry in fetch.Index.Plugins)
                {
                    // First store wins for a given id, so a plugin listed in several stores shows once.
                    if (!seen.Add(entry.Id))
                    {
                        continue;
                    }

                    var installedRow = Plugins.FirstOrDefault(row => row.FolderId == PluginFolderName.Normalize(entry.Id));
                    AvailablePlugins.Add(new StorePluginRowViewModel(
                        entry,
                        fetch.IndexUrl,
                        installedRow?.Discovered.Manifest.Version,
                        isEnabled: installedRow?.CanDisable ?? false,
                        hasSettings: installedRow?.HasSettings ?? false));
                }
            }

            StatusMessage = AvailablePlugins.Count == 0
                ? (problems.Count > 0 ? $"No plugins found ({problems[0]})." : "No plugins found in the configured stores.")
                : $"{AvailablePlugins.Count} plugin(s) available." + (problems.Count > 0 ? $" ({problems.Count} store(s) unreachable.)" : string.Empty);
        }
        finally
        {
            IsBusy = false;
        }
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
            foreach (var row in updates)
            {
                StatusMessage = $"Updating '{row.Name}' ({updated + 1} of {updates.Count})…";
                if (await _DownloadAndInstallRowAsync(row))
                {
                    updated++;
                }
            }

            await BrowseStoresAsync();
            StatusMessage = updated == updates.Count
                ? $"Updated {updated} plugin(s). Restart the cockpit to activate."
                : $"Updated {updated} of {updates.Count} plugin(s); the rest failed — see the last error above. Restart to activate.";
            NeedsRestart = updated > 0;
        }
        finally
        {
            IsBusy = false;
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
        var download = await _storeClient!.DownloadZipAsync(row.IndexUrl, version.Path, version.Sha256);
        if (!download.IsSuccess || download.ZipPath is null)
        {
            StatusMessage = download.Error ?? "Download failed.";
            return false;
        }

        try
        {
            var result = await _installer!.InstallFromZipAsync(download.ZipPath, AbstractionsContract.Version);
            await _AfterInstallAsync(result, $"'{row.Name}' installed. Restart the cockpit to activate it.");
            return result.IsSuccess;
        }
        finally
        {
            _TryDelete(download.ZipPath);
        }
    }

    // Shared tail of every install path: on success reload, then walk a freshly installed (needs-consent)
    // plugin straight into the consent step; otherwise report the restart-to-activate note.
    private async Task _AfterInstallAsync(PluginInstallResult result, string installedMessage)
    {
        if (!result.IsSuccess)
        {
            StatusMessage = result.Error ?? "Install failed.";
            return;
        }

        await LoadAsync();
        var installed = Plugins.FirstOrDefault(row => row.FolderId == result.FolderId);
        if (installed is null)
        {
            StatusMessage = installedMessage;
            NeedsRestart = true;
            return;
        }

        // Updating an already-enabled plugin must keep it enabled. The install writes new bytes, so the pinned
        // SHA no longer matches and the plugin would drop to "needs consent" — which reads as disabled after a
        // restart (the bug behind "update all disabled everything"). Since the operator explicitly updated it
        // from the configured, sha256-verified store, re-pin the new hash and keep it enabled instead of
        // re-prompting consent. A fresh install (no prior enabled registration) still walks into consent.
        if (_registrationStore is not null)
        {
            var registrations = await _registrationStore.LoadAllAsync();
            if (registrations.TryGetValue(result.FolderId, out var prior) && prior.Enabled)
            {
                await _registrationStore.SaveAsync(result.FolderId, new PluginRegistration(Enabled: true, PinnedSha256: installed.Discovered.Sha256));
                await LoadAsync();
                StatusMessage = installedMessage;
                NeedsRestart = true;
                return;
            }
        }

        if (installed.CanEnable)
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
