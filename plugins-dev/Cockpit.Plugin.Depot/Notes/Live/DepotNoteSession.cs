using System.Net;
using System.Text.RegularExpressions;
using Cockpit.Plugin.Depot.Contracts;
using Cockpit.Plugin.Depot.Events;
using Cockpit.Plugin.Depot.Model;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;

namespace Cockpit.Plugin.Depot.Notes.Live;

// The schema version of the notes the plugin draws, sent with every Join (DEP-530). It is the hash of AI-Depot's schema.json
// at the commit in Bundle/PIN.md; AC-1899 reads it from its bundle instead.
internal static class DepotNoteSchema
{
    public const string Version = "77f8a24281ff";
}

// One note's connection to /hubs/notes: connect, Join (protocol section 3), keep the room's Y.Doc, and join again when the hub closes
// the connection (after five minutes at most, or when the token runs out). The session owns its room; a `reset` swaps it for a new one.
internal sealed partial class DepotNoteSession : IAsyncDisposable
{
    private readonly DepotConnectionRegistration _connection;
    private readonly DepotNoteLiveRequest _note;
    private readonly Func<CancellationToken, Task<string>> _userId;
    private readonly DepotNotesHubFactory _hubFactory;
    private readonly DepotNoteRoomFactory _roomFactory;
    private readonly Action<DepotNoteLiveStatus> _publish;
    private readonly IReadOnlyList<TimeSpan> _backoff;
    private readonly ILogger? _logger;
    private readonly CancellationTokenSource _life = new();
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
        ILogger? logger = null)
    {
        _connection = connection;
        _note = note;
        _userId = userId;
        _hubFactory = hubFactory;
        _roomFactory = roomFactory;
        _publish = publish;
        _backoff = backoff ?? DepotEventReconnect.Backoff;
        _logger = logger;
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

    public async ValueTask DisposeAsync()
    {
        await _life.CancelAsync().ConfigureAwait(false);
        await _CloseHubAsync().ConfigureAwait(false);
        await _loop.ConfigureAwait(false);
        await _room.DisposeAsync().ConfigureAwait(false);
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
    }

    // True when the Join went through and the connection is live; false when the attempt failed (the loop decides what next).
    private async Task<bool> _ConnectOnceAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (_reset)
            {
                // The room was refilled: nothing of the old doc is worth keeping, and the epoch belongs to the old fill.
                _reset = false;
                var old = _room;
                _room = _roomFactory(_NewClientId());
                _epoch = null;
                await old.DisposeAsync().ConfigureAwait(false);
            }

            var user = await _userId(cancellationToken).ConfigureAwait(false);
            _closed = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_gate)
            {
                _documentId = null;
                _early.Clear();
            }

            var hub = _hubFactory(_connection, user, _OnClosedAsync);
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

    // Section 3 steps 3 to 5: the rows, then the updates that came in before the result, then what the hub still lacks.
    private async Task _EnterAsync(IDepotNotesHub hub, DepotNoteJoinResult result, CancellationToken cancellationToken)
    {
        List<byte[]> known = [.. result.Updates.OrderBy(row => row.Seq).Select(row => row.Data)];
        foreach (var row in known)
        {
            await _room.ApplyAsync(row, cancellationToken).ConfigureAwait(false);
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
            await _room.ApplyAsync(update, cancellationToken).ConfigureAwait(false);
        }

        // Early updates came from the hub, so the hub has them: they count as known, or the diff would send them back.
        known.AddRange(early);
        _epoch = result.Epoch ?? _epoch;
        if (result.Role != "editor" || result.SchemaMismatch)
        {
            return;
        }

        var missing = await _room.MissingAsync(known, cancellationToken).ConfigureAwait(false);
        if (missing is not null)
        {
            await hub.UpdateAsync(result.DocumentId, missing, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task _OnEventAsync(DepotNoteEvent hubEvent)
    {
        if (hubEvent.Kind == "refused")
        {
            if (_StateFor(hubEvent.Args is [string code, ..] ? code : null) is { } state)
            {
                _Terminal(state);
            }

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
                await _GuardAsync(() => _room.ApplyAsync(data, _life.Token), "Applying a live note update").ConfigureAwait(false);
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
            HubException hub => _StateFor(RefusedCode().Match(hub.Message) is { Success: true } match ? match.Groups[1].Value : null),
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

    private Task _OnClosedAsync(Exception? exception)
    {
        _closed.TrySetResult(exception);
        return Task.CompletedTask;
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
        if (_state == state)
        {
            return;
        }

        _state = state;
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
