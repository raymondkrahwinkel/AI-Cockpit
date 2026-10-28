using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cockpit.Plugin.Depot.Contracts;
using Cockpit.Plugin.Depot.Model;
using Cockpit.Plugin.Depot.Notes.Live;

namespace Cockpit.Plugin.Depot.Tests;

// AC-1899, layer 2: the bridge between the host's Markdown and the room's Y.Doc, with Depot's own bundle in Jint and an in-process hub.
public class DepotNoteBridgeTests
{
    private static readonly DepotConnectionRegistration Connection = new("work", "Work", "https://work.example.com");
    private static readonly DepotNoteLiveRequest Note = new("work", "acme", "roadmap", "notes/plan.md");
    private static readonly string Fixtures = Path.Combine(AppContext.BaseDirectory, "Fixtures", "NotesRoundtrip");

    // AI-Depot scripts/notes-roundtrip-fixtures at the pinned commit, copied byte for byte: a silent change to a copy fails here.
    private const string SchemaSha = "f87f401d6f82a641bba300a3cf953e3e541b31b86515cf1a9c68f386e06c1fd5";

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

    // One keystroke in a paragraph is one Edited and one Update, and that update changes that paragraph and no other Y item.
    [Fact]
    public async Task Keystroke_InAParagraph_IsOneUpdate_ThatLeavesEveryOtherBlockTheSameYItem()
    {
        await using var web = new JintNoteRoom(2);
        var hub = new _Hub(await _SeedAsync(web, "canonical.md"));
        var patches = new List<DepotNoteLivePatch>();
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

        var result = await bridge.EditAsync(1, [new DepotNoteTextOp(text.IndexOf("Slotregel", StringComparison.Ordinal) + 4, 0, "x")], CancellationToken.None);
        Assert.Equal(new DepotNoteEditResult(true, 2), result);
        Assert.Equal(["edited", "update"], hub.Calls);
        await web.ApplyAsync(Assert.Single(hub.Updates), CancellationToken.None);
        var bridgeAfter = _Parse(await room.EvaluateAsync(Blocks, CancellationToken.None));
        var webAfter = _Parse(await web.EvaluateAsync(Blocks, CancellationToken.None));

        Assert.Equal(bridgeBefore.Select(block => block[0]), bridgeAfter.Select(block => block[0]));
        Assert.Equal(webBefore.Select(block => block[0]), webAfter.Select(block => block[0]));
        Assert.Equal(webBefore.SkipLast(1).Select(block => block[1]), webAfter.SkipLast(1).Select(block => block[1]));
        Assert.Equal("<paragraph>Slotxregel.</paragraph>", webAfter[^1][1]);
        Assert.Single(patches);
    }

    // AC-1897's join (an update that comes before the result is applied and not sent back), then the bridge and the web typing at once,
    // in the same block and in others: both docs end equal, the host's text plus its patches is the mirror and the serialization, and
    // each remote keystroke reaches the host as one insert of just its own text.
    [Fact]
    public async Task BothSidesTypingAtOnce_ConvergeWithoutLoss_AndEachRemoteKeystrokeIsOneInsert()
    {
        await using var web = new JintNoteRoom(2);
        var seed = await _SeedAsync(web, "structure.md");
        var early = _Taken(await web.EvaluateAsync($"(() => {{ {TextOf} t(f.get(1)).insert(0, 'EARLY '); return {Taken}; }})()", CancellationToken.None));
        var hub = new _Hub(seed, early);
        var patches = new List<DepotNoteLivePatch>();
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
        DepotNoteTextOp[] typed = [new(joined.Text.IndexOf("ruc", StringComparison.Ordinal), 0, "AB"), new(joined.Text.IndexOf("three", StringComparison.Ordinal) + "AB".Length + 2, 0, "CD")];
        Assert.Equal(new DepotNoteEditResult(true, 2), await bridge.EditAsync(1, typed, CancellationToken.None));
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
    }

    private static DepotNoteSession _Session(_Hub hub, Action<DepotNoteLivePatch> patched) => new(
        Connection, Note, _ => Task.FromResult("user-1"), (_, _, onClosed) => hub.With(onClosed), clientId => new JintNoteRoom(clientId),
        _ => { }, [TimeSpan.Zero], patched: patched);

    // The web's seed of a fixture (protocol §5.3), as the one row the hub holds.
    private static async Task<byte[]> _SeedAsync(JintNoteRoom web, string fixture)
    {
        await web.SeedAsync(await File.ReadAllTextAsync(Path.Combine(Fixtures, fixture)), CancellationToken.None);
        return await web.MissingAsync([], CancellationToken.None) ?? throw new InvalidOperationException("The seed is empty.");
    }

    private static string _Applied(string text, IEnumerable<DepotNoteTextOp> ops) =>
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

        public DepotNoteJoinRequest? Join { get; private set; }

        public _Hub With(Func<Exception?, Task> onClosed)
        {
            _closed = onClosed;
            return this;
        }

        public Task Push(DepotNoteEvent hubEvent) => _handler?.Invoke(hubEvent) ?? Task.CompletedTask;

        public void Subscribe(Func<DepotNoteEvent, Task> onEvent) => _handler = onEvent;

        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public async Task<DepotNoteJoinResult> JoinAsync(DepotNoteJoinRequest request, CancellationToken cancellationToken)
        {
            Join = request;
            foreach (var update in early)
            {
                await Push(new DepotNoteEvent("update", ["doc-1", update]));
            }

            return new DepotNoteJoinResult(
                "doc-1", "editor", [new DepotNoteRow(1, row)], Seed: false, AwaitingSeed: false, Epoch: "epoch-1", Stale: false, SchemaMismatch: false);
        }

        public Task UpdateAsync(string documentId, byte[] update, CancellationToken cancellationToken)
        {
            Calls.Add("update");
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

        public ValueTask DisposeAsync() => _closed is null ? ValueTask.CompletedTask : new ValueTask(_closed(null));
    }
}
