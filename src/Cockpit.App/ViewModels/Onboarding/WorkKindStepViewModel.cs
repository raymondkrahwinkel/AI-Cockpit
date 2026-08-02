using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cockpit.Core.Abstractions.Plugins;
using Cockpit.Core.Plugins;
using Cockpit.Plugins.Abstractions;

namespace Cockpit.App.ViewModels.Onboarding;

/// <summary>
/// The first-run wizard's work-kind step (AC-511): pick the kind of work, see the plugins that suggests already
/// ticked, change any tick, and confirm the lot once. A work kind is a set of answers that pre-ticks boxes and
/// nothing else — it is not written down, and after this step there is only a set of installed plugins.
/// </summary>
/// <remarks>
/// <para>
/// The batch is the whole point and also the risk: enabling a plugin costs a dialog each (<c>PluginManagerViewModel
/// .EnablePluginAsync</c>), which four plugins turn into four dialogs. The guard moves rather than goes — every row
/// carries what its own dialog would have said, and <see cref="PluginConsentTerms.PermissionsNotice"/> is the same
/// constant that dialog shows.
/// </para>
/// <para>
/// Nothing here installs until <see cref="ConfirmCommand"/> runs. The wizard's own Skip and Next never reach it, so
/// leaving the step with rows ticked installs nothing: the pre-tick is a suggestion, not a decision already taken.
/// </para>
/// </remarks>
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

    /// <summary>Design-time/preview constructor: rows handed in, no store to load them from and nothing to install with.</summary>
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

    /// <summary>
    /// Whether any store said which work kind its plugins are for. An index published before the field exists says
    /// nothing, and then the chooser has nothing to offer — the list still works, ticked by hand.
    /// </summary>
    [ObservableProperty]
    private bool _hasRecommendations;

    [ObservableProperty]
    private bool _isLoading = true;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConfirmCommand))]
    private bool _isInstalling;

    /// <summary>What the last batch did, per the provisioning service's own summary — empty until one has run.</summary>
    [ObservableProperty]
    private string? _summary;

    [ObservableProperty]
    private string? _loadError;

    private PluginWorkKindOption? _selectedWorkKind;

    /// <summary>
    /// The chosen work kind. Setting it re-ticks the list from scratch: it is the answer to "what do you do", so a
    /// second answer replaces the first rather than adding to it. Every tick is still the operator's to change.
    /// </summary>
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

    /// <summary>Reads every configured store's catalogue and turns it into rows. Called by the view when it appears.</summary>
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

            HasRecommendations = Plugins.Any(plugin => !string.IsNullOrWhiteSpace(plugin.WorkKind));
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

    /// <summary>
    /// Installs everything ticked, in one pass: the provisioning service isolates each plugin, so one failure
    /// leaves the rest installed, and what lands is enabled with the checksum the install pinned.
    /// </summary>
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
            plugin.IsSelected = SelectedWorkKind is not null
                && string.Equals(plugin.WorkKind, SelectedWorkKind.Key, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// Keeps the button's count and enabled state on a hand-ticked box. Each row raises its own change, so the
    /// count is derived rather than tracked — a tracked one would drift the first time a row was ticked twice.
    /// </summary>
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
