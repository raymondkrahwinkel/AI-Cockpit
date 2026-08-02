using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cockpit.Core;
using Cockpit.Core.Abstractions.Plugins;
using Cockpit.Core.Plugins;
using Cockpit.Core.Profiles;
using Cockpit.Infrastructure.Plugins;
using Cockpit.Plugins.Abstractions;

namespace Cockpit.App.ViewModels.Onboarding;

// Drives the first-run wizard's provider step (AC-510[b]): what the operator already has (observed where
// possible — criterion 1), the four ways an install can land (criterion 2), the offline path (criterion 3) and
// the fact that Skip/Next never install anything on their own (criterion 4). Installs go through
// `IPluginProvisioningService` — the same DI-registered instance the plugin store dialog now
// receives (`Cockpit.App.ViewModels.PluginManagerViewModel`'s own constructor), so there is exactly
// one install path, not a second one for onboarding.
public sealed partial class ProviderStepViewModel : ObservableObject
{
    private readonly IPluginStoreConfigStore? _storeConfigStore;
    private readonly IPluginStoreClient? _storeClient;
    private readonly IPluginProvisioningService? _provisioningService;
    private readonly PluginBootstrap? _bootstrap;

    public ObservableCollection<ProviderPickerRowViewModel> Providers { get; } = [];

    // The two providers that need neither internet nor an install (AC-510[b] criterion 3), joined for direct
    // display — core, not a plugin (see `SessionProviderCatalog`), so this is always accurate regardless of
    // what the store says or whether it could even be reached. Always shown, not only while offline: a fair
    // alternative on any network.
    public string LocalProvidersText { get; } = string.Join(" and ",
        SessionProviderCatalog.Providers
            .Where(option => option.Value is SessionProvider.Ollama or SessionProvider.LmStudio)
            .Select(option => option.Label));

    [ObservableProperty]
    private bool _isLoading = true;

    // True once every configured store failed to fetch (AC-510[b] criterion 3) — a plain statement of fact, not an error: `LocalProviderNames` still work.
    [ObservableProperty]
    private bool _isOffline;

    [ObservableProperty]
    private string _offlineMessage = string.Empty;

    [ObservableProperty]
    private bool _isInstalling;

    // The batch's own summary line once `InstallSelectedCommand` finishes — a half-succeeded batch names it plainly (criterion 2) instead of leaving the operator to read every row.
    [ObservableProperty]
    private string _summaryMessage = string.Empty;

    public bool HasProviders => Providers.Count > 0;

    public bool CanInstallSelected => !IsInstalling && !IsLoading && Providers.Any(row => row.IsSelected);

    // Design-time/previewer constructor: an inert catalogue with nothing to fetch. Also what the Screenshotter
    // stages scenes from, seeding `Providers` directly (the plugin store dialog's own pattern) — so
    // the checkbox-to-command wiring below has to be live here too, not only when the real DI constructor runs.
    public ProviderStepViewModel()
    {
        IsLoading = false;
        _WireProvidersToInstallGate();
    }

    public ProviderStepViewModel(
        IPluginStoreConfigStore storeConfigStore,
        IPluginStoreClient storeClient,
        IPluginProvisioningService provisioningService,
        PluginBootstrap bootstrap)
    {
        _storeConfigStore = storeConfigStore;
        _storeClient = storeClient;
        _provisioningService = provisioningService;
        _bootstrap = bootstrap;

        _WireProvidersToInstallGate();

        // Fire-and-forget, the same pattern CockpitViewModel already uses for its own startup reads (Projects,
        // Worktrees): the step's content is built once, synchronously, when the wizard opens, and there is
        // nothing to await it from — the view simply starts on IsLoading and updates as this lands.
        _ = LoadAsync();
    }

    private void _WireProvidersToInstallGate() =>
        Providers.CollectionChanged += (_, e) =>
        {
            OnPropertyChanged(nameof(HasProviders));
            InstallSelectedCommand.NotifyCanExecuteChanged();

            // Each row's own checkbox can flip CanInstallSelected (nothing checked → something checked), so the
            // command's gate has to hear about it too — not just about rows being added or removed.
            if (e.NewItems is not null)
            {
                foreach (ProviderPickerRowViewModel row in e.NewItems)
                {
                    row.PropertyChanged += (_, args) =>
                    {
                        if (args.PropertyName == nameof(ProviderPickerRowViewModel.IsSelected))
                        {
                            InstallSelectedCommand.NotifyCanExecuteChanged();
                        }
                    };
                }
            }
        };

    // Bumped at the start of every LoadAsync call so a stale, still-in-flight run can tell it has been
    // superseded and stop touching Providers instead of racing a newer run's writes (AC-510[b]: the constructor
    // already fires one fire-and-forget LoadAsync, so a caller — a test, or Back/Next rebuilding the step — that
    // also awaits LoadAsync directly must not end up with two runs both adding rows into the same collection).
    private int _loadGeneration;

