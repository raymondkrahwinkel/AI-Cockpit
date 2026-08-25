using Cockpit.Plugins.Abstractions;

namespace Cockpit.Plugin.Autopilot;

// A point the "Start in Autopilot" trigger (AC-150) hands to the pipeline: which tracker it came from and enough of
// the issue to show and, later, run it. Built from the `PluginIntent` data a tracker sends.
internal sealed record AutopilotRun(string Tracker, string IssueId, string Title, string Stage, IReadOnlyDictionary<string, string> Data)
{
    // Tracker/issue/title are the fields both trackers agree to send; the whole payload rides along in Data for
    // later phases, and the caller's own id backs the tracker name when a sender omits it. Stage is what the start
    // gate keys on (AC-345); a tracker that does not report it sends nothing rather than a guess.
    public static AutopilotRun FromIntent(PluginIntent intent) => new(
        Tracker: intent.Data.GetValueOrDefault("tracker", intent.CallerPluginId),
        IssueId: intent.Data.GetValueOrDefault("issue", string.Empty),
        Title: intent.Data.GetValueOrDefault("title", string.Empty),
        Stage: intent.Data.GetValueOrDefault("stage", string.Empty),
        Data: intent.Data);

    // The epic this run's sub was picked from by `AutopilotEpicRunner` (AC-346), or empty when clicked directly on
    // its own item. Carried onto the frozen plan so the settle-hook (AC-346's progress comment) can write onto the
    // epic without the coordinator or driver ever needing to know an epic exists.
    public string EpicId { get; init; } = string.Empty;
}
