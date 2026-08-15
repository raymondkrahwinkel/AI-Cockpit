using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cockpit.Core.Abstractions.Plugins;
using Cockpit.Core.Plugins;
using Cockpit.Plugins.Abstractions;

namespace Cockpit.App.ViewModels.Onboarding;

// The first-run wizard's work-kind step (AC-511): pick a kind of work, see the plugins it pre-ticks, change any
// tick by hand, confirm once. The batch replaces one consent dialog per plugin — every row still carries what its
// own dialog would have said — and nothing installs until `ConfirmCommand` runs; Skip/Next never reach it.
public sealed partial class WorkKindStepViewModel : ObservableObject
{
    private readonly IPluginStoreConfigStore? _storeConfigStore;
    private readonly IPluginStoreClient? _storeClient;
    private readonly IPluginProvisioningService? _provisioning;
    private readonly IPluginRegistrationStore? _registrationStore;

    public WorkKindStepViewModel(
        IPluginStoreConfigStore storeConfigStore,
        IPluginStoreClient storeClient,
        IPluginProvisioningService provisioning,
        IPluginRegistrationStore registrationStore)
    {
        _storeConfigStore = storeConfigStore;
        _storeClient = storeClient;
        _provisioning = provisioning;
        _registrationStore = registrationStore;
    }

    // Design-time/preview constructor: rows handed in, no store to load them from and nothing to install with.
    internal WorkKindStepViewModel(IEnumerable<WorkKindPluginRowViewModel> plugins)
    {
        foreach (var plugin in plugins)
        {
            Plugins.Add(plugin);
        }

        HasRecommendations = true;
        IsLoading = false;
        _WatchSelection();
    }

    public IReadOnlyList<PluginWorkKindOption> WorkKinds => PluginWorkKinds.All;

    public ObservableCollection<WorkKindPluginRowViewModel> Plugins { get; } = [];

    // Whether any store said which work kind its plugins are for. An index published before the field exists says
    // nothing, and then the chooser has nothing to offer — the list still works, ticked by hand.
    [ObservableProperty]
    private bool _hasRecommendations;

