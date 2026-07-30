using Cockpit.Plugins.Abstractions.Tracking;

namespace Cockpit.Plugin.Autopilot;

/// <summary>Which of the four shapes <see cref="AutopilotEpicRunner.ResolveAsync"/> can end in.</summary>
internal enum AutopilotEpicOutcomeKind
{
    /// <summary>The clicked item has no children — an ordinary issue. The caller runs its existing, unchanged path.</summary>
    NotEpic,

    /// <summary>The next executable sub was found and Ready; <see cref="AutopilotEpicOutcome.Run"/> is what to plan.</summary>
    Ready,

    /// <summary>The next sub in order is not Ready; the chain pauses — <see cref="AutopilotEpicOutcome.PausedSubId"/> and <see cref="AutopilotEpicOutcome.Reason"/> say which and why.</summary>
    Paused,

    /// <summary>Every sub is already merged into <c>origin/main</c> — the epic is done, nothing to plan.</summary>
    Complete,
}

/// <summary>The result of resolving an epic click to its next sub (AC-346), or the reason it is not one.</summary>
internal sealed record AutopilotEpicOutcome(AutopilotEpicOutcomeKind Kind, AutopilotRun? Run, string? PausedSubId, string? Reason)
{
    public static AutopilotEpicOutcome NotEpic { get; } = new(AutopilotEpicOutcomeKind.NotEpic, null, null, null);
    public static AutopilotEpicOutcome Complete { get; } = new(AutopilotEpicOutcomeKind.Complete, null, null, null);
    public static AutopilotEpicOutcome Ready(AutopilotRun run) => new(AutopilotEpicOutcomeKind.Ready, run, null, null);
    public static AutopilotEpicOutcome Paused(string subId, string reason) => new(AutopilotEpicOutcomeKind.Paused, null, subId, reason);
}

/// <summary>
/// The AC-346 orchestration layer: starting Autopilot on an epic (an issue with subs) instead of a single item. Sits
/// entirely ahead of the existing single-issue pipeline — <see cref="ResolveAsync"/> either says "not an epic" (the
/// caller's existing path is untouched) or hands back the one <see cref="AutopilotRun"/> to plan next, built exactly
/// the way <see cref="AutopilotRun.FromIntent"/> would have built it for that sub if it had been clicked directly.
/// Nothing here changes how a run itself executes — this only decides <em>which</em> run to start, once, per call.
/// <para>
/// Reads the epic's children via <c>"parent for"</c> links (YouTrack: <c>has: {parent for}</c>), the order among them
/// via <c>"depends on"</c> links (<see cref="EpicSubTopologicalOrder"/>), skips a sub already in <c>origin/main</c>
/// (<see cref="IEpicSubMergeChecker"/> — merge-ready is not the same as merged), and gates the first sub left standing
/// through the same <see cref="AutopilotReadyGate"/> a single-issue run already passes through — an epic's own
/// stage/text says nothing about which sub is executable, only the sub's own does.
/// </para>
/// <para>
/// The <c>ResolveAsync → one Run → the caller plans it → the pipeline stops at merge-ready</c> shape <em>is</em> the
/// "stop bij merge-klaar" gate the ticket's DoD calls out: this method returns after the first Ready sub it finds and
/// never looks past it, so one call can never produce more than one run. Nothing outside a fresh call to this method
/// (i.e. a fresh trigger, after the human merged) ever asks it for a second sub.
/// </para>
/// </summary>
internal static class AutopilotEpicRunner
{
    // Exactly the link-type strings YouTrack (and any future tracker) reports for these two relationships, resolved
    // from the queried issue's own side (see ITrackerProvider.GetLinkedIssuesAsync). Case-insensitive comparison below
    // — a tracker's exact casing of its own link-type name is not part of the contract.
    private const string ChildLinkType = "parent for";
    private const string DependsOnLinkType = "depends on";

    /// <summary>
    /// Resolves what a "plan" intent on <paramref name="clicked"/> should actually run. Reads <paramref name="clicked"/>'s
    /// links once; when it has no <c>"parent for"</c> children this is <see cref="AutopilotEpicOutcome.NotEpic"/> and
    /// costs the caller nothing beyond that one read. An epic reads each child's own <c>"depends on"</c> links in turn
    /// (bounded by the epic's own sub count — never large) to build the order, then walks that order asking
    /// <paramref name="mergeChecker"/> whether each sub is already delivered before gating the first one that is not.
    /// </summary>
    public static async Task<AutopilotEpicOutcome> ResolveAsync(
        ITrackerProvider provider,
        AutopilotRun clicked,
        string executableStage,
        IEpicSubMergeChecker mergeChecker,
        CancellationToken cancellationToken)
    {
        var links = await provider.GetLinkedIssuesAsync(clicked.IssueId, cancellationToken);
        var children = links
            .Where(link => link.Direction == TrackerLinkDirection.Outward && string.Equals(link.LinkType, ChildLinkType, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (children.Count == 0)
        {
            return AutopilotEpicOutcome.NotEpic;
        }

        var childIds = new HashSet<string>(children.Select(child => child.IssueId), StringComparer.OrdinalIgnoreCase);
        var byId = new Dictionary<string, TrackerLinkedIssue>(StringComparer.OrdinalIgnoreCase);
        foreach (var child in children)
        {
            // A tracker can report the same "parent for" target twice (a duplicate link, an epic linked from two
            // angles); the first reading wins rather than throwing on a duplicate key.
            byId.TryAdd(child.IssueId, child);
        }

        var dependsOn = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var childId in byId.Keys)
        {
            var childLinks = await provider.GetLinkedIssuesAsync(childId, cancellationToken);
            dependsOn[childId] = childLinks
                .Where(link => link.Direction == TrackerLinkDirection.Outward
                    && string.Equals(link.LinkType, DependsOnLinkType, StringComparison.OrdinalIgnoreCase)
                    && childIds.Contains(link.IssueId))
                .Select(link => link.IssueId)
                .ToList();
        }

        var order = EpicSubTopologicalOrder.Resolve([.. byId.Keys], dependsOn);

        foreach (var subId in order)
        {
            if (await mergeChecker.IsMergedAsync(subId, cancellationToken))
            {
                continue;
            }

            var sub = byId[subId];
            var decision = AutopilotReadyGate.Decide(sub.Title, sub.Stage, executableStage);
            if (!decision.IsAllowed)
            {
                return AutopilotEpicOutcome.Paused(subId, decision.Reason);
            }

            var stage = sub.Stage ?? string.Empty;
            var run = new AutopilotRun(
                clicked.Tracker,
                sub.IssueId,
                sub.Title,
                stage,
                new Dictionary<string, string>
                {
                    ["tracker"] = clicked.Tracker,
                    ["issue"] = sub.IssueId,
                    ["title"] = sub.Title,
                    ["stage"] = stage,
                })
            {
                EpicId = clicked.IssueId,
            };

            return AutopilotEpicOutcome.Ready(run);
        }

        return AutopilotEpicOutcome.Complete;
    }
}
