using System.Net;
using System.Reflection;
using System.Text.Json;
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

// Who this Cockpit is in Depot: the id the hub's gate compares, and the name and colour /cockpit/me gives once Depot says them.
internal sealed record DepotNoteUser(string Id, string? Name = null, string? Color = null);

// One note's connection to /hubs/notes: Join (protocol section 3), the room's Y.Doc, and a new Join when the hub closes it; a `reset`
// swaps the room. The host's text goes through the room's mirror on ILiveFileSession's terms (AC-1898). With a post it joins with
// save=true and does the saver's duties when the hub names it (section 6, AC-1901): seed, save, merge what was written outside.
internal sealed partial class DepotNoteSession : ILiveFileSession, ILivePresence
{
    // Protocol §7: an Update or a Compact is no larger.
    private const int MaxSnapshotBytes = 4 * 1024 * 1024;

    private readonly DepotConnectionRegistration _connection;
    private readonly DepotNoteLiveRequest _note;
    private readonly Func<CancellationToken, Task<DepotNoteUser>> _userId;
    private readonly DepotNotesHubFactory _hubFactory;
    private readonly DepotNoteRoomFactory _roomFactory;
    private readonly Action<DepotNoteLiveStatus> _publish;
    private readonly IReadOnlyList<TimeSpan> _backoff;
    private readonly ILogger? _logger;
    private readonly Action<LiveFilePatch>? _patched;
    private readonly Action<LiveFileStatus>? _statusChanged;
    private readonly TimeProvider _time;
    private readonly DepotNoteSaver? _saver;
    private readonly Action<DepotNoteSession>? _disposed;
    private int _disposedOnce;
    private readonly CancellationTokenSource _life = new();

    // The saver side (section 6): who the hub names saver, whether that is this connection, and the last save this session knows of.
    private string? _saverId;
    private volatile bool _mine;
    private bool _awaitingSeed;
    private volatile bool _saveRefused;
    private string? _joinedSession;
    private LiveFileSave? _saved;
    private volatile bool _savedByHub;
    private int _merging;
    private volatile bool _mergeAgain;
    private DateTimeOffset _mergeFrom;

