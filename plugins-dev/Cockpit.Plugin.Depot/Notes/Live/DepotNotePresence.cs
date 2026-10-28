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

// This host's own identity in the room, for its entry in the list. The hub stamps the same fields on what it relays to others, but not
// back to the sender, and /cockpit/me names only the user id: the entry is "This Cockpit" in the colour the hub gives that id.
internal sealed record DepotPresenceSelf(string Id, string Name, string Color, string Role, string Source)
{
    // NoteAwareness.Palette of AI-Depot: the colour is fixed per user id, SHA-256 of the id mod 8.
    // ponytail: a copy of the hub's rule for this one entry; drop it when Depot tells the client its own identity.
    private static readonly string[] Palette = ["#3b82f6", "#d9a441", "#5fc58a", "#a78bda", "#ef6a5c", "#3fb8c4", "#e07bb0", "#a3c94d"];

    public static DepotPresenceSelf For(string userId, string role) => new(
        userId, "This Cockpit",
        Palette[(int)(BitConverter.ToUInt32(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(userId)), 0) % Palette.Length)],
        role.Length == 0 ? role : char.ToUpperInvariant(role[0]) + role[1..], "Cockpit");
}

// Presence of one note (Docs/notes-hub-protocol.md section 5.4): sends this editor's cursor every five seconds with a clock that
// only goes up, reads what the hub relays, and drops someone who left or stayed silent for fifteen seconds. The hub stamps `user`
// (id, name, role, colour, source) on what it relays; the colour is the hub's and is passed on as it is.
internal sealed class DepotNotePresence : IDisposable
{
    public static readonly TimeSpan Beat = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan Silence = TimeSpan.FromSeconds(15);

    private readonly ulong _clientId;
    private readonly IDepotCursorMap _map;
    private readonly Func<byte[], CancellationToken, Task> _send;
    private readonly Action<IReadOnlyList<LivePeer>> _changed;
    private readonly TimeProvider _time;
    private readonly ILogger? _logger;
    private readonly Lock _gate = new();
    private readonly Lock _raiseGate = new();
    private readonly SemaphoreSlim _sending = new(1, 1);
    private readonly Dictionary<ulong, Remote> _remote = [];
    private readonly ITimer _timer;
    private readonly CancellationTokenSource _life = new();
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

    // The clock of this client's last update; each update has a higher one, or the hub ignores it. The client id must be new for every
    // instance: a web client still holds the clock of an old one and would ignore a restart from 1.
    public ulong Clock => Volatile.Read(ref _clock);

    public IReadOnlyList<LivePeer> Peers
    {
        get
        {
            lock (_gate)
            {
                return _Peers();
            }
        }
    }

    public void SetSelf(DepotPresenceSelf? self)
    {
        lock (_gate)
        {
            _self = self;
        }

        _Raise();
    }

    // Offline: the people stay in the list, dimmed, until the connection is back and they are heard from again.
    public void SetAway(bool away)
    {
        lock (_gate)
        {
            if (_away == away)
            {
                return;
            }

            _away = away;
        }

        _Raise();
    }

    public async Task SetOwnAsync(LivePresenceState state, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _own = state;
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

        foreach (var entry in entries)
        {
            if (entry.ClientId == _clientId)
            {
                continue;
            }

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

            var remote = await _ReadAsync(from, entry, cancellationToken).ConfigureAwait(false);
            lock (_gate)
            {
                if (remote is not null && (!_remote.TryGetValue(entry.ClientId, out var known) || known.Clock < entry.Clock))
                {
                    _remote[entry.ClientId] = remote;
                }
            }
        }

        _Raise();
    }

    // The hub's `left` event: that connection's clients are gone.
    public void Left(string from)
    {
        lock (_gate)
        {
            foreach (var id in _remote.Where(pair => pair.Value.From == from).Select(pair => pair.Key).ToList())
            {
                _remote.Remove(id);
            }
        }

        _Raise();
    }

