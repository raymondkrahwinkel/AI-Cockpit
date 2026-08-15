using System.Collections.ObjectModel;
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
// ponytail: no live updates — the card reads when the Options window opens and when Refresh is pressed. Upgrade
// path is a poll or a push over the same connection, if watching a node's work turns out to be a thing anybody
// does for longer than a glance.
public sealed partial class NodeSessionsViewModel(INodeSessionsClient client, string nodeName) : ObservableObject
{
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
