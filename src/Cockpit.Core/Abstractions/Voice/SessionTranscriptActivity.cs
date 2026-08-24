using Cockpit.Core.Sessions;

namespace Cockpit.Core.Abstractions.Voice;

// AC-1013: One activity reading from a session's transcript (Activity, RawLine, Usage, OutstandingShells),
// so the host drives its status dot and raw-line observe surface from one tail. Trimmed: Usage is per-line
// so summing across a tool-call round trip's several assistant lines totals correctly; OutstandingShells (AC-276) stays out of BackgroundBusy because a never-ending dev-server shell would strand the status on "working".
public sealed record SessionTranscriptActivity(
    SessionActivity Activity,
    string? RawLine,
    TokenUsage? Usage = null,
    int OutstandingShells = 0);
