using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Mcp;
using Cockpit.Core.Profiles;

namespace Cockpit.Core.Tests.ViewModels;

/// <summary>
/// The node card on the Security tab (AC-795): criterion 1, that what is on screen is a node's sessions and is
/// plainly not this machine's, and criterion 3, that pressing Stop on a row stops that row.
/// </summary>
public class NodeSessionsViewModelTests
{
    private static readonly NodeSessionRow SweepOnTheNode = new("node-pane-b", "AC-795 tests", "Laptop Sonnet", "running");

    [Fact]
    public async Task Refresh_ShowsTheNodesSessionsAndWhatMayBeStartedThere()
    {
        var client = new FakeNodeSessions
        {
            Snapshot = new NodeSessionsSnapshot(
                "laptop",
                [SweepOnTheNode],
                [new NodeScopedProfileSummary("Laptop Sonnet", SessionProvider.ClaudeCli, "the laptop's own key")],
                [new NodeProjectRow("project-allowed", "Allowed")]),
        };
        var card = new NodeSessionsViewModel(client, "laptop");

        await card.RefreshAsync();

        Assert.Equal("laptop", card.NodeName);
        Assert.Equal("node-pane-b", Assert.Single(card.Sessions).PaneId);
        Assert.Equal("Laptop Sonnet — the laptop's own key", Assert.Single(card.Profiles).Display);
        // "No project" first, and selected: a session that names none runs on its profile's own folder.
        Assert.Null(card.Projects[0].Id);
        Assert.Equal(card.Projects[0], card.SelectedProject);
    }