    // Fetches every configured store's index, keeps only the AI-provider entries (AC-510[b] criterion 5:
    // `PluginStoreEntry.ProviderCategory`), and marks each one found/not-found/cloud. Offline when
    // every store fails — never when the list of providers merely comes back empty, which is a different, honest
    // state of its own (a store that carries no providers today). The latest call always wins over an older one
    // still in flight.
    public async Task LoadAsync()
    {
        if (_storeConfigStore is null || _storeClient is null)
        {
            IsLoading = false;
            return;
        }

        var generation = ++_loadGeneration;

        IsLoading = true;
        IsOffline = false;
        OfflineMessage = string.Empty;
        Providers.Clear();

        var installedVersions = await _LoadInstalledVersionsAsync().ConfigureAwait(true);
        if (generation != _loadGeneration)
        {
            return;
        }

        var stores = await _storeConfigStore.LoadAsync().ConfigureAwait(true);
        if (generation != _loadGeneration)
        {
            return;
        }

        var reachedAny = false;
        string? firstError = null;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var store in stores)
        {
            var fetch = await _storeClient.FetchIndexAsync(store).ConfigureAwait(true);
            if (generation != _loadGeneration)
            {
                return;
            }

            if (!fetch.IsSuccess || fetch.Index is null)
            {
                firstError ??= fetch.Error;
                continue;
            }

            reachedAny = true;
            foreach (var entry in fetch.Index.Plugins.Where(candidate => candidate.Category == PluginStoreEntry.ProviderCategory))
            {
                // First store wins for a given id, same rule the plugin store catalogue follows.
                if (!seen.Add(entry.Id))
                {
                    continue;
                }

                installedVersions.TryGetValue(PluginFolderName.Normalize(entry.Id), out var installedVersion);
                var row = new StorePluginRowViewModel(entry, store, installedVersion);
                var detection = ProviderHostExecutables.CommandFor(entry.Id) is { } command
                    ? (HostExecutableProbe.Resolve(command) is not null ? ProviderDetectionState.Found : ProviderDetectionState.NotFound)
                    : ProviderDetectionState.NotApplicable;

                Providers.Add(new ProviderPickerRowViewModel(row, detection));
            }
        }

        // Offline is every store unreachable, not "no providers were listed" — those are different, both honest,
        // facts and criterion 3 is only about the first one.
        IsOffline = stores.Count > 0 && !reachedAny;
        OfflineMessage = IsOffline
            ? firstError ?? "None of the configured plugin stores could be reached."
            : string.Empty;
        IsLoading = false;
    }

    private async Task<Dictionary<string, string>> _LoadInstalledVersionsAsync()
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        if (_bootstrap is null)
        {
            return map;
        }

        foreach (var plugin in await _bootstrap.DiscoverAsync(AbstractionsContract.Version).ConfigureAwait(true))
        {
            map[plugin.FolderId] = plugin.Manifest.Version;
        }

        return map;
    }

    partial void OnIsInstallingChanged(bool value) => InstallSelectedCommand.NotifyCanExecuteChanged();

    partial void OnIsLoadingChanged(bool value) => InstallSelectedCommand.NotifyCanExecuteChanged();

    // Installs every checked row through the provisioning seam's batch call — one plugin failing isolated from
    // the rest (AC-510[b] criterion 2's "half-succeeded" shape), each row's own outcome applied once the batch
    // returns. Never runs on its own: Skip and Next (the wizard shell) never call this, so a pre-filled
    // selection nobody acted on installs nothing (criterion 4).
    [RelayCommand(CanExecute = nameof(CanInstallSelected))]
    private async Task InstallSelectedAsync()
    {
        if (_provisioningService is null)
        {
            return;
        }

        var selectedRows = Providers.Where(row => row.IsSelected).ToList();
        if (selectedRows.Count == 0)
        {
            return;
        }

        IsInstalling = true;
        SummaryMessage = string.Empty;
        try
        {
            var requests = new List<PluginProvisionRequest>();
            var requestRows = new List<ProviderPickerRowViewModel>();
            foreach (var row in selectedRows)
            {
                if (row.Row.LatestVersionEntry is not { } version)
                {
                    row.ApplyOutcome(new PluginProvisionResult(
                        PluginProvisionOutcome.Failed, row.Row.Id, row.Row.Name, "No downloadable version in the store.", Warning: null, FolderId: null, Sha256: null));
                    continue;
                }

                requests.Add(new PluginProvisionRequest(row.Row.Id, row.Row.Name, row.Row.Store, version));
                requestRows.Add(row);
            }

            if (requests.Count == 0)
            {
                return;
            }

            var batch = await _provisioningService.InstallManyAsync(requests, AbstractionsContract.Version).ConfigureAwait(true);

            // InstallManyAsync returns one result per request, in the same order it received them (isolated per
            // plugin, never reordered) — see PluginProvisioningService.InstallManyAsync.
            for (var index = 0; index < batch.Results.Count && index < requestRows.Count; index++)
            {
                requestRows[index].ApplyOutcome(batch.Results[index]);
            }

            SummaryMessage = batch.SucceededCount == batch.Results.Count
                ? $"Installed {batch.SucceededCount} of {batch.Results.Count} provider(s). Open the plugin store afterwards to approve them — nothing runs until you do."
                : batch.SucceededCount == 0
                    ? $"Couldn't install any of the {batch.Results.Count} selected provider(s) — see the reasons below."
                    : $"Installed {batch.SucceededCount} of {batch.Results.Count} provider(s); {string.Join(", ", batch.FailedNames)} didn't make it — see the reasons below.";
        }
        finally
        {
            IsInstalling = false;
        }
    }
}
