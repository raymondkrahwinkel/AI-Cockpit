using System.Text.Json;
using Cockpit.Plugin.Depot.Notes.Live;
using Cockpit.Plugins.Abstractions.Files;

namespace Cockpit.Plugin.Depot.Tests;

// AC-1900, layer 2 (contract with another system): the awareness bytes of Depot's web client, and the clocks and timeouts the hub expects.
public class DepotNotePresenceTests
{
    // Written by y-protocols 1.0.7 (`encodeAwarenessUpdate`, the library Depot's web editor uses) for client 2113437100 at clock 1: the
    // hub-stamped user, activity "selecting" and a cursor of two relativePositionToJSON values. `Left` is the same client at clock 2, state null.
    private const string Selecting = "Aaz74e8HAeQCeyJ1c2VyIjp7ImlkIjoidS1zYW5uZSIsIm5hbWUiOiJTYW5uZSBkZSBWcmllcyIsInJvbGUiOiJlZGl0b3IiLCJjb2xvciI6IiMzZmI4YzQiLCJzb3VyY2UiOiJ3ZWIifSwiYWN0aXZpdHkiOiJzZWxlY3RpbmciLCJjdXJzb3IiOnsiYW5jaG9yIjp7InR5cGUiOnsiY2xpZW50IjoyMTEzNDM3MTAwLCJjbG9jayI6M30sInRuYW1lIjpudWxsLCJpdGVtIjp7ImNsaWVudCI6MjExMzQzNzEwMCwiY2xvY2siOjE3fSwiYXNzb2MiOjB9LCJoZWFkIjp7InR5cGUiOnsiY2xpZW50IjoyMTEzNDM3MTAwLCJjbG9jayI6M30sInRuYW1lIjpudWxsLCJpdGVtIjp7ImNsaWVudCI6MjExMzQzNzEwMCwiY2xvY2siOjMyfSwiYXNzb2MiOjB9fX0=";
    private const string Left = "Aaz74e8HAgRudWxs";

    // Relayed by a local Depot (AI-Depot 6fe38ee8, SQLite) as the `presence` event: Depot's web editor, a range selected in the note,
    // the hub's own stamp (client 3670873689, clock 4, anchor at item clock 71, head at 87, colour #a78bda).
    private const string Relayed = "AdmstNYNBIgDeyJ1c2VyIjp7ImlkIjoiMTNjYjE1MWItNjk1Yi00OWQ1LWIwYmItMDk0ZDg4YzllZWQzIiwibmFtZSI6ImFkbWluQGRlcG90LmxvY2FsIiwicm9sZSI6ImVkaXRvciIsImNvbG9yIjoiI2E3OGJkYSIsInNvdXJjZSI6IndlYiJ9LCJhY3Rpdml0eSI6InNlbGVjdGluZyIsImN1cnNvciI6eyJhbmNob3IiOnsidHlwZSI6eyJjbGllbnQiOjM2NzA4NzM2ODksImNsb2NrIjo2OX0sInRuYW1lIjpudWxsLCJpdGVtIjp7ImNsaWVudCI6MzY3MDg3MzY4OSwiY2xvY2siOjcxfSwiYXNzb2MiOi0xfSwiaGVhZCI6eyJ0eXBlIjp7ImNsaWVudCI6MzY3MDg3MzY4OSwiY2xvY2siOjY5fSwidG5hbWUiOm51bGwsIml0ZW0iOnsiY2xpZW50IjozNjcwODczNjg5LCJjbG9jayI6ODd9LCJhc3NvYyI6LTF9fX0=";

