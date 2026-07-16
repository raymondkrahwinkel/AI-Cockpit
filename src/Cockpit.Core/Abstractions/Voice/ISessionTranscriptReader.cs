using Cockpit.Core.Profiles;

namespace Cockpit.Core.Abstractions.Voice;

/// <summary>
/// Tails a live TTY session's transcript for read-aloud (#35b) and status (#39): TTY mode runs the real
/// interactive TUI, so there is no parsed event stream to read prose from (unlike SDK mode) — but a provider's
/// CLI writes the session to disk, so tailing that record gets the assistant's text cleanly without touching the
/// ANSI/TUI stream at all. The host owns neither the location nor the format: this is a generic façade keyed by
/// <see cref="SessionProfile"/>, and it dispatches to the profile's provider plugin (which resolves and reads its
/// own transcript). The core therefore knows nothing of any provider's transcript vocabulary.
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
    /// <paramref name="knownTranscriptsAtLaunch"/> (the session's own new record), then yields each assistant
    /// turn's concatenated text as new lines are appended. Starts tailing from the record's current end, so only
    /// text written after this call is yielded — never the session's prior history. Runs until
    /// <paramref name="cancellationToken"/> is cancelled; ends immediately when the provider records nothing.
    /// </summary>
    IAsyncEnumerable<string> ReadAssistantTextAsync(SessionProfile? profile, IReadOnlySet<string> knownTranscriptsAtLaunch, CancellationToken cancellationToken);

    /// <summary>
    /// The same tail as <see cref="ReadAssistantTextAsync"/> but yields every appended raw transcript line, not
    /// only assistant text — used to drive a TTY session's coarse status from transcript activity (any new line
    /// means a turn is in flight). Starts from the record's current end and runs until cancelled.
    /// </summary>
    IAsyncEnumerable<string> ReadLinesAsync(SessionProfile? profile, IReadOnlySet<string> knownTranscriptsAtLaunch, CancellationToken cancellationToken);
}
