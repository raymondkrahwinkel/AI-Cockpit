using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cockpit.Plugin.Depot.Contracts;
using Cockpit.Plugin.Depot.Model;
using Cockpit.Plugin.Depot.Notes.Live;
using Cockpit.Plugins.Abstractions.Files;
using Microsoft.AspNetCore.SignalR;

namespace Cockpit.Plugin.Depot.Tests;

// AC-1899, layer 2: the bridge between the host's Markdown and the room's Y.Doc, with Depot's own bundle in Jint and an in-process hub.
public class DepotNoteBridgeTests
{
    private static readonly DepotConnectionRegistration Connection = new("work", "Work", "https://work.example.com");
    private static readonly DepotNoteLiveRequest Note = new("work", "acme", "roadmap", "notes/plan.md");
    private static readonly string Fixtures = Path.Combine(AppContext.BaseDirectory, "Fixtures", "NotesRoundtrip");

    // AI-Depot scripts/notes-roundtrip-fixtures at the pinned commit, copied byte for byte: a silent change to a copy fails here.
    private const string SchemaSha = "77f8a24281ff1cb26e5872600f7bdbc7266788a92fff723e8b01fa29a59d6266";

    // The block ids and contents of the room's fragment, and the fragment and front matter as one string, to compare two docs.
    private const string Blocks = "JSON.stringify(DepotRoom.current().getXmlFragment('default').toArray().map(t => [t._item.id.client + ':' + t._item.id.clock, t.toString()]))";
    // The web's own updates since the last call (an edit with no ops returns what is waiting), and the first text item of a block.
    private const string Taken = "DepotRoom.edit('[]')";
    private const string TextOf = "const f = DepotRoom.current().getXmlFragment('default'); const t = (n) => { while (!(n instanceof DepotRoom.Y.XmlText)) { n = n.get(0); } return n; };";
    private const string State = "JSON.stringify([DepotRoom.current().getXmlFragment('default').toString(), DepotRoom.current().getText('frontmatter').toString()])";

