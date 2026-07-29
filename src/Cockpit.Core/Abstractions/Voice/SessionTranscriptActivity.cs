using Cockpit.Core.Sessions;

namespace Cockpit.Core.Abstractions.Voice;

/// <summary>
/// One activity reading from a session's transcript: the classified <see cref="Activity"/> plus the
/// <see cref="RawLine"/> it came from (null for a synthetic keep-alive), so the host drives both the status dot
/// and its raw-line observe surface from one tail.
/// </summary>
/// <param name="Activity">The classified turn-activity this reading represents.</param>
/// <param name="RawLine">The raw transcript line, or null for a synthetic signal (e.g. a background keep-alive).</param>
/// <param name="Usage">
/// The token usage this transcript line carried (AC-398), or null for a line that carried none — most lines,
/// including every non-assistant one. Provided per line rather than only on <see cref="SessionActivity.TurnComplete"/>
/// because a single logical turn can write several assistant lines (a tool-call round trip), each with its own
/// usage; summing every reading is what makes the total correct for a turn that used a tool.
/// </param>
public sealed record SessionTranscriptActivity(SessionActivity Activity, string? RawLine, TokenUsage? Usage = null);
