using System.Security.Cryptography;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Abstractions.Secrets;
using Cockpit.Core.Abstractions.Terminal;
using Cockpit.Core.Mcp;
using Cockpit.Core.Secrets;
using Cockpit.Core.Terminal;

namespace Cockpit.App.ViewModels;

// The Security tab: whether the credentials in `cockpit.json` are encrypted, and the migration that runs
// when the operator changes their mind either way.
//
// Both directions migrate, and both are shown while they happen. The work is usually over in a blink, but this
// is the one operation that rewrites every credential the operator has: a screen that flickers is better than
// an app that goes quiet while it does that.
public sealed partial class SecurityOptionsViewModel(
    ISecretProtectionService protection,
    IScreenLockSettingsStore? screenLockSettings = null,
    ITerminalAccessSwitch? terminalAccessSwitch = null,
    ITerminalAccessSettingsStore? terminalAccessSettings = null,
    INodeEndpointSettingsStore? nodeEndpointSettings = null,
    IEnumerable<ICockpitInternalMcpProvider>? mcpEndpointHosts = null) : ObservableObject
{
    // True only while RefreshAsync seeds the toggle from disk, so setting the property then does not turn around and
    // write the same value straight back.
    private bool _loadingTerminalAccess;

    // True only while RefreshAsync seeds the node toggle from disk (AC-790) — same guard, same reason, as
    // _loadingTerminalAccess above.
    private bool _loadingNodeEndpoint;

    [ObservableProperty]
    private bool _isEncrypted;

    // AC-5: whether AI-Cockpit locks itself when the operating system locks (screen lock), re-asking for the
    // encryption password just as at startup. On by default, and only shown while encryption is on — there is
    // nothing to re-ask for otherwise. Its row is hidden, not disabled, when encryption is off: a control that does
    // nothing is worse than an absent one. Persisted the moment it changes, in its own `ScreenLock` section so
    // it survives turning encryption off and on again. Without a store (design-time/unit-test) it is an in-memory
    // default that simply does not persist.
    [ObservableProperty]
    private bool _lockWithOperatingSystem = true;

    // The terminal-access master switch (AC-34): off by default, an opt-in. While off, the `cockpit-terminal`
    // MCP is not advertised to any session — for an agent the feature does not exist. Turning it on makes it
    // reachable, still behind a per-pane Approve/Deny. Persisted, and flipped live so the next session sees the
    // change without a restart.
    [ObservableProperty]
    private bool _terminalAccessEnabled;

    // The network-node master switch (AC-790): off by default. While off, every mounted MCP endpoint stays
    // loopback-only. Turning it on takes effect on the next launch — unlike the terminal-access toggle above, this
    // one reconfigures Kestrel listeners at startup (CockpitMcpEndpointHost.MountAsync), so there is nothing to
    // flip live.
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

    [ObservableProperty]
    private bool _isMigrating;

    [ObservableProperty]
    private string _migrationCaption = string.Empty;

    [ObservableProperty]
    private double _migrationProgress;

    [ObservableProperty]
    private string? _status;

    // Whether the app-level awareness banner (AC-41) should show: encryption is off and the settings hold at
    // least one credential in the clear that the operator has not dismissed the warning for. Bound by
    // `CockpitView.axaml`'s banner, and re-read on every `RefreshAsync` — startup, a save that
    // wrote a new credential, and after either migration — so a single property is the whole of its visibility.
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

        // AC-790: same "absent in design-time/unit-test graph" shape as terminal access above.
        if (nodeEndpointSettings is not null)
        {
            var node = await nodeEndpointSettings.LoadAsync().ConfigureAwait(true);
            _loadingNodeEndpoint = true;
            NodeEndpointEnabled = node.Enabled;
            _loadingNodeEndpoint = false;
            NodeEndpointSharedSecret = node.SharedSecret;
            NodeEndpointAddressText = _ResolveNodeEndpointAddressText(node.Enabled);
        }
    }

    // Node binding off: normally nothing to show — unless this run already bound an off-loopback listener earlier
    // (the switch was on at this run's own startup, then turned off just now): Kestrel is only reconfigured on the
    // next launch, so that listener — and the secret it still accepts — stays live regardless of the toggle. Saying
    // so beats a blank line that reads as "access revoked" when it was not.
    // On: the host has not (yet) reported an off-loopback address for any mounted endpoint — either this run has
    // not restarted since the switch turned on (MountAsync reads the setting once, at mount time), or
    // NodeReachableAddress found no LAN-facing IPv4 on this machine — the two are indistinguishable from here, so
    // one explanation covers both rather than guessing which applies.
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

    // Persists the AC-5 toggle the moment it changes. The load above sets it too, which is why that path suppresses this — a seed from disk must not be a write back to disk.
    partial void OnLockWithOperatingSystemChanged(bool value)
    {
        if (_loadingLockSetting || screenLockSettings is null)
        {
            return;
        }

        _ = screenLockSettings.SaveAsync(new ScreenLockSettings { LockWhenOperatingSystemLocks = value });
    }

    // The toggle changed. Flip the live switch at once (so the next session sees it without a restart) and persist,
    // unless we are only seeding the value from disk in RefreshAsync (or the store is absent in a test graph).
    async partial void OnTerminalAccessEnabledChanged(bool value)
    {
        if (_loadingTerminalAccess || terminalAccessSettings is null)
        {
            return;
        }

        if (terminalAccessSwitch is not null)
        {
            terminalAccessSwitch.Enabled = value;
        }

        await terminalAccessSettings.SaveAsync(new TerminalAccessSettings { Enabled = value }).ConfigureAwait(true);
    }

    // The node toggle changed (AC-790). Unlike terminal access above, this never flips anything live — the
    // Kestrel listeners it governs are only reconfigured at the next launch (CockpitMcpEndpointHost.MountAsync) —
    // so this only persists. Turning it on for the first time (no secret saved yet) mints one; turning it off
    // leaves whatever secret is there untouched, so a second Cockpit that already typed it in still works the
    // next time binding is turned back on.
    async partial void OnNodeEndpointEnabledChanged(bool value)
    {
        if (_loadingNodeEndpoint || nodeEndpointSettings is null)
        {
            return;
        }

        var sharedSecret = NodeEndpointSharedSecret is { Length: > 0 }
            ? NodeEndpointSharedSecret
            : Convert.ToHexString(RandomNumberGenerator.GetBytes(32));

        NodeEndpointSharedSecret = sharedSecret;
        NodeEndpointAddressText = _ResolveNodeEndpointAddressText(value);
        await nodeEndpointSettings.SaveAsync(new NodeEndpointSettings { Enabled = value, SharedSecret = sharedSecret }).ConfigureAwait(true);
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
