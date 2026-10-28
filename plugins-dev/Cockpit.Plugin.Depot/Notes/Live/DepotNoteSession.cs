using System.Net;
using System.Reflection;
using System.Text.RegularExpressions;
using Cockpit.Plugin.Depot.Contracts;
using Cockpit.Plugin.Depot.Events;
using Cockpit.Plugin.Depot.Model;
using Cockpit.Plugins.Abstractions.Files;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;

namespace Cockpit.Plugin.Depot.Notes.Live;

// The schema version of the notes the plugin draws, sent with every Join (DEP-530): SHA-256[:12] of Bundle/schema.json, which the
// build computes (DepotRoomBundlePin in the csproj), so it always belongs to the bundle that ships.
internal static class DepotNoteSchema
{
    public static readonly string Version =
        typeof(DepotNoteSchema).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(attribute => attribute.Key == "DepotNoteSchema").Value
        ?? throw new InvalidOperationException("The build left out the schema version.");
}

// One note's connection to /hubs/notes: Join (protocol section 3), the room's Y.Doc, and a new Join when the hub closes it; a `reset`
// swaps the room. The host's text goes through the room's mirror on ILiveFileSession's terms (AC-1898): every change is one revision,
// the host's edits go out as Edited plus Update, and everything else, its own undo included, comes back as a patch.
internal sealed partial class DepotNoteSession : IAsyncDisposable, ILivePresence
{
    // Protocol §7: an Update or a Compact is no larger.
    private const int MaxSnapshotBytes = 4 * 1024 * 1024;

    private readonly DepotConnectionRegistration _connection;
    private readonly DepotNoteLiveRequest _note;
    private readonly Func<CancellationToken, Task<string>> _userId;
    private readonly DepotNotesHubFactory _hubFactory;
    private readonly DepotNoteRoomFactory _roomFactory;
    private readonly Action<DepotNoteLiveStatus> _publish;
    private readonly IReadOnlyList<TimeSpan> _backoff;
    private readonly ILogger? _logger;
    private readonly Action<LiveFilePatch>? _patched;
    private readonly CancellationTokenSource _life = new();

    // The text side: the room's content, the revision and the host's edits change under this one, so a revision names one text.
    private readonly SemaphoreSlim _text = new(1, 1);
    private long _rev;
    private bool _loaded;
    private string _previous = string.Empty;
    private bool _editor;
    private bool _typedAway;
    private string? _lastEditId;
    private long _lastEditRev;
    private string? _role;
    private DepotNotePresence? _presence;
    private LivePresenceState _wanted = new("reading");
    private IReadOnlyList<LivePeer> _peers = [];
    private bool _schemaMismatch;
    private bool _roleLost;
    private IDepotNoteRoom? _sharedReadOnlyRoom;
    private string? _unsentSourceText;

    // Set once the room has a text or the session has stopped: a read before that would hand the host an empty note.
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Lock _gate = new();
    private readonly List<byte[]> _early = [];
    private IDepotNoteRoom _room;
    private IDepotNotesHub? _hub;
    private TaskCompletionSource<Exception?> _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private string? _documentId;
    private string? _epoch;
    private string? _state;
    private volatile bool _terminal;
    private volatile bool _reset;
    private Task _loop = Task.CompletedTask;

    public DepotNoteSession(
        DepotConnectionRegistration connection,
        DepotNoteLiveRequest note,
        Func<CancellationToken, Task<string>> userId,
        DepotNotesHubFactory hubFactory,
        DepotNoteRoomFactory roomFactory,
        Action<DepotNoteLiveStatus> publish,
        IReadOnlyList<TimeSpan>? backoff = null,
        ILogger? logger = null,
        Action<LiveFilePatch>? patched = null)
    {
        _connection = connection;
        _note = note;
        _userId = userId;
        _hubFactory = hubFactory;
        _roomFactory = roomFactory;
        _publish = publish;
        _backoff = backoff ?? DepotEventReconnect.Backoff;
        _logger = logger;
        _patched = patched;
        _room = roomFactory(_NewClientId());
    }

    public DepotConnectionRegistration Connection => _connection;

    public DepotNoteLiveRequest Note => _note;

