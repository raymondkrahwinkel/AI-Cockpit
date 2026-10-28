using System.Net;
using System.Reflection;
using System.Text.RegularExpressions;
using Cockpit.Plugin.Depot.Contracts;
using Cockpit.Plugin.Depot.Events;
using Cockpit.Plugin.Depot.Model;
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

// LB-2's host contract (AC-1898) inside the plugin: every change to the text raises the revision by one. A patch is a change the host
// did not make, and its ops apply to the text at Rev - 1; an edit is applied only on the current revision.
internal sealed record DepotNoteLivePatch(long Rev, IReadOnlyList<DepotNoteTextOp> Ops);

internal sealed record DepotNoteLiveSnapshot(string Text, long Rev);

internal sealed record DepotNoteEditResult(bool Applied, long Rev);

// One note's connection to /hubs/notes: connect, Join (protocol section 3), keep the room's Y.Doc, and join again when the hub closes
// the connection (after five minutes at most, or when the token runs out). The session owns its room; a `reset` swaps it for a new one.
// The host's text goes through the room's mirror: its edits go out as Edited plus Update, the room's changes come back as patches.
internal sealed partial class DepotNoteSession : IAsyncDisposable
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
    private readonly Action<DepotNoteLivePatch>? _patched;
    private readonly CancellationTokenSource _life = new();

    // The text side: the room's content, the revision and the host's edits change under this one, so a revision names one text.
    private readonly SemaphoreSlim _text = new(1, 1);
    private long _rev;
    private bool _loaded;
    private string _previous = string.Empty;
    private bool _editor;
    private bool _typedAway;
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
        Action<DepotNoteLivePatch>? patched = null)
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

    // The whole text and its revision, from one moment; empty at revision 0 until the room has a text.
    public async Task<DepotNoteLiveSnapshot> ReadAsync(CancellationToken cancellationToken)
    {
        await _text.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return new DepotNoteLiveSnapshot(_loaded ? await _room.ReadAsync(cancellationToken).ConfigureAwait(false) : _previous, _rev);
        }
        finally
        {
            _text.Release();
        }
    }

    // The host's ops on the text at `baseRev`; on any other revision nothing applies, and the host takes the patches it is behind on
    // first. Typed while the hub is away, the change stays in the doc and goes out with the next Join, Edited first (protocol §3 step 5).
    public async Task<DepotNoteEditResult> EditAsync(long baseRev, IReadOnlyList<DepotNoteTextOp> ops, CancellationToken cancellationToken)
    {
        await _text.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_loaded || !_editor || baseRev != _rev)
            {
                return new DepotNoteEditResult(false, _rev);
            }

            var updates = await _room.EditAsync(ops, cancellationToken).ConfigureAwait(false);
            _Patch(ops);
            await _SendTypedAsync(updates, cancellationToken).ConfigureAwait(false);
            return new DepotNoteEditResult(true, _rev);
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
        await _room.DisposeAsync().ConfigureAwait(false);
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
                _Patch(await _room.ApplyAsync(row, cancellationToken).ConfigureAwait(false));
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
                _Patch(await _room.ApplyAsync(update, cancellationToken).ConfigureAwait(false));
            }

            // Early updates came from the hub, so the hub has them: they count as known, or the diff would send them back.
            known.AddRange(early);
            _epoch = result.Epoch ?? _epoch;
            _editor = result.Role == "editor" && !result.SchemaMismatch;
            if (_editor && await _WriteAsync(hub, result, known, cancellationToken).ConfigureAwait(false))
            {
                return;
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

        var ops = await _room.LoadAsync(_previous, cancellationToken).ConfigureAwait(false);
        _loaded = true;
        _previous = string.Empty;
        _Patch(ops);
    }

    private async Task _ApplyRemoteAsync(byte[] update, CancellationToken cancellationToken)
    {
        await _text.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _Patch(await _room.ApplyAsync(update, cancellationToken).ConfigureAwait(false));
            await _LoadIfReadyAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _text.Release();
        }
    }

    // Under _text: a change the host did not make, as the next revision.
    private void _Patch(IReadOnlyList<DepotNoteTextOp> ops)
    {
        if (ops.Count == 0)
        {
            return;
        }

        _rev++;
        _patched?.Invoke(new DepotNoteLivePatch(_rev, ops));
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
        if (hub is null || documentId is null)
        {
            _typedAway = true;
            return;
        }

        try
        {
            await hub.EditedAsync(documentId, cancellationToken).ConfigureAwait(false);
            foreach (var update in updates)
            {
                await hub.UpdateAsync(documentId, update, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _typedAway = true;
            _logger?.LogWarning(exception, "Sending a live note edit for {Connection} failed; it goes out with the next Join.", _connection.Name);
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
            case { Kind: "reset" }:
                // The loop closes the hub and joins again; closing it here would wait for this very handler.
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
