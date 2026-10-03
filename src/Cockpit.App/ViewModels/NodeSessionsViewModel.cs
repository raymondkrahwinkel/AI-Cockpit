using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cockpit.App.Services;
using Cockpit.Core.Abstractions.Mcp;

namespace Cockpit.App.ViewModels;

// Here the separation is structural rather than typographic (AC-795, AC-561, AC-796).
// AC-1322/AC-1324: the two relays ride the same poll — mail from the node's agents, and the node's open Allow/Deny
// questions drawn in the assistant's conversation; absent in the design-time/unit-test graph, where the card only lists.
public sealed partial class NodeSessionsViewModel(
    INodeSessionsClient client,
    string nodeName,
    INodeInboxRelay? inboxRelay = null,
    NodePermissionRelay? permissionRelay = null,
    IBehaviourMemorySync? behaviourSync = null) : ObservableObject, IDisposable
{
    // 20s: often enough that a dropout or a return shows up without feeling like a bug report, rarely enough that
    // it stays a handshake and three small calls rather than something the node's operator would notice.
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(20);

    private DispatcherTimer? _pollTimer;

    // AC-1458: only redraws the Reconnecting line's countdown; the 20s poll above stays the only retry.
    private DispatcherTimer? _countdownTimer;

    private DateTimeOffset _connectedSince;
    private DateTimeOffset _lostAt;
    private DateTimeOffset _nextAttemptAt;
    private DateTimeOffset _disconnectedAt;
    private int _retries;
    private long _latencyMs;
    private string? _version;

    // AC-1458: the running refresh's token; Disconnect cancels it, so nothing more leaves for this node.
    private CancellationTokenSource? _tickCancel;

    public string NodeName { get; } = nodeName;

    // AC-1458: the row's address, for "Tailscale" or "network", and its key's expiry: stored at connect time and
    // brought up to date by every poll's /whoami, so a key renewed or replaced on the server is followed.
    public string? Url { get; init; }

    public DateTimeOffset? KeyExpiresAt { get; set; }

    // Stores a changed expiry on the row, through the page's own write path.
    public Func<DateTimeOffset?, Task>? KeyExpiryChanged { get; init; }

    // "Enter a new key": hands the connect form this row, with only the key left to type.
    public Action? EnterNewKey { get; init; }

    // Told when the operator disconnects (the time) or connects again (null), so it outlives a rebuilt card.
    public Action<DateTimeOffset?>? DisconnectedChanged { get; init; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ConnectionLabel), nameof(IsConnected), nameof(IsReconnecting), nameof(IsKeyExpired), nameof(IsDisconnected), nameof(CanDisconnect))]
    private NodeConnectionState _connectionState;

    [ObservableProperty]
    private string _connectionDetail = "";

    public string ConnectionLabel => ConnectionState switch
    {
        NodeConnectionState.Connected => "Connected",
        NodeConnectionState.Reconnecting => "Reconnecting",
        NodeConnectionState.KeyExpired => "Key expired",
        NodeConnectionState.Disconnected => "Disconnected",
        _ => "",
    };

    public bool IsConnected => ConnectionState == NodeConnectionState.Connected;

    public bool IsReconnecting => ConnectionState == NodeConnectionState.Reconnecting;

    public bool IsKeyExpired => ConnectionState == NodeConnectionState.KeyExpired;

    public bool IsDisconnected => ConnectionState == NodeConnectionState.Disconnected;

    public bool CanDisconnect => ConnectionState is NodeConnectionState.Connected or NodeConnectionState.Reconnecting;

    // The sessions on the node. Deliberately not merged with anything local — see the remarks above.
    public ObservableCollection<NodeSessionRow> Sessions { get; } = [];

    // What this controller has been allowed to start there (AC-794's grant, as the node reports it). Empty is the
    // ordinary state of a fresh pairing, and the card says so rather than looking broken.
    public ObservableCollection<NodeProfileChoice> Profiles { get; } = [];

    public ObservableCollection<NodeProjectChoice> Projects { get; } = [];

    [ObservableProperty]
    private NodeProfileChoice? _selectedProfile;

    [ObservableProperty]
    private NodeProjectChoice? _selectedProject;

    [ObservableProperty]
    private string _newSessionPrompt = "";

    [ObservableProperty]
    private string _newSessionName = "";

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _status = "";

    // AC-1327 criterion 2: starts reachable so the very first successful refresh does not read as a "node-back"
    // nobody asked about — only an edge after that fires a message, never a level.
    private bool _wasReachable = true;

    // AC-1327 criterion 2: one missed 20s poll is a blip, not a drop — mirrors the node-side 60s fallback, which
    // does not tip on a single miss either. Counts consecutive failures; the second is the threshold.
    private int _consecutiveMisses;

    // AC-1330: its own flag rather than `_wasReachable` above — starting false makes this card's very first
    // successful read count as an edge too, which is exactly the "once at launch" case, where `_wasReachable`
    // deliberately must not (that one exists to keep the very first read from reading as a node-back message).
    private bool _wasReachableForBehaviourSync;

    [RelayCommand]
    public async Task RefreshAsync()
    {
        // AC-1458: an operator's disconnect and a key past its expiry both mean no attempt at all.
        if (ConnectionState == NodeConnectionState.Disconnected)
        {
            return;
        }

        if (KeyExpiresAt is { } expiresAt && expiresAt <= DateTimeOffset.Now)
        {
            ConnectionState = NodeConnectionState.KeyExpired;
            _DescribeConnection();
            return;
        }

        _tickCancel?.Dispose();
        _tickCancel = new CancellationTokenSource();
        var stop = _tickCancel.Token;

        IsBusy = true;
        try
        {
            var attemptAt = DateTimeOffset.Now;
            var started = Stopwatch.GetTimestamp();
            var snapshot = await client.ReadAsync(NodeName, stop).ConfigureAwait(true);
            var latency = Stopwatch.GetElapsedTime(started);

            // Checked after every await: a disconnect mid-refresh must neither flip the line back nor send anything more.
            if (stop.IsCancellationRequested)
            {
                return;
            }

            _NoteAttempt(snapshot.Error is not { Length: > 0 }, attemptAt, latency);

            // What the operator had picked, so rebuilding the dropdowns does not quietly change what the next
            // Start will run. Both Start and Stop refresh when they are done, so without this a second start goes
            // out under the first profile in the list and with no project — neither of which anybody chose.
            var hadProfile = SelectedProfile?.Label;
            var hadProject = SelectedProject?.Id;

            Sessions.Clear();
            Profiles.Clear();
            Projects.Clear();

            if (snapshot.Error is { Length: > 0 } error)
            {
                // A node that is off or off the network is an ordinary state of this feature, not a fault to
                // swallow: the lists stay empty and the reason is on screen, so nothing here reads as "nothing is
                // running there" when the truth is "nobody answered".
                Status = error;
                _NoteReachability(reachable: false, sessionCount: 0);
                return;
            }

            foreach (var session in snapshot.Sessions)
            {
                Sessions.Add(session);
            }

            foreach (var profile in snapshot.Profiles)
            {
                Profiles.Add(new NodeProfileChoice(profile.Label, profile.Purpose));
            }

            // "No project" first and selected: a session that names none runs on its profile's own folder, which is
            // the right answer more often than any single project would be.
            Projects.Add(new NodeProjectChoice(null, "No project"));
            foreach (var project in snapshot.Projects)
            {
                Projects.Add(new NodeProjectChoice(project.Id, project.Name));
            }

            // The previous choice where it still exists — a profile the node's operator has since unticked is gone
            // from the list, and falling back to the first one is then the honest answer rather than keeping a
            // selection that would be refused.
            SelectedProfile = Profiles.FirstOrDefault(profile => string.Equals(profile.Label, hadProfile, StringComparison.Ordinal))
                ?? Profiles.FirstOrDefault();
            SelectedProject = Projects.FirstOrDefault(project => string.Equals(project.Id, hadProject, StringComparison.Ordinal))
                ?? Projects.FirstOrDefault();
            Status = Sessions.Count == 0 ? "Nothing is running on this node that you may see." : "";

            permissionRelay?.Reconcile(snapshot);

            // After the lists, and only once the node answered them: a node that is off costs one timeout, not two.
            if (inboxRelay is not null)
            {
                await inboxRelay.PollAsync(NodeName, stop).ConfigureAwait(true);
                if (stop.IsCancellationRequested)
                {
                    return;
                }
            }

            if (_NoteReachability(reachable: true, sessionCount: Sessions.Count) && behaviourSync is not null)
            {
                await behaviourSync.RunAsync(NodeName, stop).ConfigureAwait(true);
                if (stop.IsCancellationRequested)
                {
                    return;
                }
            }

            // Null from a pairing or an older node: the line then carries no version, and the stored expiry stands.
            var who = await client.ReadWhoAmIAsync(NodeName, stop).ConfigureAwait(true);
            if (stop.IsCancellationRequested)
            {
                return;
            }

            _version = who?.Version;
            if (who is not null && who.ExpiresAt != KeyExpiresAt)
            {
                KeyExpiresAt = who.ExpiresAt;
                if (KeyExpiryChanged is { } store)
                {
                    await store(who.ExpiresAt).ConfigureAwait(true);
                }
            }

            _DescribeConnection();
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            // Disconnected while a call was out: what it would have said no longer matters.
        }
        finally
        {
            IsBusy = false;
        }
    }

    // AC-1327 criterion 2: fires only on the reachable↔unreachable edge, past the second consecutive miss, using
    // the same relay/Deliver path as the mail poll above — `InboxWakeScheduler` wakes for this post like any other.
    // AC-1330: the bool return is that same edge, for the behaviour sync — its caller awaits it, this stays sync.
    private bool _NoteReachability(bool reachable, int sessionCount)
    {
        if (reachable)
        {
            _consecutiveMisses = 0;
        }
        else if (++_consecutiveMisses < 2)
        {
            return false;
        }

        var syncBehaviour = reachable && !_wasReachableForBehaviourSync;
        _wasReachableForBehaviourSync = reachable;

        if (reachable == _wasReachable)
        {
            return syncBehaviour;
        }

        _wasReachable = reachable;
        inboxRelay?.NotifyTransition(NodeName, reachable, sessionCount, DateTimeOffset.UtcNow);
        return syncBehaviour;
    }

    // Its own method rather than constructor logic: a `DispatcherTimer` only ever ticks on the thread that constructed
    // it, and building one outside a running Avalonia dispatcher — a plain unit test, exactly what
    // `Cockpit.Core.Tests`' own banned-symbols rule exists to keep out of that project — is the class of bug that stays
    public void StartPolling()
    {
        if (_pollTimer is not null)
        {
            return;
        }

        _pollTimer = new DispatcherTimer { Interval = PollInterval };
        _pollTimer.Tick += _OnPollTick;
        _pollTimer.Start();

        _countdownTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _countdownTimer.Tick += _OnCountdownTick;
        _countdownTimer.Start();
    }

    public void Dispose()
    {
        _tickCancel?.Cancel();
        if (_countdownTimer is not null)
        {
            _countdownTimer.Stop();
            _countdownTimer.Tick -= _OnCountdownTick;
            _countdownTimer = null;
        }

        if (_pollTimer is null)
        {
            return;
        }

        _pollTimer.Stop();
        _pollTimer.Tick -= _OnPollTick;
        _pollTimer = null;
    }

    private void _OnCountdownTick(object? sender, EventArgs e)
    {
        if (ConnectionState == NodeConnectionState.Reconnecting)
        {
            _DescribeConnection();
        }
    }

    // AC-1458: stops this card's poll, and with it the relays that ride it. For this run only; the node is told
    // nothing, and what runs there keeps running.
    [RelayCommand]
    private void Disconnect()
    {
        ShowDisconnected(DateTimeOffset.Now);
        DisconnectedChanged?.Invoke(_disconnectedAt);
    }

    // Also how a rebuilt card picks up a disconnect from before: no poll, and the time it happened.
    public void ShowDisconnected(DateTimeOffset at)
    {
        Dispose();
        _disconnectedAt = at;
        ConnectionState = NodeConnectionState.Disconnected;
        _DescribeConnection();
    }

    [RelayCommand]
    private async Task ReconnectAsync()
    {
        DisconnectedChanged?.Invoke(null);
        ConnectionState = NodeConnectionState.Unknown;
        ConnectionDetail = "";
        StartPolling();
        await RefreshAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private void EnterKey() => EnterNewKey?.Invoke();

    // One poll's outcome. A miss starts or extends a Reconnecting streak; an answer ends it.
    private void _NoteAttempt(bool answered, DateTimeOffset attemptAt, TimeSpan latency)
    {
        var now = DateTimeOffset.Now;
        _nextAttemptAt = attemptAt + PollInterval;
        if (answered)
        {
            if (ConnectionState != NodeConnectionState.Connected)
            {
                _connectedSince = now;
            }

            _retries = 0;
            _latencyMs = (long)latency.TotalMilliseconds;
            ConnectionState = NodeConnectionState.Connected;
        }
        else
        {
            if (ConnectionState != NodeConnectionState.Reconnecting)
            {
                _lostAt = now;
            }

            _retries++;
            ConnectionState = NodeConnectionState.Reconnecting;
        }

        _DescribeConnection();
    }

    private void _DescribeConnection() => ConnectionDetail = ConnectionState switch
    {
        NodeConnectionState.Connected =>
            $"since {_Clock(_connectedSince)} · {_latencyMs} ms · {NetworkOf(Url)}{(_version is { Length: > 0 } version ? $" · v{version}" : "")}",
        NodeConnectionState.Reconnecting =>
            $"lost at {_Clock(_lostAt)}, retry {_retries} of ∞ in {Math.Max(0, (int)Math.Ceiling((_nextAttemptAt - DateTimeOffset.Now).TotalSeconds))} s · sessions on the server keep running",
        NodeConnectionState.KeyExpired when KeyExpiresAt is { } expiresAt =>
            $"on {expiresAt.ToLocalTime().ToString("d MMM", CultureInfo.InvariantCulture)} · reconnecting will not help",
        NodeConnectionState.Disconnected => $"by you at {_Clock(_disconnectedAt)}",
        _ => "",
    };

    private static string _Clock(DateTimeOffset at) => at.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture);

    // "Tailscale" for an address in its 100.64.0.0/10 range or a MagicDNS name, "network" for anything else.
    public static string NetworkOf(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return "network";
        }

        if (uri.Host.EndsWith(".ts.net", StringComparison.OrdinalIgnoreCase))
        {
            return "Tailscale";
        }

        return IPAddress.TryParse(uri.Host, out var ip) && ip.AddressFamily == AddressFamily.InterNetwork
            && ip.GetAddressBytes() is [100, var second, _, _] && (second & 0xC0) == 64
            ? "Tailscale"
            : "network";
    }

    // A tick that lands while the previous one is still out (a node that is slow to answer) is skipped rather than
    // queued — the same "one refresh at a time" the Start/Stop commands already lean on via `IsBusy`, and the
    // single-threaded UI dispatcher is what makes this check race-free without a lock.
    private void _OnPollTick(object? sender, EventArgs e)
    {
        if (!IsBusy)
        {
            _ = RefreshAsync();
        }
    }

    [RelayCommand]
    private async Task StartAsync()
    {
        if (SelectedProfile is not { } profile)
        {
            Status = "Pick a profile first. If the list is empty, this node's operator has not allowed any yet.";
            return;
        }

        string? refusal;
        IsBusy = true;
        try
        {
            refusal = (await client.StartAsync(
                NodeName,
                profile.Label,
                SelectedProject?.Id,
                NewSessionPrompt,
                NewSessionName).ConfigureAwait(true)).Error;

            if (refusal is null)
            {
                NewSessionPrompt = "";
                NewSessionName = "";
            }
        }
        finally
        {
            IsBusy = false;
        }

        // The list first, the outcome after: `RefreshAsync` writes `Status` of its own accord, so reporting before
        // refreshing would put the node's refusal on screen for exactly as long as it took the refresh to answer.
        await RefreshAsync().ConfigureAwait(true);

        // The node's own words when it refused — it names the profile or project to go and tick, and a tidier
        // sentence written here would lose exactly that.
        Status = refusal ?? $"Started on {NodeName}. It keeps running there even if you close this cockpit.";
    }

    [RelayCommand]
    private async Task StopAsync(NodeSessionRow? session)
    {
        if (session is null)
        {
            return;
        }

        string? refusal;
        IsBusy = true;
        try
        {
            // By the pane id of the row that was pressed, never by name: the node may well be running something
            // called the same thing as a session on this machine, and a name is not an address.
            refusal = await client.StopAsync(NodeName, session.PaneId).ConfigureAwait(true);
        }
        finally
        {
            IsBusy = false;
        }

        await RefreshAsync().ConfigureAwait(true);
        Status = refusal ?? $"Stopped '{session.Name}' on {NodeName}.";
    }
}

// One profile in the node's dropdown. Its own type rather than the wire record so the list can show the operator's
// note next to the label without the view reaching into a transport type.
public sealed record NodeProfileChoice(string Label, string? Purpose)
{
    public string Display => string.IsNullOrWhiteSpace(Purpose) ? Label : $"{Label} — {Purpose}";
}

// One project in the node's dropdown, or the "no project" row, whose `Id` is null.
public sealed record NodeProjectChoice(string? Id, string Name);

// AC-1458: what a node card's status line says. `Unknown` until the first poll has come back.
public enum NodeConnectionState
{
    Unknown,
    Connected,
    Reconnecting,
    KeyExpired,
    Disconnected,
}