    public IDepotNoteRoom Room => _room;

    public event EventHandler<IReadOnlyList<LivePeer>>? PeersChanged;

    // Everyone in the note, this Cockpit first; empty until the first Join is through.
    public IReadOnlyList<LivePeer> Peers => _peers;

    // The note in Depot's own web editor.
    public string? OpenUrl => string.Join('/', new[] { _connection.Url.TrimEnd('/'), "projects", Uri.EscapeDataString(_note.Organization), Uri.EscapeDataString(_note.Project), "view" }
        .Concat(_note.Path.Split('/').Select(Uri.EscapeDataString)));

    // Where this Cockpit's caret is and what it is doing; sent at once when joined, and with every beat after that.
    public async Task SetPresenceAsync(LivePresenceState state, CancellationToken cancellationToken)
    {
        _wanted = state;
        if (_presence is { } presence)
        {
            await presence.SetOwnAsync(state, cancellationToken).ConfigureAwait(false);
        }
    }

    // Ends when the session stops trying: signed out, refused, stale, or disposed.
    public Task Completion => _loop;

    // Starts the connection loop, or starts it again after SignInNeeded once the user has signed in. Running already: nothing.
    public void Connect()
    {
        lock (_gate)
        {
            if (!_loop.IsCompleted || _life.IsCancellationRequested)
            {
                return;
            }

            _terminal = false;
            _loop = _RunAsync(_life.Token);
        }
    }

