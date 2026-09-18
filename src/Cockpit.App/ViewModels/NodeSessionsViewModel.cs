using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cockpit.App.Services;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Infrastructure.Mcp;

namespace Cockpit.App.ViewModels;

// Here the separation is structural rather than typographic (AC-795, AC-561, AC-796).
// AC-1322/AC-1324: the two relays ride the same poll — mail from the node's agents, and the node's open Allow/Deny
// questions drawn in the assistant's conversation; absent in the design-time/unit-test graph, where the card only lists.
public sealed partial class NodeSessionsViewModel(
    INodeSessionsClient client,
    string nodeName,
    NodeInboxRelay? inboxRelay = null,
    NodePermissionRelay? permissionRelay = null,
    BehaviourMemorySync? behaviourSync = null) : ObservableObject, IDisposable
{
    // 20s: often enough that a dropout or a return shows up without feeling like a bug report, rarely enough that
    // it stays a handshake and three small calls rather than something the node's operator would notice.
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(20);

    private DispatcherTimer? _pollTimer;

    public string NodeName { get; } = nodeName;

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
        IsBusy = true;
        try
        {
            var snapshot = await client.ReadAsync(NodeName).ConfigureAwait(true);

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
                await inboxRelay.PollAsync(NodeName).ConfigureAwait(true);
            }

            if (_NoteReachability(reachable: true, sessionCount: Sessions.Count) && behaviourSync is not null)
            {
                await behaviourSync.RunAsync(NodeName).ConfigureAwait(true);
            }
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
    }

    public void Dispose()
    {
        if (_pollTimer is null)
        {
            return;
        }

        _pollTimer.Stop();
        _pollTimer.Tick -= _OnPollTick;
        _pollTimer = null;
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
