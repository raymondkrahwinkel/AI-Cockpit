namespace Cockpit.Plugin.Autopilot;

/// <summary>
/// How hard the CEO leans on cost when it picks a model per step (AC-174, Raymond) — the operator's steer on the
/// cost/quality trade-off, since only they know how much they want to spend. It shapes the model-choice instruction in
/// the CEO's brief; the CEO still fits the model to the work, this only moves where the line sits.
/// </summary>
internal enum AutopilotCostStrategy
{
    /// <summary>Cheapest wins: put every step on a local, free model unless a local one has actually failed, then the cheapest paid model that can pass it.</summary>
    CostFirst,

    /// <summary>The default: default each step to a local, free model, and reserve a paid one for the steps that genuinely need frontier reasoning.</summary>
    Balanced,

    /// <summary>Quality wins: pick the most capable model each step warrants, sparing an expensive one only where a local model is plainly sufficient.</summary>
    QualityFirst,
}
