using Cockpit.Core.Profiles;

namespace Cockpit.Core.Abstractions.Voice;

/// <summary>
/// Tails a live TTY session's transcript for its status (#39): TTY mode runs the real interactive TUI, so there
/// is no parsed event stream to read from (unlike SDK mode) — but a provider's CLI writes the session to disk, so
/// tailing that record gets the turn's activity cleanly without touching the ANSI/TUI stream at all. The host
/// owns neither the location nor the format: this is a generic façade keyed by <see cref="SessionProfile"/>, and
/// it dispatches to the profile's provider plugin (which resolves and reads its own transcript). The core
/// therefore knows nothing of any provider's transcript vocabulary.
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
    /// Waits for a transcript to appear for <paramref name="profile"/> that was not in
    /// <paramref name="knownTranscriptsAtLaunch"/> (the session's own new record), then classifies each appended
    /// line into a coarse <see cref="SessionActivity"/> for the TTY status dot, dispatching to the profile's
    /// provider (which owns the format-specific reading). Also carries the raw line so the host's output-signal
    /// observe surface reads from the same tail. A provider that runs background work it records apart from the
    /// main transcript reports <see cref="SessionActivity.BackgroundBusy"/> while it runs, so a long background
    /// run never reads as done. Starts tailing from the record's current end, so only lines written after this
    /// call are seen — never the session's prior history. Runs until <paramref name="cancellationToken"/> is
    /// cancelled; ends immediately when the provider records nothing.
    /// </summary>
    /// <param name="statusFile">
    /// The snapshot file this session's own statusline writes, when its provider installs one (AC-609). It is what
    /// lets the provider name the session's transcript instead of inferring it from
    /// <paramref name="knownTranscriptsAtLaunch"/> — an inference every other invocation of the same CLI on the
    /// machine can win, silently and for the whole life of the session. Null before the pty is up, and for a
    /// provider that writes no such file; the provider then falls back to the inference.
    /// </param>
    IAsyncEnumerable<SessionTranscriptActivity> ReadActivityAsync(SessionProfile? profile, IReadOnlySet<string> knownTranscriptsAtLaunch, string? statusFile, CancellationToken cancellationToken);

    /// <summary>
    /// Reads back the rows a TTY session has already written (AC-609) — the last <paramref name="count"/> of them,
    /// oldest first, with the total the transcript holds. Unlike an SDK session, whose transcript the host builds
    /// and keeps as it streams, a TTY session's only record is the one its CLI writes to disk, so this is how the
    /// read surfaces (the assistant's <c>read_transcript</c>) answer for one at all.
    /// <para>
    /// Empty when the profile's provider records nothing, when it cannot name this session's transcript — the same
    /// <paramref name="statusFile"/> the tail is keyed on — or when the session has yet to write anything. Empty is
    /// the honest answer to all three: the alternative is handing back somebody else's conversation.
    /// </para>
    /// </summary>
    SessionTranscriptSlice ReadEntries(SessionProfile? profile, string? statusFile, int count);
}

/// <summary>
/// A TTY session's transcript as read back on demand (AC-609): the last rows asked for, oldest first, and how many
/// the whole record holds — the core's mirror of the plugin-facing slice, so nothing above this line handles a
/// plugin type.
/// </summary>
/// <param name="Entries">The rows, oldest first.</param>
/// <param name="TotalEntries">How many rows the transcript holds in all.</param>
public sealed record SessionTranscriptSlice(IReadOnlyList<SessionTranscriptEntry> Entries, int TotalEntries)
{
    /// <summary>Nothing read — a session whose transcript cannot be named, or which has written nothing yet.</summary>
    public static SessionTranscriptSlice Empty { get; } = new([], 0);
}

/// <summary>
/// One already-written row of a TTY session's transcript (AC-609), in the coarse vocabulary shared by every
/// provider. <paramref name="Kind"/> is a name from the host's own transcript vocabulary
/// (<c>UserText</c>, <c>AssistantText</c>, <c>ToolUse</c>, <c>ToolResult</c>, <c>Thinking</c>, <c>Error</c>), so a
/// reader of this does not have to know which provider produced it.
/// </summary>
/// <param name="Kind">What the row is.</param>
/// <param name="Text">Its text: the message, the thinking, or a tool call's name and arguments.</param>
/// <param name="ToolResult">What a tool call returned, on the row that made it. Null on every other kind.</param>
public sealed record SessionTranscriptEntry(string Kind, string Text, string? ToolResult);
