using System.Collections.ObjectModel;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Abstractions.Remote;
using Cockpit.Core.Abstractions.Secrets;
using Cockpit.Core.Abstractions.Shell;
using Cockpit.Core.Abstractions.Terminal;
using Cockpit.Core.Mcp;
using Cockpit.Core.Secrets;
using Cockpit.Core.Shell;
using Cockpit.Core.Terminal;
using Cockpit.Core.Provisioning;

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
    INodePairingEndpoint? nodePairingEndpoint = null,
    IMcpServerStore? mcpServers = null,
    // AC-1984: only connected server cards remain; since AC-2011 they are on the Servers page.
    INodeSessionsClient? nodeSessions = null,
    // AC-1456: the connect servers the session list shows as groups; their cards follow those instead of polling.
    IRemoteServers? remoteServers = null,
    // AC-1593: what the organisation's policy forbids; absent in the design-time/unit-test graph, where nothing is.
    IOrgPolicy? orgPolicy = null) : ObservableObject
{
    public bool TerminalAccessEditable => !OrgPolicyCeilings.TerminalForbidden(orgPolicy?.Current);

    public string? TerminalAccessManagedLabel => TerminalAccessEditable ? null : $"Managed by {orgPolicy?.Current?.By}: off for every agent";

    public bool ShellAccessEditable => !OrgPolicyCeilings.ShellForbidden(orgPolicy?.Current);

    public string? ShellAccessManagedLabel => ShellAccessEditable ? null : $"Managed by {orgPolicy?.Current?.By}: off for every agent";

    // AC-1593: the backup checkbox lives on the Backup page; the policy that locks it is read here, with the others.
    public bool BackupCredentialsEditable => !OrgPolicyCeilings.CredentialsForbidden(orgPolicy?.Current);

    public string? BackupCredentialsManagedLabel => BackupCredentialsEditable ? null : $"Managed by {orgPolicy?.Current?.By}: keys, tokens and agent logins never go in a backup";

    // AC-1593: shown only when the organisation sets `neverSend`; nothing on this machine turns it off.
    public bool NeverSendManaged => OrgPolicyCeilings.NeverSend(orgPolicy?.Current);

    public string? NeverSendManagedLabel => NeverSendManaged ? $"Managed by {orgPolicy?.Current?.By}: an instruction to the assistant, not a block" : null;

    // AC-999: while the Options dialog is staging, these three toggles are values like any other — held in the view
    // model, written only when the operator applies.
    public bool SuspendPersistence { get; set; }

    // AC-1286: what could not be written, in the operator's words. Empty while everything the operator chose is on
    // disk, and cleared again by the next write that succeeds — a reason left standing after it stopped being true
    // is the next bug.
    [ObservableProperty]
    private string _persistError = "";

    // Which Options category the failed write belongs to, so a blocked Apply can put the operator in front of it.
    public string? PersistErrorCategoryTag { get; private set; }

    // The writes this tab has in flight, chained end to end. A chain rather than a lock: these all read-modify-write
    // the same section of cockpit.json, and a gate held across an await that needs the UI thread is how the dialog

    // Chained from the UI thread only — every caller is a bound property's handler or Apply. Two threads chaining
    // at once would read the same predecessor and run side by side, which is the update loss this exists to stop.
    private Task _persist = Task.CompletedTask;

    // Everything this tab still has to write, as one task to wait on. Apply follows it implicitly by going through
    // the same chain; a test waits on it rather than on the clock.
    public Task PendingWrites => _persist;

    // AC-1286: the one door every write goes through, staged and unstaged alike. It is still not awaited by the
    // change handlers — a property setter cannot await — but the task is kept, so Apply follows it and a failure
    // reaches the screen instead of the log.
    private Task _PersistAsync(string what, string categoryTag, Func<Task> write)
    {
        _persist = _RunPersistAsync(_persist, what, categoryTag, write);

        return _persist;
    }

    private async Task _RunPersistAsync(Task previous, string what, string categoryTag, Func<Task> write)
    {
        // Never throws: it caught its own failure below, which is what makes chaining onto it safe.
        await previous.ConfigureAwait(true);

        try
        {
            await write().ConfigureAwait(true);

            // Only this tag's own failure is cleared. Apply writes in two steps, and clearing unconditionally let
            // the second step's success erase the first step's failure — Apply then closed over an unwritten
            // setting with nothing on screen, which is the very thing this reports.
            if (PersistErrorCategoryTag is null || PersistErrorCategoryTag == categoryTag)
            {
                PersistError = "";
                PersistErrorCategoryTag = null;
            }
        }
        catch (Exception exception)
        {
            PersistError = $"Not saved: {what} could not be written. {exception.Message}";
            PersistErrorCategoryTag = categoryTag;
        }
    }

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
    // empty while no listener is running (see _ResolveNodeEndpointAddressText).
    [ObservableProperty]
    private string _nodeEndpointAddressText = "";

    // The switch is on but no listener answers: both address fields have nothing to show, and this hides them.
    // Set alongside the address text so the two can never disagree.
    [ObservableProperty]
    private bool _nodeEndpointHasNoListener;

    // What stands in their place — the restart line, or, when the host knows why the listener is missing, that
    // reason instead (AC-1288). One property, so the operator never reads both at once.
    [ObservableProperty]
    private string _nodeEndpointNoListenerText = "";

    // AC-793: CIDR ranges allowed to see this node from outside its own local network — comma-separated, so no
    // new list-editing control is needed for what is, in practice, an occasional one-or-two-entry setting. Empty
    // by default: the node's own subnet is always visible, and nothing past it until the operator opts in.
    [ObservableProperty]
    private string _allowedDiscoveryRangesText = "";

    // AC-1984: Swarm still shows this listener address for joining by address.
    [ObservableProperty]
    private string _nodePairingAddress = "";

    // AC-1984: connected server cards follow their session-list groups.

    public ObservableCollection<NodeSessionsViewModel> PairedNodes { get; } = [];

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

    // AC-1286: `reseedStagedValues` is Cancel's, and only Cancel's. A refresh that lands for its own reasons — a
    // plugin declaring credential fields, a credential written in the clear — must not overwrite what the operator
    // has ticked in the open dialog, because Apply would then write the disk value straight back.
    public async Task RefreshAsync(bool reseedStagedValues = false)
    {
        var seedStaged = !SuspendPersistence || reseedStagedValues;
        if (reseedStagedValues)
        {
            // Cancel put every value back to what is on disk, so a failure to write one of them describes a choice
            // that no longer exists.
            PersistError = "";
            PersistErrorCategoryTag = null;
        }

        var status = await protection.GetStatusAsync().ConfigureAwait(true);
        IsEncrypted = status.Enabled;
        ShowUnprotectedBanner = status.ShouldWarnUnprotected;
        await _ShowHelloAsync(status).ConfigureAwait(true);

        if (screenLockSettings is not null && seedStaged)
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
        if (terminalAccessSettings is not null && seedStaged)
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
        if (shellAccessSettings is not null && seedStaged)
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
            if (seedStaged)
            {
                var node = await nodeEndpointSettings.LoadAsync().ConfigureAwait(true);
                _loadingNodeEndpoint = true;
                NodeEndpointEnabled = node.Enabled;
                AllowedDiscoveryRangesText = string.Join(", ", node.AllowedDiscoveryRanges);
                _loadingNodeEndpoint = false;
                NodeEndpointSharedSecret = node.SharedSecret;
            }

            // Outside the guard: the addresses are what this run's listeners answer, not a staged value, and the
            // Servers page has to keep telling the truth about them while the dialog is open.
            _ApplyNodeEndpointAddresses(NodeEndpointEnabled);
        }

        // The MCP listener owns the shared message when both ports are unavailable.
        var pairingAddress = nodePairingEndpoint?.Address;
        var mcpListenerError = mcpEndpointHosts?.Select(host => host.NodeListenerError).FirstOrDefault(error => error is not null);
        NodePairingAddress = pairingAddress ?? "";
        if (pairingAddress is null && mcpListenerError is null)
        {
            NodePairingAddress = nodePairingEndpoint?.Error ?? "";
        }

        await _LoadPairedNodesAsync().ConfigureAwait(true);
    }

    // Without this, two overlapping calls could each pass the empty-collection `Clear()` before either had added a
    // card, then both populate `PairedNodes` from their own `ListNodesAsync()` result: the second call's cards land on
    // top of the first's rather than replacing them, leaving the first call's cards (AC-796).
    private readonly SemaphoreSlim _pairedNodesGate = new(1, 1);

    private async Task _LoadPairedNodesAsync()
    {
        await _pairedNodesGate.WaitAsync().ConfigureAwait(true);
        try
        {
            // Release subscriptions before replacing the cards.
            DisposeNodeCards();
            PairedNodes.Clear();
            if (nodeSessions is null)
            {
                return;
            }

            // AC-1458: the row behind each card, for its address and its key's expiry.
            IReadOnlyList<McpServerConfig> rows = mcpServers is null ? [] : await mcpServers.LoadAsync().ConfigureAwait(true);

            // AC-1456: a row just connected, rotated or removed reaches the session list here too.
            if (remoteServers is not null)
            {
                await remoteServers.ReloadAsync().ConfigureAwait(true);
            }

            foreach (var node in await nodeSessions.ListNodesAsync().ConfigureAwait(true))
            {
                var wanted = NodeServerName.For(node, NodeServerName.SessionsServerName);
                var row = rows.FirstOrDefault(candidate => string.Equals(candidate.Name, wanted, StringComparison.Ordinal));
                if (remoteServers?.Knows(node) != true)
                {
                    continue;
                }

                var card = new NodeSessionsViewModel(node)
                {
                    Url = row?.Url,
                    KeyExpiresAt = row?.KeyExpiresAt,
                    EnterNewKey = row is null ? null : () => _PrefillConnect(node, row),
                };
                card.FollowServer(remoteServers);
                PairedNodes.Add(card);
            }
        }
        finally
        {
            _pairedNodesGate.Release();
        }
    }

    // Release server subscriptions when rebuilding the cards or disposing the cockpit.
    public void DisposeNodeCards()
    {
        foreach (var card in PairedNodes)
        {
            card.Dispose();
        }
    }

    private string _ResolveNodeEndpointAddressText(bool enabled)
    {
        var addresses = _NodeAddresses();

        if (!enabled)
        {
            return addresses.Count > 0
                ? "Still reachable until you restart Cockpit — this session already opened the listener below:"
                    + Environment.NewLine + string.Join(Environment.NewLine, addresses.Select(address => $"{address.ServerName}: {address.Url}"))
                : "";
        }

        // Empty rather than an explanation: with no listener the address fields are hidden and
        // `NodeEndpointNoListenerText` says why, next to the switch that caused it.
        return string.Join(Environment.NewLine, addresses.Select(address => $"{address.ServerName}: {address.Url}"));
    }

    // AC-1288: in the view model rather than in the view, so it and the host's own reason are one property the
    // Servers page binds — two TextBlocks fighting over one line is how they end up both visible.
    private const string NeedsRestartText =
        "Not listening yet — apply this first, then restart Cockpit before it starts listening. The addresses to "
        + "type into the other Cockpit appear here after that restart; if they stay away, check that this machine "
        + "has a network connection.";

    private List<NodeEndpointAddress> _NodeAddresses() =>
        mcpEndpointHosts?.SelectMany(host => host.GetNodeAddresses()).ToList() ?? [];

    // The only place these two are written: the switch reads from disk, from a staged edit and from Apply, and all
    // three have to leave the same answer behind.
    private void _ApplyNodeEndpointAddresses(bool enabled)
    {
        NodeEndpointAddressText = _ResolveNodeEndpointAddressText(enabled);

        NodeEndpointHasNoListener = enabled && _NodeAddresses().Count == 0;

        // A reason the host knows takes precedence over the restart line: telling an operator whose port is taken
        // to restart sends them round the same failure again (AC-1288).
        NodeEndpointNoListenerText = NodeEndpointHasNoListener
            ? mcpEndpointHosts?.Select(host => host.NodeListenerError).FirstOrDefault(error => error is not null) ?? NeedsRestartText
            : "";
    }

    // Writes the three staged toggles in one pass, for the Options dialog's Apply (AC-999). Everything else on

    // AC-1286: through the same chain as the unstaged path, in two steps so a failure names its own tab. Apply
    // therefore follows anything the operator's last click still had in flight rather than racing it, and never
    // throws at `OnApplyAndClose` — `async void`, which would drop it on the dispatcher and say nothing.
    public async Task SaveStagedAsync()
    {
        await _PersistAsync("your security settings", "security", async () =>
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
        }).ConfigureAwait(true);

        if (nodeEndpointSettings is null)
        {
            return;
        }

        await _PersistAsync("the node settings", "servers", async () =>
        {
            // Read-modify-write off disk for the same reason `OnNodeEndpointEnabledChanged` does it (AC-792): the
            // section carries a pairing this screen knows nothing about, and the secret held here can have gone
            // stale under a rotation.
            var current = await nodeEndpointSettings.LoadAsync().ConfigureAwait(true);
            var sharedSecret = current.SharedSecret is { Length: > 0 }
                ? current.SharedSecret
                : Convert.ToHexString(RandomNumberGenerator.GetBytes(32));

            NodeEndpointSharedSecret = sharedSecret;
            _ApplyNodeEndpointAddresses(NodeEndpointEnabled);

            await nodeEndpointSettings.SaveAsync(current with
            {
                Enabled = NodeEndpointEnabled,
                SharedSecret = sharedSecret,
                AllowedDiscoveryRanges = AllowedDiscoveryRangesText.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries),
            }).ConfigureAwait(true);
        }).ConfigureAwait(true);
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

        _ = _PersistAsync("locking with the operating system", "security", () =>
            screenLockSettings.SaveAsync(new ScreenLockSettings { LockWhenOperatingSystemLocks = value }));
    }

    // The toggle changed. Flip the live switch at once (so the next session sees it without a restart) and persist,
    // unless we are only seeding the value from disk in RefreshAsync (or the store is absent in a test graph).
    partial void OnTerminalAccessEnabledChanged(bool value)
    {
        if (_loadingTerminalAccess || SuspendPersistence || terminalAccessSettings is null)
        {
            return;
        }

        if (terminalAccessSwitch is not null)
        {
            terminalAccessSwitch.Enabled = value;
        }

        _ = _PersistAsync("terminal access", "security", () =>
            terminalAccessSettings.SaveAsync(new TerminalAccessSettings { Enabled = value }));
    }

    // Same shape as OnTerminalAccessEnabledChanged above, for the shell-access toggle (AC-1066).
    partial void OnShellAccessEnabledChanged(bool value)
    {
        if (_loadingShellAccess || SuspendPersistence || shellAccessSettings is null)
        {
            return;
        }

        if (shellAccessSwitch is not null)
        {
            shellAccessSwitch.Enabled = value;
        }

        _ = _PersistAsync("shell access", "security", () =>
            shellAccessSettings.SaveAsync(new ShellAccessSettings { Enabled = value }));
    }

    // Unlike terminal access above, this never flips anything live — the Kestrel listeners it governs are only
    // reconfigured at the next launch (CockpitMcpEndpointHost.MountAsync) — so this only persists (AC-790).
    partial void OnNodeEndpointEnabledChanged(bool value)
    {
        // Display, not persistence, so it runs before the guards below: while the Options dialog stages this toggle
        // (SuspendPersistence, AC-999) the operator would otherwise tick the box and be told nothing at all.
        _ApplyNodeEndpointAddresses(value);

        if (_loadingNodeEndpoint || SuspendPersistence || nodeEndpointSettings is null)
        {
            return;
        }

        // AC-1286: the read sits inside the chained write, not beside it. Read outside, and the sibling handler
        // below loads while this save is still running and writes the value it just replaced back over it.
        _ = _PersistAsync("the node switch", "servers", async () =>
        {
            // AC-792: read-modify-write rather than a fresh record, and the secret comes from what is on disk right
            // now rather than from this view model's copy.
            var current = await nodeEndpointSettings.LoadAsync().ConfigureAwait(true);
            var sharedSecret = current.SharedSecret is { Length: > 0 }
                ? current.SharedSecret
                : Convert.ToHexString(RandomNumberGenerator.GetBytes(32));

            NodeEndpointSharedSecret = sharedSecret;

            await nodeEndpointSettings.SaveAsync(current with { Enabled = value, SharedSecret = sharedSecret }).ConfigureAwait(true);
        });
    }

    // A malformed entry is simply a range that never matches anything — `NodeVisibilityPolicy` skips what does not
    // parse as a CIDR — so there is nothing here worth validating before it reaches disk (AC-793).
    partial void OnAllowedDiscoveryRangesTextChanged(string value)
    {
        if (_loadingNodeEndpoint || SuspendPersistence || nodeEndpointSettings is null)
        {
            return;
        }

        _ = _PersistAsync("the discovery ranges", "servers", async () =>
        {
            var ranges = value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            var current = await nodeEndpointSettings.LoadAsync().ConfigureAwait(true);
            await nodeEndpointSettings.SaveAsync(current with { AllowedDiscoveryRanges = ranges }).ConfigureAwait(true);
        });
    }


    private static McpServerConfig _BuildNodeRow(string nodeName, string serverName, string url, string bearer, string fingerprint) =>
        new()
        {
            Id = McpServerIdentity.NewId(),
            Name = NodeServerName.For(nodeName, serverName),
            IsNodeConnection = true,
            Transport = McpTransport.Http,
            // Only the in-process tool loop builds its own HTTP transport (`McpToolProvider`), so only it can be
            // told which certificate to trust.
            Scope = McpServerScope.LocalOnly,
            Url = url,
            Auth = McpServerAuth.ApiKey,
            ApiKey = bearer,
            PinnedCertificateFingerprint = fingerprint,
        };

    // Replaces this node's rows with `rows` in one save. Takes an already-loaded registry when the caller has
    // one (ConnectToServerAsync's own clash-check), so a connect does not pay for the load twice.
    private async Task _StoreNodeRowsAsync(string nodeName, IEnumerable<McpServerConfig> rows, IReadOnlyList<McpServerConfig>? existing = null)
    {
        if (mcpServers is null)
        {
            return;
        }

        existing ??= await mcpServers.LoadAsync().ConfigureAwait(true);
        var prefix = NodeServerName.PrefixFor(nodeName);
        var kept = existing.Where(server => !server.Name.StartsWith(prefix, StringComparison.Ordinal)).ToList();
        kept.AddRange(rows);

        await mcpServers.SaveAsync(kept).ConfigureAwait(true);
    }

    // AC-1458: host and port apart, with the default port prefilled.

    private static readonly string DefaultConnectPort =
        (NodeEndpointSettings.DefaultPort + NodeEndpointSettings.McpPortOffset).ToString(CultureInfo.InvariantCulture);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ConnectFingerprintHint))]
    private string _connectNodeName = "";

    [ObservableProperty]
    private string _connectHost = "";

    [ObservableProperty]
    private string _connectPort = DefaultConnectPort;

    // Never left standing once a connect succeeds — see ConnectToServerAsync.
    [ObservableProperty]
    private string _connectKey = "";

    [ObservableProperty]
    private string _connectFingerprint = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ConnectStatus), nameof(HasConnectOutcome))]
    private string _connectStatusTitle = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ConnectStatus), nameof(HasConnectStatusDetail))]
    private string _connectStatusDetail = "";

    // Drives the outcome's look: the glyph and the tone of the box the view draws around it.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ConnectGlyph), nameof(IsConnectSuccess), nameof(IsConnectRefusal), nameof(IsConnectUnanswered))]
    private ConnectOutcome _connectOutcome;

    // AC-1458: the entered fingerprint beside the server's, shown once the TLS handshake has been reached.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowConnectFingerprints))]
    private string _connectEnteredFingerprint = "";

    [ObservableProperty]
    private string _connectPresentedFingerprint = "";

    [ObservableProperty]
    private bool _connectFingerprintsMatch;

    [ObservableProperty]
    private bool _isConnectBusy;

    // The whole outcome as one sentence, what the tests read; the view draws the title and the detail apart.
    public string ConnectStatus => ConnectStatusDetail.Length == 0 ? ConnectStatusTitle : $"{ConnectStatusTitle} {ConnectStatusDetail}";

    public bool HasConnectOutcome => ConnectStatusTitle.Length > 0;

    public bool HasConnectStatusDetail => ConnectStatusDetail.Length > 0;

    public string ConnectGlyph => ConnectOutcome switch
    {
        ConnectOutcome.Connected => "✓",
        ConnectOutcome.Refused => "✕",
        ConnectOutcome.Unanswered => "○",
        _ => "",
    };

    public bool IsConnectSuccess => ConnectOutcome == ConnectOutcome.Connected;

    public bool IsConnectRefusal => ConnectOutcome == ConnectOutcome.Refused;

    public bool IsConnectUnanswered => ConnectOutcome == ConnectOutcome.Unanswered;

    public bool ShowConnectFingerprints => ConnectEnteredFingerprint.Length > 0;

    public string ConnectFingerprintHint =>
        $"From the server's startup log or docker logs {(string.IsNullOrWhiteSpace(ConnectNodeName) ? "<name>" : ConnectNodeName.Trim())}.";

    // AC-1352: save only after the address, key and certificate pin pass the probe.
    [RelayCommand]
    private async Task ConnectToServerAsync()
    {
        if (nodeSessions is null
            || string.IsNullOrWhiteSpace(ConnectHost)
            || string.IsNullOrWhiteSpace(ConnectKey)
            || string.IsNullOrWhiteSpace(ConnectFingerprint))
        {
            return;
        }

        _ShowConnectOutcome(ConnectOutcome.None, "", "");

        if (!int.TryParse(ConnectPort.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var typedPort) || typedPort is < 1 or > 65535)
        {
            _ShowConnectOutcome(ConnectOutcome.Other, "The port must be a number from 1 to 65535.", "");
            return;
        }

        // Named here, ahead of the try, so a parse failure below can still report against something readable.
        var nodeName = string.IsNullOrWhiteSpace(ConnectNodeName) ? ConnectHost.Trim() : ConnectNodeName.Trim();
        var where = $"{ConnectHost.Trim()}:{typedPort}";
        var fingerprint = NodePairingCode.Normalize(_WithoutAlgorithm(ConnectFingerprint.Trim()));

        IsConnectBusy = true;
        try
        {
            var (host, mcpUrl) = _ParseConnectAddress(ConnectHost, typedPort);
            nodeName = string.IsNullOrWhiteSpace(ConnectNodeName) ? host : ConnectNodeName.Trim();
            where = $"{host}:{mcpUrl.Port}";
            var url = mcpUrl.ToString();

            IReadOnlyList<McpServerConfig>? existing = null;
            if (mcpServers is not null)
            {
                existing = await mcpServers.LoadAsync().ConfigureAwait(true);
                var prefix = NodeServerName.PrefixFor(nodeName);

                // A typo that happens to match an existing name must not silently overwrite it — only the same
                // address may replace what is there, which is rotating a key rather than a mistake (AC-1352).
                if (existing.FirstOrDefault(server => server.Name.StartsWith(prefix, StringComparison.Ordinal)
                        && !string.Equals(server.Url, url, StringComparison.Ordinal)) is { } clash)
                {
                    _ShowConnectOutcome(ConnectOutcome.Other, $"\"{nodeName}\" is already connected at {clash.Url}. Use a different name, or fix the address to reconnect to that one.", "");
                    return;
                }
            }

            var row = _BuildNodeRow(nodeName, NodeServerName.SessionsServerName, url, ConnectKey, fingerprint);
            var probe = await nodeSessions.ProbeAsync(row).ConfigureAwait(true);
            await _StoreNodeRowsAsync(nodeName, [row with { KeyExpiresAt = probe.WhoAmI?.ExpiresAt }], existing).ConfigureAwait(true);
            ConnectKey = "";
            _ShowConnectOutcome(ConnectOutcome.Connected, $"Connected to \"{nodeName}\".", _DescribeKey(probe.WhoAmI));
            _ShowFingerprints(fingerprint, probe.PresentedFingerprint);
            await _LoadPairedNodesAsync().ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is HttpRequestException or SocketException or NotSupportedException
            or System.Text.Json.JsonException or OperationCanceledException or InvalidOperationException or UriFormatException)
        {
            _ShowConnectFailure(nodeName, where, ex);
        }
        finally
        {
            IsConnectBusy = false;
        }
    }

    // Empties the form back to how it opened, port and all.
    [RelayCommand]
    private void CancelConnect()
    {
        ConnectNodeName = "";
        ConnectHost = "";
        ConnectPort = DefaultConnectPort;
        ConnectKey = "";
        ConnectFingerprint = "";
        _ShowConnectOutcome(ConnectOutcome.None, "", "");
    }

    // AC-1458: "Enter a new key" on a card whose key ran out — the same server, only the key left to type.
    private void _PrefillConnect(string nodeName, McpServerConfig row)
    {
        ConnectNodeName = nodeName;
        if (Uri.TryCreate(row.Url, UriKind.Absolute, out var url))
        {
            ConnectHost = url.Host;
            ConnectPort = url.Port.ToString(CultureInfo.InvariantCulture);
        }

        ConnectKey = "";
        ConnectFingerprint = row.PinnedCertificateFingerprint ?? "";
        _ShowConnectOutcome(ConnectOutcome.None, "", "");
    }

    // DEP-208: a wrong key, a revoked or expired one and a locked-out address are one answer from the server, and
    // so one sentence here — never refined into which of them it was.
    private void _ShowConnectFailure(string nodeName, string where, Exception exception)
    {
        if (NodeConnectionFailure.Find<NodeCertificatePinMismatchException>(exception) is { } mismatch)
        {
            _ShowConnectOutcome(
                ConnectOutcome.Refused,
                "This is not the server you meant.",
                "Its certificate does not match the fingerprint you entered. Not connected, and your key was not sent.");
            _ShowFingerprints(mismatch.ExpectedFingerprint, mismatch.PresentedFingerprint ?? "");
            return;
        }

        if (exception is HttpRequestException { StatusCode: HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden })
        {
            _ShowConnectOutcome(
                ConnectOutcome.Refused,
                "The server refused this key.",
                "Check that you copied the whole key. If it was revoked or has expired, ask for a new one.");
            return;
        }

        if (exception is InvalidOperationException { InnerException: null })
        {
            // The node answered and refused in its own words (ProbeAsync's list_node_sessions error) — shown
            // verbatim, the same posture NodeSessionsClient takes for a node's own refusal elsewhere.
            _ShowConnectOutcome(ConnectOutcome.Other, exception.Message, "");
            return;
        }

        if (NodeConnectionFailure.Find<SocketException>(exception) is not null
            || NodeConnectionFailure.Find<OperationCanceledException>(exception) is not null)
        {
            _ShowConnectOutcome(ConnectOutcome.Unanswered, $"No answer from {where}.", "Is Tailscale on, and is the container running?");
            return;
        }

        _ShowConnectOutcome(ConnectOutcome.Other, NodeConnectionFailure.Describe(nodeName, exception, NodeConnectionFailure.DefaultBudget), "");
    }

    // Clears the fingerprint comparison too; a mismatch puts its own back right after.
    private void _ShowConnectOutcome(ConnectOutcome outcome, string title, string detail)
    {
        _ShowFingerprints("", "");
        ConnectOutcome = outcome;
        ConnectStatusTitle = title;
        ConnectStatusDetail = detail;
    }

    private void _ShowFingerprints(string entered, string presented)
    {
        ConnectFingerprintsMatch = entered.Length > 0 && string.Equals(entered, presented, StringComparison.Ordinal);
        ConnectPresentedFingerprint = presented.Length == 0 ? "" : _Abbreviate(presented);
        ConnectEnteredFingerprint = entered.Length == 0 ? "" : _Abbreviate(entered);
    }

    // `4F:9A:21:C7 … E0:3B:58:D2`: the first and last four bytes, as the mockup and a startup log line are compared.
    private static string _Abbreviate(string fingerprint)
    {
        var pairs = fingerprint.Chunk(2).Select(pair => new string(pair)).ToArray();
        return pairs.Length <= 8
            ? string.Join(':', pairs)
            : $"{string.Join(':', pairs[..4])} … {string.Join(':', pairs[^4..])}";
    }

    // `Key "<label>" · <capability> · holds the assistant · expires <date>.` — each part only when the server said it.
    private static string _DescribeKey(NodeWhoAmI? who)
    {
        if (who is null)
        {
            return "";
        }

        List<string> parts = [$"Key \"{who.Label}\"", who.Capability];
        if (who.HoldsAssistant)
        {
            parts.Add("holds the assistant");
        }

        if (who.ExpiresAt is { } expiresAt)
        {
            parts.Add($"expires {expiresAt.ToLocalTime().ToString("d MMM", CultureInfo.InvariantCulture)}");
        }

        return string.Join(" · ", parts) + ".";
    }

    // The spelling "SHA256:4F:9A:…" pasted whole: the algorithm's name is not part of the fingerprint.
    private static string _WithoutAlgorithm(string fingerprint) =>
        fingerprint.StartsWith("SHA256:", StringComparison.OrdinalIgnoreCase) ? fingerprint["SHA256:".Length..] : fingerprint;

    // A bare host takes the port field; a pasted host:port or URL keeps its own port. A bare IPv6 address goes
    // through UriBuilder, which brackets it, so its colons are never read as a port (AC-1352).
    private static (string Host, Uri McpUrl) _ParseConnectAddress(string address, int port)
    {
        var trimmed = address.Trim();
        if (IPAddress.TryParse(trimmed, out var ip) && ip.AddressFamily == AddressFamily.InterNetworkV6 && !trimmed.StartsWith('['))
        {
            return (trimmed, new UriBuilder("https", trimmed, port, "mcp").Uri);
        }

        var withScheme = trimmed.Contains("://", StringComparison.Ordinal) ? trimmed : $"https://{trimmed}";
        var uri = new Uri(withScheme, UriKind.Absolute);
        return (uri.Host, new UriBuilder("https", uri.Host, uri.IsDefaultPort ? port : uri.Port, "mcp").Uri);
    }

    // Dismisses the awareness banner for the credentials now in the file (AC-41). Hides it at once, then persists
    // the dismissal so it stays hidden across restarts — until a new credential changes the set and brings it back.
    [RelayCommand]
    private async Task DismissBannerAsync()
    {
        ShowUnprotectedBanner = false;
        await protection.DismissUnprotectedWarningAsync().ConfigureAwait(true);
    }

    // AC-1879: Windows Hello as a second way to unlock; the Options window sets the signer, which owns the prompt.
    public ISecretKeySigner? HelloSigner { get; set; }

    // Only on Windows 11 24H2+ with Hello set up, and only while encryption is on.
    [ObservableProperty]
    private bool _helloOffered;

    [ObservableProperty]
    private bool _helloEnabled;

    [ObservableProperty]
    private string _helloStatus = "";

    // While Hello is being set up or removed: the checkbox waits and a bar shows that it is working (Raymond 31-10).
    [ObservableProperty]
    private bool _isHelloBusy;

    // Why turning Hello on or off did not go through, shown under the checkbox; empty when it did.
    [ObservableProperty]
    private string _helloError = "";

    // Called when the Options window opens and hands over its signer; only the Hello row is read again.
    [RelayCommand]
    private async Task RefreshHelloAsync() =>
        await _ShowHelloAsync(await protection.GetStatusAsync().ConfigureAwait(true)).ConfigureAwait(true);

    private async Task _ShowHelloAsync(SecretProtectionStatus status)
    {
        HelloOffered = status.Enabled && HelloSigner is { } signer && await signer.IsAvailableAsync().ConfigureAwait(true);

        // A PIN reset or a cleared TPM took the key: the box shows that, and ticking it sets Hello up again. The stored
        // entry stays, as at the unlock, for a computer that does still hold the key (AC-1879).
        var keyGone = status.HelloEnabled && HelloOffered && HelloSigner is { } holder
            && status.HelloKeyName is { } keyName && !await holder.ExistsAsync(keyName).ConfigureAwait(true);
        HelloEnabled = status.HelloEnabled && !keyGone;
        HelloError = keyGone
            ? "The Windows Hello key is no longer on this computer. Tick the box to set Windows Hello up again."
            : HelloError;
        HelloStatus = !HelloEnabled
            ? "Off. Turn it on to unlock with your face, fingerprint or PIN instead of typing the password."
            : status.HelloInSoftware
                ? "On — no security chip found, so Windows keeps the key in software and only your Windows PIN protects it. Your password keeps working."
                : "On — bound to this computer’s security chip (TPM). Your password keeps working.";
    }

    // The Options window asks for the password, a view's job; null means the operator cancelled.
    public Func<Task<string?>>? AskHelloPassword { get; set; }

    // The checkbox: on asks the password, then Windows once; off removes the key. The box then mirrors what is stored.
    [RelayCommand]
    private async Task ToggleHelloAsync()
    {
        try
        {
            if (HelloEnabled)
            {
                IsHelloBusy = true;
                await DisableHelloAsync().ConfigureAwait(true);
            }
            else if (AskHelloPassword is { } ask && await ask().ConfigureAwait(true) is { } password)
            {
                IsHelloBusy = true;
                await EnableHelloAsync(password).ConfigureAwait(true);
            }
        }
        finally
        {
            IsHelloBusy = false;
            OnPropertyChanged(nameof(HelloEnabled));
        }
    }

    // Runs straight away, like the other encryption actions. A wrong password reads as at the unlock.
    public async Task EnableHelloAsync(string password)
    {
        if (HelloSigner is not { } signer)
        {
            return;
        }

        HelloError = "";
        try
        {
            // Off the UI thread: checking the password derives the key (210k PBKDF2 rounds), which froze the window.
            if (!await Task.Run(() => protection.EnableHelloAsync(password, signer)).ConfigureAwait(true))
            {
                HelloError = $"Windows Hello was not turned on: {signer.LastFailure ?? "the Windows prompt was cancelled"}.";
            }
        }
        catch (SecretProtectionException exception)
        {
            HelloError = exception.Message;
        }
        catch (Exception exception)
        {
            Logging.LifecycleLog.Write($"Turning Windows Hello on failed: {exception}");
            HelloError = "Windows Hello was not turned on: something went wrong; the cockpit log has the details.";
        }

        if (HelloError.Length > 0)
        {
            Logging.LifecycleLog.Write(HelloError);
        }

        await RefreshAsync().ConfigureAwait(true);
    }

    public async Task DisableHelloAsync()
    {
        if (HelloSigner is { } signer)
        {
            await protection.DisableHelloAsync(signer).ConfigureAwait(true);
        }

        await RefreshAsync().ConfigureAwait(true);
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
        // The Hello key wraps the key that is about to stop existing (AC-1879).
        if (HelloSigner is { } signer)
        {
            await protection.DisableHelloAsync(signer).ConfigureAwait(true);
        }

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

            // The password change dropped the Hello wrap; the OS key that made it goes too (AC-1879).
            if (HelloSigner is { } signer)
            {
                await protection.DisableHelloAsync(signer).ConfigureAwait(true);
            }

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

// AC-1458: which of the connect dialog's outcomes is on screen; `Other` is a plain sentence without a box tone.
public enum ConnectOutcome
{
    None,
    Connected,
    Refused,
    Unanswered,
    Other,
}
