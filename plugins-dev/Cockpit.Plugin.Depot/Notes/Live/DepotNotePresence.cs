using System.Text.Json;
using Cockpit.Plugins.Abstractions.Files;
using Microsoft.Extensions.Logging;

namespace Cockpit.Plugin.Depot.Notes.Live;

// Offset <-> Yjs RelativePosition (relativePositionToJSON), in the room's own text. AC-1899's bridge implements it: the spike's G6
// maps a Markdown offset to a position in the block it sits in and back. Null means the position is not in the text any more.
internal interface IDepotCursorMap
{
    Task<int?> OffsetOfAsync(string relativeJson, CancellationToken cancellationToken);

    Task<string?> RelativeAtAsync(int offset, CancellationToken cancellationToken);
}

// The room's current Y.Doc and mirror, whichever room the session holds now (a `reset` swaps it).
internal sealed class DepotRoomCursorMap(Func<IDepotNoteRoom> room) : IDepotCursorMap
{
    public Task<int?> OffsetOfAsync(string relativeJson, CancellationToken cancellationToken) => room().OffsetOfAsync(relativeJson, cancellationToken);

    public Task<string?> RelativeAtAsync(int offset, CancellationToken cancellationToken) => room().RelativeAtAsync(offset, cancellationToken);
}

// This host's own identity in the room, for its entry in the list. The hub stamps name and colour on what it relays to others but
// not back to the sender, and /cockpit/me names only the user id: both stay null until Depot says them, and the host shows "You".
internal sealed record DepotPresenceSelf(string Id, string? Name, string? Color, string Role, string Source)
{
    public static DepotPresenceSelf For(string userId, string role, string? name = null, string? color = null) => new(
        userId, name, color, role.Length == 0 ? role : char.ToUpperInvariant(role[0]) + role[1..], "Cockpit");
}

// Presence of one note (notes-hub-protocol section 5.4): this editor's cursor every five seconds under a rising clock, what the hub
// relays (its stamped user and colour passed on as they are), and anyone gone on left or after fifteen seconds of silence.
// One instance serves the session: a new hub connection brings a new client id and the people stay, dimmed, until heard again.
internal sealed class DepotNotePresence : IDisposable
{
    public static readonly TimeSpan Beat = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan Silence = TimeSpan.FromSeconds(15);

    private readonly IDepotCursorMap _map;
    private readonly Action<IReadOnlyList<LivePeer>> _changed;
    private readonly TimeProvider _time;
    private readonly ILogger? _logger;
    private readonly Lock _gate = new();
    private readonly SemaphoreSlim _raising = new(1, 1);
    private readonly SemaphoreSlim _sending = new(1, 1);
    private readonly Dictionary<ulong, Remote> _remote = [];
    private readonly ITimer _timer;
    private readonly CancellationTokenSource _life = new();
    private ulong _clientId;
    private Func<byte[], CancellationToken, Task> _send;
    private IReadOnlyList<LivePeer> _published = [];
    private LivePresenceState _own = new("reading");
    private DepotPresenceSelf? _self;
    private ulong _clock;
    private bool _away;

    public DepotNotePresence(
        ulong clientId, IDepotCursorMap map, Func<byte[], CancellationToken, Task> send, Action<IReadOnlyList<LivePeer>> changed,
        TimeProvider? time = null, ILogger? logger = null)
    {
        _clientId = clientId;
        _map = map;
        _send = send;
        _changed = changed;
        _time = time ?? TimeProvider.System;
        _logger = logger;
        _timer = _time.CreateTimer(async _ => await BeatAsync().ConfigureAwait(false), null, Beat, Beat);
    }

    // The clock of this client's last update; each update has a higher one, or the hub ignores it.
    public ulong Clock => Volatile.Read(ref _clock);

    // The list as last handed to the host, with the offsets of that moment.
    public IReadOnlyList<LivePeer> Peers => Volatile.Read(ref _published);

    public Task SetSelfAsync(DepotPresenceSelf? self, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            _self = self;
        }

