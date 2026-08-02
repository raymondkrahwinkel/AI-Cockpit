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

    /// <summary>
    /// The provisioning seam (AC-510[b]) every store install/update/rollback below now goes through, wrapping the
    /// same <see cref="_storeClient"/>/<see cref="_installer"/> this view model already receives — built here
    /// rather than threaded in as its own constructor parameter, so the dozens of existing tests that stub
    /// <see cref="IPluginStoreClient"/>/<see cref="IPluginInstaller"/> directly still observe every call.
    /// </summary>
    private readonly IPluginProvisioningService? _provisioningService;

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
    /// The busy overlay's bar (AC-420), in the shape the calibration overlay already uses: indeterminate
    /// until there is an honest fraction to draw, and a 0..100 value once there is. A single install is one
    /// step and has no fraction — the download arrives in one piece — so only "Update all" leaves it, fed by
    /// the same counter it already writes into <see cref="StatusMessage"/>.
    /// </summary>
    [ObservableProperty]
    private bool _busyProgressIndeterminate = true;

    [ObservableProperty]
    private double _busyProgressValue;

    // "The store is working" is a depth, not a flag (AC-420). These scopes nest: every install path ends by
    // re-browsing the catalogue, and a plugin toggle does the same, and BrowseStoresAsync raises the flag
    // itself. With a plain bool the inner finally reported the store idle while the outer was still
    // downloading — which re-opened every gate that reads it, the restart offer included, mid-install.
    // Measured, and the reason a one-line gate on each button was not enough.
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

    // Whether something is unpacking into the plugins folder right now (AC-456). Not the same question as the
    // depth above: that one answers "is the store working", which the zip route deliberately says no to while
    // the operator browses for a file. Three per-route guards each closed the seam they were aimed at and left
    // the next one, so the routes no longer decide this for themselves — _InstallExclusivelyAsync does, once.
    private bool _installInFlight;

    /// <summary>
    /// Runs work that unpacks into the plugins folder, and refuses while another such run holds it (AC-456) —
    /// the single owner the per-route gates were standing in for. A plain bool carries it because these are
    /// dispatcher-thread commands: they interleave at their awaits, never in parallel, so there is no
    /// read-modify-write to lose.
    /// </summary>
    private async Task _InstallExclusivelyAsync(Func<Task> install)
    {
        if (_installInFlight)
        {
            // Refused without a word, deliberately. StatusMessage is the running install's only line — the
            // overlay draws it, and InstallFromStoreAsync captures it across its browse to put it back — so a
            // refusal written here would be restored as that install's closing message, which is worse than
            // saying nothing.
            //
            // That is a real cost, so it is worth being plain about what it buys: this is a backstop, not the
            // gate. The gates are the four CanExecute properties, and over the one window they cannot cover —
            // an install started while the file picker is open — the picker is owned by the store dialog, so
            // the catalogue is not clickable behind it. How firmly an owned native picker holds its owner is
            // the platform's decision, not ours, and it is not covered by a test; this is what stands under it
            // when the answer is "not firmly enough".
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

    // A restart and every route that unpacks are closed while work is in flight (AC-420), gated by their
    // command's CanExecute — which is what a bound Button consults, so the affordance goes dead rather than
    // only looking dead. "Update all" is here too since AC-456: its gate used to be an IsEnabled binding on
    // the whole search bar, so the fourth unpacking route was held shut by a control it has nothing to do
    // with, and a rearrangement of that bar would have dropped it silently onto the wordless backstop.
    //
    // What this gate is *not* for: a second click of the install button while its own install runs.
    // AsyncRelayCommand already refuses to re-enter itself (measured: with this gate neutralised, CanExecute
    // is still false for the duration), so the ticket's account of that failure was wrong. What was missing
    // is gating *across* commands — the toolkit's guard is per command, so an install stayed startable while
    // Update all or a version install was running, and those do reach the same download and folder move.
    //
    // Everything that changes what the store is made of joined them in AC-455, gated for a different reason:
    // not that they start a second install, but that they write over what one is working through — see
    // CanChangePlugins for which of them writes what.
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

    /// <summary>
    /// True once an install/enable/disable/remove has actually changed plugin state this session (#53) — the
    /// manager shows a "Restart now" button once this flips, instead of the operator having to remember to
    /// close and relaunch the app by hand. Sticky for the session: it never resets to false, since an
    /// earlier change still needs that restart even after a later one.
    /// </summary>
    [ObservableProperty]
    private bool _needsRestart;

    /// <summary>
    /// Whether a "Restart now" affordance can do anything — false in the design-time/no-op constructor, where
    /// there is no real app to restart, and false while an install or a batch update is running (AC-420).
    /// "Update all" raises <see cref="NeedsRestart"/> after the *first* plugin of the batch, so the button
    /// appeared while the other nine were still downloading; restarting there left them silently un-updated
    /// with a banner saying the update was done.
    /// </summary>
    public bool CanRestart => _restartService is not null && !IsBusy;

    /// <summary>
    /// Whether the operator may change what the store is made of — the installed plugins, the workflow
    /// templates, and the stores they come from (AC-455). Closed while the store is working, and read by every
    /// one of those commands rather than by the overlay drawn over their buttons: an overlay stops a pointer
    /// and leaves the focus underneath it, so a Tab and a space bar walk straight past it.
    /// </summary>
    /// <remarks>
    /// Four families, with four different reasons, which is why the list is longer than "everything that
    /// reloads". All twelve are here:
    /// <list type="bullet">
    /// <item><see cref="EnablePluginCommand"/>, <see cref="DisablePluginCommand"/>,
    /// <see cref="RemovePluginCommand"/>, <see cref="TogglePluginMenuVisibilityCommand"/> and
    /// <see cref="ToggleStorePluginCommand"/> rewrite the registration entry an install re-pins on its way out
    /// (<see cref="_AfterInstallAsync"/>), then reload — and the store toggle re-browses on top of that,
    /// rebuilding the catalogue the install is walking.</item>
    /// <item><see cref="MovePluginUpCommand"/> and <see cref="MovePluginDownCommand"/> reload nothing, but
    /// write <em>every</em> plugin's position into that same registration file, one save per plugin,
    /// interleaved with the install's own writes to it.</item>
    /// <item><see cref="InstallTemplateCommand"/> and <see cref="RemoveTemplateCommand"/> touch neither the
    /// plugins folder nor the registration store. They are here for <see cref="StatusMessage"/> and
    /// <see cref="NeedsRestart"/>: the status line is the running install's only line — it is what the overlay
    /// is showing — and a template installed over it replaces the sentence naming the plugin being downloaded
    /// with one naming the template.</item>
    /// <item><see cref="AddStoreCommand"/>, <see cref="RemoveStoreCommand"/> and
    /// <see cref="BrowseStoresCommand"/> clear and refill <see cref="Stores"/>, <see cref="StoreInfos"/> and
    /// <see cref="AvailablePlugins"/> — the collections a running browse is walking and enriching, and the one
    /// a batch update took its snapshot from.</item>
    /// </list>
    /// The two settings buttons deliberately stay live: they open a plugin's own settings dialog, which
    /// writes that plugin's settings and touches none of the above.
    /// </remarks>
    public bool CanChangePlugins => !IsBusy;

    /// <summary>Design-time constructor for the previewer.</summary>
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
        IWorkflowTemplateLibrary? templateLibrary = null)
    {
        _registrationStore = registrationStore;
        _installer = installer;
        _bootstrap = bootstrap;
        _dialogService = dialogService;
        _storeConfigStore = storeConfigStore;
        _storeClient = storeClient;
        _provisioningService = new PluginProvisioningService(storeClient, installer);
        _settingsRegistry = settingsRegistry;
        _diagnostics = diagnostics;
        _contributionSink = contributionSink;
        _restartService = restartService;
        _templateLibrary = templateLibrary;
        _WatchAvailablePluginsForUpdateGate();
        _WatchPluginsForPendingApprovalBadge();
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
    /// <remarks>
    /// Deliberately not gated on <see cref="IsBusy"/>, unlike the commands above (AC-455). Three routes reach
    /// it from the main window — Options, the store, the update toast — and none of them belongs to the store
    /// dialog, so a gate here would mean "you cannot open Options while a plugin installs", a real cost for a
    /// small risk: since the sweeps moved to startup this is a read of the plugins folder.
    /// <para>
    /// They are genuinely reachable mid-install, though it takes one step to see how: the store dialog is
    /// modal over the main window, but its footer's Close sits outside the busy layer and closes
    /// unconditionally, while the install carries on — the work belongs to this manager, not to the dialog. So
    /// close it and Ctrl+O is live with an install still running. The one crash that opens (clearing the store
    /// list a running browse is enumerating) is closed by that browse taking a snapshot. What is left is
    /// cosmetic and outlives this ticket: a rebuild mid-install leaves the Manage-stores dialog's per-store
    /// counts stale until the next browse.
    /// </para>
    /// </remarks>
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
                Plugins.Add(new PluginRowViewModel(
                    plugin,
                    _settingsRegistry?.ContainsKey(plugin.FolderId) ?? false,
                    _diagnostics?.AllForFolder(plugin.FolderId),
                    registrations.TryGetValue(plugin.FolderId, out var menuRegistration) && menuRegistration.HiddenInMenu));
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

    /// <summary>Whether a zip install can be started — it reaches the same installer as a store install, so it is closed while the store is working (AC-420). Nothing queues: the button goes dead and the operator presses it again afterwards.</summary>
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

        // Claimed only now, not around the picker: the operator choosing a file is their time, and covering the
        // dialog behind a busy overlay while a file picker is open would be covering nothing that is working.
        // The store can therefore have been claimed by something else while we were parked here, which is why
        // the claim is asked for rather than assumed (AC-456).
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
    [RelayCommand(CanExecute = nameof(CanChangePlugins))]
    private Task MovePluginUpAsync(PluginRowViewModel row) => MovePluginToAsync(row, Plugins.IndexOf(row) - 1);

    /// <summary>Moves the plugin down the left menu (#72).</summary>
    [RelayCommand(CanExecute = nameof(CanChangePlugins))]
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

    /// <summary>
    /// Refetches every configured store's index and rebuilds the catalogue. Public because the install paths
    /// call it directly on their way out and must not be refused: the command is gated for the operator (a
    /// refresh mid-install clears and refills the collection that install is walking), and the method is the
    /// way in for the code that owns the install around it.
    /// </summary>
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
            // Over a snapshot, for the reason "Update all" takes one: this loop awaits a fetch per store, and
            // LoadAsync clears that list. Reachable mid-install by closing the store dialog — which its footer
            // allows while the install carries on here — and then opening Options; enumerating a collection
            // someone cleared throws. See LoadAsync's remarks for why that route is left open.
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

    /// <summary>
    /// Installs a workflow template (#69): fetches its flow, checks it against the store's checksum, and writes it into
    /// the library the editor's picker reads. Nothing is loaded and no code runs — a template is a flow as text, and it
    /// arrives switched off, for the operator to read before arming it.
    /// </summary>
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

    /// <summary>Takes an installed template out of the library. The flows already made from it are yours and stay.</summary>
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

    /// <summary>Whether a store install can be started — nothing else may already be in flight (AC-420).</summary>
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

    /// <summary>
    /// True when at least one installed plugin has a newer, installable version in a store — gates the "Update
    /// all" button. Reads <see cref="StorePluginRowViewModel.CanUpdate"/>, not the raw version comparison
    /// (AC-181): an update this host cannot run yet (a <c>minHostVersion</c>/contract mismatch) is not something
    /// "Update all" should download and have the installer refuse — the row's own card already shows why.
    /// </summary>
    public bool HasAvailableUpdates => AvailablePlugins.Any(row => row.CanUpdate);

    /// <summary>How many installed plugins have a newer, installable version available — shown on the "Update all" button.</summary>
    public int AvailableUpdateCount => AvailablePlugins.Count(row => row.CanUpdate);

    /// <summary>Whether the sidebar "Plugin store" badge shows — true while the background checker's last count found updates (AC-76).</summary>
    public bool HasUpdateBadge => UpdateBadgeCount > 0;

    partial void OnUpdateBadgeCountChanged(int value) => OnPropertyChanged(nameof(HasUpdateBadge));

    // AC-208: Plugins is only ever populated by LoadAsync, which runs when the operator opens the Options/store
    // dialog — not at startup. Counting off Plugins alone left the sidebar badge invisible from launch until the
    // operator happened to open the manager, the exact moment AC-208 needs to be visible from. So there are two
    // sources, chosen the same way the view's two banners are: PluginDiagnostics.PendingApprovals is the true
    // count the instant startup discovery ran (recorded by PluginManager, read once via SeedPendingApprovalCount —
    // mirrors how SetUpdateBadgeCount seeds AvailableUpdateCount's sidebar sibling from outside this VM); once a
    // real LoadAsync has run at least once, Plugins is live and has to win, since only it drops to 0 as the
    // operator approves/disables each one — the seed is a snapshot from startup and never updates on its own.
    private int _seededPendingApprovalCount;
    private bool _hasDiscoveredPluginsOnce;

    /// <summary>
    /// How many installed plugins are sitting at awaiting-approval (AC-208) — new, or their bytes changed since
    /// last approved. Before the first <see cref="LoadAsync"/> this is the startup snapshot seeded via
    /// <see cref="SeedPendingApprovalCount"/>; after, it reads straight off <see cref="Plugins"/>, live.
    /// </summary>
    public int PendingApprovalCount => _hasDiscoveredPluginsOnce
        ? Plugins.Count(row => row.Discovered.Decision is PluginLoadDecision.NeedsConsent)
        : _seededPendingApprovalCount;

    /// <summary>Whether the sidebar "Plugin store" badge should show the pending-approval count (AC-208).</summary>
    public bool HasPendingApproval => PendingApprovalCount > 0;

    /// <summary>
    /// Seeds the sidebar pending-approval badge at app startup (AC-208), before <see cref="Plugins"/> is ever
    /// populated — called from <see cref="Cockpit.App.ViewModels.CockpitViewModel.RefreshPluginFailures"/> with
    /// <see cref="Cockpit.App.Plugins.PluginDiagnostics.PendingApprovals"/>'s count, the same place the startup
    /// banner is raised — itself run synchronously from <c>App.axaml.cs</c>'s UI-thread startup sequence, so
    /// unlike <see cref="SetUpdateBadgeCount"/> (fed from a background timer) this needs no dispatcher marshal.
    /// A no-op once a real discovery (<see cref="LoadAsync"/>) has run — the live count owns the badge from
    /// then on, so a stale seed can never keep it showing after the operator has dealt with everything.
    /// </summary>
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

    // Plugins is rebuilt wholesale (Clear + Add) on every LoadAsync, so PendingApprovalCount/HasPendingApproval —
    // both computed from it — need their own change raised the same way AvailableUpdateCount does for
    // AvailablePlugins (_WatchAvailablePluginsForUpdateGate): notifying only after specific commands would miss
    // the moment a fresh discovery is what changed the count. Any mutation here also means Plugins is now the
    // live source (belt-and-braces alongside the explicit flip in LoadAsync): a plugin row only ever lands in this
    // collection via a real discovery, so seeing one is itself proof the startup seed is stale.
    private void _WatchPluginsForPendingApprovalBadge() =>
        Plugins.CollectionChanged += (_, _) =>
        {
            _hasDiscoveredPluginsOnce = true;
            OnPropertyChanged(nameof(PendingApprovalCount));
            OnPropertyChanged(nameof(HasPendingApproval));
        };

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

    /// <summary>Whether the batch update can be started — it unpacks into the same folder as every other route, so it is closed while the store is working (AC-456).</summary>
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

    /// <summary>Installs a specific advertised version of a plugin (the detail panel's per-version Install), so a newer install can be rolled back to an older one.</summary>
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
    // provisioning service (AC-510[b]), then run the same UI-side aftercare every install path always has: the
    // consent walk for a fresh install, the registration re-pin for a staged update. No IsBusy/browse of its own
    // so it composes into the single-row install, the batch "Update all" and the per-version install. Returns
    // whether the install succeeded.
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
                if (registrations.TryGetValue(folderId, out var prior))
                {
                    await _registrationStore.SaveAsync(folderId, new PluginRegistration(Enabled: prior.Enabled, PinnedSha256: newSha256));
                }

                // No registration at all means this is not an update: the operator removed this plugin and has
                // now installed it again, so the folder is still on disk (the removal is applied at the next
                // start) and the installer staged over it. Writing a registration here would answer a question
                // nobody asked — "keep the state it had" reads a state that was deleted, so it lands on
                // disabled — and would pin bytes that were never consented to. Left absent, the restart applies
                // the staged copy and discovery meets a plugin with no registration, which is exactly what it
                // is: one awaiting approval (AC-455).
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