    [Fact]
    public async Task Refresh_OnANodeThatDoesNotAnswer_SaysSo_RatherThanShowingAnEmptyNode()
    {
        // The difference that matters: "nothing is running there" and "nobody answered" look identical as an empty
        // list, and only one of them is a reason to go and look at the other machine.
        var client = new FakeNodeSessions
        {
            Snapshot = new NodeSessionsSnapshot("laptop", [], [], [], "Could not reach laptop: no route to host"),
        };
        var card = new NodeSessionsViewModel(client, "laptop");

        await card.RefreshAsync();

        Assert.Empty(card.Sessions);
        Assert.Contains("Could not reach laptop", card.Status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Stop_SendsThePaneIdOfTheRowThatWasPressed_NotAName()
    {
        // Criterion 3, at the operator's end. Two sessions carry the same name — the name is what they would be
        // recognised by out loud, and it is exactly what must not be acted on.
        var client = new FakeNodeSessions
        {
            Snapshot = new NodeSessionsSnapshot(
                "laptop",
                [new NodeSessionRow("node-pane-a", "AC-795 tests", "Laptop Sonnet", ""), SweepOnTheNode],
                [],
                []),
        };
        var card = new NodeSessionsViewModel(client, "laptop");
        await card.RefreshAsync();

        await card.StopCommand.ExecuteAsync(card.Sessions[1]);

        Assert.Equal(("laptop", "node-pane-b"), Assert.Single(client.Stopped));
        // And what happened survives the refresh that follows it, which writes this same field.
        Assert.Contains("Stopped 'AC-795 tests' on laptop", card.Status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Start_KeepsWhatWasPicked_SoASecondOneGoesOutTheSameWay()
    {
        var client = new FakeNodeSessions
        {
            Snapshot = new NodeSessionsSnapshot(
                "laptop",
                [],
                [
                    new NodeScopedProfileSummary("Laptop Haiku", SessionProvider.ClaudeCli, null),
                    new NodeScopedProfileSummary("Laptop Sonnet", SessionProvider.ClaudeCli, null),
                ],
                [new NodeProjectRow("project-allowed", "Allowed")]),
        };
        var card = new NodeSessionsViewModel(client, "laptop");
        await card.RefreshAsync();

        card.SelectedProfile = card.Profiles[1];
        card.SelectedProject = card.Projects[1];
        await card.StartCommand.ExecuteAsync(null);
        // Start refreshes when it is done, which rebuilds both dropdowns — the second start must not quietly run
        // under the first profile in the list with no project.
        await card.StartCommand.ExecuteAsync(null);

        Assert.Equal([("laptop", "Laptop Sonnet"), ("laptop", "Laptop Sonnet")], client.Started);
        Assert.Equal("project-allowed", card.SelectedProject?.Id);
    }

    [Fact]
    public async Task Start_WithoutAProfile_AsksForOne_AndCallsNothing()
    {
        var client = new FakeNodeSessions { Snapshot = new NodeSessionsSnapshot("laptop", [], [], []) };
        var card = new NodeSessionsViewModel(client, "laptop");
        await card.RefreshAsync();

        await card.StartCommand.ExecuteAsync(null);

        Assert.Empty(client.Started);
        Assert.Contains("Pick a profile", card.Status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Refresh_AfterANodeComesBackWithTheSamePaneId_ShowsOneRow_NotADuplicate()
    {
        // AC-796, criterion 3: a node that drops out and comes back is recognised as the same session — or shown
        // as a new one — never a silent duplicate. Nothing here is asked to remember the pane id across the two
        // refreshes; a fresh, fully-rebuilt list is what makes "the same row" true rather than something tracked.
        var client = new FakeNodeSessions
        {
            Snapshot = new NodeSessionsSnapshot("laptop", [], [], [], "Could not reach laptop: no route to host"),
        };
        var card = new NodeSessionsViewModel(client, "laptop");
        await card.RefreshAsync();
        Assert.Empty(card.Sessions);
        Assert.Contains("Could not reach laptop", card.Status, StringComparison.Ordinal);

        client.Snapshot = new NodeSessionsSnapshot("laptop", [SweepOnTheNode], [], []);
        await card.RefreshAsync();

        Assert.Equal("node-pane-b", Assert.Single(card.Sessions).PaneId);
        Assert.Equal("", card.Status);
    }

    [Fact]
    public void Dispose_WithoutStartPollingHavingBeenCalled_DoesNothingAndIsSafeToCallTwice()
    {
        // A card that is torn down before `StartPolling` ever ran — a rebuild of `PairedNodes` racing a card whose
        // constructor has returned but which nothing has started polling yet — must not throw for want of a timer
        // that was never built.
        var card = new NodeSessionsViewModel(
            new FakeNodeSessions { Snapshot = new NodeSessionsSnapshot("laptop", [], [], []) }, "laptop");

        card.Dispose();
        card.Dispose();
    }

    [Fact]
    public async Task Start_ThatTheNodeRefuses_ShowsTheNodesOwnWords()
    {
        // The node's refusal names the profile or project to go and tick on that machine. A tidier sentence written
        // here would lose the one detail the operator can act on.
        var client = new FakeNodeSessions
        {
            Snapshot = new NodeSessionsSnapshot(
                "laptop",
                [],
                [new NodeScopedProfileSummary("Laptop Sonnet", SessionProvider.ClaudeCli, null)],
                []),
            Refusal = "This node's operator has not allowed the profile 'Laptop Sonnet'.",
        };
        var card = new NodeSessionsViewModel(client, "laptop");
        await card.RefreshAsync();

        await card.StartCommand.ExecuteAsync(null);

        Assert.Equal("This node's operator has not allowed the profile 'Laptop Sonnet'.", card.Status);
    }

    private sealed class FakeNodeSessions : INodeSessionsClient
    {
        public required NodeSessionsSnapshot Snapshot { get; set; }

        public string? Refusal { get; set; }

        public List<(string Node, string Profile)> Started { get; } = [];

        public List<(string Node, string PaneId)> Stopped { get; } = [];

        public Task<IReadOnlyList<string>> ListNodesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>([Snapshot.NodeName]);

        public Task<NodeSessionsSnapshot> ReadAsync(string nodeName, CancellationToken cancellationToken = default) =>
            Task.FromResult(Snapshot);

        public Task<string?> StartAsync(
            string nodeName,
            string profileLabel,
            string? projectId = null,
            string? prompt = null,
            string? sessionName = null,
            CancellationToken cancellationToken = default)
        {
            Started.Add((nodeName, profileLabel));
            return Task.FromResult(Refusal);
        }

        public Task<string?> StopAsync(string nodeName, string paneId, CancellationToken cancellationToken = default)
        {
            Stopped.Add((nodeName, paneId));
            return Task.FromResult<string?>(null);
        }
    }
}