    // The whole text, its revision and its status, from one moment, once the room has a text (or the session stopped without one).
    public async Task<LiveFileSnapshot> ReadAsync(CancellationToken cancellationToken)
    {
        await _ready.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        await _text.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var text = _loaded ? await _room.ReadAsync(cancellationToken).ConfigureAwait(false) : _previous;
            return new LiveFileSnapshot(text, _rev, _LiveStatus());
        }
        finally
        {
            _text.Release();
        }
    }

    // The host's ops on the text at `baseRev`; on any other revision nothing applies, and the host takes the patches it is behind on
    // first. An edit repeated under the last id answers what it got. Typed while the hub is away, the change stays in the doc and
    // goes out with the next Join, Edited first (protocol §3 step 5).
    public async Task<LiveEditResult> EditAsync(string editId, long baseRev, IReadOnlyList<TextOp> ops, CancellationToken cancellationToken)
    {
        await _text.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (editId == _lastEditId)
            {
                return new LiveEditResult(LiveEditStatus.Applied, _lastEditRev);
            }

            if (_loaded && !_editor)
            {
                throw new FileSourceException(FileSourceErrorKind.Forbidden, "This note is read-only here.");
            }

            if (!_loaded || baseRev != _rev)
            {
                return new LiveEditResult(LiveEditStatus.Stale, _rev);
            }

            var before = await _room.SnapshotAsync(cancellationToken).ConfigureAwait(false);
            var updates = await _room.EditAsync(ops, cancellationToken).ConfigureAwait(false);
            try
            {
                await _SendTypedAsync(updates, cancellationToken).ConfigureAwait(false);
            }
            catch (FileSourceException error) when (error.Kind == FileSourceErrorKind.Forbidden)
            {
                // The host keeps the rejected ops; the mirror must still describe the shared text for incoming patches.
                await _RestoreSharedAsync(before, cancellationToken).ConfigureAwait(false);
                throw;
            }
            _rev++;
            _lastEditId = editId;
            _lastEditRev = _rev;
            return new LiveEditResult(LiveEditStatus.Applied, _rev);
        }
        finally
        {
            _text.Release();
        }
    }

    // Undoes (or redoes) the host's own edits only, the way the web's Y.UndoManager does; the result comes back as a patch.
    public async Task UndoAsync(bool redo, CancellationToken cancellationToken)
    {
        await _text.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_loaded || !_editor)
            {
                return;
            }

            var before = await _room.SnapshotAsync(cancellationToken).ConfigureAwait(false);
            var undo = await _room.UndoAsync(redo, cancellationToken).ConfigureAwait(false);
            try
            {
                await _SendTypedAsync(undo.Updates, cancellationToken).ConfigureAwait(false);
            }
            catch (FileSourceException error) when (error.Kind == FileSourceErrorKind.Forbidden)
            {
                await _RestoreSharedAsync(before, cancellationToken).ConfigureAwait(false);
                throw;
            }
            _Patch(undo.Ops);
        }
        finally
        {
            _text.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _life.CancelAsync().ConfigureAwait(false);
        await _CloseHubAsync().ConfigureAwait(false);
        await _loop.ConfigureAwait(false);
        _presence?.Dispose();
        await _room.DisposeAsync().ConfigureAwait(false);
        if (_sharedReadOnlyRoom is { } shared)
        {
            await shared.DisposeAsync().ConfigureAwait(false);
        }
        _ready.TrySetResult();
        _text.Dispose();
        _life.Dispose();
    }

    private async Task _RunAsync(CancellationToken cancellationToken)
    {
        var attempt = 0;
        try
        {
            while (!cancellationToken.IsCancellationRequested && !_terminal)
            {
                _Status(_state is null ? DepotNoteLiveState.Connecting : DepotNoteLiveState.Reconnecting);
                if (await _ConnectOnceAsync(cancellationToken).ConfigureAwait(false))
                {
                    attempt = 0;
                    await _closed.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                }

                await _CloseHubAsync().ConfigureAwait(false);
                if (_presence is { } away)
                {
                    await away.SetAwayAsync(true, CancellationToken.None).ConfigureAwait(false);
                }

                if (_terminal)
                {
                    break;
                }

                _Status(DepotNoteLiveState.Reconnecting);
                await Task.Delay(_backoff[Math.Min(attempt++, _backoff.Count - 1)], cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            _logger?.LogError(exception, "The live note connection for {Connection} stopped unexpectedly.", _connection.Name);
            _Terminal(DepotNoteLiveState.Unavailable);
        }
        finally
        {
            await _CloseHubAsync().ConfigureAwait(false);
        }
    }

    // True when the Join went through and the connection is live; false when the attempt failed (the loop decides what next).
    private async Task<bool> _ConnectOnceAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (_reset)
            {
                // The room was refilled: nothing of the old doc is worth keeping, and the epoch belongs to the old fill. The host's text
                // stays, so the new room's text arrives as patches on it.
                _reset = false;
                await _text.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    var old = _room;
                    _previous = _loaded ? await old.ReadAsync(cancellationToken).ConfigureAwait(false) : _previous;
                    _loaded = false;
                    _room = _roomFactory(_NewClientId());
                    _epoch = null;
                    await old.DisposeAsync().ConfigureAwait(false);
                }
                finally
                {
                    _text.Release();
                }
            }

            var user = await _userId(cancellationToken).ConfigureAwait(false);
            // Each attempt waits on its own signal: a late Closed from the hub before it must not end this one.
            var closed = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
            _closed = closed;
            lock (_gate)
            {
                _documentId = null;
                _early.Clear();
            }

            var hub = _hubFactory(_connection, user, _ =>
            {
                closed.TrySetResult(null);
                return Task.CompletedTask;
            });
            _hub = hub;

            // Handlers first, then Join (section 3 step 1): events can arrive before the Join result does.
            hub.Subscribe(_OnEventAsync);
            await hub.StartAsync(cancellationToken).ConfigureAwait(false);
            var result = await hub.JoinAsync(
                new DepotNoteJoinRequest(_note.Organization, _note.Project, _note.Path, Watch: false, _epoch, DepotNoteSchema.Version, Save: false),
                cancellationToken).ConfigureAwait(false);
            if (result.Stale)
            {
                _Terminal(DepotNoteLiveState.Stale);
                return false;
            }

            await _EnterAsync(hub, result, cancellationToken).ConfigureAwait(false);

            // A refusal that came in during the Join already named the state; "live" must not cover it.
            if (_terminal)
            {
                return false;
            }

            await _StartPresenceAsync(hub, result, user, cancellationToken).ConfigureAwait(false);
            _Status(DepotNoteLiveState.Live);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _Failed(exception);
            return false;
        }
    }

    // One presence for the session: the hub gives a connection one client id and ignores any other, so each connection brings a new id,
    // and the people stay in the list, dimmed, until they are heard again. A first send that fails does not fail the Join.
    private async Task _StartPresenceAsync(IDepotNotesHub hub, DepotNoteJoinResult result, string userId, CancellationToken cancellationToken)
    {
        var documentId = result.DocumentId;
        Task Send(byte[] bytes, CancellationToken ct) => hub.PresenceAsync(documentId, bytes, ct);
        try
        {
            var presence = _presence;
            if (presence is null)
            {
                presence = new DepotNotePresence(_NewClientId(), new DepotRoomCursorMap(() => _room), Send, _OnPeers, logger: _logger);
                _presence = presence;
            }
            else
            {
                await presence.ReconnectAsync(_NewClientId(), Send, cancellationToken).ConfigureAwait(false);
            }

            await presence.SetSelfAsync(DepotPresenceSelf.For(userId, result.Role), cancellationToken).ConfigureAwait(false);
            await presence.SetOwnAsync(_wanted, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger?.LogWarning(exception, "Starting presence for the live note of {Connection} failed; the next beat tries again.", _connection.Name);
        }
    }

    private void _OnPeers(IReadOnlyList<LivePeer> peers)
    {
        _peers = peers;
        PeersChanged?.Invoke(this, peers);
    }

    // Section 3 steps 3 to 5: the rows, then the updates that came in before the result, then the seed or the repair, then what the hub
    // still lacks, and a Compact once the rows have piled up (§7). A room joined again keeps its mirror, so its rows come in as patches.
    private async Task _EnterAsync(IDepotNotesHub hub, DepotNoteJoinResult result, CancellationToken cancellationToken)
    {
        await _text.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            List<byte[]> known = [.. result.Updates.OrderBy(row => row.Seq).Select(row => row.Data)];
            foreach (var row in known)
            {
                await _ApplyAsync(row, cancellationToken).ConfigureAwait(false);
            }

            List<byte[]> early;
            lock (_gate)
            {
                _documentId = result.DocumentId;
                early = [.. _early];
                _early.Clear();
            }

            foreach (var update in early)
            {
                await _ApplyAsync(update, cancellationToken).ConfigureAwait(false);
            }

            // Early updates came from the hub, so the hub has them: they count as known, or the diff would send them back.
            known.AddRange(early);
            _epoch = result.Epoch ?? _epoch;
            _roleLost = result.Role != "editor" && (_role == "editor" || _roleLost);
            _role = result.Role;
            _schemaMismatch = result.SchemaMismatch;
            _editor = result.Role == "editor";
            try
            {
                if (_editor && await _WriteAsync(hub, result, known, cancellationToken).ConfigureAwait(false))
                {
                    return;
                }
            }
            catch (HubException exception) when (exception.Message.Contains("notes.read_only", StringComparison.Ordinal))
            {
                _editor = false;
                _roleLost = true;
            }
            if (_sharedReadOnlyRoom is { } previous)
            {
                await previous.DisposeAsync().ConfigureAwait(false);
                _sharedReadOnlyRoom = null;
                _unsentSourceText = null;
            }
            if (!_editor && _typedAway)
            {
                _sharedReadOnlyRoom = _roomFactory(_NewClientId());
                foreach (var update in known)
                {
                    await _sharedReadOnlyRoom.ApplyAsync(update, cancellationToken).ConfigureAwait(false);
                }
                await _sharedReadOnlyRoom.LoadAsync(string.Empty, cancellationToken).ConfigureAwait(false);
                _unsentSourceText = await _sharedReadOnlyRoom.ReadAsync(cancellationToken).ConfigureAwait(false);
            }

            await _LoadIfReadyAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _text.Release();
        }
    }

    // True when the room is being refilled: a `reset` follows, and this Join's rows are not worth a text.
    private async Task<bool> _WriteAsync(IDepotNotesHub hub, DepotNoteJoinResult result, List<byte[]> known, CancellationToken cancellationToken)
    {
        if (result.Seed && await _room.IsEmptyAsync(cancellationToken).ConfigureAwait(false))
        {
            await _room.SeedAsync(result.Markdown, cancellationToken).ConfigureAwait(false);
        }
        else if (known.Count > 0 && await _room.NeedsRefillAsync(result.Markdown, cancellationToken).ConfigureAwait(false))
        {
            _logger?.LogWarning("The live note for {Connection} has rows that draw nothing; asking the hub to refill it.", _connection.Name);
            if (await hub.RefillAsync(result.DocumentId, cancellationToken).ConfigureAwait(false))
            {
                return true;
            }
        }

        var missing = await _room.MissingAsync(known, cancellationToken).ConfigureAwait(false);
        if (missing is not null)
        {
            if (_typedAway)
            {
                await hub.EditedAsync(result.DocumentId, cancellationToken).ConfigureAwait(false);
            }

            await hub.UpdateAsync(result.DocumentId, missing, cancellationToken).ConfigureAwait(false);
        }

        _typedAway = false;
        if (result.Updates.Count > result.CompactAfter)
        {
            var snapshot = await _room.SnapshotAsync(cancellationToken).ConfigureAwait(false);
            if (snapshot.Length <= MaxSnapshotBytes)
            {
                await hub.CompactAsync(result.DocumentId, snapshot, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                _logger?.LogWarning("The live note for {Connection} is too large to compact ({Bytes} bytes).", _connection.Name, snapshot.Length);
            }
        }

        return false;
    }

    // The first text, once the room has one: an empty room that waits for its seed (awaitingSeed) gets it with the seeder's update.
    private async Task _LoadIfReadyAsync(CancellationToken cancellationToken)
    {
        if (_loaded || await _room.IsEmptyAsync(cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        IReadOnlyList<TextOp> ops;
        try
        {
            ops = await _room.LoadAsync(_previous, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // A doc no note can be read from (protocol §5.3): the host keeps its text, and the next update may make the doc read again.
            _logger?.LogWarning(exception, "The live note for {Connection} does not read as a note; waiting for the next change.", _connection.Name);
            return;
        }

        _loaded = true;
        _previous = string.Empty;
        _Patch(ops);
        _ready.TrySetResult();
    }

    // Under _text. A change the room cannot read lets go of the mirror instead of editing on a stale view: the host's text stays
    // the last one it got, edits are stale until the doc reads again, and then the difference arrives as one patch.
    private async Task _ApplyAsync(byte[] update, CancellationToken cancellationToken)
    {
        try
        {
            _Patch(await _room.ApplyAsync(update, cancellationToken).ConfigureAwait(false));
        }
        catch (Exception exception) when (exception is not OperationCanceledException && _loaded)
        {
            _logger?.LogWarning(exception, "A change to the live note for {Connection} does not read; holding the text until it does.", _connection.Name);
            _previous = await _room.ReadAsync(cancellationToken).ConfigureAwait(false);
            _loaded = false;
        }
    }

    private async Task _ApplyRemoteAsync(byte[] update, CancellationToken cancellationToken)
    {
        await _text.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_sharedReadOnlyRoom is { } shared)
            {
                await shared.ApplyAsync(update, cancellationToken).ConfigureAwait(false);
                _unsentSourceText = await shared.ReadAsync(cancellationToken).ConfigureAwait(false);
            }
            await _ApplyAsync(update, cancellationToken).ConfigureAwait(false);
            await _LoadIfReadyAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _text.Release();
        }
    }

    private async Task _UnloadAsync()
    {
        await _text.WaitAsync(_life.Token).ConfigureAwait(false);
        try
        {
            if (_loaded)
            {
                _previous = await _room.ReadAsync(_life.Token).ConfigureAwait(false);
                _loaded = false;
            }
        }
        finally
        {
            _text.Release();
        }
    }

    // Under _text: a change the host did not make, as the next revision.
    private void _Patch(IReadOnlyList<TextOp> ops)
    {
        if (ops.Count == 0)
        {
            return;
        }

        _rev++;
        _patched?.Invoke(new LiveFilePatch(_rev, ops));
    }

    private async Task _RestoreSharedAsync(byte[] snapshot, CancellationToken cancellationToken)
    {
        var rejected = _room;
        _room = _roomFactory(_NewClientId());
        await _room.ApplyAsync(snapshot, cancellationToken).ConfigureAwait(false);
        await _room.LoadAsync(string.Empty, cancellationToken).ConfigureAwait(false);
        await rejected.DisposeAsync().ConfigureAwait(false);
        _typedAway = false;
    }

    // Under _text. A send that fails leaves the change in the doc, and the next Join's diff carries it.
    private async Task _SendTypedAsync(IReadOnlyList<byte[]> updates, CancellationToken cancellationToken)
    {
        if (updates.Count == 0)
        {
            return;
        }

        string? documentId;
        lock (_gate)
        {
            documentId = _documentId;
        }

        var hub = _hub;
        if (hub is null || documentId is null || _typedAway)
        {
            _typedAway = true;
            return;
        }

        // Set before the send and cleared after it: a send that fails or is cancelled halfway still goes with the next Join, Edited first.
        _typedAway = true;
        try
        {
            await hub.EditedAsync(documentId, cancellationToken).ConfigureAwait(false);
            foreach (var update in updates)
            {
                await hub.UpdateAsync(documentId, update, cancellationToken).ConfigureAwait(false);
            }

            _typedAway = false;
        }
        catch (HubException exception) when (exception.Message.Contains("notes.read_only", StringComparison.Ordinal))
        {
            _editor = false;
            _roleLost = true;
            throw new FileSourceException(FileSourceErrorKind.Forbidden, "You can no longer edit this note.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger?.LogWarning(exception, "Sending a live note edit for {Connection} failed; it goes out with the next Join.", _connection.Name);
            _closed.TrySetResult(null);
        }
    }

    private async Task _OnEventAsync(DepotNoteEvent hubEvent)
    {
        if (hubEvent.Kind == "refused")
        {
            // An unknown code still means the hub dropped us: say so instead of joining into the same refusal again.
            _Terminal(_StateFor(hubEvent.Args is [string code, ..] ? code : null) ?? DepotNoteLiveState.Unavailable);
            return;
        }

        string? documentId;
        lock (_gate)
        {
            if (_documentId is null)
            {
                if (hubEvent is { Kind: "update", Args: [_, byte[] early, ..] })
                {
                    _early.Add(early);
                }

                // The rows this Join is getting are gone: once it is in, the loop joins again into the new fill.
                if (hubEvent.Kind == "reset")
                {
                    _reset = true;
                    _closed.TrySetResult(null);
                }

                return;
            }

            documentId = _documentId;
        }

        if (hubEvent.DocumentId != documentId)
        {
            return;
        }

        switch (hubEvent)
        {
            case { Kind: "update", Args: [_, byte[] data, ..] }:
                await _GuardAsync(() => _ApplyRemoteAsync(data, _life.Token), "Applying a live note update").ConfigureAwait(false);
                break;
            case { Kind: "presence", Args: [_, string from, byte[] data, ..] }:
                if (_presence is { } presence)
                {
                    await _GuardAsync(() => presence.ReceiveAsync(from, data, _life.Token), "Reading a presence update").ConfigureAwait(false);
                }

                break;
            case { Kind: "left", Args: [_, string from, ..] }:
                if (_presence is { } leaving)
                {
                    await _GuardAsync(() => leaving.LeftAsync(from, _life.Token), "Removing someone who left").ConfigureAwait(false);
                }

                break;
            case { Kind: "reset" }:
                // Edits stop at once (stale until the new fill), so nothing typed goes to the rows that are gone. The loop closes the hub
                // and joins again; closing it here would wait for this very handler.
                await _GuardAsync(_UnloadAsync, "Letting go of a reset live note").ConfigureAwait(false);
                _reset = true;
                _closed.TrySetResult(null);
                break;
        }
    }

    // The reason is a code ("auth.session_expired") or an HTTP status; both end up as one of the states, or as another attempt.
    private void _Failed(Exception exception)
    {
        var state = exception switch
        {
            HttpRequestException { StatusCode: HttpStatusCode.Unauthorized } => DepotNoteLiveState.SignInNeeded,
            HttpRequestException { StatusCode: HttpStatusCode.Forbidden } => DepotNoteLiveState.Unavailable,
            HubException hub => _StateFor(RefusedCode().Match(hub.Message) is { Success: true } match ? match.Groups[1].Value : null) ?? DepotNoteLiveState.Unavailable,
            _ => null,
        };
        if (state is null)
        {
            _logger?.LogWarning(exception, "Connecting the live note for {Connection} failed; trying again.", _connection.Name);
            return;
        }

        _logger?.LogInformation("The live note for {Connection} is {State}.", _connection.Name, state);
        _Terminal(state);
    }

    private static string? _StateFor(string? code) => code switch
    {
        "auth.session_expired" => DepotNoteLiveState.SignInNeeded,
        "notes.unavailable" => DepotNoteLiveState.Unavailable,
        "plan.feature_disabled" => DepotNoteLiveState.Disabled,
        _ => null,
    };

    // The state ends the loop: no second attempt until Connect is called again.
    private void _Terminal(string state)
    {
        _terminal = true;
        _Status(state);
        _ready.TrySetResult();
    }

    private async Task _CloseHubAsync()
    {
        var hub = Interlocked.Exchange(ref _hub, null);
        if (hub is not null)
        {
            await hub.DisposeAsync().ConfigureAwait(false);
        }

        _closed.TrySetResult(null);
    }

    private async Task _GuardAsync(Func<Task> work, string what)
    {
        try
        {
            await work().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_life.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _logger?.LogWarning(exception, "{What} failed for {Connection}.", what, _connection.Name);
        }
    }

    private void _Status(string state)
    {
        lock (_gate)
        {
            if (_state == state)
            {
                return;
            }

            _state = state;
        }

        _publish(new DepotNoteLiveStatus(_connection.Id, _note.Organization, _note.Project, _note.Path, state, _MessageFor(state)));
    }

    // The session's state in the host's words (AC-1898). A viewer, or a Cockpit with another schema, may watch but not edit.
    private LiveFileStatus _LiveStatus()
    {
        string? state;
        lock (_gate)
        {
            state = _state;
        }

        var status = state switch
        {
            DepotNoteLiveState.Live when _schemaMismatch => new LiveFileStatus(LiveFileState.ReadOnly,
                "This note uses a newer Depot editor. You can watch it live; editing comes back once Cockpit's Depot plugin is updated.") { ReadOnlyReason = LiveReadOnlyReason.SchemaMismatch },
            DepotNoteLiveState.Live when _roleLost => new LiveFileStatus(LiveFileState.ReadOnly, "You can no longer edit this note.") { ReadOnlyReason = LiveReadOnlyReason.RoleLost },
            DepotNoteLiveState.Live when _role != "editor" => new LiveFileStatus(LiveFileState.ReadOnly) { ReadOnlyReason = LiveReadOnlyReason.Viewer },
            DepotNoteLiveState.Live => new LiveFileStatus(LiveFileState.Live),
            DepotNoteLiveState.Reconnecting => new LiveFileStatus(LiveFileState.Offline),
            DepotNoteLiveState.SignInNeeded => new LiveFileStatus(LiveFileState.SignInNeeded, _MessageFor(state)),
            DepotNoteLiveState.Stale => new LiveFileStatus(LiveFileState.Stale, _MessageFor(state)),
            DepotNoteLiveState.Unavailable or DepotNoteLiveState.Disabled => new LiveFileStatus(LiveFileState.ReadOnly, _MessageFor(state)),
            _ => new LiveFileStatus(LiveFileState.Connecting),
        };
        return status with { UnsentSourceText = _unsentSourceText };
    }

    // Fixed sentences: never text from an exception or a response, which could carry a header.
    private static string? _MessageFor(string state) => state switch
    {
        DepotNoteLiveState.SignInNeeded => "Sign in to Depot again to keep editing this note.",
        DepotNoteLiveState.Unavailable => "This note is not available in Depot.",
        DepotNoteLiveState.Disabled => "Live notes are not part of this Depot plan.",
        DepotNoteLiveState.Stale => "The note was rebuilt while you were away.",
        _ => null,
    };

    private static uint _NewClientId() => (uint)Random.Shared.NextInt64(1, uint.MaxValue);

    [GeneratedRegex(@"([a-z_]+\.[a-z_]+): refused")]
    private static partial Regex RefusedCode();
}
