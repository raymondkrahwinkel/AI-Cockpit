namespace Cockpit.Plugin.Autopilot;

/// <summary>
/// How much of a run's conversation Autopilot mirrors into the tracker's comment channel (decision #6). The live
/// session view is always the full picture; this governs the second window onto it. Questions and blocks always
/// go to comments regardless.
/// </summary>
internal enum CommentLevel
{
    /// <summary>Only the questions Autopilot must ask and the milestones it passes — the default, so the tracker stays readable.</summary>
    QuestionsAndMilestones,

    /// <summary>Every turn mirrored, for when the tracker is where someone watches the whole run.</summary>
    Full,
}
