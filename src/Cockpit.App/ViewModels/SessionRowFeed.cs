using System.Text.Json;
using Cockpit.Core.Abstractions.Events;
using Cockpit.Core.Sessions;

namespace Cockpit.App.ViewModels;

// AC-1438: one pane's transcript rows as the backend event log carries them, in seq order, and the host's signals held
// until the rows they lean on are drawn: a turn's end names a row the log may not have delivered yet. The reader runs
// on the pool; drawing, resyncing and the held signals run on the thread `post` names.
internal sealed class SessionRowFeed : IDisposable
{
    private readonly IBackendEventLog _log;
    private readonly Func<string> _paneId;
    private readonly Func<IReadOnlyList<TranscriptSnapshotEntry>> _snapshot;
    private readonly Action<TranscriptSnapshotEntry> _draw;
    private readonly Action<IReadOnlyList<TranscriptSnapshotEntry>> _resync;
    private readonly Action<Action> _post;
    private readonly CancellationTokenSource _stop = new();

    // Filled by the reader, emptied by a drain. A row upserted again before the drain keeps its first place and takes
    // its newest version, so a starved thread holds one entry per row rather than one per delta (AC-1204).
    private readonly Lock _gate = new();
    private List<(long Seq, TranscriptSnapshotEntry Row)> _arrived = [];
    private readonly Dictionary<string, int> _arrivedAt = new(StringComparer.Ordinal);
    private long _received;
    private bool _reset;
    private bool _drainPosted;

    // The pane's own thread only: how far the log is drawn, and the signals waiting for it to get further.
    private long _drawnThrough;
    private readonly Queue<(long Barrier, Action Signal)> _held = new();

    public SessionRowFeed(
        IBackendEventLog log,
        Func<string> paneId,
        Func<IReadOnlyList<TranscriptSnapshotEntry>> snapshot,
        Action<TranscriptSnapshotEntry> draw,
        Action<IReadOnlyList<TranscriptSnapshotEntry>> resync,
        Action<Action> post,
        Action<Exception> readerFailed)
    {
        _log = log;
        _paneId = paneId;
        _snapshot = snapshot;
        _draw = draw;
        _resync = resync;
        _post = post;

        // Built before the host's first row: everything this pane will draw comes after this point in the log.
        _drawnThrough = _received = log.LastSeq;
        var from = _drawnThrough;
        _ = Task.Run(async () =>
        {
            try
            {
                await _ReadAsync(from, _stop.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                readerFailed(exception);
            }
        });
    }

    // Runs `signal` once every row the log held when it was raised is drawn; at once when they already are. Held
    // signals keep their order, so a later one never overtakes an earlier one still waiting.
    public void InOrder(Action signal)
    {
        var barrier = _log.LastSeq;
        if (_held.Count == 0 && barrier <= _drawnThrough)
        {
            signal();
            return;
        }

        _held.Enqueue((barrier, signal));
    }

    public void Dispose()
    {
        _stop.Cancel();
        _held.Clear();
    }

    private async Task _ReadAsync(long from, CancellationToken cancellationToken)
    {
        await foreach (var evt in _log.ReadFromAsync(from, cancellationToken).ConfigureAwait(false))
        {
            var row = evt.Kind == "row" && string.Equals(evt.PaneId, _paneId(), StringComparison.Ordinal)
                ? evt.Data.GetProperty("Row").Deserialize<TranscriptSnapshotEntry>()
                : null;
            lock (_gate)
            {
                if (evt.Kind == "reset")
                {
                    // What was waiting is part of what the reset says was missed; the snapshot carries it instead.
                    _reset = true;
                    _arrived.Clear();
                    _arrivedAt.Clear();
                }
                else if (row is not null && _arrivedAt.TryGetValue(row.Id, out var index))
                {
                    _arrived[index] = (evt.Seq, row);
                }
                else if (row is not null)
                {
                    _arrivedAt[row.Id] = _arrived.Count;
                    _arrived.Add((evt.Seq, row));
                }

                _received = evt.Seq;
                if (_drainPosted)
                {
                    continue;
                }

                _drainPosted = true;
            }

            _post(_Drain);
        }
    }

    private void _Drain()
    {
        List<(long Seq, TranscriptSnapshotEntry Row)> arrived;
        long received;
        bool reset;
        lock (_gate)
        {
            arrived = _arrived;
            _arrived = [];
            _arrivedAt.Clear();
            received = _received;
            reset = _reset;
            _reset = false;
            _drainPosted = false;
        }

        // A reset means rows were missed. The host's rows as they stand now hold every upsert the log has for this
        // pane up to `LastSeq`, read on the thread that makes them, so the stream resumes after that point.
        var resyncedThrough = 0L;
        if (reset)
        {
            resyncedThrough = _log.LastSeq;
            _resync(_snapshot());
            _drawnThrough = Math.Max(_drawnThrough, resyncedThrough);
        }

        foreach (var (seq, row) in arrived)
        {
            if (seq > resyncedThrough)
            {
                _draw(row);
            }
        }

        _drawnThrough = Math.Max(_drawnThrough, received);
        while (_held.TryPeek(out var held) && held.Barrier <= _drawnThrough)
        {
            _held.Dequeue();
            held.Signal();
        }
    }
}