    public void Dispose()
    {
        _timer.Dispose();
        _life.Cancel();
        _life.Dispose();
        _sending.Dispose();
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

            // Offline the people stay, dimmed, and nothing can be sent; the beat resumes with the connection.
            if (_away)
            {
                return;
            }

            var now = _time.GetUtcNow();
            bool dropped;
            lock (_gate)
            {
                var gone = _remote.Where(pair => now - pair.Value.Seen >= Silence).Select(pair => pair.Key).ToList();
                gone.ForEach(id => _remote.Remove(id));
                dropped = gone.Count > 0;
            }

            if (dropped)
            {
                _Raise();
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
            lock (_gate)
            {
                own = _own;
            }

            var state = new Dictionary<string, object?> { ["activity"] = own.Activity, ["cursor"] = await _CursorAsync(own, cancellationToken).ConfigureAwait(false) };
            var entry = new DepotAwarenessEntry(_clientId, Interlocked.Increment(ref _clock), JsonSerializer.Serialize(state));
            await _send(DepotAwarenessCodec.Encode([entry]), cancellationToken).ConfigureAwait(false);
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
        var head = await _map.RelativeAtAsync(caret, cancellationToken).ConfigureAwait(false);
        var tail = anchor == caret ? head : await _map.RelativeAtAsync(anchor, cancellationToken).ConfigureAwait(false);
        return head is null || tail is null
            ? null
            : JsonSerializer.SerializeToElement(new Dictionary<string, JsonElement>
            {
                ["anchor"] = JsonDocument.Parse(tail).RootElement.Clone(),
                ["head"] = JsonDocument.Parse(head).RootElement.Clone(),
            });
    }

    private async Task<Remote?> _ReadAsync(string from, DepotAwarenessEntry entry, CancellationToken cancellationToken)
    {
        try
        {
            using var document = JsonDocument.Parse(entry.State);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("user", out var user) || user.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            int? caret = null;
            LiveRange? selection = null;
            if (root.TryGetProperty("cursor", out var cursor) && cursor.ValueKind == JsonValueKind.Object
                && cursor.TryGetProperty("anchor", out var anchor) && cursor.TryGetProperty("head", out var head))
            {
                var headAt = await _map.OffsetOfAsync(head.GetRawText(), cancellationToken).ConfigureAwait(false);
                var anchorAt = await _map.OffsetOfAsync(anchor.GetRawText(), cancellationToken).ConfigureAwait(false);
                caret = headAt;
                selection = headAt is { } h && anchorAt is { } a && a != h ? new LiveRange(Math.Min(a, h), Math.Max(a, h)) : null;
            }

            string Text(string name) => user.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
            var role = Text("role");
            return new Remote(from, entry.Clock, _time.GetUtcNow(), new LivePeer(
                Text("id"), Text("name"), Text("color"), Text("source"), role.Length == 0 ? role : char.ToUpperInvariant(role[0]) + role[1..],
                root.TryGetProperty("activity", out var activity) && activity.ValueKind == JsonValueKind.String ? activity.GetString() ?? "reading" : "reading",
                Caret: caret, Selection: selection));
        }
        catch (JsonException error)
        {
            _logger?.LogDebug(error, "The awareness state of a client could not be read.");
            return null;
        }
    }

    private List<LivePeer> _Peers()
    {
        var peers = new List<LivePeer>();
        if (_self is { } self)
        {
            peers.Add(new LivePeer(self.Id, self.Name, self.Color, self.Source, self.Role, _own.Activity, You: true, Away: _away));
        }

        peers.AddRange(_remote.Values.OrderBy(remote => remote.Peer.Who, StringComparer.Ordinal).Select(remote => remote.Peer with { Away = _away }));
        return peers;
    }

    // Snapshot and delivery under one gate, so lists reach the host in the order they were made.
    private void _Raise()
    {
        lock (_raiseGate)
        {
            IReadOnlyList<LivePeer> peers;
            lock (_gate)
            {
                peers = _Peers();
            }

            _changed(peers);
        }
    }

    private sealed record Remote(string From, ulong Clock, DateTimeOffset Seen, LivePeer Peer);
}
