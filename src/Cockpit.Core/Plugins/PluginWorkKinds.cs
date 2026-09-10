namespace Cockpit.Core.Plugins;

// AC-511 criterion 6 (Raymond, 2026-09-10): the work-kind wizard's categories and stable store-index keys.
// Untagged entries remain visible but are not recommended for any work kind.
public static class PluginWorkKinds
{
    public const string Developer = "developer";
    public const string RunningSystems = "running-systems";
    public const string PlanningAndTrackingWork = "planning-and-tracking-work";
    public const string DocumentsAndDesign = "documents-and-design";

    // What the wizard's chooser offers, in the order it offers it.
    public static IReadOnlyList<PluginWorkKindOption> All { get; } =
    [
        new(Developer, "Development", "Writing, reviewing and shipping code."),
        new(RunningSystems, "Running systems", "Running servers, containers and clusters."),
        new(PlanningAndTrackingWork, "Planning and tracking work", "Planning, tracking and coordinating work."),
        new(DocumentsAndDesign, "Documents and design", "Writing, designing and finding knowledge."),
    ];
}
