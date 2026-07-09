namespace Cockpit.Core.Abstractions.Voice;

/// <summary>
/// Tails a live <c>claude</c> session's JSONL transcript for TTY read-aloud (#35b) and status (#39): TTY
/// mode runs the real interactive TUI, so there is no parsed event stream to read prose from (unlike SDK
/// mode) — but <c>claude</c> itself writes every session live to
/// <c>&lt;config-dir&gt;/projects/&lt;cwd-hash&gt;/&lt;session-id&gt;.jsonl</c>, so tailing that file gets the
/// assistant's text cleanly without touching the ANSI/TUI stream at all. The session id is <em>not</em>
/// forced on the launch (that is undocumented for interactive sessions and does not persist a transcript),
/// so the file is identified as the new transcript that appears after launch — see
/// <see cref="SnapshotTranscripts"/>.
/// </summary>
public interface ISessionTranscriptReader
{
    /// <summary>
    /// Snapshots the transcript files that already exist under <paramref name="configDir"/> at launch, so
    /// the tailers can single out the one new file <c>claude</c> writes for this session. Call once, before
    /// the session is spawned.
    /// </summary>
    IReadOnlySet<string> SnapshotTranscripts(string configDir);

    /// <summary>
    /// Waits for a transcript to appear under <paramref name="configDir"/> that was not in
    /// <paramref name="knownTranscriptsAtLaunch"/> (the session's own new <c>.jsonl</c>), then yields each
    /// assistant turn's concatenated text as new lines are appended. Starts tailing from the file's current
    /// end, so only lines written after this call yields text — never the session's prior history. Runs
    /// until <paramref name="cancellationToken"/> is cancelled.
    /// </summary>
    IAsyncEnumerable<string> ReadAssistantTextAsync(string configDir, IReadOnlySet<string> knownTranscriptsAtLaunch, CancellationToken cancellationToken);

    /// <summary>
    /// The same tail as <see cref="ReadAssistantTextAsync"/> but yields every appended raw JSONL line, not
    /// only assistant text — used to drive a TTY session's coarse status from transcript activity (any new
    /// line means a turn is in flight). Starts from the file's current end and runs until cancelled.
    /// </summary>
    IAsyncEnumerable<string> ReadLinesAsync(string configDir, IReadOnlySet<string> knownTranscriptsAtLaunch, CancellationToken cancellationToken);
}
