namespace Cockpit.Core.Abstractions.Voice;

/// <summary>
/// One activity reading from a session's transcript: the classified <see cref="Activity"/> plus the
/// <see cref="RawLine"/> it came from (null for a synthetic keep-alive), so the host drives both the status dot
/// and its raw-line observe surface from one tail.
/// </summary>
/// <param name="Activity">The classified turn-activity this reading represents.</param>
/// <param name="RawLine">The raw transcript line, or null for a synthetic signal (e.g. a background keep-alive).</param>
public sealed record SessionTranscriptActivity(SessionActivity Activity, string? RawLine);
