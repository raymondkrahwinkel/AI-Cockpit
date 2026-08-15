using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cockpit.Core.Abstractions.Mcp;

namespace Cockpit.App.ViewModels;

// One paired node's sessions on the controller's screen (AC-795, criterion 1): what runs over there, with a way to
// start one and to stop one.
//
// *Why this lives on the Security tab and not in the session grid.* The epic's own risk is criterion 3 — an action
// landing on the wrong row, the AC-561 class of defect — and two machines' sessions in one list is precisely the
// arrangement where that happens: same names, same shapes, one badge between them. Here the separation is
// structural rather than typographic. This card only ever exists inside the pairing it belongs to, headed by the
// node's name; no local session appears in it, and no pane id from it can be typed anywhere that would act on this
// machine. It is also where the operator already is when they think about a node, since the pairing itself, its
// address and its scope are all on this tab.
// AC-796: the card now also polls on its own (see `StartPolling`) rather than only reading on open and on Refresh
// — a node that drops out or comes back shows up without the operator doing anything, which is what criterion 1
// asks for.
// ponytail: a fixed interval, not a backoff. A node that has been unreachable for an hour is polled exactly as
// often as one that answered a second ago, which is a wasted call every time but never a wrong one. Upgrade path
// is backing off after a run of failures, if a paired node being off for long stretches turns out to be common.
public sealed partial class NodeSessionsViewModel(INodeSessionsClient client, string nodeName) : ObservableObject, IDisposable
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
        }
        finally
        {
            IsBusy = false;
        }
    }

    // Starts the poll (AC-796). Its own method rather than constructor logic: a `DispatcherTimer` only ever ticks
    // on the thread that constructed it, and building one outside a running Avalonia dispatcher — a plain unit
    // test, exactly what `Cockpit.Core.Tests`' own banned-symbols rule exists to keep out of that project — is the
    // class of bug that stays quiet until it hangs a run. Call this once, from the UI thread, after the card is
    // built; a test that only wants `RefreshAsync()` never has to touch it.
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
            refusal = await client.StartAsync(
                NodeName,
                profile.Label,
                SelectedProject?.Id,
                NewSessionPrompt,
                NewSessionName).ConfigureAwait(true);

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
