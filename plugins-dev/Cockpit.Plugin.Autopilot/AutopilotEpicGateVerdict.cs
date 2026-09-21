namespace Cockpit.Plugin.Autopilot;

// What the gate concluded (AC-1341): the report as posted, and the findings in it — a red test that is ours, a test
// file gone from the tip, a suite that left no verdict, a measurement that failed. The gate repairs nothing; a
// finding is what stops the chain mid-epic and what the go at the end is asked against.
internal sealed record AutopilotEpicGateVerdict(string Report, IReadOnlyList<string> Findings)
{
    public bool HasFindings => Findings.Count > 0;
}
