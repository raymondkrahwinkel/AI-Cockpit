using System.Text.Json;

namespace Cockpit.Core.Abstractions.Events;

// AC-1421: a pane event keeps the profile and project its pane had when it was written, so a reader is scoped by
// those even after the pane has closed. They stay on the backend; an SSE frame does not carry them.
public readonly record struct BackendEvent(long Seq, string Kind, string? PaneId, JsonElement Data, string? ProfileLabel = null, string? ProjectId = null);

public interface IBackendEventLog
{
    /// <summary>
    /// Appends an event and assigns the backend-wide sequence number. A pane event carries its pane's profile and project, which decide who may read it.
    /// </summary>
    long Append(string kind, string? paneId, object data, string? profileLabel = null, string? projectId = null);

    /// <summary>The sequence number of the last event appended, or zero before the first (AC-1438).</summary>
    long LastSeq { get; }

    /// <summary>Reads buffered events after the sequence, then follows live events. Minus one starts live only.</summary>
    IAsyncEnumerable<BackendEvent> ReadFromAsync(long afterSeq, CancellationToken cancellationToken);
}