        return _RaiseAsync(cancellationToken);
    }

    // Offline: the people stay in the list, dimmed, and nothing is sent until the connection is back.
    public Task SetAwayAsync(bool away, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_away == away)
            {
                return Task.CompletedTask;
            }

            _away = away;
        }

        return _RaiseAsync(cancellationToken);
    }

    // A new hub connection: the hub knows a connection by the first client id it uses, so the id is new and the clock starts over.
    // A web client holds the clock of an old id and would ignore a restart on it.
    public Task ReconnectAsync(ulong clientId, Func<byte[], CancellationToken, Task> send, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            _clientId = clientId;
            _send = send;
            _clock = 0;
            _away = false;
        }

        return _RaiseAsync(cancellationToken);
    }

    public async Task SetOwnAsync(LivePresenceState state, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _own = state;
            if (_away)
            {
                return;
            }
        }

        await _SendAsync(cancellationToken).ConfigureAwait(false);
    }

    // Bytes of the hub's `presence` event.
    public async Task ReceiveAsync(string from, byte[] data, CancellationToken cancellationToken)
    {
        IReadOnlyList<DepotAwarenessEntry> entries;
        try
        {
            entries = DepotAwarenessCodec.Decode(data);
        }
        catch (FormatException error)
        {
            _logger?.LogDebug(error, "An awareness update from the hub could not be read.");
            return;
        }

        ulong own;
        lock (_gate)
        {
            own = _clientId;
        }

        foreach (var entry in entries.Where(entry => entry.ClientId != own))
        {
            // Like y-protocols: a state replaces an older clock, and a null removes a client only at the same or a higher clock.
            if (entry.State == "null")
            {
                lock (_gate)
                {
                    if (_remote.TryGetValue(entry.ClientId, out var gone) && gone.Clock <= entry.Clock)
                    {
                        _remote.Remove(entry.ClientId);
                    }
                }

                continue;
            }

            var remote = _Read(from, entry);
            lock (_gate)
            {
                if (remote is not null && (!_remote.TryGetValue(entry.ClientId, out var known) || known.Clock < entry.Clock))
                {
                    _remote[entry.ClientId] = remote;
                }
            }
        }

        await _RaiseAsync(cancellationToken).ConfigureAwait(false);
    }

    // The hub's `left` event: that connection's clients are gone.
    public Task LeftAsync(string from, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            foreach (var id in _remote.Where(pair => pair.Value.From == from).Select(pair => pair.Key).ToList())
            {
                _remote.Remove(id);
            }
        }

        return _RaiseAsync(cancellationToken);
    }

    public void Dispose()
    {
        _timer.Dispose();
        _life.Cancel();
        _life.Dispose();
    }

    // Seen again or not, the beat resends this client's cursor and drops whoever has been silent too long.
    internal async Task BeatAsync()
    {
        try
        {
            if (_life.IsCancellationRequested)
            {
                return;
            }

            var now = _time.GetUtcNow();
            bool dropped;
            lock (_gate)
            {
                // Offline the people stay, dimmed, and nothing can be sent; the beat resumes with the connection.
                if (_away)
                {
                    return;
                }

                var gone = _remote.Where(pair => now - pair.Value.Seen >= Silence).Select(pair => pair.Key).ToList();
                gone.ForEach(id => _remote.Remove(id));
                dropped = gone.Count > 0;
            }

            if (dropped)
            {
                await _RaiseAsync(_life.Token).ConfigureAwait(false);
            }

            await _SendAsync(_life.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_life.IsCancellationRequested) { }
        catch (Exception error)
        {
            _logger?.LogWarning(error, "The presence beat of a live note failed; the next one tries again.");
        }
    }

    // One send at a time, each reading the newest state, so a slow beat can never put an older cursor under a higher clock.
    private async Task _SendAsync(CancellationToken cancellationToken)
    {
        await _sending.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            LivePresenceState own;
            ulong clientId;
            Func<byte[], CancellationToken, Task> send;
            lock (_gate)
            {
                (own, clientId, send) = (_own, _clientId, _send);
            }

            var state = new Dictionary<string, object?> { ["activity"] = own.Activity, ["cursor"] = await _CursorAsync(own, cancellationToken).ConfigureAwait(false) };
            var entry = new DepotAwarenessEntry(clientId, Interlocked.Increment(ref _clock), JsonSerializer.Serialize(state));
            await send(DepotAwarenessCodec.Encode([entry]), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _sending.Release();
        }
    }

    // {anchor, head} as relativePositionToJSON, or null when the caret is not in the text.
    private async Task<JsonElement?> _CursorAsync(LivePresenceState own, CancellationToken cancellationToken)
    {
        if (own.Caret is not { } caret)
        {
            return null;
        }

        var anchor = own.Selection is { } range ? (caret == range.End ? range.Start : range.End) : caret;
        var head = await _RelativeAsync(caret, cancellationToken).ConfigureAwait(false);
        var tail = anchor == caret ? head : await _RelativeAsync(anchor, cancellationToken).ConfigureAwait(false);
        return head is null || tail is null
            ? null
            : JsonSerializer.SerializeToElement(new Dictionary<string, JsonElement>
            {
                ["anchor"] = JsonDocument.Parse(tail).RootElement.Clone(),
                ["head"] = JsonDocument.Parse(head).RootElement.Clone(),
            });
    }

    // The room can be gone (a reset swaps it) or the text not loaded: no cursor then, not a failed beat.
    private async Task<string?> _RelativeAsync(int offset, CancellationToken cancellationToken)
    {
        try
        {
            return await _map.RelativeAtAsync(offset, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            _logger?.LogDebug(error, "No cursor could be made for offset {Offset}.", offset);
            return null;
        }
    }

    // Another client's position can be one this room cannot read (the hub only checks that a key is there): no caret, but the person stays.
    private async Task<int?> _OffsetAsync(string relativeJson, CancellationToken cancellationToken)
    {
        try
        {
            return await _map.OffsetOfAsync(relativeJson, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            _logger?.LogDebug(error, "A cursor of another client could not be read.");
            return null;
        }
    }

    private Remote? _Read(string from, DepotAwarenessEntry entry)
    {
        try
        {
            using var document = JsonDocument.Parse(entry.State);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("user", out var user) || user.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            string? anchor = null;
            string? head = null;
            if (root.TryGetProperty("cursor", out var cursor) && cursor.ValueKind == JsonValueKind.Object
                && cursor.TryGetProperty("anchor", out var a) && cursor.TryGetProperty("head", out var h))
            {
                (anchor, head) = (a.GetRawText(), h.GetRawText());
            }

            string Text(string name) => user.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
            var role = Text("role");
            var source = Text("source");
            return new Remote(from, entry.Clock, _time.GetUtcNow(), anchor, head, new LivePeer(
                Text("id"), Text("name"), Text("color"), source == "cockpit" ? "Cockpit" : source,
                role.Length == 0 ? role : char.ToUpperInvariant(role[0]) + role[1..],
                root.TryGetProperty("activity", out var activity) && activity.ValueKind == JsonValueKind.String ? activity.GetString() ?? "reading" : "reading"));
        }
        catch (JsonException error)
        {
            _logger?.LogDebug(error, "The awareness state of a client could not be read.");
            return null;
        }
    }

    // The list as the host should see it now: the offsets are worked out from the stored positions each time, on the text as it is.
    // Snapshot, mapping and delivery under one gate, so lists reach the host in the order they were made.
    private async Task _RaiseAsync(CancellationToken cancellationToken)
    {
        await _raising.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            List<Remote> remote;
            DepotPresenceSelf? self;
            LivePresenceState own;
            bool away;
            lock (_gate)
            {
                (remote, self, own, away) = ([.. _remote.Values.OrderBy(item => item.Peer.Who, StringComparer.Ordinal)], _self, _own, _away);
            }

            var peers = new List<LivePeer>();
            if (self is not null)
            {
                peers.Add(new LivePeer(self.Id, self.Name ?? "", self.Color ?? "", self.Source, self.Role, self.Role == "Viewer" ? "watching" : own.Activity, You: true, Away: away));
            }

            foreach (var item in remote)
            {
                var caret = item.Head is null ? null : await _OffsetAsync(item.Head, cancellationToken).ConfigureAwait(false);
                var anchor = caret is null || item.Anchor is null ? null : await _OffsetAsync(item.Anchor, cancellationToken).ConfigureAwait(false);
                var selection = caret is { } c && anchor is { } a && a != c ? new LiveRange(Math.Min(a, c), Math.Max(a, c)) : null;
                peers.Add(item.Peer with { Away = away, Caret = caret, Selection = selection });
            }

            Volatile.Write(ref _published, peers);
            _changed(peers);
        }
        finally
        {
            _raising.Release();
        }
    }

    private sealed record Remote(string From, ulong Clock, DateTimeOffset Seen, string? Anchor, string? Head, LivePeer Peer);
}
