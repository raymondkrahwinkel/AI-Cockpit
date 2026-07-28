namespace Cockpit.Plugin.Autopilot;

/// <summary>
/// The one number the ticket asks for (AC-347): how many runs in a row settled without a correction, out of how many of
/// the last <see cref="ConsideredRuns"/> were clean. <see cref="Describe"/> is the single text format for it — the
/// history header, the merge-ready toast and any later tracker report all read the same line, so the figure never drifts
/// between the places it is shown.
/// </summary>
internal sealed record AutopilotReliabilitySummary(int Streak, int CleanRuns, int ConsideredRuns)
{
    /// <summary>The one line this number is shown as, everywhere it is shown.</summary>
    public string Describe() => ConsideredRuns == 0
        ? "No settled runs yet"
        : $"{Streak} in a row without a correction · {CleanRuns} of the last {ConsideredRuns}";
}
