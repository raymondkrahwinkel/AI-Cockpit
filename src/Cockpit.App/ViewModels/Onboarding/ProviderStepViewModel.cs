using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cockpit.App.Services;
using Cockpit.Core;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Plugins;
using Cockpit.Core.Plugins;
using Cockpit.Core.Profiles;
using Cockpit.Plugins.Abstractions;

namespace Cockpit.App.ViewModels.Onboarding;

// Drives the first-run wizard's provider step (AC-510[b]): what the operator already has (observed where possible —
// criterion 1), the four ways an install can land (criterion 2), the offline path (criterion 3) and the fact that
// Skip/Next never install anything on their own (criterion 4).
public sealed partial class ProviderStepViewModel : ObservableObject
{
    private readonly IPluginStoreConfigStore? _storeConfigStore;
    private readonly IPluginStoreClient? _storeClient;
    private readonly IPluginProvisioningService? _provisioningService;
    private readonly IPluginAdministration? _plugins;
    private readonly ISessionDialogService? _dialogs;
    private readonly IFirstRunWizardStateStore? _wizardState;
    private readonly IAppRestartService? _restart;
    private readonly Func<string>? _startChoiceId;
    private FirstRunWizardResumeState? _resumeState;

    public ObservableCollection<ProviderPickerRowViewModel> Providers { get; } = [];

    public IEnumerable<ProviderPickerRowViewModel> FoundProviders =>
        Providers.Where(row => row.Detection == ProviderDetectionState.Found);

    public IEnumerable<ProviderPickerRowViewModel> OtherProviders =>
        Providers.Where(row => row.Detection != ProviderDetectionState.Found);

    // Always shown, not only while offline: a fair alternative on any network (AC-510[b]).
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

    internal bool RestartRequested { get; private set; }

    // What the assistant step lists as chosen here: ticked, or installed in this wizard.
    public IReadOnlyList<string> ChosenProviderLines =>
        [.. Providers.Where(row => row.IsSelected || row.WasInstalled).Select(row => row.ChosenLine)];

    // Next installs nothing (criterion 4), so a provider that looks ticked and is not installed would be a surprise two steps on, a suggestion's tick included.
    public string? NextBlockedReason =>
        Providers.Any(row => row.IsSelected && !row.WasInstalled && !row.Row.IsInstalled) ? "Press Install and restart for what you ticked, or untick it." : null;

    public event EventHandler? NextBlockedChanged;

    public bool HasProviders => Providers.Count > 0;

    public bool HasFoundProviders => FoundProviders.Any();

