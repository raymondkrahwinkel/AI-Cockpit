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

// A TTY session's transcript as read back on demand (AC-609): the last rows asked for, oldest first, and how many
// the whole record holds — the core's mirror of the plugin-facing slice, so nothing above this line handles a
// plugin type.
//
// `Entries`: The rows, oldest first.
// `TotalEntries`: How many rows the transcript holds in all.
public sealed record SessionTranscriptSlice(IReadOnlyList<SessionTranscriptEntry> Entries, int TotalEntries)
{
    // Nothing read — a session whose transcript cannot be named, or which has written nothing yet.
    public static SessionTranscriptSlice Empty { get; } = new([], 0);
}

// One already-written row of a TTY session's transcript (AC-609), in the coarse vocabulary shared by every
// provider. `Kind` is a name from the host's own transcript vocabulary
// (`UserText`, `AssistantText`, `ToolUse`, `ToolResult`, `Thinking`, `Error`), so a
// reader of this does not have to know which provider produced it.
//
// `Kind`: What the row is.
// `Text`: Its text: the message, the thinking, or a tool call's name and arguments.
// `ToolResult`: What a tool call returned, on the row that made it. Null on every other kind.
public sealed record SessionTranscriptEntry(string Kind, string Text, string? ToolResult);
