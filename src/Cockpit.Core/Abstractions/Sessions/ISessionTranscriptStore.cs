using Cockpit.Core.Sessions;

namespace Cockpit.Core.Abstractions.Sessions;

/// <summary>
/// Cockpit's own copy of a pane's conversation (AC-1090) — the content <see cref="ISessionStateStore"/> deliberately
/// leaves out, so a resume has something to fall back on when the provider no longer has the conversation.
/// </summary>
/// <remarks>
/// One append-only log per pane, not one file for the app (AC-684's assistant-only store, which rewrote the whole
/// file per row: 702x write amplification over a day's conversation). A row that changes is appended again under
/// its own id; <see cref="LoadAsync"/> resolves last-version-wins.
/// </remarks>
public interface ISessionTranscriptStore
{
    /// <summary>
    /// Records the current state of one row. Coalesced: rows changed inside the same window are written once, in the
    /// order they were first seen. Never throws — a row that could not be recorded must not fail the turn that produced it.
    /// </summary>
    Task AppendAsync(string paneId, TranscriptSnapshotEntry entry, CancellationToken cancellationToken = default);

    /// <summary>
    /// The pane's rows as last recorded, in first-appearance order. Empty when nothing was ever recorded for this
    /// pane or the log could not be read.
    /// </summary>
    Task<IReadOnlyList<TranscriptSnapshotEntry>> LoadAsync(string paneId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Rolls this pane's log aside as a numbered "previous" generation (AC-947), for a launch that starts a new
    /// conversation rather than replaying this one. AC-1090 expected this to become unnecessary once the log
    /// appended instead of overwriting — kept anyway, and deliberately: rolling is what makes "a new conversation
    /// is a new log" literally true, and it is the only thing bounding a pane's log, retention itself being one of
    /// that ticket's unanswered decisions. A no-op when nothing was ever recorded. Never throws.
    /// </summary>
    Task ArchiveAsync(string paneId, CancellationToken cancellationToken = default);
}
