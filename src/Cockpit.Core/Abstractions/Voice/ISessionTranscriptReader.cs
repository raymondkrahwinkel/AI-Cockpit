namespace Cockpit.Core.Abstractions.Voice;

/// <summary>
/// Tails a live <c>claude</c> session's JSONL transcript for TTY read-aloud (#35b): TTY mode runs the
/// real interactive TUI, so there is no parsed event stream to read prose from (unlike SDK mode) — but
/// <c>claude</c> itself writes every session live to
/// <c>&lt;config-dir&gt;/projects/&lt;cwd-hash&gt;/&lt;session-id&gt;.jsonl</c>, so tailing that file gets
/// the assistant's text cleanly without touching the ANSI/TUI stream at all.
/// </summary>
public interface ISessionTranscriptReader
{
    /// <summary>
    /// Locates the transcript for <paramref name="sessionId"/> under <paramref name="configDir"/> (via
    /// <c>projects/*/&lt;session-id&gt;.jsonl</c> — the exact cwd-hash subfolder is not needed), waiting for
    /// it to appear if the launch has not written it yet, then yields each assistant turn's concatenated
    /// text as new lines are appended. Starts tailing from the file's current end, so only lines written
    /// after this call yields text — never the session's prior history. Runs until
    /// <paramref name="cancellationToken"/> is cancelled.
    /// </summary>
    IAsyncEnumerable<string> ReadAssistantTextAsync(string configDir, Guid sessionId, CancellationToken cancellationToken);

    /// <summary>
    /// The same tail as <see cref="ReadAssistantTextAsync"/> but yields every appended raw JSONL line, not
    /// only assistant text — used to drive a TTY session's coarse status from transcript activity (any new
    /// line means a turn is in flight). Starts from the file's current end and runs until cancelled.
    /// </summary>
    IAsyncEnumerable<string> ReadLinesAsync(string configDir, Guid sessionId, CancellationToken cancellationToken);
}
