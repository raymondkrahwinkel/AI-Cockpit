using System.Collections.ObjectModel;
using System.Net.Sockets;
using System.Security.Cryptography;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Abstractions.Profiles;
using Cockpit.Core.Abstractions.Projects;
using Cockpit.Core.Abstractions.Secrets;
using Cockpit.Core.Abstractions.Shell;
using Cockpit.Core.Abstractions.Terminal;
using Cockpit.Core.Mcp;
using Cockpit.Core.Secrets;
using Cockpit.Core.Shell;
using Cockpit.Core.Terminal;

namespace Cockpit.App.ViewModels;

// The Security tab: whether the credentials in `cockpit.json` are encrypted, and the migration that runs when the
// operator changes their mind either way.
public sealed partial class SecurityOptionsViewModel(
    ISecretProtectionService protection,
    IScreenLockSettingsStore? screenLockSettings = null,
    ITerminalAccessSwitch? terminalAccessSwitch = null,
    ITerminalAccessSettingsStore? terminalAccessSettings = null,
    IShellAccessSwitch? shellAccessSwitch = null,
    IShellAccessSettingsStore? shellAccessSettings = null,
    INodeEndpointSettingsStore? nodeEndpointSettings = null,
    IEnumerable<ICockpitInternalMcpProvider>? mcpEndpointHosts = null,
    INodePairingBroker? nodePairing = null,
    INodePairingClient? nodePairingClient = null,
    INodePairingEndpoint? nodePairingEndpoint = null,
    IMcpServerStore? mcpServers = null,
    INodeDiscoveryClient? nodeDiscoveryClient = null,
    // AC-794: what the scope checklist below offers to tick. Absent in the design-time/unit-test graph, same as
    // every other store above — the checklist then simply stays empty rather than the tab failing to open.
    ISessionProfileStore? sessionProfileStore = null,
    IProjectStore? projectStore = null,
    // AC-795: the other direction — the sessions on the nodes *this* cockpit is the controller of. Absent in the
    // design-time/unit-test graph like every store above, and then the node cards simply do not appear.
    INodeSessionsClient? nodeSessions = null) : ObservableObject
{
    // AC-999: while the Options dialog is staging, these three toggles are values like any other — held in the view
    // model, written only when the operator applies.
    public bool SuspendPersistence { get; set; }

    // True only while RefreshAsync seeds the toggle from disk, so setting the property then does not turn around and
    // write the same value straight back.
    private bool _loadingTerminalAccess;

    // Same guard, same reason, for the shell-access toggle (AC-1066) below.
    private bool _loadingShellAccess;

    // True only while RefreshAsync seeds the node toggle from disk (AC-790) — same guard, same reason, as
    // _loadingTerminalAccess above.
    private bool _loadingNodeEndpoint;

    [ObservableProperty]
    private bool _isEncrypted;

    // On by default, and only shown while encryption is on — there is nothing to re-ask for otherwise (AC-5).
    [ObservableProperty]
    private bool _lockWithOperatingSystem = true;

    // Persisted, and flipped live so the next session sees the change without a restart (AC-34).
    [ObservableProperty]
    private bool _terminalAccessEnabled;

    // The shell-access master switch (AC-1066): off by default. While off, `cockpit-shell` is not advertised to
    // any session; once on, what runs is decided by the session's own permission mode, not a command list.
    [ObservableProperty]
    private bool _shellAccessEnabled;

    // While off, every mounted MCP endpoint stays loopback-only (AC-790).
    [ObservableProperty]
    private bool _nodeEndpointEnabled;

    // The persistent shared secret a second Cockpit types into its own "add MCP server" dialog (AC-354) to reach
    // this instance as a node. Generated once, the first time the switch turns on; reused on every later toggle so
    // a second Cockpit that already has it keeps working after this one restarts.
    [ObservableProperty]
    private string _nodeEndpointSharedSecret = "";

    // What the operator reads off to type into a second Cockpit — one line per mounted endpoint's live node URL,
    // or an explanatory placeholder while there is nothing to show yet (see _ResolveNodeEndpointAddressText).
    [ObservableProperty]
    private string _nodeEndpointAddressText = "";

    // AC-793: CIDR ranges allowed to see this node from outside its own local network — comma-separated, so no
    // new list-editing control is needed for what is, in practice, an occasional one-or-two-entry setting. Empty
    // by default: the node's own subnet is always visible, and nothing past it until the operator opts in.
    [ObservableProperty]
    private string _allowedDiscoveryRangesText = "";

    // The pairing prompt lives on this tab rather than in an app-wide notification, and that is a real ceiling: a
    // request that arrives while the Options window is shut is never seen and expires after two minutes (AC-792).

    // The address a second cockpit types here to start a pairing with this one. One address, not one per endpoint:
    // the grant carries the endpoint list.
    [ObservableProperty]
    private string _nodePairingAddress = "";

    [ObservableProperty]
    private bool _hasIncomingPairing;

    // The six digits this cockpit derived. The operator compares them with the other screen; they are never sent.
    [ObservableProperty]
    private string _incomingPairingCode = "";

    [ObservableProperty]
    private string _incomingPairingCaption = "";

    [ObservableProperty]
    private bool _isPaired;

    [ObservableProperty]
    private string _pairedControllerText = "";

    // Both rebuild on every `RefreshAsync` (the dialog is rebuilt each time it opens, same reasoning as the pairing
    // subscription above) rather than trying to diff the previous set against a changed profile/project list (AC-794).

    public ObservableCollection<NodeScopeRowViewModel> ScopedProfiles { get; } = [];

    public ObservableCollection<NodeScopeRowViewModel> ScopedProjects { get; } = [];

    // One card per paired node, and no local session in any of them — see `NodeSessionsViewModel` for why that
    // separation is structural rather than a badge (AC-795).

    public ObservableCollection<NodeSessionsViewModel> PairedNodes { get; } = [];

    // True only while a row's IsAllowed is being seeded from the current grant, so ticking a box for real is the
    // only path that writes back to the broker — the same shape as _loadingNodeEndpoint above.
    private bool _loadingScope;

    // ── AC-792, this cockpit as a controller ───────────────────────────────────────────────────────────────────

    // What the operator types: the pairing address read off the other machine's Security tab.
    [ObservableProperty]
    private string _pairWithNodeAddress = "";

    // A second entrance to the same handshake above, not a separate one: picking a row here only fills
    // `PairWithNodeAddress` (`UseFoundNodeCommand`) — the pairing code, the certificate pin, everything from
    // `StartPairingCommand` onward is unaware whether the address it received was typed or found (AC-793).

    public ObservableCollection<NodeDiscoveryFound> FoundNodes { get; } = [];

    [ObservableProperty]
    private bool _isDiscoveringNodes;

    [ObservableProperty]
    private string _discoveryStatus = "";

    // Set between "start" and "the codes match": the handshake exists, nothing is granted, and both screens are
    // showing a number. Null at every other moment, which is what the two buttons key on.
    private NodePairingHandshake? _handshake;

    // Cancels the claim poll. Without one, Cancel would only forget the handshake locally while the poll ran on to
    // completion and wrote the node's servers into the registry anyway — a button that says it stopped something
    // it did not.
    private CancellationTokenSource? _pairingCancellation;

    [ObservableProperty]
    private bool _isComparingPairingCode;

    [ObservableProperty]
    private string _outgoingPairingCode = "";

    [ObservableProperty]
    private string _outgoingPairingCaption = "";

    [ObservableProperty]
    private bool _isPairingBusy;

    [ObservableProperty]
    private string _pairingStatus = "";

    private bool _subscribedToPairing;

    [ObservableProperty]
    private bool _isMigrating;

    [ObservableProperty]
    private string _migrationCaption = string.Empty;

    [ObservableProperty]
    private double _migrationProgress;

    [ObservableProperty]
    private string? _status;

    // Whether the app-level awareness banner (AC-41) should show: encryption is off and the settings hold at least one
    // credential in the clear that the operator has not dismissed the warning for.
    [ObservableProperty]
    private bool _showUnprotectedBanner;

    // True only while `RefreshAsync` is seeding the toggle from disk, so the change it makes to the property is not written straight back out.
    private bool _loadingLockSetting;

    public async Task RefreshAsync()
    {
        var status = await protection.GetStatusAsync().ConfigureAwait(true);
        IsEncrypted = status.Enabled;
        ShowUnprotectedBanner = status.ShouldWarnUnprotected;

        if (screenLockSettings is not null)
        {
            var settings = await screenLockSettings.LoadAsync().ConfigureAwait(true);
            _loadingLockSetting = true;
            try
            {
                LockWithOperatingSystem = settings.LockWhenOperatingSystemLocks;
            }
            finally
            {
                _loadingLockSetting = false;
            }
        }

        // Absent in the design-time/unit-test graph — the toggle then stays off and inert.
        if (terminalAccessSettings is not null)
        {
            var terminal = await terminalAccessSettings.LoadAsync().ConfigureAwait(true);
            _loadingTerminalAccess = true;
            TerminalAccessEnabled = terminal.Enabled;
            _loadingTerminalAccess = false;
            if (terminalAccessSwitch is not null)
            {
                terminalAccessSwitch.Enabled = terminal.Enabled;
            }
        }

        // AC-1066: same "absent in design-time/unit-test graph" shape as terminal access above.
        if (shellAccessSettings is not null)
        {
            var shell = await shellAccessSettings.LoadAsync().ConfigureAwait(true);
            _loadingShellAccess = true;
            ShellAccessEnabled = shell.Enabled;
            _loadingShellAccess = false;
            if (shellAccessSwitch is not null)
            {
                shellAccessSwitch.Enabled = shell.Enabled;
            }
        }

        // AC-790: same "absent in design-time/unit-test graph" shape as terminal access above.
        if (nodeEndpointSettings is not null)
        {
            var node = await nodeEndpointSettings.LoadAsync().ConfigureAwait(true);
            _loadingNodeEndpoint = true;
            NodeEndpointEnabled = node.Enabled;
            AllowedDiscoveryRangesText = string.Join(", ", node.AllowedDiscoveryRanges);
            _loadingNodeEndpoint = false;
            NodeEndpointSharedSecret = node.SharedSecret;
            NodeEndpointAddressText = _ResolveNodeEndpointAddressText(node.Enabled);
        }

        // AC-792: subscribe once, not per refresh — this tab is rebuilt every time the dialog opens, and a
        // handler added on each one would fire as many times as the operator has opened Options this run.
        if (nodePairing is not null && !_subscribedToPairing)
        {
            // AC-794: a pairing/unpairing changes who the scope checklist is even for, so it reloads here too —
            // not just IsPaired/PairedControllerText. An unpair, in particular, has to clear the checklist rather
            // than leave stale rows an operator could still tick against a coupling that no longer exists.
            nodePairing.Changed += (_, _) => Dispatcher.UIThread.Post(() =>
            {
                _ReadPairingState();
                _ = _LoadScopeRowsAsync();
            });
            _subscribedToPairing = true;
        }

        // Without this the broker only reads its pairing off disk when something acts on it, so a node that was
        // paired before this launch would show as unpaired here — and offer no way to unpair.
        if (nodePairing is not null)
        {
            await nodePairing.EnsureLoadedAsync().ConfigureAwait(true);
        }

        NodePairingAddress = nodePairingEndpoint?.Address ?? "";
        _ReadPairingState();
        await _LoadScopeRowsAsync().ConfigureAwait(true);
        await _LoadPairedNodesAsync().ConfigureAwait(true);
    }

    // Without this, two overlapping calls could each pass the empty-collection `Clear()` before either had added a
    // card, then both populate `PairedNodes` from their own `ListNodesAsync()` result: the second call's cards land on
    // top of the first's rather than replacing them, leaving the first call's cards (AC-796).
    private readonly SemaphoreSlim _pairedNodesGate = new(1, 1);

    // Each card reads its own node when it is built, so a node that is off costs this tab a timeout and not the other
    // nodes' contents — and the cards appear at once rather than after the slowest one (AC-795, AC-796).
    private async Task _LoadPairedNodesAsync()
    {
        await _pairedNodesGate.WaitAsync().ConfigureAwait(true);
        try
        {
            // The old cards' polls would otherwise keep ticking after `Clear()` drops them from this collection —
            // nothing else references them, so nothing else would ever stop them.
            StopPairedNodePolling();
            PairedNodes.Clear();
            if (nodeSessions is null)
            {
                return;
            }

            foreach (var node in await nodeSessions.ListNodesAsync().ConfigureAwait(true))
            {
                var card = new NodeSessionsViewModel(nodeSessions, node);
                PairedNodes.Add(card);
                card.StartPolling();

                // Deliberately not awaited: `RefreshAsync` on a node that is asleep sits out its whole budget, and
                // the Options window must not wait on that — the card fills in when its node answers, or shows why
                // not.
                _ = card.RefreshAsync();
            }
        }
        finally
        {
            _pairedNodesGate.Release();
        }
    }

    // Stops every paired-node card's poll (AC-796) without touching the collection itself — reached both before
    // this tab rebuilds `PairedNodes` and from `CockpitViewModel.DisposeAsync`, so a card's `DispatcherTimer` never
    // outlives the cockpit it reports on.
    public void StopPairedNodePolling()
    {
        foreach (var card in PairedNodes)
        {
            card.Dispose();
        }
    }

    // Mirrors the broker onto the tab. The broker is the source of truth for both halves — an incoming request and
    // the pairing it became — so this reads rather than tracks: a pairing that expired while the dialog sat open
    // reads as gone here for exactly the same reason the claim would refuse it.
    private void _ReadPairingState()
    {
        var pending = nodePairing?.Pending;
        HasIncomingPairing = pending is not null;
        IncomingPairingCode = pending?.Code ?? "";
        IncomingPairingCaption = pending is null
            ? ""
            : $"\"{pending.ControllerName}\" at {pending.ControllerAddress} wants to pair. Confirm only if the code below is the one that cockpit is showing.";

        var pairing = nodePairing?.Pairing;
        IsPaired = pairing is not null;
        PairedControllerText = pairing is null
            ? ""
            : $"Paired with \"{pairing.ControllerName}\" ({pairing.ControllerAddress}) since {pairing.PairedAtUtc.ToLocalTime():g}.";
    }

    // AC-794: rebuilds both checklists from the current pairing and the profile/project stores, and seeds each row's
    // IsAllowed from NodePairing.AllowedProfileLabels/AllowedProjectIds.
    private async Task _LoadScopeRowsAsync()
    {
        _UnsubscribeScopeRows();
        ScopedProfiles.Clear();
        ScopedProjects.Clear();

        if (!IsPaired || sessionProfileStore is null || projectStore is null)
        {
            return;
        }

        var allowedProfiles = nodePairing?.Pairing?.AllowedProfileLabels ?? [];
        var allowedProjects = nodePairing?.Pairing?.AllowedProjectIds ?? [];

        _loadingScope = true;
        try
        {
            // Neither load depends on the other — run them side by side rather than paying their latency twice.
            var profilesTask = sessionProfileStore.LoadAsync();
            var projectSettingsTask = projectStore.LoadAsync();
            await Task.WhenAll(profilesTask, projectSettingsTask).ConfigureAwait(true);

            foreach (var profile in profilesTask.Result)
            {
                var row = new NodeScopeRowViewModel(profile.Label, profile.Label)
                {
                    IsAllowed = allowedProfiles.Contains(profile.Label, StringComparer.Ordinal),
                };
                row.PropertyChanged += _OnScopeRowChanged;
                ScopedProfiles.Add(row);
            }

            foreach (var project in projectSettingsTask.Result.Projects)
            {
                var row = new NodeScopeRowViewModel(project.Id, project.Name)
                {
                    IsAllowed = allowedProjects.Contains(project.Id, StringComparer.Ordinal),
                };
                row.PropertyChanged += _OnScopeRowChanged;
                ScopedProjects.Add(row);
            }
        }
        finally
        {
            _loadingScope = false;
        }
    }

    private void _UnsubscribeScopeRows()
    {
        foreach (var row in ScopedProfiles.Concat(ScopedProjects))
        {
            row.PropertyChanged -= _OnScopeRowChanged;
        }
    }

    // Fires for any row's IsAllowed flip — seeding included, which is exactly what _loadingScope exists to filter
    // out, the same shape _loadingNodeEndpoint takes for the toggle above.
    private void _OnScopeRowChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (_loadingScope || e.PropertyName != nameof(NodeScopeRowViewModel.IsAllowed))
        {
            return;
        }

        _ = _PersistScopeAsync();
    }

    // Writes the whole checklist back, not just the row that changed — SetScopeAsync replaces the grant wholesale
    // (see its own remarks), so a partial write here would silently drop whatever the other rows already held.
    private Task _PersistScopeAsync()
    {
        if (nodePairing is null)
        {
            return Task.CompletedTask;
        }

        var allowedProfiles = ScopedProfiles.Where(row => row.IsAllowed).Select(row => row.Key).ToList();
        var allowedProjects = ScopedProjects.Where(row => row.IsAllowed).Select(row => row.Key).ToList();
        return nodePairing.SetScopeAsync(allowedProfiles, allowedProjects);
    }

    // Node binding off: normally nothing to show.
    private string _ResolveNodeEndpointAddressText(bool enabled)
    {
        var addresses = mcpEndpointHosts?.SelectMany(host => host.GetNodeAddresses()).ToList() ?? [];

        if (!enabled)
        {
            return addresses.Count > 0
                ? "Still reachable until you restart Cockpit — this session already opened the listener below:"
                    + Environment.NewLine + string.Join(Environment.NewLine, addresses.Select(address => $"{address.ServerName}: {address.Url}"))
                : "";
        }

        return addresses.Count > 0
            ? string.Join(Environment.NewLine, addresses.Select(address => $"{address.ServerName}: {address.Url}"))
            : "No address yet — restart Cockpit for this to take effect, or check that this machine has a network connection.";
    }

    // Writes the three staged toggles in one pass, for the Options dialog's Apply (AC-999). Everything else on
    // this tab — encryption, pairing, the MCP server list — acts on the spot and is not part of this.
    public async Task SaveStagedAsync()
    {
        if (screenLockSettings is not null)
        {
            await screenLockSettings.SaveAsync(new ScreenLockSettings { LockWhenOperatingSystemLocks = LockWithOperatingSystem }).ConfigureAwait(true);
        }

        if (terminalAccessSettings is not null)
        {
            if (terminalAccessSwitch is not null)
            {
                terminalAccessSwitch.Enabled = TerminalAccessEnabled;
            }

            await terminalAccessSettings.SaveAsync(new TerminalAccessSettings { Enabled = TerminalAccessEnabled }).ConfigureAwait(true);
        }

        if (shellAccessSettings is not null)
        {
            if (shellAccessSwitch is not null)
            {
                shellAccessSwitch.Enabled = ShellAccessEnabled;
            }

            await shellAccessSettings.SaveAsync(new ShellAccessSettings { Enabled = ShellAccessEnabled }).ConfigureAwait(true);
        }

        if (nodeEndpointSettings is not null)
        {
            // Read-modify-write off disk for the same reason `OnNodeEndpointEnabledChanged` does it (AC-792): the
            // section carries a pairing this screen knows nothing about, and the secret held here can have gone
            // stale under a rotation.
            var current = await nodeEndpointSettings.LoadAsync().ConfigureAwait(true);
            var sharedSecret = current.SharedSecret is { Length: > 0 }
                ? current.SharedSecret
                : Convert.ToHexString(RandomNumberGenerator.GetBytes(32));

            NodeEndpointSharedSecret = sharedSecret;
            NodeEndpointAddressText = _ResolveNodeEndpointAddressText(NodeEndpointEnabled);

            await nodeEndpointSettings.SaveAsync(current with
            {
                Enabled = NodeEndpointEnabled,
                SharedSecret = sharedSecret,
                AllowedDiscoveryRanges = AllowedDiscoveryRangesText.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries),
            }).ConfigureAwait(true);
        }
    }

    // Puts the three staged toggles back to what a cockpit that had never been configured would show (AC-999).
    // Writes nothing: this fills the buffer, and Cancel still undoes it.
    public void RestoreDefaults()
    {
        LockWithOperatingSystem = new ScreenLockSettings().LockWhenOperatingSystemLocks;
        TerminalAccessEnabled = new TerminalAccessSettings().Enabled;
        ShellAccessEnabled = new ShellAccessSettings().Enabled;
        NodeEndpointEnabled = new NodeEndpointSettings().Enabled;
        AllowedDiscoveryRangesText = string.Join(", ", new NodeEndpointSettings().AllowedDiscoveryRanges);
    }

    // Persists the AC-5 toggle the moment it changes. The load above sets it too, which is why that path suppresses this — a seed from disk must not be a write back to disk.
    partial void OnLockWithOperatingSystemChanged(bool value)
    {
        if (_loadingLockSetting || SuspendPersistence || screenLockSettings is null)
        {
            return;
        }

        _ = screenLockSettings.SaveAsync(new ScreenLockSettings { LockWhenOperatingSystemLocks = value });
    }

    // The toggle changed. Flip the live switch at once (so the next session sees it without a restart) and persist,
    // unless we are only seeding the value from disk in RefreshAsync (or the store is absent in a test graph).
    async partial void OnTerminalAccessEnabledChanged(bool value)
    {
        if (_loadingTerminalAccess || SuspendPersistence || terminalAccessSettings is null)
        {
            return;
        }

        if (terminalAccessSwitch is not null)
        {
            terminalAccessSwitch.Enabled = value;
        }

        await terminalAccessSettings.SaveAsync(new TerminalAccessSettings { Enabled = value }).ConfigureAwait(true);
    }

    // Same shape as OnTerminalAccessEnabledChanged above, for the shell-access toggle (AC-1066).
    async partial void OnShellAccessEnabledChanged(bool value)
    {
        if (_loadingShellAccess || SuspendPersistence || shellAccessSettings is null)
        {
            return;
        }

        if (shellAccessSwitch is not null)
        {
            shellAccessSwitch.Enabled = value;
        }

        await shellAccessSettings.SaveAsync(new ShellAccessSettings { Enabled = value }).ConfigureAwait(true);
    }

    // Unlike terminal access above, this never flips anything live — the Kestrel listeners it governs are only
    // reconfigured at the next launch (CockpitMcpEndpointHost.MountAsync) — so this only persists (AC-790).
    async partial void OnNodeEndpointEnabledChanged(bool value)
    {
        if (_loadingNodeEndpoint || SuspendPersistence || nodeEndpointSettings is null)
        {
            return;
        }

        // AC-792: read-modify-write rather than a fresh record, and the secret comes from what is on disk right now
        // rather than from this view model's copy.
        var current = await nodeEndpointSettings.LoadAsync().ConfigureAwait(true);
        var sharedSecret = current.SharedSecret is { Length: > 0 }
            ? current.SharedSecret
            : Convert.ToHexString(RandomNumberGenerator.GetBytes(32));

        NodeEndpointSharedSecret = sharedSecret;
        NodeEndpointAddressText = _ResolveNodeEndpointAddressText(value);

        await nodeEndpointSettings.SaveAsync(current with { Enabled = value, SharedSecret = sharedSecret }).ConfigureAwait(true);
    }

    // A malformed entry is simply a range that never matches anything — `NodeVisibilityPolicy` skips what does not
    // parse as a CIDR — so there is nothing here worth validating before it reaches disk (AC-793).
    async partial void OnAllowedDiscoveryRangesTextChanged(string value)
    {
        if (_loadingNodeEndpoint || SuspendPersistence || nodeEndpointSettings is null)
        {
            return;
        }

        var ranges = value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var current = await nodeEndpointSettings.LoadAsync().ConfigureAwait(true);
        await nodeEndpointSettings.SaveAsync(current with { AllowedDiscoveryRanges = ranges }).ConfigureAwait(true);
    }

    // ── AC-792 commands, node side ─────────────────────────────────────────────────────────────────────────────

    // The operator says the two screens show the same number. This is the moment a shared secret comes into
    // being — before it, a pairing request has changed nothing about this cockpit.
    [RelayCommand]
    private async Task ConfirmIncomingPairingAsync()
    {
        if (nodePairing?.Pending is not { } pending)
        {
            return;
        }

        try
        {
            await nodePairing.ConfirmAsync(pending.PairingId).ConfigureAwait(true);
            PairingStatus = "Pairing confirmed.";
        }
        catch (NodePairingException ex)
        {
            PairingStatus = ex.Message;
        }

        await RefreshAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private void RefuseIncomingPairing()
    {
        if (nodePairing?.Pending is { } pending)
        {
            nodePairing.Refuse(pending.PairingId);
        }
    }

    // Ends the coupling from this side. The shared secret goes with it, which is what makes the controller stop
    // being able to call in rather than merely stop being listed.
    [RelayCommand]
    private async Task UnpairNodeAsync()
    {
        if (nodePairing is null)
        {
            return;
        }

        await nodePairing.UnpairAsync().ConfigureAwait(true);
        PairingStatus = "Unpaired. The key that controller was given no longer works.";
        await RefreshAsync().ConfigureAwait(true);
    }

    // ── AC-792 commands, controller side ───────────────────────────────────────────────────────────────────────

    // Step one: ask the node for a pairing and derive the code from the certificate that address actually
    // presented. Nothing is stored yet — the operator has a number to compare and a way out.
    [RelayCommand]
    private async Task StartPairingAsync()
    {
        if (nodePairingClient is null || string.IsNullOrWhiteSpace(PairWithNodeAddress))
        {
            return;
        }

        IsPairingBusy = true;
        PairingStatus = "";
        try
        {
            _handshake = await nodePairingClient.BeginAsync(PairWithNodeAddress, Environment.MachineName).ConfigureAwait(true);
            OutgoingPairingCode = _handshake.Code;
            OutgoingPairingCaption =
                $"\"{_handshake.NodeName}\" should be showing this same code. Continue only if it matches — a different number means something else is answering at that address.";
            IsComparingPairingCode = true;
        }
        catch (NodePairingException ex)
        {
            PairingStatus = ex.Message;
        }
        catch (Exception ex) when (ex is HttpRequestException or UriFormatException or ArgumentException or TaskCanceledException
            or NotSupportedException or System.Text.Json.JsonException)
        {
            // The last two are what a mistyped address produces when it lands on some other HTTPS service that
            // answers 200 with HTML: `ReadFromJsonAsync` throws rather than returning null, and an escaping
            // exception out of an async RelayCommand is an unhandled one.
            PairingStatus = $"Could not reach a Cockpit node at {PairWithNodeAddress}: {ex.Message}";
        }
        finally
        {
            IsPairingBusy = false;
        }
    }

    // Step two: this operator has compared the codes. Now wait for the node's operator to do the same — which is
    // the second of the two confirmations the pairing needs — and store what comes back.
    [RelayCommand]
    private async Task ConfirmPairingCodeAsync()
    {
        if (nodePairingClient is null || _handshake is not { } handshake)
        {
            return;
        }

        IsPairingBusy = true;
        PairingStatus = $"Waiting for \"{handshake.NodeName}\" to confirm the same code…";

        using var cancellation = new CancellationTokenSource();
        _pairingCancellation = cancellation;
        try
        {
            var grant = await nodePairingClient.CompleteAsync(handshake, cancellation.Token).ConfigureAwait(true);
            var added = await _StoreNodeServersAsync(handshake, grant).ConfigureAwait(true);
            PairingStatus = added == 0
                ? $"Paired with \"{handshake.NodeName}\", but it offered no endpoints — turn its node switch on and restart it."
                : $"Paired with \"{handshake.NodeName}\". Added {added} MCP server(s), pinned to that machine's certificate.";
        }
        catch (OperationCanceledException)
        {
            PairingStatus = "Pairing cancelled.";
        }
        catch (NodePairingException ex)
        {
            PairingStatus = ex.Message;
        }
        catch (Exception ex) when (ex is HttpRequestException or NotSupportedException or System.Text.Json.JsonException)
        {
            // A pin mismatch arrives here wrapped in an HttpRequestException; its inner exception is the reason
            // worth showing, because "that is not the machine you paired with" is not a network problem.
            PairingStatus = ex.InnerException is NodeCertificatePinMismatchException mismatch ? mismatch.Message : ex.Message;
        }
        finally
        {
            _pairingCancellation = null;
            IsPairingBusy = false;
            _ClearHandshake();
        }
    }

    [RelayCommand]
    private void CancelPairing()
    {
        // Cancel before clearing: a poll already in flight has to be told to stop, not merely forgotten.
        _pairingCancellation?.Cancel();
        _ClearHandshake();
    }

    private void _ClearHandshake()
    {
        _handshake = null;
        IsComparingPairingCode = false;
        OutgoingPairingCode = "";
        OutgoingPairingCaption = "";
    }

    // ── AC-793 commands, finding a node ────────────────────────────────────────────────────────────────────────

    [RelayCommand]
    private async Task DiscoverNodesAsync()
    {
        if (nodeDiscoveryClient is null)
        {
            return;
        }

        IsDiscoveringNodes = true;
        DiscoveryStatus = "";
        FoundNodes.Clear();
        try
        {
            var found = await nodeDiscoveryClient.FindAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(true);
            foreach (var node in found)
            {
                FoundNodes.Add(node);
            }

            // An empty result is not an error — the switch could be off on every other cockpit on this segment,
            // or this one's own whitelist/range is what is keeping them out — so this reads as guidance rather
            // than a failure.
            DiscoveryStatus = found.Count == 0
                ? "Nothing answered. Check the other cockpit's node switch is on, and that it is on this network's own range or your whitelist."
                : "";
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
        {
            // The same posture `StartPairingAsync` takes for its own network call: a failure here is something
            // to tell the operator, not an unhandled exception out of a button press.
            DiscoveryStatus = $"Could not search for nodes on this network: {ex.Message}";
        }
        finally
        {
            IsDiscoveringNodes = false;
        }
    }

    // Picking a row only fills the address field — starting the pairing itself is still `StartPairingCommand`,
    // so a discovered address and a typed one go through the exact same handshake from here on.
    [RelayCommand]
    private void UseFoundNode(NodeDiscoveryFound node) => PairWithNodeAddress = node.Address;

    // Turns the grant into ordinary registry rows — the same `Transport = Http` + bearer + URL an operator would
    // have typed by hand after AC-790, with the certificate pin added, which is the part they could not have
    // typed. Rows for this node are replaced rather than appended to, so pairing twice does not double the list.
    private async Task<int> _StoreNodeServersAsync(NodePairingHandshake handshake, NodePairingGrant grant)
    {
        if (mcpServers is null || grant.Endpoints.Count == 0)
        {
            return 0;
        }

        var existing = await mcpServers.LoadAsync().ConfigureAwait(true);
        var prefix = NodeServerName.PrefixFor(handshake.NodeName);
        var kept = existing.Where(server => !server.Name.StartsWith(prefix, StringComparison.Ordinal)).ToList();

        kept.AddRange(grant.Endpoints.Select(endpoint => new McpServerConfig
        {
            Id = McpServerIdentity.NewId(),
            // AC-795 reads this name back to find one node's session server again, so the shape is stated once in
            // `NodeServerName` rather than built here and parsed there.
            Name = NodeServerName.For(handshake.NodeName, endpoint.ServerName),
            Transport = McpTransport.Http,
            // Only the in-process tool loop builds its own HTTP transport (`McpToolProvider`), so only it can be told
            // which certificate to trust.
            Scope = McpServerScope.LocalOnly,
            Url = endpoint.Url,
            Auth = McpServerAuth.ApiKey,
            ApiKey = grant.SharedSecret,
            PinnedCertificateFingerprint = handshake.Fingerprint,
        }));

        await mcpServers.SaveAsync(kept).ConfigureAwait(true);
        return grant.Endpoints.Count;
    }

    // Dismisses the awareness banner for the credentials now in the file (AC-41). Hides it at once, then persists
    // the dismissal so it stays hidden across restarts — until a new credential changes the set and brings it back.
    [RelayCommand]
    private async Task DismissBannerAsync()
    {
        ShowUnprotectedBanner = false;
        await protection.DismissUnprotectedWarningAsync().ConfigureAwait(true);
    }

    public async Task EnableAsync(string password)
    {
        await RunMigrationAsync(
            "Encrypting your credentials…",
            progress => protection.EnableAsync(password, progress)).ConfigureAwait(true);

        Status = "Your keys and tokens are encrypted. You will be asked for this password the next time the cockpit starts.";
    }

    public async Task DisableAsync()
    {
        await RunMigrationAsync(
            "Writing your credentials back in the clear…",
            progress => protection.DisableAsync(progress)).ConfigureAwait(true);

        Status = "Encryption is off. Your keys and tokens are readable in cockpit.json again, and the cockpit starts without asking for a password.";
    }

    public async Task ChangePasswordAsync(string currentPassword, string newPassword)
    {
        try
        {
            await RunMigrationAsync(
                "Re-encrypting your credentials…",
                progress => protection.ChangePasswordAsync(currentPassword, newPassword, progress)).ConfigureAwait(true);

            Status = "Your password has been changed.";
        }
        catch (SecretProtectionException)
        {
            Status = "That is not your current password — nothing was changed.";
        }
    }

    private async Task RunMigrationAsync(string caption, Func<IProgress<SecretMigrationProgress>, Task> migrate)
    {
        IsMigrating = true;
        MigrationCaption = caption;
        MigrationProgress = 0;
        Status = null;

        try
        {
            var progress = new Progress<SecretMigrationProgress>(report =>
                MigrationProgress = report.Total == 0 ? 100 : 100.0 * report.Completed / report.Total);

            // Off the UI thread: deriving the key is deliberately expensive, and a window that stops repainting
            // in the middle of rewriting the operator's credentials reads as a crash.
            await Task.Run(() => migrate(progress)).ConfigureAwait(true);

            MigrationProgress = 100;
            await RefreshAsync().ConfigureAwait(true);
        }
        finally
        {
            IsMigrating = false;
        }
    }
}
