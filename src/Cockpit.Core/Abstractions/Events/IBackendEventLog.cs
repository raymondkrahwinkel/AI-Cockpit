using System.Text.Json;

namespace Cockpit.Core.Abstractions.Events;

public readonly record struct BackendEvent(long Seq, string Kind, string? PaneId, JsonElement Data);

public interface IBackendEventLog
{
    /// <summary>
    /// Appends an event under <paramref name="seq"/>, or under the next backend-wide sequence number when that is null.
    /// A given sequence number must come from the same backend-wide counter, so it is never reused.
    /// </summary>
    long Append(string kind, string? paneId, object data, long? seq = null);

    /// <summary>Reads buffered events after the sequence, then follows live events. Minus one starts live only.</summary>
    IAsyncEnumerable<BackendEvent> ReadFromAsync(long afterSeq, CancellationToken cancellationToken);
}
