using Cockpit.Core.Assistant;

namespace Cockpit.Core.Abstractions.Assistant;

/// <summary>
/// Persists the assistant's transcript (AC-684) so a resumed conversation can be redrawn once the window
/// reopens or the app restarts — the other half of what <see cref="Sessions.ISessionStateStore"/> leaves out.
/// </summary>
/// <remarks>One file, not one per pane: there is exactly one assistant. <see cref="SaveAsync"/> overwrites the whole file.</remarks>
public interface IAssistantTranscriptStore
{
    /// <summary>The transcript as it stood when it was last saved. Empty when nothing was ever saved or the file could not be read.</summary>
    Task<IReadOnlyList<AssistantTranscriptSnapshotEntry>> LoadAsync(CancellationToken cancellationToken = default);

    /// <summary>Replaces the saved transcript with <paramref name="entries"/>. Never throws: a write that fails is logged, not surfaced to the caller that changed the transcript.</summary>
    Task SaveAsync(IReadOnlyList<AssistantTranscriptSnapshotEntry> entries, CancellationToken cancellationToken = default);
}
