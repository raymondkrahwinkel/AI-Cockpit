namespace Cockpit.Plugin.Autopilot;

// The one number the ticket asks for (AC-347): how many runs in a row settled without a correction, out of the
// last `ConsideredRuns`. `Describe` is the single text format — history header, merge-ready toast, and any
// tracker report all read the same line, so the figure never drifts between places it is shown.
internal sealed record AutopilotReliabilitySummary(int Streak, int CleanRuns, int ConsideredRuns)
{
    // The one line this number is shown as, everywhere it is shown.
    public string Describe() => ConsideredRuns == 0
        ? "No settled runs yet"
        : $"{Streak} in a row without a correction · {CleanRuns} of the last {ConsideredRuns}";
}
