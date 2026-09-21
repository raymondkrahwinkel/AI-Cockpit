namespace Cockpit.Plugin.Autopilot;

// The last merge-gate build on a collection branch (AC-1338): which tip it built and how it exited. A red record
// pauses the epic's next sub for as long as `origin/<branch>` still stands at `Sha` — a fix pushed by hand moves
// the tip and lifts the pause without anyone clearing anything.
internal sealed record AutopilotMergeBuildRecord(string Branch, string Sha, int ExitCode)
{
    public bool IsRed => ExitCode != 0;
}
