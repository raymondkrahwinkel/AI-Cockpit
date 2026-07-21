using Cockpit.Plugins.Abstractions;

namespace Cockpit.Plugin.Autopilot;

/// <summary>
/// A point the "Start in Autopilot" trigger (AC-150) hands to the pipeline: which tracker it came from and enough of
/// the issue to show and, later, run it. Built from the <see cref="PluginIntent"/> data a tracker sends; the run
/// pipeline (later Autopilot sub-tickets) reads the rest of <see cref="Data"/> — branch, repository, url — as it needs it.
/// </summary>
internal sealed record AutopilotRun(string Tracker, string IssueId, string Title, IReadOnlyDictionary<string, string> Data)
{
    // tracker, issue and title are the fields both trackers agree to send; the whole payload rides along in Data for
    // the phases that need more, and the caller's own id backs the tracker name when a sender omits it.
    public static AutopilotRun FromIntent(PluginIntent intent) => new(
        Tracker: intent.Data.GetValueOrDefault("tracker", intent.CallerPluginId),
        IssueId: intent.Data.GetValueOrDefault("issue", string.Empty),
        Title: intent.Data.GetValueOrDefault("title", string.Empty),
        Data: intent.Data);
}
