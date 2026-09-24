using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Events;
using Cockpit.Infrastructure.Sessions;

namespace Cockpit.Infrastructure.Events;

public sealed class BackendEventLog : IBackendEventLog, ISingletonService
{
    // ponytail: 10,000 events per process; a reader older than this must reload state after reset.
    private const int Capacity = 10_000;
    private const int ReaderCapacity = 256;

    private readonly Lock _gate = new();
    private readonly Queue<BackendEvent> _buffer = new();
    private readonly HashSet<Channel<BackendEvent>> _readers = [];
    private long _evictedThrough;

    public long Append(string kind, string? paneId, object data)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        var json = JsonSerializer.SerializeToElement(data);
        lock (_gate)
        {
            var evt = new BackendEvent(SessionEventSequence.Next(), kind, paneId, json);
            _buffer.Enqueue(evt);
            if (_buffer.Count > Capacity)
            {
                _evictedThrough = _buffer.Dequeue().Seq;
            }

            foreach (var reader in _readers)
            {
                if (!reader.Writer.TryWrite(evt))
                {
                    while (reader.Reader.TryRead(out _))
                    {
                    }

                    reader.Writer.TryWrite(Reset(evt.Seq));
                }
            }

            return evt.Seq;
        }
    }

    public async IAsyncEnumerable<BackendEvent> ReadFromAsync(long afterSeq, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var reader = Channel.CreateBounded<BackendEvent>(new BoundedChannelOptions(ReaderCapacity)
        {
            SingleReader = false,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
        });
        BackendEvent[] backlog;
        bool stale;
        lock (_gate)
        {
            stale = afterSeq >= 0 && afterSeq < _evictedThrough;
            backlog = afterSeq < 0 ? [] : [.. _buffer.Where(evt => evt.Seq > afterSeq)];
            _readers.Add(reader);
        }

        try
        {
            if (stale)
            {
                yield return Reset(backlog.Length > 0 ? backlog[0].Seq - 1 : _evictedThrough);
            }

            foreach (var evt in backlog)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return evt;
            }

            await foreach (var evt in reader.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                yield return evt;
            }
        }
        finally
        {
            lock (_gate)
            {
                _readers.Remove(reader);
            }

            reader.Writer.TryComplete();
        }
    }

    private static BackendEvent Reset(long seq) => new(seq, "reset", null, JsonSerializer.SerializeToElement(new { }));
}
