namespace Cockpit.Plugin.Autopilot;

// Whether Autopilot may start an item, and — when it may not — the sentence the operator and the issue get told.
internal sealed record AutopilotReadyDecision(bool IsAllowed, string Reason)
{
    public static AutopilotReadyDecision Allowed { get; } = new(true, string.Empty);
}

// The gate that decides whether an item is the tracker's own idea of executable before any planning starts (AC-345).
// Autopilot already asks its CEO to judge scope from the issue text, but the text is the thing that lies: the backlog
// audit of 2026-07-27 found items claiming a fix was impossible where the gate was already in the code, and items
// calling a decision open that their own subtask records as taken. A model reading that text has no way to know. A
// stage a human moved the item onto is evidence of a different kind, so Autopilot keys on that first and only then
// lets the CEO judge whether the work also fits one run.
internal static class AutopilotReadyGate
{
    // Still an idea rather than a work item. Refused even when it also sits on the executable stage: the two
    // statements contradict each other and the marker is the one someone wrote on purpose.
    private const string BrainstormMarker = "[Brainstorm]";

    private const string GatePointer =
        "Autopilot keys on the tracker's own gate instead of judging the text: a person marks an item executable once its premise still holds, it fits one item, no decision is outstanding, its dependencies are done, and it says how you know it is finished.";

    // Decides on `reportedStages` — the stages the tracker says the item is on, one per line (a
    // YouTrack issue has a single stage, a GitHub issue has its labels) — against the stage the operator configured as
    // executable for that tracker. A blank `executableStage` turns the gate off deliberately; an item
    // whose stage could not be read is refused, because "cannot tell" is not the same as "is executable".
    public static AutopilotReadyDecision Decide(string? title, string? reportedStages, string? executableStage)
    {
        if (title is not null && title.Contains(BrainstormMarker, StringComparison.OrdinalIgnoreCase))
        {
            return new AutopilotReadyDecision(
                false,
                $"This item is still marked {BrainstormMarker} — Autopilot does not start a brainstorm. Split it into an item that can be executed first. {GatePointer}");
        }

        if (string.IsNullOrWhiteSpace(executableStage))
        {
            return AutopilotReadyDecision.Allowed;
        }

        var executable = executableStage.Trim();
        var stages = _Stages(reportedStages);

        if (stages.Count == 0)
        {
            // The tracker sent no stage at all. Two causes, and the operator cannot tell them apart from the board:
            // the item genuinely carries none, or the tracker plugin is older than this one and does not report it yet
            // (they are published separately). Naming both beats sending someone to look at a stage that is fine.
            return new AutopilotReadyDecision(
                false,
                $"Autopilot could not read which stage this item is on, so it did not start it — it starts an item only from \"{executable}\". Either the item carries no stage, or the tracker plugin is older than Autopilot and does not report one yet; updating it in the plugin store is worth checking first. {GatePointer}");
        }

        if (stages.Any(stage => string.Equals(stage, executable, StringComparison.OrdinalIgnoreCase)))
        {
            return AutopilotReadyDecision.Allowed;
        }

        return new AutopilotReadyDecision(
            false,
            $"Autopilot starts an item only from \"{executable}\"; this one is on \"{string.Join("\", \"", stages)}\". {GatePointer}");
    }

    private static IReadOnlyList<string> _Stages(string? reportedStages) => reportedStages is null
        ? []
        : reportedStages.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
