using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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
        IPluginStoreClient storeClient)
    {
        _registrationStore = registrationStore;
        _installer = installer;
        _bootstrap = bootstrap;
        _dialogService = dialogService;
        _storeConfigStore = storeConfigStore;
        _storeClient = storeClient;
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
                Plugins.Add(new PluginRowViewModel(plugin));
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

                    var installedVersion = Plugins
                        .FirstOrDefault(row => row.FolderId == PluginFolderName.Normalize(entry.Id))?
                        .Discovered.Manifest.Version;
                    AvailablePlugins.Add(new StorePluginRowViewModel(entry, fetch.IndexUrl, installedVersion));
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

        if (row.LatestVersionEntry is not { } version)
        {
            StatusMessage = $"'{row.Name}' has no downloadable version in the store.";
            return;
        }

        IsBusy = true;
        try
        {
            StatusMessage = $"Downloading '{row.Name}' v{version.Version}…";
            var download = await _storeClient.DownloadZipAsync(row.IndexUrl, version.Path, version.Sha256);
            if (!download.IsSuccess || download.ZipPath is null)
            {
                StatusMessage = download.Error ?? "Download failed.";
                return;
            }

            try
            {
                var result = await _installer.InstallFromZipAsync(download.ZipPath, AbstractionsContract.Version);
                await _AfterInstallAsync(result, $"'{row.Name}' installed. Restart the cockpit to activate it.");

                // Refresh the catalogue rows to their new installed/up-to-date state, but keep the install
                // (or consent) message the operator just saw rather than the browse summary.
                var installMessage = StatusMessage;
                await BrowseStoresAsync();
                StatusMessage = installMessage;
            }
            finally
            {
                _TryDelete(download.ZipPath);
            }
        }
        finally
        {
            IsBusy = false;
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
        if (installed is not null && installed.CanEnable)
        {
            await EnablePluginAsync(installed);
        }
        else
        {
            StatusMessage = installedMessage;
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
