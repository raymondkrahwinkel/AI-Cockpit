namespace Cockpit.Plugins.Abstractions.Tracking;

/// <summary>
/// A tracker-neutral point in a consumer's work lifecycle (AC-202) that a <see cref="ITrackerProvider"/> maps to its own
/// stage name through <see cref="ITrackerProvider.SuggestStageName"/>. Autopilot moves a source issue automatically as a
/// run crosses these edges — started, then merge-ready — without knowing whether the tracker is YouTrack, GitHub Issues,
/// or another, and without hardcoding any tracker's vocabulary. Deliberately small: only the edges a run reaches on its
/// own, not every column a board might have.
/// </summary>
public enum TrackerWorkStage
{
    /// <summary>The work has started and is being done — a run that just began its autonomous execution.</summary>
    InProgress,

    /// <summary>The work is finished and awaiting a human's review/merge — a run that reached merge-ready.</summary>
    InReview,

    /// <summary>The work is complete and accepted — the terminal stage a tracker closes an item at.</summary>
    Done,
}