    public bool HasOtherProviders => OtherProviders.Any();

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
        IPluginAdministration plugins,
        ISessionDialogService? dialogs = null,
        IFirstRunWizardStateStore? wizardState = null,
        IAppRestartService? restart = null,
        Func<string>? startChoiceId = null)
    {
        _storeConfigStore = storeConfigStore;
        _storeClient = storeClient;
        _provisioningService = provisioningService;
        _plugins = plugins;
        _dialogs = dialogs;
        _wizardState = wizardState;
        _restart = restart;
        _startChoiceId = startChoiceId;

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
            OnPropertyChanged(nameof(FoundProviders));
            OnPropertyChanged(nameof(OtherProviders));
            OnPropertyChanged(nameof(HasFoundProviders));
            OnPropertyChanged(nameof(HasOtherProviders));
            InstallSelectedCommand.NotifyCanExecuteChanged();

            // Each row's own checkbox can flip CanInstallSelected (nothing checked → something checked), so the
            // command's gate has to hear about it too — not just about rows being added or removed.
            if (e.NewItems is not null)
            {
                foreach (ProviderPickerRowViewModel row in e.NewItems)
                {
                    if (_resumeState?.Providers.Any(provider => provider.Id == row.Row.Id) == true)
                    {
                        row.IsSelected = true;
                    }

                    row.PropertyChanged += (_, args) =>
                    {
                        if (args.PropertyName == nameof(ProviderPickerRowViewModel.IsSelected))
                        {
                            InstallSelectedCommand.NotifyCanExecuteChanged();
                        }

                        if (args.PropertyName is nameof(ProviderPickerRowViewModel.IsSelected) or nameof(ProviderPickerRowViewModel.WasInstalled))
                        {
                            NextBlockedChanged?.Invoke(this, EventArgs.Empty);
                        }
                    };
                }
            }
        };

    internal void Restore(FirstRunWizardResumeState state)
    {
        _resumeState = state;
        foreach (var row in Providers)
        {
            if (state.Providers.Any(provider => provider.Id == row.Row.Id))
            {
                row.IsSelected = true;
            }
        }
    }

    // Version each load so the constructor's fire-and-forget pass cannot overwrite a newer caller's results
    // (AC-510[b]).
    private int _loadGeneration;

    // Fetches every configured store's index, keeps only the AI-provider entries (AC-510[b] criterion 5:
    // `PluginStoreEntry.ProviderCategory`), and marks each one found/not-found/cloud.
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
                var detection = ProviderHostExecutables.For(entry.Id) is { } executable
                    ? (HostExecutableProbe.Resolve(executable) is not null ? ProviderDetectionState.Found : ProviderDetectionState.NotFound)
                    : ProviderDetectionState.NotApplicable;

                // Appended in the catalogue's own order: what is found stands apart from the rest because the view
                // draws `FoundProviders` and `OtherProviders` as two lists, not because this collection is sorted.
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
        if (_plugins is null)
        {
            return map;
        }

        foreach (var plugin in await _plugins.GetInstalledAsync().ConfigureAwait(true))
        {
            map[plugin.Discovered.FolderId] = plugin.Discovered.Manifest.Version;
        }

        return map;
    }

    partial void OnIsInstallingChanged(bool value) => InstallSelectedCommand.NotifyCanExecuteChanged();

    partial void OnIsLoadingChanged(bool value) => InstallSelectedCommand.NotifyCanExecuteChanged();

    // Never runs on its own: Skip and Next (the wizard shell) never call this, so a pre-filled selection nobody acted
    // on installs nothing (criterion 4) (AC-510[b]).
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

            if (requests.Count != selectedRows.Count)
            {
                SummaryMessage = "One of your choices could not be added. Check the message above and try again.";
                return;
            }

            var batch = await _provisioningService.InstallManyAsync(requests, AbstractionsContract.Version).ConfigureAwait(true);

            // InstallManyAsync returns one result per request, in the same order it received them (isolated per
            // plugin, never reordered) — see PluginProvisioningService.InstallManyAsync.
            for (var index = 0; index < batch.Results.Count && index < requestRows.Count; index++)
            {
                requestRows[index].ApplyOutcome(batch.Results[index]);
            }

            if (batch.SucceededCount != batch.Results.Count)
            {
                SummaryMessage = batch.SucceededCount == 0
                    ? "Nothing was added. Check the messages above and try again."
                    : $"Some choices were not added: {string.Join(", ", batch.FailedNames)}. Check the messages above and try again.";
                return;
            }

            if (_plugins is null || _dialogs is null || _wizardState is null || _restart is null)
            {
                SummaryMessage = "Cockpit couldn't finish setting this up. Try again.";
                return;
            }

            var installed = await _plugins.GetInstalledAsync().ConfigureAwait(true);
            for (var index = 0; index < batch.Results.Count; index++)
            {
                var result = batch.Results[index];
                var plugin = installed.FirstOrDefault(candidate => candidate.Discovered.FolderId == result.FolderId);
                if (plugin is null)
                {
                    SummaryMessage = $"{result.Name} was added, but Cockpit couldn't prepare it. Try again.";
                    return;
                }

                var needsConsent = plugin.Discovered.Decision is PluginLoadDecision.NeedsConsent or PluginLoadDecision.Disabled;
                var pinnedSha256 = result.Sha256 ?? plugin.Discovered.Sha256;
                if (needsConsent)
                {
                    var consent = result.Outcome == PluginProvisionOutcome.Staged && result.Manifest is { } manifest
                        ? new Cockpit.App.ViewModels.PluginConsentInfo(
                            manifest.Name,
                            manifest.Version,
                            manifest.Author,
                            plugin.Discovered.FolderPath,
                            pinnedSha256)
                        : new Cockpit.App.ViewModels.PluginRowViewModel(plugin).ToConsentInfo();
                    if (!await _dialogs.ShowPluginConsentAsync(consent).ConfigureAwait(true))
                    {
                        SummaryMessage = $"{result.Name} was not enabled. Review it and try again when you're ready.";
                        return;
                    }

                    await _plugins.SetEnabledAsync(
                        plugin.Discovered.FolderId,
                        enabled: true,
                        pinnedSha256).ConfigureAwait(true);
                }
                else if (result.Outcome == PluginProvisionOutcome.Staged && result.Sha256 is { } stagedSha256)
                {
                    await _plugins.SetEnabledAsync(
                        plugin.Discovered.FolderId,
                        plugin.Registration?.Enabled ?? false,
                        stagedSha256).ConfigureAwait(true);
                }

                requestRows[index].MarkReady();
            }

            var choices = selectedRows.Select((row, index) =>
            {
                var manifest = batch.Results[index].Manifest
                    ?? installed.First(candidate => candidate.Discovered.FolderId == row.Row.Id).Discovered.Manifest;
                return new FirstRunProviderChoice(
                    row.Row.Id,
                    row.Row.Name,
                    [.. manifest.SessionProviders.Select(provider => new FirstRunSessionProviderChoice(provider.Id, provider.Name))]);
            }).ToList();
            await _wizardState.SaveResumeStateAsync(new FirstRunWizardResumeState(
                StartAtOrder: 25,
                StartChoiceId: _startChoiceId?.Invoke() ?? "start-fresh",
                Providers: choices,
                AssistantProviderId: null,
                AssistantProviderName: null)).ConfigureAwait(true);

            SummaryMessage = "Ready. Restarting Cockpit…";
            RestartRequested = true;
            _restart.Restart();
        }
        catch (Exception)
        {
            SummaryMessage = "Cockpit couldn't finish setting this up. Check the messages above and try again.";
        }
        finally
        {
            IsInstalling = false;
        }
    }
}