    // Contract with Depot (another system): parse is the fixture's tree, serialize is the fixture's bytes, and the schema is the pinned one.
    [Theory]
    [InlineData("canonical", "1359be864f357048ea4a9c764be370105b08aac2b3a80e1123bbecb36ba4bfa5", "55eefc2b1aebc7afaa18d63492743641fb4d518d334f57ecb8a4cf8428839a8c")]
    [InlineData("crlf", "757993a42984248c987e03f173b15d9ca2dd5ea8c94ab5c0912898bc564f5b0c", "859bccfefdd805fbd044a544aceb75d6dfdb180e43414cf21e9b757f27741c62")]
    [InlineData("depot-flavour", "fef3429685dcdcd040dea1a4dcbbeacbed48980d644ea7c7e024c5eb2964b1f9", "b4fa6d9fbb32d28c1b33502e61e2aeae84834154ce443b11f310dd509ebdcec5")]
    [InlineData("hostile", "1a6dbe569feb6aa0ba34c4de82816a6d05e1cd9fce5e1a031754be85dc17f976", "222a350f89e258a14c31a3709a85567260d2d660a6f21bfbf457050897feb496")]
    [InlineData("no-frontmatter", "a758886460cdbeb172133702d63dfc07b684712444208c2306fa6d00f219a055", "3475dfcf15fc929e8386cdee7a8313675cb74a4be264dcae2b3c9580feac8f27")]
    [InlineData("structure", "c615aba34aa40236308e2943623b7415fa09bef8c641ce179ff69e234196dfb1", "6f1d90fcc203fa79113ac82ff4af220a3159ad2117b8da8f8994fbef595c430a")]
    public async Task Fixture_ParsesToItsTree_AndSerializesToItsOwnBytes(string name, string markdownSha, string treeSha)
    {
        var markdown = await File.ReadAllBytesAsync(Path.Combine(Fixtures, $"{name}.md"));
        var tree = await File.ReadAllBytesAsync(Path.Combine(Fixtures, $"{name}.pm.json"));
        var schema = await File.ReadAllBytesAsync(Path.Combine(Fixtures, "schema.json"));
        Assert.Equal(markdownSha, _Sha(markdown));
        Assert.Equal(treeSha, _Sha(tree));
        Assert.Equal(SchemaSha, _Sha(schema));
        await using var room = new JintNoteRoom(1);
        var text = Encoding.UTF8.GetString(markdown);
        var treeJson = Encoding.UTF8.GetString(tree);

        var parsed = await room.EvaluateAsync($"DepotRoom.parse({_Js(text)})", CancellationToken.None);
        var serialized = await room.EvaluateAsync($"DepotRoom.serialize({_Js(text)}, {_Js(treeJson)})", CancellationToken.None);
        var bundleSchema = await room.EvaluateAsync("DepotRoom.schemaJson()", CancellationToken.None);

        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(treeJson), JsonNode.Parse(parsed)), $"{name}: parse is not {name}.pm.json\n{parsed}");
        Assert.Equal(text, serialized);
        Assert.Equal(SchemaSha[..12], _Sha(Encoding.UTF8.GetBytes(bundleSchema))[..12]);
        Assert.Equal(SchemaSha[..12], DepotNoteSchema.Version);
    }

    // One keystroke in a paragraph is one Edited and one Update, and that update changes that paragraph and no other Y item; the same
    // edit again under its id (an answer the host lost) applies nothing.
    [Fact]
    public async Task Keystroke_InAParagraph_IsOneUpdate_ThatLeavesEveryOtherBlockTheSameYItem()
    {
        await using var web = new JintNoteRoom(2);
        var hub = new _Hub(await _SeedAsync(web, "canonical.md"));
        var patches = new List<LiveFilePatch>();
        var loaded = new TaskCompletionSource();
        await using var bridge = _Session(hub, patch =>
        {
            patches.Add(patch);
            loaded.TrySetResult();
        });
        bridge.Connect();
        await loaded.Task.WaitAsync(TimeSpan.FromSeconds(60));
        var room = (JintNoteRoom)bridge.Room;
        var text = (await bridge.ReadAsync(CancellationToken.None)).Text;
        var bridgeBefore = _Parse(await room.EvaluateAsync(Blocks, CancellationToken.None));
        var webBefore = _Parse(await web.EvaluateAsync(Blocks, CancellationToken.None));

        TextOp[] keystroke = [new(text.IndexOf("Slotregel", StringComparison.Ordinal) + 4, 0, "x")];
        Assert.Equal(new LiveEditResult(LiveEditStatus.Applied, 2), await bridge.EditAsync("edit-1", 1, keystroke, CancellationToken.None));
        Assert.Equal(new LiveEditResult(LiveEditStatus.Applied, 2), await bridge.EditAsync("edit-1", 1, keystroke, CancellationToken.None));
        Assert.Equal(["edited", "update"], hub.Calls);
        await web.ApplyAsync(Assert.Single(hub.Updates), CancellationToken.None);
        var bridgeAfter = _Parse(await room.EvaluateAsync(Blocks, CancellationToken.None));
        var webAfter = _Parse(await web.EvaluateAsync(Blocks, CancellationToken.None));

        Assert.Equal(bridgeBefore.Select(block => block[0]), bridgeAfter.Select(block => block[0]));
        Assert.Equal(webBefore.Select(block => block[0]), webAfter.Select(block => block[0]));
        Assert.Equal(webBefore.SkipLast(1).Select(block => block[1]), webAfter.SkipLast(1).Select(block => block[1]));
        Assert.Equal("<paragraph>Slotxregel.</paragraph>", webAfter[^1][1]);
        Assert.Single(patches);

        // Layer 2, contract: a role revoked during Update keeps the mirror at the shared revision, still watching.
        hub.ReadOnly = true;
        var accepted = await bridge.ReadAsync(CancellationToken.None);
        var refused = await Assert.ThrowsAsync<FileSourceException>(() => bridge.EditAsync("refused", accepted.Rev, [new(0, 0, "MINE ")], CancellationToken.None));
        Assert.Equal(FileSourceErrorKind.Forbidden, refused.Kind);
        var watching = await bridge.ReadAsync(CancellationToken.None);
        Assert.Equal(accepted.Text, watching.Text);
        Assert.Equal(accepted.Rev, watching.Rev);
        Assert.Equal(LiveReadOnlyReason.RoleLost, watching.Status.ReadOnlyReason);
        Assert.Equal(LiveFileState.ReadOnly, watching.Status.State);
        await Assert.ThrowsAsync<FileSourceException>(() => bridge.EditAsync("again", watching.Rev, [new(0, 0, "NO ")], CancellationToken.None));
        await web.LoadAsync(string.Empty, CancellationToken.None);
        var remoteOffset = accepted.Text.IndexOf("Slotxregel", StringComparison.Ordinal);
        var remote = await web.EditAsync([new(remoteOffset, 0, "WEB ")], CancellationToken.None);
        await hub.Push(new DepotNoteEvent("update", ["doc-1", Assert.Single(remote)]));
        Assert.Equal(await web.ReadAsync(CancellationToken.None), (await bridge.ReadAsync(CancellationToken.None)).Text);
        Assert.Single(hub.Updates);
    }

    // AC-1897's join (an update that comes before the result is applied and not sent back), then the bridge and the web typing at once,
    // in the same block and in others: both docs end equal, the host's text plus its patches is the mirror and the serialization, and
    // each remote keystroke reaches the host as one insert of just its own text. Undo then takes back the bridge's typing, not the web's.
    [Fact]
    public async Task BothSidesTypingAtOnce_ConvergeWithoutLoss_AndEachRemoteKeystrokeIsOneInsert()
    {
        await using var web = new JintNoteRoom(2);
        var seed = await _SeedAsync(web, "structure.md");
        var early = _Taken(await web.EvaluateAsync($"(() => {{ {TextOf} t(f.get(1)).insert(0, 'EARLY '); return {Taken}; }})()", CancellationToken.None));
        var hub = new _Hub(seed, early);
        var patches = new List<LiveFilePatch>();
        var loaded = new TaskCompletionSource();
        await using var bridge = _Session(hub, patch =>
        {
            patches.Add(patch);
            loaded.TrySetResult();
        });
        bridge.Connect();
        await loaded.Task.WaitAsync(TimeSpan.FromSeconds(60));
        var joined = await bridge.ReadAsync(CancellationToken.None);
        Assert.Contains("EARLY A **bold", joined.Text);
        Assert.Empty(hub.Updates);
        Assert.Equal(DepotNoteSchema.Version, hub.Join?.Schema);
        Assert.False(hub.Join?.Save);
        Assert.Null(hub.Join?.Epoch);

        // The bridge types in the heading and in the bullet list (each op on the text the one before left); the web, not having seen
        // that, in the heading and in the ordered list.
        TextOp[] typed = [new(joined.Text.IndexOf("ruc", StringComparison.Ordinal), 0, "AB"), new(joined.Text.IndexOf("three", StringComparison.Ordinal) + "AB".Length + 2, 0, "CD")];
        Assert.Equal(new LiveEditResult(LiveEditStatus.Applied, 2), await bridge.EditAsync("edit-1", 1, typed, CancellationToken.None));
        var webTyped = _Taken(await web.EvaluateAsync(
            $"(() => {{ {TextOf} t(f.get(0)).insert(0, 'WEB1 '); const one = JSON.parse({Taken}); t(f.get(3)).insert(0, 'WEB2 '); return JSON.stringify([...one, ...JSON.parse({Taken})]); }})()",
            CancellationToken.None));
        Assert.Equal(2, webTyped.Length);
        await hub.Push(new DepotNoteEvent("update", ["doc-1", webTyped[0]]));
        await hub.Push(new DepotNoteEvent("update", ["doc-1", webTyped[1]]));
        await web.ApplyAsync(Assert.Single(hub.Updates), CancellationToken.None);

        var mirror = await bridge.ReadAsync(CancellationToken.None);
        await web.LoadAsync(string.Empty, CancellationToken.None);
        Assert.Equal(await web.EvaluateAsync(State, CancellationToken.None), await ((JintNoteRoom)bridge.Room).EvaluateAsync(State, CancellationToken.None));
        Assert.Equal(await web.ReadAsync(CancellationToken.None), mirror.Text);
        Assert.Equal(mirror.Text, _Applied(_Applied(joined.Text, typed), patches.Skip(1).SelectMany(patch => patch.Ops)));
        Assert.Contains("# WEB1 StABructure", mirror.Text);
        Assert.Contains("thCDree", mirror.Text);
        Assert.Contains("1. WEB2 first", mirror.Text);
        Assert.Equal(4, mirror.Rev);
        Assert.Equal([3L, 4L], patches.Skip(1).Select(patch => patch.Rev));
        Assert.Equal([(0, "WEB1 "), (0, "WEB2 ")], patches.Skip(1).Select(patch => Assert.Single(patch.Ops)).Select(op => (op.Length, op.Text)));

        await bridge.UndoAsync(redo: false, CancellationToken.None);
        await web.ApplyAsync(hub.Updates[^1], CancellationToken.None);
        var undone = await bridge.ReadAsync(CancellationToken.None);
        await web.LoadAsync(string.Empty, CancellationToken.None);
        Assert.Equal(_Applied(mirror.Text, patches.Skip(3).SelectMany(patch => patch.Ops)), undone.Text);
        Assert.Equal(await web.ReadAsync(CancellationToken.None), undone.Text);
        Assert.Contains("# WEB1 Structure", undone.Text);
        Assert.Contains("- three", undone.Text);
        Assert.Contains("1. WEB2 first", undone.Text);
        Assert.Equal(5, undone.Rev);

        await _ReadOnlyJoinAsync(seed, schemaMismatch: true, LiveReadOnlyReason.SchemaMismatch);
        await _ReadOnlyJoinAsync(seed, schemaMismatch: false, LiveReadOnlyReason.Viewer);
        await _RejectedUndoAsync(seed);
        await _OfflineReadOnlyAsync(seed);
    }

    // AC-1900, contract with Depot's web editor: a cursor is one Markdown offset in the bridge and one relativePositionToJSON in the web,
    // and both name the same character, in a paragraph, a heading, a list item and a table cell, in either direction. Through the session:
    // a web client's awareness becomes a peer with its caret and selection as offsets, and this host's caret goes out under a higher clock.
    [Fact]
    public async Task Cursors_NameTheSameCharacterOnBothSides_AndTravelThroughThePresenceOfTheSession()
    {
        await using var web = new JintNoteRoom(2);
        var hub = new _Hub(await _SeedAsync(web, "structure.md"));
        var loaded = new TaskCompletionSource();
        var patches = new List<LiveFilePatch>();
        var statuses = new List<LiveFileStatus>();
        await using var bridge = _Session(hub, patch => { patches.Add(patch); loaded.TrySetResult(); }, user: new DepotNoteUser("user-1", "Raymond", "#2563eb"), statusChanged: statuses.Add);
        bridge.Connect();
        await loaded.Task.WaitAsync(TimeSpan.FromSeconds(60));
        await web.LoadAsync(string.Empty, CancellationToken.None);
        var text = await web.ReadAsync(CancellationToken.None);
        var paragraph = text.IndexOf("and italic", StringComparison.Ordinal);
        var heading = text.IndexOf("ructure", StringComparison.Ordinal);
        var item = text.IndexOf("ck to two", StringComparison.Ordinal);
        var cell = text.IndexOf("ight", StringComparison.Ordinal);
        Assert.Equal(text, (await bridge.ReadAsync(CancellationToken.None)).Text);
        await _SameCharacterAsync(web, bridge.Room, paragraph);
        await _SameCharacterAsync(web, bridge.Room, heading);
        await _SameCharacterAsync(web, bridge.Room, item);
        await _SameCharacterAsync(web, bridge.Room, cell);

        // The end of a line inside a block (a list item with another after it, a table cell) is the end of that line, not the start of the next.
        await _SameCharacterAsync(web, bridge.Room, text.IndexOf("one\n", StringComparison.Ordinal) + 3);
        await _SameCharacterAsync(web, bridge.Room, text.IndexOf("nested first\n", StringComparison.Ordinal) + 12);
        await _SameCharacterAsync(web, bridge.Room, text.IndexOf("| a    |", StringComparison.Ordinal) + 3);

        var anchorAt = await web.RelativeAtAsync(paragraph, CancellationToken.None);
        var headAt = await web.RelativeAtAsync(heading, CancellationToken.None);
        var state = """{"user":{"id":"u-sanne","name":"Sanne de Vries","role":"editor","color":"#3fb8c4","source":"web"},"activity":"selecting","cursor":{"anchor":""" + anchorAt + ""","head":""" + headAt + "}}";
        await hub.Push(new DepotNoteEvent("presence", ["doc-1", "conn-9", DepotAwarenessCodec.Encode([new DepotAwarenessEntry(2113437100, 1, state)])]));
        var you = bridge.Peers[0];
        var sanne = bridge.Peers[1];
        // AC-1580: the own entry carries the name and colour /cockpit/me gave; an older Depot gives none and the host shows "You".
        Assert.Equal(("Raymond", "#2563eb", true, "Cockpit"), (you.Name, you.Color, you.You, you.Source));
        Assert.Null(DepotPresenceSelf.For("user-1", "editor").Name);
        Assert.Equal(("Sanne de Vries", "#3fb8c4", "web", "selecting"), (sanne.Name, sanne.Color, sanne.Source, sanne.Activity));
        Assert.Equal((heading, new LiveRange(heading, paragraph)), (sanne.Caret, sanne.Selection));

        await bridge.SetPresenceAsync(new LivePresenceState("editing", item), CancellationToken.None);
        var first = DepotAwarenessCodec.Decode(hub.Presence[0])[0];
        var last = DepotAwarenessCodec.Decode(hub.Presence[^1])[0];
        Assert.Equal((first.ClientId, 1UL, 2UL), (last.ClientId, first.Clock, last.Clock));
        var head = JsonDocument.Parse(last.State).RootElement.GetProperty("cursor").GetProperty("head").GetRawText();
        Assert.Equal(item, await web.OffsetOfAsync(head, CancellationToken.None));

        await hub.Push(new DepotNoteEvent("outside", ["doc-1", JsonSerializer.SerializeToElement(new { name = "Assistant", onBehalfOf = "Raymond", source = "agent", via = "Mcp", version = 2 })]));
        Assert.Equal("Assistant", statuses[^1].Saved?.Name);
        Assert.Equal("#a78bda", statuses[^1].Saved?.Color);
        Assert.Equal("agent", bridge.Peers[^1].Source);
        await hub.Push(new DepotNoteEvent("merged", ["doc-1", new[] { 0 }, 0]));
        var external = patches[^1].ExternalEdit ?? throw new InvalidOperationException("The outside writer was not marked.");
        Assert.Equal("Assistant · on behalf of Raymond", external.Who);
        Assert.Equal(await bridge.Room.BlockRangesAsync([0], CancellationToken.None), external.Ranges);
        var marked = patches.Count;
        await hub.Push(new DepotNoteEvent("outside", ["doc-1", JsonSerializer.SerializeToElement(new { name = "Assistant", source = "agent" })]));
        await hub.Push(new DepotNoteEvent("outside", ["doc-1", JsonSerializer.SerializeToElement(new { name = "Other", source = "agent" })]));
        await hub.Push(new DepotNoteEvent("merged", ["doc-1", new[] { 0 }, 0]));
        Assert.Equal(marked, patches.Count);
        await hub.Push(new DepotNoteEvent("outside", ["doc-1", JsonSerializer.SerializeToElement(new { name = "Third", source = "agent" })]));
        await hub.Push(new DepotNoteEvent("merged", ["doc-1", new[] { 0 }, 0]));
        Assert.Equal(marked, patches.Count);
        await hub.Push(new DepotNoteEvent("saved", ["doc-1", JsonSerializer.SerializeToElement(new { by = new { name = "Sanne", color = "#3fb8c4" }, at = "2026-10-28T14:12:00Z" })]));
        Assert.Equal("Sanne", statuses[^1].Saved?.Name);
    }

    private static async Task _SameCharacterAsync(JintNoteRoom web, IDepotNoteRoom bridge, int offset)
    {
        var fromWeb = await web.RelativeAtAsync(offset, CancellationToken.None) ?? throw new InvalidOperationException("The web has no cursor there.");
        var fromBridge = await bridge.RelativeAtAsync(offset, CancellationToken.None) ?? throw new InvalidOperationException("The bridge has no cursor there.");
        Assert.Equal(offset, await bridge.OffsetOfAsync(fromWeb, CancellationToken.None));
        Assert.Equal(offset, await web.OffsetOfAsync(fromBridge, CancellationToken.None));
    }

    private static async Task _RejectedUndoAsync(byte[] seed)
    {
        var hub = new _Hub(seed);
        await using var bridge = _Session(hub, _ => { });
        bridge.Connect();
        var before = await bridge.ReadAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30));
        await bridge.EditAsync("accepted", before.Rev, [new(5, 0, "MINE")], CancellationToken.None);
        var accepted = await bridge.ReadAsync(CancellationToken.None);
        hub.ReadOnly = true;
        await Assert.ThrowsAsync<FileSourceException>(() => bridge.UndoAsync(false, CancellationToken.None));
        var refused = await bridge.ReadAsync(CancellationToken.None);
        Assert.Equal(accepted.Text, refused.Text);
        Assert.Equal(accepted.Rev, refused.Rev);
        Assert.Equal(LiveReadOnlyReason.RoleLost, refused.Status.ReadOnlyReason);
    }

    private static async Task _OfflineReadOnlyAsync(byte[] seed)
    {
        var hub = new _Hub(seed);
        var joined = new _Joins();
        await using var bridge = _Session(hub, _ => { }, joined.Publish, statusChanged: joined.Host);
        bridge.Connect();
        await joined.First.Task.WaitAsync(TimeSpan.FromSeconds(30));
        var before = await bridge.ReadAsync(CancellationToken.None);
        var rejoin = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        hub.JoinHold = rejoin.Task;
        hub.Offline = true;
        await bridge.EditAsync("offline", before.Rev, [new(5, 0, "MINE")], CancellationToken.None);
        hub.Role = "viewer";
        hub.Offline = false;
        // The refused update already sent the session back to Join, held until the role changed; a second disconnect could drop
        // that rejoin again.
        rejoin.SetResult();
        await joined.ReadOnly.Task.WaitAsync(TimeSpan.FromSeconds(30));
        var readOnly = await bridge.ReadAsync(CancellationToken.None);
        Assert.Equal(LiveFileState.ReadOnly, readOnly.Status.State);
        Assert.Contains("MINE", readOnly.Text);
        Assert.Equal(before.Text, readOnly.Status.UnsentSourceText);
        Assert.Empty(hub.Updates);
    }

    private sealed class _Joins
    {
        public TaskCompletionSource First { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        // The host's view: read-only once the rejoin as a viewer landed.
        public TaskCompletionSource ReadOnly { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Host(LiveFileStatus status)
        {
            if (status.State == LiveFileState.ReadOnly)
            {
                ReadOnly.TrySetResult();
            }
        }
        public void Publish(DepotNoteLiveStatus status)
        {
            if (status.State == DepotNoteLiveState.Live)
            {
                First.TrySetResult();
            }
        }
    }

    // Layer 2, contract: even an editor role cannot write a schema mismatch; both read-only joins receive remote patches.
    private static async Task _ReadOnlyJoinAsync(byte[] seed, bool schemaMismatch, LiveReadOnlyReason reason)
    {
        var hub = new _Hub(seed) { Role = schemaMismatch ? "editor" : "viewer", SchemaMismatch = schemaMismatch };
        var joined = new _Joins();
        await using var bridge = _Session(hub, _ => { }, joined.Publish);
        bridge.Connect();
        await joined.First.Task.WaitAsync(TimeSpan.FromSeconds(30));
        var snapshot = await bridge.ReadAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(LiveFileState.ReadOnly, snapshot.Status.State);
        Assert.Equal(reason, snapshot.Status.ReadOnlyReason);
        await Assert.ThrowsAsync<FileSourceException>(() => bridge.EditAsync("blocked", snapshot.Rev, [new(0, 0, "NO ")], CancellationToken.None));
        await bridge.UndoAsync(false, CancellationToken.None);
        await using var web = new JintNoteRoom(3);
        await web.ApplyAsync(seed, CancellationToken.None);
        await web.LoadAsync(string.Empty, CancellationToken.None);
        var remote = await web.EditAsync([new(0, 0, "WEB ")], CancellationToken.None);
        await hub.Push(new DepotNoteEvent("update", ["doc-1", Assert.Single(remote)]));
        Assert.StartsWith("WEB ", (await bridge.ReadAsync(CancellationToken.None)).Text);
        Assert.Empty(hub.Calls);
    }

    // The patches arrive through the Patched event, the one the host's file channel listens to.
    private static DepotNoteSession _Session(_Hub hub, Action<LiveFilePatch> patched, Action<DepotNoteLiveStatus>? publish = null, DepotNoteUser? user = null, Action<LiveFileStatus>? statusChanged = null)
    {
        var session = new DepotNoteSession(
            Connection, Note, _ => Task.FromResult(user ?? new DepotNoteUser("user-1")), (_, _, onClosed) => hub.With(onClosed), clientId => new JintNoteRoom(clientId),
            publish ?? (_ => { }), [TimeSpan.Zero], statusChanged: statusChanged);
        session.Patched += (_, patch) => patched(patch);
        return session;
    }

    // The web's seed of a fixture (protocol §5.3), as the one row the hub holds.
    private static async Task<byte[]> _SeedAsync(JintNoteRoom web, string fixture)
    {
        await web.SeedAsync(await File.ReadAllTextAsync(Path.Combine(Fixtures, fixture)), CancellationToken.None);
        return await web.MissingAsync([], CancellationToken.None) ?? throw new InvalidOperationException("The seed is empty.");
    }

    private static string _Applied(string text, IEnumerable<TextOp> ops) =>
        ops.Aggregate(text, (current, op) => current[..op.Offset] + op.Text + current[(op.Offset + op.Length)..]);

    private static byte[][] _Taken(string json) => [.. (JsonSerializer.Deserialize<string[]>(json) ?? []).Select(Convert.FromBase64String)];

    private static string[][] _Parse(string json) => JsonSerializer.Deserialize<string[][]>(json) ?? [];

    private static string _Js(string text) => JsonSerializer.Serialize(text);

    private static string _Sha(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    // An in-process hub: the seed as the one row, `early` as updates that arrive before the Join result, and every call recorded.
    private sealed class _Hub(byte[] row, params byte[][] early) : IDepotNotesHub
    {
        private Func<DepotNoteEvent, Task>? _handler;
        private Func<Exception?, Task>? _closed;

        public List<string> Calls { get; } = [];

        public List<byte[]> Updates { get; } = [];
        public bool ReadOnly { get; set; }
        public bool Offline { get; set; }
        public Task? JoinHold { get; set; }
        public string Role { get; set; } = "editor";
        public bool SchemaMismatch { get; set; }

        public DepotNoteJoinRequest? Join { get; private set; }

        public _Hub With(Func<Exception?, Task> onClosed)
        {
            _closed = onClosed;
            return this;
        }

        public Task Push(DepotNoteEvent hubEvent) => _handler?.Invoke(hubEvent) ?? Task.CompletedTask;
        public Task DisconnectAsync() => _closed?.Invoke(null) ?? Task.CompletedTask;

        public void Subscribe(Func<DepotNoteEvent, Task> onEvent) => _handler = onEvent;

        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public async Task<DepotNoteJoinResult> JoinAsync(DepotNoteJoinRequest request, CancellationToken cancellationToken)
        {
            if (JoinHold is { } hold)
            {
                await hold.WaitAsync(cancellationToken);
            }
            Join = request;
            foreach (var update in early)
            {
                await Push(new DepotNoteEvent("update", ["doc-1", update]));
            }

            return new DepotNoteJoinResult(
                "doc-1", Role, [new DepotNoteRow(1, row)], Seed: false, AwaitingSeed: false, Epoch: "epoch-1", Stale: false, SchemaMismatch: SchemaMismatch);
        }

        public Task UpdateAsync(string documentId, byte[] update, CancellationToken cancellationToken)
        {
            Calls.Add("update");
            if (Offline)
            {
                throw new IOException("Disconnected");
            }
            if (ReadOnly)
            {
                throw new HubException("notes.read_only: refused");
            }
            Updates.Add(update);
            return Task.CompletedTask;
        }

        public Task EditedAsync(string documentId, CancellationToken cancellationToken)
        {
            Calls.Add("edited");
            return Task.CompletedTask;
        }

        public Task CompactAsync(string documentId, byte[] snapshot, CancellationToken cancellationToken)
        {
            Calls.Add("compact");
            return Task.CompletedTask;
        }

        public Task<bool> RefillAsync(string documentId, CancellationToken cancellationToken)
        {
            Calls.Add("refill");
            return Task.FromResult(false);
        }

        public List<byte[]> Presence { get; } = [];

        public Task PresenceAsync(string documentId, byte[] awareness, CancellationToken cancellationToken)
        {
            Presence.Add(awareness);
            return Task.CompletedTask;
        }

        public string? ConnectionId => null;

        public Task<DepotNoteText?> TextAsync(string documentId, string? checksum, CancellationToken cancellationToken) => Task.FromResult<DepotNoteText?>(null);

        public Task MergedAsync(string documentId, string checksum, int[] blocks, int conflicts, CancellationToken cancellationToken) => Task.CompletedTask;

        public ValueTask DisposeAsync() => _closed is null ? ValueTask.CompletedTask : new ValueTask(_closed(null));
    }
}
