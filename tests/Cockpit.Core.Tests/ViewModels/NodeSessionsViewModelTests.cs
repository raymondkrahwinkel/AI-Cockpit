using System.Globalization;
using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Assistant;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Assistant;
using Cockpit.Core.Mcp;
using Cockpit.Core.Profiles;
using Cockpit.Infrastructure.Agents;
using Cockpit.Infrastructure.Mcp;

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
        // The first refresh also carries the unreachable case: "nothing is running there" and "nobody answered"
        // look identical as an empty list, and only one of them is a reason to go and look at the other machine.
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

    [Fact]
    public async Task Refresh_CollectsWhatAgentsOnTheNodeSentTheirAssistant_WithOrigin_ExactlyOnce()
    {
        // AC-1322 criteria 1 and 2 at the controller's end. The read fails once before it answers — the node
        // then drops nothing, so the next poll gets the message; after that it is acknowledged and never comes
        // again. The assistant drains its inbox in between, so a second delivery could not hide behind dedup.
        var client = new FakeNodeSessions
        {
            Snapshot = new NodeSessionsSnapshot("laptop", [SweepOnTheNode], [], []),
            FailNextInboxRead = true,
        };
        client.Queued.Add(new NodeInboxMessage("m1", "node-pane-b", "done", "AC-795 tests are green.", DateTimeOffset.UtcNow));
        var inbox = new AgentMessageInbox();
        var card = new NodeSessionsViewModel(client, "laptop", new NodeInboxRelay(client, inbox));

        await card.RefreshAsync();
        Assert.Null(inbox.PeekOldest(AssistantIdentity.PaneId));

        await card.RefreshAsync();
        var delivered = Assert.Single(inbox.Drain(AssistantIdentity.PaneId, 25).Messages);
        Assert.Equal("laptop · node-pane-b", delivered.FromPaneId);
        Assert.Equal("done", delivered.Kind);
        Assert.Equal("[From node laptop (disc-laptop), session laptop · node-pane-b. Reply with send_message to that address; notify does not reach it.] AC-795 tests are green.", delivered.Body);

        await card.RefreshAsync();
        Assert.Null(inbox.PeekOldest(AssistantIdentity.PaneId));
        Assert.Empty(client.Queued);
    }

    // AC-1327 criterion 2: one missed 20s poll is a blip, not a drop, so the first miss reports nothing; the
    // second is the threshold and reports "node-dropped"; a third still in a row adds no second message.
    [Fact]
    public async Task Refresh_NeedsASecondConsecutiveMiss_ThenReportsOncePerEdge()
    {
        var client = new FakeNodeSessions
        {
            Snapshot = new NodeSessionsSnapshot("laptop", [], [], [], "Could not reach laptop: no route to host"),
        };
        var inbox = new AgentMessageInbox();
        var card = new NodeSessionsViewModel(client, "laptop", new NodeInboxRelay(client, inbox));

        await card.RefreshAsync();
        Assert.Empty(inbox.Drain(AssistantIdentity.PaneId, 25).Messages);

        await card.RefreshAsync();
        var dropped = Assert.Single(inbox.Drain(AssistantIdentity.PaneId, 25).Messages);
        Assert.Equal("laptop", dropped.FromPaneId);
        Assert.Equal("node-dropped", dropped.Kind);

        await card.RefreshAsync();
        Assert.Empty(inbox.Drain(AssistantIdentity.PaneId, 25).Messages);

        client.Snapshot = new NodeSessionsSnapshot("laptop", [SweepOnTheNode], [], []);
        await card.RefreshAsync();

        var back = Assert.Single(inbox.Drain(AssistantIdentity.PaneId, 25).Messages);
        Assert.Equal("node-back", back.Kind);
        Assert.Contains("with 1 session", back.Body, StringComparison.Ordinal);
    }

    // AC-1330 criterion 1: controller {A, B}, node {B, C} → controller {A, B, C(from laptop)}, node {B, C,
    // A(from <this machine>)}; a second sync right after changes neither (idempotent, no pingpong on the suffix).
    [Fact]
    public async Task Refresh_TakesOverBehaviourRulesBothWays_AndASecondSyncRightAfterChangesNothing()
    {
        var memory = new InMemoryAssistantMemory();
        await memory.RememberAsync("A", AssistantMemoryScope.Behaviour);
        await memory.RememberAsync("B", AssistantMemoryScope.Behaviour);
        var machineBefore = await memory.ReadAsync(AssistantMemoryScope.Machine);
        var client = new FakeNodeSessions { Snapshot = new NodeSessionsSnapshot("laptop", [], [], []) };
        await client.RememberOnNodeAsync("laptop", "B", "behaviour");
        await client.RememberOnNodeAsync("laptop", "C", "behaviour");
        var inbox = new AgentMessageInbox();
        var sync = new BehaviourMemorySync(client, memory, inbox);
        var card = new NodeSessionsViewModel(client, "laptop", behaviourSync: sync);
        var today = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        await card.RefreshAsync();

        var localAfterFirstSync = await memory.ReadAsync(AssistantMemoryScope.Behaviour);
        var nodeAfterFirstSync = client.NodeBehaviour;
        // Each side kept its own two entries and took over exactly one, tagged with where it came from — the
        // real `- {date} — {text}` markdown shape (AC-1330 review), not the bare lines the fakes used before.
        Assert.Contains("— A", localAfterFirstSync, StringComparison.Ordinal);
        Assert.Contains("— B", localAfterFirstSync, StringComparison.Ordinal);
        Assert.Contains($"C (from laptop, {today})", localAfterFirstSync, StringComparison.Ordinal);
        Assert.Contains($"A (from {Environment.MachineName}, {today})", nodeAfterFirstSync, StringComparison.Ordinal);
        Assert.Equal(machineBefore, await memory.ReadAsync(AssistantMemoryScope.Machine));
        var report = Assert.Single(inbox.Drain(AssistantIdentity.PaneId, 25).Messages);
        Assert.Equal("Took over 1 behaviour rules from laptop and sent 1 there.", report.Body);

        await sync.RunAsync("laptop");

        Assert.Equal(localAfterFirstSync, await memory.ReadAsync(AssistantMemoryScope.Behaviour));
        Assert.Equal(nodeAfterFirstSync, client.NodeBehaviour);
        Assert.Empty(inbox.Drain(AssistantIdentity.PaneId, 25).Messages);
    }

    // AC-1330 criterion 2: the sync runs once at launch (this card's first successful read) and once more on the
    // unreachable→reachable edge AC-1327 gates behind a second consecutive miss — never on a poll in between.
    [Fact]
    public async Task Refresh_RunsTheBehaviourSyncOnLaunchAndOnTheReachableEdge_NeverPerPoll()
    {
        var client = new FakeNodeSessions { Snapshot = new NodeSessionsSnapshot("laptop", [], [], []) };
        var sync = new BehaviourMemorySync(client, new InMemoryAssistantMemory(), new AgentMessageInbox());
        var card = new NodeSessionsViewModel(client, "laptop", behaviourSync: sync);

        await card.RefreshAsync();
        await card.RefreshAsync();
        await card.RefreshAsync();
        await card.RefreshAsync();
        await card.RefreshAsync();
        Assert.Equal(1, client.ReadMemoryCalls);

        client.Snapshot = new NodeSessionsSnapshot("laptop", [], [], [], "Could not reach laptop: no route to host");
        await card.RefreshAsync();
        await card.RefreshAsync();
        client.Snapshot = new NodeSessionsSnapshot("laptop", [], [], []);
        await card.RefreshAsync();

        Assert.Equal(2, client.ReadMemoryCalls);
    }

    // An in-memory `IAssistantMemory` keyed by scope, since `AssistantMemoryFile` (the real one) touches disk.
    private sealed class InMemoryAssistantMemory : IAssistantMemory
    {
        private readonly Dictionary<AssistantMemoryScope, string> _text = new();

        public Task<string> ReadAsync(AssistantMemoryScope scope, CancellationToken cancellationToken = default) =>
            Task.FromResult(_text.GetValueOrDefault(scope, ""));

        // Mirrors `AssistantMemoryFile.RememberAsync`'s on-disk shape — a heading, then one `- {date} — {text}`
        // entry per line — closely enough that `BehaviourMemorySync`'s parsing is exercised the way it runs for
        // real (AC-1330 review: the bare-line fakes let a parsing bug through unnoticed).
        public Task RememberAsync(string text, AssistantMemoryScope scope, CancellationToken cancellationToken = default)
        {
            var entry = $"- {DateTimeOffset.Now:yyyy-MM-dd} — {text}";
            var existing = _text.GetValueOrDefault(scope, "");
            _text[scope] = existing.Length == 0 ? $"# Remembered\n\n{entry}" : $"{existing}\n{entry}";
            return Task.CompletedTask;
        }

        public Task<string> ReadCurrentStateAsync(CancellationToken cancellationToken = default) => Task.FromResult("");

        public Task NoteCurrentStateAsync(string text, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<IReadOnlyList<string>> ExportAsync(string archivePath, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>([]);

        public Task<IReadOnlyList<string>> ImportAsync(string archivePath, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>([]);
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

        public (NodeSessionsSnapshot Snapshot, DateTimeOffset AtUtc)? TryGetLastSnapshot(string nodeName) =>
            string.Equals(nodeName, Snapshot.NodeName, StringComparison.Ordinal) ? (Snapshot, DateTimeOffset.UtcNow) : null;

        public Task<NodeStartResult> StartAsync(
            string nodeName,
            string profileLabel,
            string? projectId = null,
            string? prompt = null,
            string? sessionName = null,
            CancellationToken cancellationToken = default)
        {
            Started.Add((nodeName, profileLabel));
            return Task.FromResult(new NodeStartResult(Refusal));
        }

        public Task<string?> StopAsync(string nodeName, string paneId, CancellationToken cancellationToken = default)
        {
            Stopped.Add((nodeName, paneId));
            return Task.FromResult<string?>(null);
        }

        public Task<string?> SendPromptAsync(string nodeName, string paneId, string prompt, CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>(null);

        public Task<string?> SendMessageAsync(string nodeName, string paneId, string kind, string body, CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>(null);

        public Task<string?> RenameAsync(string nodeName, string paneId, string name, CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>(null);

        public Task<NodeTranscriptRead> ReadTranscriptAsync(string nodeName, string paneId, int count, CancellationToken cancellationToken = default) =>
            Task.FromResult(new NodeTranscriptRead(null));

        // AC-1322: the node's queue for its controller, with the node's own rule — everything up to and including
        // `afterMessageId` is dropped, the rest comes back — and a read that fails before answering drops nothing.
        public List<NodeInboxMessage> Queued { get; } = [];

        public bool FailNextInboxRead { get; set; }

        public Task<NodePermissionAnswer> AnswerPermissionAsync(string nodeName, string paneId, string toolUseId, bool allow, CancellationToken cancellationToken = default) =>
            Task.FromResult(new NodePermissionAnswer(false));

        // AC-1330: the node's own "behaviour" memory file, and how many times it was read — the sync's only call
        // that nothing else on this card makes, so a count of it is a count of syncs.
        public string NodeBehaviour { get; set; } = "";

        public int ReadMemoryCalls { get; private set; }

        public Task<NodeMemoryRead> ReadMemoryAsync(string nodeName, string scope, CancellationToken cancellationToken = default)
        {
            ReadMemoryCalls++;
            return Task.FromResult(new NodeMemoryRead(NodeBehaviour));
        }

        // Same real on-disk shape as `InMemoryAssistantMemory.RememberAsync` above — a node's own memory file is
        // written by the same `AssistantMemoryFile` code, just on that machine.
        public Task<string?> RememberOnNodeAsync(string nodeName, string text, string scope, CancellationToken cancellationToken = default)
        {
            var entry = $"- {DateTimeOffset.Now:yyyy-MM-dd} — {text}";
            NodeBehaviour = NodeBehaviour.Length == 0 ? $"# Remembered\n\n{entry}" : $"{NodeBehaviour}\n{entry}";
            return Task.FromResult<string?>(null);
        }

        public Task<NodeInboxBatch> ReadInboxAsync(string nodeName, string? afterMessageId, CancellationToken cancellationToken = default)
        {
            if (FailNextInboxRead)
            {
                FailNextInboxRead = false;
                return Task.FromResult(new NodeInboxBatch(nodeName, [], Error: "laptop did not answer within 2s."));
            }

            var acknowledged = Queued.FindIndex(message => message.Id == afterMessageId);
            Queued.RemoveRange(0, acknowledged + 1);
            return Task.FromResult(new NodeInboxBatch(nodeName, [.. Queued], DiscoveryId: "disc-laptop"));
        }
    }
}
