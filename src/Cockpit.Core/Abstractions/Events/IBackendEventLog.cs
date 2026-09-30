using System.Text.Json;

namespace Cockpit.Core.Abstractions.Events;

public readonly record struct BackendEvent(long Seq, string Kind, string? PaneId, JsonElement Data);

public interface IBackendEventLog
{
    /// <summary>Appends an event and assigns the backend-wide sequence number.</summary>
    long Append(string kind, string? paneId, object data);

    /// <summary>Reads buffered events after the sequence, then follows live events. Minus one starts live only.</summary>
    IAsyncEnumerable<BackendEvent> ReadFromAsync(long afterSeq, CancellationToken cancellationToken);
}