    // editor.ts' MERGE_RETRY_MS: a merge that failed is tried again while the saves are still held.
    private static readonly TimeSpan MergeRetry = TimeSpan.FromSeconds(5);

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
    private LiveFileStatus? _announced;

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
        Func<CancellationToken, Task<DepotNoteUser>> userId,
        DepotNotesHubFactory hubFactory,
        DepotNoteRoomFactory roomFactory,
        Action<DepotNoteLiveStatus> publish,
        IReadOnlyList<TimeSpan>? backoff = null,
        ILogger? logger = null,
        Action<LiveFilePatch>? patched = null,
        DepotNotePost? post = null,
        Action<LiveFileStatus>? statusChanged = null,
        TimeProvider? time = null,
        TimeSpan? quiet = null,
        IReadOnlyList<TimeSpan>? retry = null,
        Action<DepotNoteSession>? disposed = null)
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
        _statusChanged = statusChanged;
        _time = time ?? TimeProvider.System;
        _disposed = disposed;
        _room = roomFactory(_NewClientId());
        if (post is not null)
        {
            _saver = new DepotNoteSaver(_SerializedAsync, post, _SessionGoneAsync, _SaveRefused, _MergeOutside, _Saved, _time, quiet, retry, logger);
        }
    }

    public DepotConnectionRegistration Connection => _connection;

    public DepotNoteLiveRequest Note => _note;

    public IDepotNoteRoom Room => _room;

    public event EventHandler<LiveFilePatch>? Patched;

    public event EventHandler<LiveFileStatus>? StatusChanged;

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

    public LiveFileStatus Status => _LiveStatus();

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
            _saver?.Edited();
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
            if (undo.Ops.Count > 0)
            {
                _saver?.Edited();
            }
        }
        finally
        {
            _text.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposedOnce, 1) == 1)
        {
            return;
        }

        // Off the list first: a Join that comes while this one winds down must start a session of its own.
        _disposed?.Invoke(this);
        // What is owed goes to the file before the room is let go of, as a closing web tab flushes; at most five seconds.
        if (_saver is { } saver)
        {
            try
            {
                await saver.FlushAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                _logger?.LogWarning(exception, "Saving the live note for {Connection} before closing it failed; its rows keep the text.", _connection.Name);
            }

            saver.Dispose();
        }

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

            var hub = _hubFactory(_connection, user.Id, _ =>
            {
                closed.TrySetResult(null);
                return Task.CompletedTask;
            });
            _hub = hub;

            // Handlers first, then Join (section 3 step 1): events can arrive before the Join result does.
            hub.Subscribe(_OnEventAsync);
            await hub.StartAsync(cancellationToken).ConfigureAwait(false);
            var result = await hub.JoinAsync(
                new DepotNoteJoinRequest(_note.Organization, _note.Project, _note.Path, Watch: false, _epoch, DepotNoteSchema.Version, Save: _saver is not null),
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
            _TakeRoles(hub, result);
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
    private async Task _StartPresenceAsync(IDepotNotesHub hub, DepotNoteJoinResult result, DepotNoteUser user, CancellationToken cancellationToken)
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

            await presence.SetSelfAsync(DepotPresenceSelf.For(user.Id, result.Role, user.Name, user.Color), cancellationToken).ConfigureAwait(false);
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
            _RaiseStatusIfChanged();
            _role = result.Role;
            _schemaMismatch = result.SchemaMismatch;
            _editor = result.Role == "editor" && !result.SchemaMismatch;
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
                _RaiseStatusIfChanged();
            }
            if (_sharedReadOnlyRoom is { } previous)
            {
                await previous.DisposeAsync().ConfigureAwait(false);
                _sharedReadOnlyRoom = null;
                _unsentSourceText = null;
                _RaiseStatusIfChanged();
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
                _RaiseStatusIfChanged();
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
                _RaiseStatusIfChanged();
            }
            await _ApplyAsync(update, cancellationToken).ConfigureAwait(false);
            await _LoadIfReadyAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _text.Release();
        }

        _saver?.Changed();
    }

    private async Task _UnloadAsync()
    {
        _externalWriter = null;
        _ambiguousExternalWriter = false;
        _outsidePending = false;
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
        var patch = new LiveFilePatch(_rev, ops);
        _patched?.Invoke(patch);
        Patched?.Invoke(this, patch);
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
            _RaiseStatusIfChanged();
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
            case { Kind: "outside", Args: [_, JsonElement outside, ..] }:
                await _GuardAsync(() => _OutsideAsync(outside), "Reading an outside writer").ConfigureAwait(false);
                break;
            case { Kind: "merged", Args: [_, int[] blocks, ..] }:
                await _GuardAsync(() => _MergedAsync(blocks), "Marking an outside write").ConfigureAwait(false);
                break;
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
            case { Kind: "saver", Args: [_, var saver, bool dirty, var checksum, ..] }:
                lock (_gate)
                {
                    _saverId = saver as string;
                }

                _SetMine(_saverId is not null && _saverId == _hub?.ConnectionId, dirty, checksum as string);
                break;
            case { Kind: "edited" }:
                _saver?.Edited();
                break;
            case { Kind: "external" }:
                _MergeOutside();
                break;
            case { Kind: "finished", Args: [_, _, var next, ..] }:
                _saver?.SetSession(next as string);
                break;
            case { Kind: "saved", Args: [_, JsonElement save, ..] }:
                _HubSaved(save);
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
        // Without the socket the hub may already have named another saver (editor.ts does the same): the next Join asks again.
        _mine = false;
        _saver?.SetSaver(false);
        _externalWriter = null;
        _ambiguousExternalWriter = false;
        _outsidePending = false;
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

        _StatusChanged();
        _publish(new DepotNoteLiveStatus(_connection.Id, _note.Organization, _note.Project, _note.Path, state, _MessageFor(state)));
    }

    // The host hears a status only when it differs from the last one it heard: read-only and unsent text change without the state moving.
    private void _RaiseStatusIfChanged()
    {
        var status = _LiveStatus();
        lock (_gate)
        {
            if (status == _announced)
            {
                return;
            }

            _announced = status;
        }

        StatusChanged?.Invoke(this, status);
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
            DepotNoteLiveState.Live when _saveRefused => new LiveFileStatus(LiveFileState.ReadOnly, "Depot does not let you save this note.") { ReadOnlyReason = LiveReadOnlyReason.RoleLost },
            // The hook for live-light (AC-1903): nobody here saves, or the room waits for its seed, so the file falls behind.
            DepotNoteLiveState.Live when _saver is not null && (_saverId is null || (_awaitingSeed && !_loaded))
                => new LiveFileStatus(LiveFileState.NoSaver, "No one in this note can save it to Depot."),
            DepotNoteLiveState.Live => new LiveFileStatus(LiveFileState.Live) { Saved = _saved ?? (_mine ? LiveFileSave.NotYet : null) },
            DepotNoteLiveState.Reconnecting => new LiveFileStatus(LiveFileState.Offline),
            DepotNoteLiveState.SignInNeeded => new LiveFileStatus(LiveFileState.SignInNeeded, _MessageFor(state)),
            DepotNoteLiveState.Stale => new LiveFileStatus(LiveFileState.Stale, _MessageFor(state)) { RequiresRejoin = true },
            DepotNoteLiveState.Unavailable or DepotNoteLiveState.Disabled => new LiveFileStatus(LiveFileState.ReadOnly, _MessageFor(state)),
            _ => new LiveFileStatus(LiveFileState.Connecting),
        };
            return status with { UnsentSourceText = _unsentSourceText, Saved = _saved ?? status.Saved };
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

    // Section 6 after a Join: the session and its base, and whether the hub names this connection the saver.
    private void _TakeRoles(IDepotNotesHub hub, DepotNoteJoinResult result)
    {
        if (_saver is null)
        {
            return;
        }

        lock (_gate)
        {
            _saverId = result.Saver;
            _awaitingSeed = result.AwaitingSeed;
        }

        if (_editor && result.SessionId is { } session)
        {
            _saver.SetSession(session);
            // A later Join of the same session (a reconnect, a compaction) would replace the base this client's own saves left behind.
            if (session != _joinedSession && (result.Base ?? result.Checksum) is { } @base)
            {
                _joinedSession = session;
                _saver.SetBase(@base);
            }
        }

        _SetMine(_editor && result.Saver is not null && result.Saver == hub.ConnectionId, result.Dirty, null);
    }

    private void _SetMine(bool mine, bool dirty, string? checksum)
    {
        if (_saver is null)
        {
            return;
        }

        _mine = mine && !_saveRefused;
        _saver.SetSaver(_mine);
        if (_mine && checksum is not null)
        {
            _saver.Rebase(checksum);
        }

        if (_mine && dirty)
        {
            _saver.Edited();
        }

        // A saver that comes back (a rejoin, a handover) while held merges again: the `external` it missed is not sent twice.
        if (_mine && _saver.IsHeld)
        {
            _MergeOutside();
        }

        _StatusChanged();
    }

    // Only a note that reads is saved: a doc the bridge let go of (unreadable, or refilled) says nothing, so nothing is written.
    private async Task<string> _SerializedAsync(CancellationToken cancellationToken)
    {
        await _text.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return _loaded ? await _room.SerializeAsync(cancellationToken).ConfigureAwait(false) : string.Empty;
        }
        finally
        {
            _text.Release();
        }
    }

    // 410: the session this client saved for has ended; the loop joins again into the next one.
    private Task _SessionGoneAsync()
    {
        // No save under the ended session until the new Join names the next one.
        _saver?.SetSession(null);
        _closed.TrySetResult(null);
        return Task.CompletedTask;
    }

    private void _SaveRefused()
    {
        _logger?.LogWarning("Depot refused to save the live note for {Connection}; it stays read-only here.", _connection.Name);
        _saveRefused = true;
        _SetMine(false, false, null);
    }

    private void _Saved(DepotNoteSaveState state, int? version)
    {
        // A Depot that announces saves (DEP-536) names this one too, with the server's own name and colour.
        if (state != DepotNoteSaveState.Saved || _savedByHub)
        {
            return;
        }

        // This Cockpit's own entry, whose name and colour stay empty until Depot says them (AC-1900): then "you", and no dot.
        var self = _peers.FirstOrDefault(peer => peer.You);
        _SetSaved(new LiveFileSave(self is { Name.Length: > 0 } ? self.Name : "you", self is { Color.Length: > 0 } ? self.Color : null, _time.GetUtcNow()));
    }

    // A completed version becomes the chip: a live save, or the external MCP version named in outside (AC-1583).
    private void _SetSaved(LiveFileSave saved)
    {
        _saved = saved;
        _StatusChanged();
    }

    // DEP-536: the next live save replaces the external version's chip with the saver's server identity.
    private void _HubSaved(JsonElement save)
    {
        if (save.ValueKind != JsonValueKind.Object || !save.TryGetProperty("by", out var by) || by.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        _savedByHub = true;
        var name = by.TryGetProperty("name", out var named) && named.ValueKind == JsonValueKind.String && named.GetString() is { Length: > 0 } text ? text : "someone";
        var color = by.TryGetProperty("color", out var colored) && colored.ValueKind == JsonValueKind.String ? colored.GetString() : null;
        var at = save.TryGetProperty("at", out var stamp) && stamp.ValueKind == JsonValueKind.String && stamp.TryGetDateTimeOffset(out var when) ? when : _time.GetUtcNow();
        _SetSaved(new LiveFileSave(name, color, at));
    }

    // Section 6 duty 4, as editor.ts' mergeOutside: one merge at a time, the saver held until it is in; one that comes in meanwhile follows.
    private void _MergeOutside()
    {
        if (_saver is not { } saver || !_mine)
        {
            return;
        }

        if (Interlocked.Exchange(ref _merging, 1) == 1)
        {
            _mergeAgain = true;
            return;
        }

        _mergeFrom = _time.GetUtcNow();
        _ = _GuardAsync(async () =>
        {
            try
            {
                do
                {
                    _mergeAgain = false;
                    saver.Hold();
                    await saver.IdleAsync().ConfigureAwait(false);
                    await _MergeOnceAsync(saver, _life.Token).ConfigureAwait(false);
                }
                while (_mergeAgain);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _logger?.LogWarning(exception, "Merging an outside write into the live note for {Connection} failed; trying again.", _connection.Name);
                _ = _GuardAsync(_RetryMergeAsync, "Retrying a merge into the live note");
            }
            finally
            {
                Volatile.Write(ref _merging, 0);
                // A request that came in after the loop's last look would otherwise be lost between the two threads.
                if (_mergeAgain)
                {
                    _MergeOutside();
                }
            }
        }, "Merging an outside write into the live note");
    }

    private async Task _RetryMergeAsync()
    {
        await Task.Delay(MergeRetry, _time, _life.Token).ConfigureAwait(false);
        if (_saver is { IsHeld: true })
        {
            _MergeOutside();
        }
    }

    // Until this ends, the saver stays held: a merge that fails is tried again on the next Join or `saver` event, never skipped.
    private async Task _MergeOnceAsync(DepotNoteSaver saver, CancellationToken cancellationToken)
    {
        string? documentId;
        lock (_gate)
        {
            documentId = _documentId;
        }

        var hub = _hub;
        if (hub is null || documentId is null)
        {
            return;
        }

        var @base = saver.Base;
        var theirs = await hub.TextAsync(documentId, null, cancellationToken).ConfigureAwait(false);
        if (theirs is null || @base is null)
        {
            _logger?.LogWarning("The outside write to the live note for {Connection} cannot be read; the saves stay held.", _connection.Name);
            return;
        }

        if (theirs.Checksum == @base)
        {
            // Merged already (a retry after a failed announcement): the room holds it, so the hub hears it again.
            saver.Rebase(@base);
            await hub.MergedAsync(documentId, @base, [], 0, cancellationToken).ConfigureAwait(false);
            return;
        }

        var before = await hub.TextAsync(documentId, @base, cancellationToken).ConfigureAwait(false);
        DepotNoteMerge merged;
        await _text.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_loaded)
            {
                throw new InvalidOperationException("The live note has no text to merge into yet.");
            }

            merged = await _room.MergeAsync(before?.Markdown, theirs.Markdown, _mergeFrom, cancellationToken).ConfigureAwait(false);
            _Patch(merged.Ops);
            foreach (var update in merged.Updates)
            {
                await hub.UpdateAsync(documentId, update, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _text.Release();
        }

        // What was typed and not yet saved goes out next, on top of the outside write; with nothing owed the room is the file.
        saver.Rebase(theirs.Checksum);
        await hub.MergedAsync(documentId, theirs.Checksum, merged.Blocks, merged.Conflicts, cancellationToken).ConfigureAwait(false);
    }

    private void _StatusChanged()
    {
        _statusChanged?.Invoke(_LiveStatus());
        _RaiseStatusIfChanged();
    }

    private static uint _NewClientId() => (uint)Random.Shared.NextInt64(1, uint.MaxValue);

    [GeneratedRegex(@"([a-z_]+\.[a-z_]+): refused")]
    private static partial Regex RefusedCode();
}