    [Fact]
    public async Task WebClientAwareness_RoundTripsByteForByte_AndEveryOwnUpdateHasAHigherClock_AndPeersGoOnLeftOrSilence()
    {
        var bytes = Convert.FromBase64String(Selecting);
        var decoded = DepotAwarenessCodec.Decode(bytes);
        Assert.Equal(bytes, DepotAwarenessCodec.Encode(decoded));
        Assert.Equal((2113437100UL, 1UL), (decoded[0].ClientId, decoded[0].Clock));
        Assert.Equal("null", DepotAwarenessCodec.Decode(Convert.FromBase64String(Left))[0].State);

        var sent = new List<byte[]>();
        var time = new ManualTime();
        var changes = new List<IReadOnlyList<LivePeer>>();
        using var presence = new DepotNotePresence(7, new CursorMap(), (update, _) => { sent.Add(update); return Task.CompletedTask; }, changes.Add, time);

        await presence.ReceiveAsync("conn-1", bytes, CancellationToken.None);
        var peer = Assert.Single(presence.Peers);
        Assert.Equal(("u-sanne", "Sanne de Vries", "#3fb8c4", "web", "Editor", "selecting"), (peer.Who, peer.Name, peer.Color, peer.Source, peer.Role, peer.Activity));
        Assert.Equal((32, new LiveRange(17, 32)), (peer.Caret, peer.Selection));

        await presence.SetOwnAsync(new LivePresenceState("editing", 40), CancellationToken.None);
        await presence.BeatAsync();
        var first = DepotAwarenessCodec.Decode(sent[0])[0];
        var second = DepotAwarenessCodec.Decode(sent[1])[0];
        Assert.Equal((7UL, 1UL, 2UL), (first.ClientId, first.Clock, second.Clock));
        Assert.Equal(40, JsonDocument.Parse(second.State).RootElement.GetProperty("cursor").GetProperty("head").GetProperty("item").GetProperty("clock").GetInt32());

        await presence.ReceiveAsync("conn-1", Convert.FromBase64String(Left), CancellationToken.None);
        Assert.Empty(presence.Peers);

        await presence.ReceiveAsync("conn-2", bytes, CancellationToken.None);
        await presence.LeftAsync("conn-2");
        Assert.Empty(presence.Peers);

        await presence.ReceiveAsync("conn-3", bytes, CancellationToken.None);
        time.Advance(DepotNotePresence.Silence - TimeSpan.FromSeconds(1));
        await presence.BeatAsync();
        Assert.Single(presence.Peers);
        time.Advance(TimeSpan.FromSeconds(1));
        await presence.BeatAsync();
        Assert.Empty(presence.Peers);

        var relayed = Convert.FromBase64String(Relayed);
        Assert.Equal(relayed, DepotAwarenessCodec.Encode(DepotAwarenessCodec.Decode(relayed)));
        await presence.ReceiveAsync("conn-4", relayed, CancellationToken.None);
        var real = Assert.Single(presence.Peers);
        Assert.Equal(("admin@depot.local", "#a78bda", "web", "Editor", "selecting"), (real.Name, real.Color, real.Source, real.Role, real.Activity));
        Assert.Equal((87, new LiveRange(71, 87)), (real.Caret, real.Selection));

        await presence.OutsideAsync("Assistant · on behalf of Raymond", CancellationToken.None);
        Assert.Equal("agent", presence.Peers[^1].Source);
        Assert.Equal("Assistant · on behalf of Raymond", presence.Peers[^1].Name);
        time.Advance(TimeSpan.FromSeconds(29));
        await presence.BeatAsync();
        Assert.Equal("agent", Assert.Single(presence.Peers).Source);
        await presence.SetAwayAsync(true);
        time.Advance(TimeSpan.FromSeconds(1));
        await presence.BeatAsync();
        Assert.Empty(presence.Peers);
    }

    // A cursor "position" that is only its item clock, which is all this test needs of the bridge's mapping.
    private sealed class CursorMap : IDepotCursorMap
    {
        public Task<int?> OffsetOfAsync(string relativeJson, CancellationToken cancellationToken) =>
            Task.FromResult<int?>(JsonDocument.Parse(relativeJson).RootElement.GetProperty("item").GetProperty("clock").GetInt32());

        public Task<string?> RelativeAtAsync(int offset, CancellationToken cancellationToken) =>
            Task.FromResult<string?>($$"""{"type":{"client":7,"clock":0},"tname":null,"item":{"client":7,"clock":{{offset}}},"assoc":0}""");
    }

    private sealed class ManualTime : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 28, 14, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}
