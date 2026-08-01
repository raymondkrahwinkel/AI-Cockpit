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
    IAsyncEnumerable<SessionTranscriptActivity> ReadActivityAsync(SessionProfile? profile, IReadOnlySet<string> knownTranscriptsAtLaunch, CancellationToken cancellationToken);
}
