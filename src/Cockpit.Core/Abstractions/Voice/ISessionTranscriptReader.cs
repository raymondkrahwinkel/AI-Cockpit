using Cockpit.Core.Profiles;

namespace Cockpit.Core.Abstractions.Voice;

/// <summary>
/// Tails a live TTY session's transcript for its status (#39): TTY mode runs the real TUI, so unlike SDK mode there is
/// no parsed event stream — but a provider's CLI writes the session to disk, so tailing it gets the activity cleanly.
/// A generic façade keyed by <see cref="SessionProfile"/>, dispatching to the provider plugin so core knows no vocabulary.
/// </summary>
public interface ISessionTranscriptReader
{
    /// <summary>
    /// Snapshots the transcript artifacts that already exist for <paramref name="profile"/> at launch, so the
    /// tailers can single out the one new record the session produces. Call once, before the session is spawned.
    /// Empty when the profile's provider records no tailable transcript.
    /// </summary>
    IReadOnlySet<string> SnapshotTranscripts(SessionProfile? profile);

    /// <summary>
    /// Waits for a new transcript, then classifies each line into a <see cref="SessionActivity"/> —
    /// <see cref="SessionActivity.BackgroundBusy"/> keeps a background run from reading as done.
    /// </summary>
    /// <param name="statusFile">
    /// Statusline snapshot file (AC-609) — names the transcript instead of inferring it.
    /// </param>
    IAsyncEnumerable<SessionTranscriptActivity> ReadActivityAsync(SessionProfile? profile, IReadOnlySet<string> knownTranscriptsAtLaunch, string? statusFile, CancellationToken cancellationToken);

    /// <summary>
    /// Reads back the last <paramref name="count"/> rows a TTY session has already written (AC-609), oldest first, with the
    /// total held — a TTY session's only record is what its CLI writes to disk, so this is how <c>read_transcript</c>
    /// answers for one at all. Empty when nothing is recorded, untraceable, or nothing was written yet.
    /// </summary>
    SessionTranscriptSlice ReadEntries(SessionProfile? profile, string? statusFile, int count);
}

// AC-1013: A TTY session's transcript as read back on demand (AC-609) — the last rows asked for (Entries,
// oldest first) and TotalEntries the whole record holds. Trimmed: this is the core's own mirror of the
// plugin-facing slice, so nothing above this line has to handle a plugin type.
public sealed record SessionTranscriptSlice(IReadOnlyList<SessionTranscriptEntry> Entries, int TotalEntries)
{
    // Nothing read — a session whose transcript cannot be named, or which has written nothing yet.
    public static SessionTranscriptSlice Empty { get; } = new([], 0);
}

// AC-1013: One already-written transcript row (AC-609), Kind/Text/ToolResult. Trimmed: Kind is a name
// from the host's own coarse vocabulary (UserText/AssistantText/ToolUse/ToolResult/Thinking/Error), shared
// across providers, so a reader never needs to know which provider produced the row.
public sealed record SessionTranscriptEntry(string Kind, string Text, string? ToolResult);
