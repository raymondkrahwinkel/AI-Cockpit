using Cockpit.Core.Sessions;

namespace Cockpit.Core.Abstractions.Voice;

// One activity reading from a session's transcript: the classified `Activity` plus the
// `RawLine` it came from (null for a synthetic keep-alive), so the host drives both the status dot
// and its raw-line observe surface from one tail.
//
// `Activity`: The classified turn-activity this reading represents.
// `RawLine`: The raw transcript line, or null for a synthetic signal (e.g. a background keep-alive).
// `Usage`:
// The token usage this transcript line carried (AC-398), or null for a line that carried none — most lines,
// including every non-assistant one. Provided per line rather than only on `SessionActivity.TurnComplete`
// because a single logical turn can write several assistant lines (a tool-call round trip), each with its own
// usage; summing every reading is what makes the total correct for a turn that used a tool.
// `OutstandingShells`:
// How many backgrounded shells the session still has running (AC-276). Deliberately not folded into
// `SessionActivity.BackgroundBusy`: a shell can be a dev server that never ends, so holding the
// status on it would strand the session on "working" — worse than the premature Done it set out to fix. The host
// uses this only to withhold the "session finished" notification.
public sealed record SessionTranscriptActivity(
    SessionActivity Activity,
    string? RawLine,
    TokenUsage? Usage = null,
    int OutstandingShells = 0);
