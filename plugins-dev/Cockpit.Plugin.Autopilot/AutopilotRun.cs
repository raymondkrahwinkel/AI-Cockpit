using Cockpit.Plugins.Abstractions;

namespace Cockpit.Plugin.Autopilot;

/// <summary>
/// A point the "Start in Autopilot" trigger (AC-150) hands to the pipeline: which tracker it came from and enough of
/// the issue to show and, later, run it. Built from the <see cref="PluginIntent"/> data a tracker sends; the run
/// pipeline (later Autopilot sub-tickets) reads the rest of <see cref="Data"/> — branch, repository, url — as it needs it.
/// </summary>
internal sealed record AutopilotRun(string Tracker, string IssueId, string Title, string Stage, IReadOnlyDictionary<string, string> Data)
{
    // tracker, issue and title are the fields both trackers agree to send; the whole payload rides along in Data for
    // the phases that need more, and the caller's own id backs the tracker name when a sender omits it. Stage is what
    // the tracker says the item sits on, one per line — a YouTrack issue has one, a GitHub issue has its labels — and
    // is what the start gate keys on (AC-345); a tracker that does not report it sends nothing rather than a guess.
    public static AutopilotRun FromIntent(PluginIntent intent) => new(
        Tracker: intent.Data.GetValueOrDefault("tracker", intent.CallerPluginId),
        IssueId: intent.Data.GetValueOrDefault("issue", string.Empty),
        Title: intent.Data.GetValueOrDefault("title", string.Empty),
        Stage: intent.Data.GetValueOrDefault("stage", string.Empty),
        Data: intent.Data);

    /// <summary>
    /// The epic this run's sub was picked from by <see cref="AutopilotEpicRunner"/> (AC-346), or empty for every run
    /// clicked directly on its own item — which is every run before this ticket, and still the common case. Carried
    /// through <see cref="AutopilotPlanSource"/> onto the frozen plan so the settle-hook (AC-346's progress comment)
    /// knows to write onto the epic rather than (or in addition to) the sub itself, without the coordinator or the
    /// driver ever needing to know an epic exists.
    /// </summary>
    public string EpicId { get; init; } = string.Empty;
}