    [ObservableProperty]
    private bool _isLoading = true;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConfirmCommand))]
    private bool _isInstalling;

    // What the last batch did, per the provisioning service's own summary — empty until one has run.
    [ObservableProperty]
    private string? _summary;

    [ObservableProperty]
    private string? _loadError;

    private PluginWorkKindOption? _selectedWorkKind;

    // The chosen work kind. Setting it re-ticks the list from scratch: it is the answer to "what do you do", so a
    // second answer replaces the first rather than adding to it. Every tick is still the operator's to change.
    public PluginWorkKindOption? SelectedWorkKind
    {
        get => _selectedWorkKind;
        set
        {
            if (!SetProperty(ref _selectedWorkKind, value))
            {
                return;
            }

            _ApplyRecommendation();
        }
    }

    public int SelectedCount => Plugins.Count(plugin => plugin.IsSelected);

    public string ConfirmLabel => SelectedCount switch
    {
        0 => "Install",
        1 => "Install 1 plugin",
        _ => $"Install {SelectedCount} plugins",
    };

    // Reads every configured store's catalogue and turns it into rows. Called by the view when it appears.
    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        if (_storeConfigStore is null || _storeClient is null)
        {
            return;
        }

        IsLoading = true;
        LoadError = null;
        Plugins.Clear();

        try
        {
            var failures = new List<string>();
            foreach (var store in await _storeConfigStore.LoadAsync(cancellationToken))
            {
                var fetched = await _storeClient.FetchIndexAsync(store, cancellationToken);
                if (fetched.Index is null)
                {
                    failures.Add(fetched.Error ?? $"'{store.Location}' could not be read.");

                    continue;
                }

                foreach (var entry in fetched.Index.Plugins)
                {
                    // Providers are chosen in the previous wizard step (AC-510[b]); showing them again here is
                    // noise, not a second chance to pick.
                    if (string.Equals(entry.Category, PluginStoreEntry.ProviderCategory, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    // A store may advertise a plugin whose only versions this build cannot run; the row would then
                    // promise something the batch refuses, so it is the version list that decides there is a row.
                    var version = entry.Versions.FirstOrDefault(candidate => candidate.Version == entry.LatestVersion)
                        ?? entry.Versions.LastOrDefault();
                    if (version is null)
                    {
                        continue;
                    }

                    Plugins.Add(new WorkKindPluginRowViewModel(entry, store, version));
                }
            }

            HasRecommendations = Plugins.Any(plugin => plugin.Audience.Count > 0);
            LoadError = failures.Count > 0 ? string.Join(" ", failures) : null;
            _WatchSelection();
        }
        catch (Exception exception)
        {
            // The view starts this without awaiting it, so an unhandled fault here would be an unobserved task and
            // a step that sits on "Loading…" for ever. A store that cannot be read is a screen with no suggestions,
            // not a broken wizard.
            LoadError = exception.Message;
        }
        finally
        {
            IsLoading = false;
        }
    }

    // Installs everything ticked, in one pass: the provisioning service isolates each plugin, so one failure
    // leaves the rest installed, and what lands is enabled with the checksum the install pinned.
    [RelayCommand(CanExecute = nameof(CanConfirm))]
    private async Task ConfirmAsync(CancellationToken cancellationToken)
    {
        if (_provisioning is null || _registrationStore is null)
        {
            return;
        }

        var requests = Plugins
            .Where(plugin => plugin is { IsSelected: true, Request: not null })
            .Select(plugin => plugin.Request!)
            .ToList();
        if (requests.Count == 0)
        {
            return;
        }

        IsInstalling = true;

        try
        {
            var batch = await _provisioning.InstallManyAsync(requests, AbstractionsContract.Version, cancellationToken: cancellationToken);

            foreach (var result in batch.Results)
            {
                if (result is { IsSuccess: true, FolderId: { } folderId, Sha256: { } sha256 })
                {
                    // This is what the batch consent bought: the checksum the operator just approved is pinned
                    // here instead of in a dialog per plugin.
                    await _registrationStore.SaveAsync(folderId, new PluginRegistration(Enabled: true, sha256), cancellationToken);
                }
            }

            Summary = _Describe(batch);
        }
        finally
        {
            IsInstalling = false;
        }
    }

    private bool CanConfirm() => !IsInstalling && SelectedCount > 0;

    private static string _Describe(PluginProvisionBatchResult batch)
    {
        var failed = batch.Results.Where(result => !result.IsSuccess).ToList();
        var landed = batch.SucceededCount == 0
            ? "Nothing was installed."
            : $"{batch.SucceededCount} of {batch.Results.Count} installed — restart the cockpit to load them.";

        return failed.Count == 0
            ? landed
            : $"{landed} Not installed: {string.Join("; ", failed.Select(result => $"{result.Name} ({result.Error})"))}";
    }

    private void _ApplyRecommendation()
    {
        foreach (var plugin in Plugins)
        {
            // Generic (no audience) ticks for whichever work kind is chosen; a tagged plugin ticks only for one
            // of its own kinds.
            plugin.IsSelected = SelectedWorkKind is not null
                && (plugin.Audience.Count == 0
                    || plugin.Audience.Contains(SelectedWorkKind.Key, StringComparer.OrdinalIgnoreCase));
        }
    }

    // Keeps the button's count and enabled state on a hand-ticked box. Each row raises its own change, so the
    // count is derived rather than tracked — a tracked one would drift the first time a row was ticked twice.
    private void _WatchSelection()
    {
        foreach (var plugin in Plugins)
        {
            plugin.PropertyChanged += (_, changed) =>
            {
                if (changed.PropertyName != nameof(WorkKindPluginRowViewModel.IsSelected))
                {
                    return;
                }

                OnPropertyChanged(nameof(SelectedCount));
                OnPropertyChanged(nameof(ConfirmLabel));
                ConfirmCommand.NotifyCanExecuteChanged();
            };
        }
    }
}
