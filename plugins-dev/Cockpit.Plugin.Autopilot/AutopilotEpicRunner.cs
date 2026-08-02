using Cockpit.Plugins.Abstractions.Tracking;

namespace Cockpit.Plugin.Autopilot;

// Which of the shapes `AutopilotEpicRunner.ResolveAsync` can end in.
internal enum AutopilotEpicOutcomeKind
{
    // The clicked item has no children — an ordinary issue. The caller runs its existing, unchanged path.
    NotEpic,

    // The next executable sub was found and Ready; `AutopilotEpicOutcome.Run` is what to plan.
    Ready,

    // The chain cannot proceed right now and pauses — `AutopilotEpicOutcome.PausedSubId` (when a specific
    // sub is the reason) and `AutopilotEpicOutcome.Reason` say why. Covers every "do not silently guess"
    // case alike: the next sub is not Ready, it is itself a nested epic, its merge status could not be determined, or
    // the epic's own link structure could not be read — all of these get a comment on the epic and no run, rather than
    // either skipping ahead or (worse) re-running something that may already be done.
    Paused,

    // Every sub is already merged into `origin/main` — the epic is done, nothing to plan.
    Complete,
}

// The result of resolving an epic click to its next sub (AC-346), or the reason it is not one.
internal sealed record AutopilotEpicOutcome(AutopilotEpicOutcomeKind Kind, AutopilotRun? Run, string? PausedSubId, string? Reason)
{
    public static AutopilotEpicOutcome NotEpic { get; } = new(AutopilotEpicOutcomeKind.NotEpic, null, null, null);
    public static AutopilotEpicOutcome Complete { get; } = new(AutopilotEpicOutcomeKind.Complete, null, null, null);
    public static AutopilotEpicOutcome Ready(AutopilotRun run) => new(AutopilotEpicOutcomeKind.Ready, run, null, null);
    public static AutopilotEpicOutcome Paused(string? subId, string reason) => new(AutopilotEpicOutcomeKind.Paused, null, subId, reason);
}

// The AC-346 orchestration layer: starting Autopilot on an epic (an issue with subs) instead of a single item. Sits
// entirely ahead of the existing single-issue pipeline — `ResolveAsync` either says "not an epic" (the
// caller's existing path is untouched) or hands back the one `AutopilotRun` to plan next, built exactly
// the way `AutopilotRun.FromIntent` would have built it for that sub if it had been clicked directly.
// Nothing here changes how a run itself executes — this only decides *which* run to start, once, per call.
//
// Reads the epic's children via `"parent for"` links (YouTrack: `has: {parent for}`), the order among them
// via `"depends on"` links (`EpicSubTopologicalOrder`), skips a sub already in `origin/main`
// (`IEpicSubMergeChecker` — merge-ready is not the same as merged), and gates the first sub left standing
// through the same `AutopilotReadyGate` a single-issue run already passes through — an epic's own
// stage/text says nothing about which sub is executable, only the sub's own does.
//
// The `ResolveAsync → one Run → the caller plans it → the pipeline stops at merge-ready` shape *is* the
// "stop bij merge-klaar" gate the ticket's DoD calls out: this method returns after the first Ready sub it finds and
// never looks past it, so one call can never produce more than one run. Nothing outside a fresh call to this method
// (i.e. a fresh trigger, after the human merged) ever asks it for a second sub.
//
// Nested epics (AC-346 review finding): a sub that itself has "parent for" children would, if handed unchanged into
// the single-issue pipeline, be planned by the CEO under the existing AC-217 behaviour that pulls *all* of an
// epic's children into one plan — silently absorbing a whole subtree into one run and bypassing this class's
// one-sub-at-a-time, stop-at-merge-ready gate one level down. Rather than unroll nested epics too (out of this
// ticket's scope) or risk that silent bypass, a sub found to have its own children is treated as not executable —
// the chain pauses on it, explicitly, the same as a sub that failed the Ready gate. The safer of the two failure
// modes the independent review asked to choose between.
internal static class AutopilotEpicRunner
{
    // Exactly the link-type strings YouTrack (and any future tracker) reports for these two relationships, resolved
    // from the queried issue's own side (see ITrackerProvider.GetLinkedIssuesAsync). Case-insensitive comparison below
    // — a tracker's exact casing of its own link-type name is not part of the contract.
    private const string ChildLinkType = "parent for";
    private const string DependsOnLinkType = "depends on";

    // Resolves what a "plan" intent on `clicked` should actually run. Reads `clicked`'s
    // links once; when it has no `"parent for"` children this is `AutopilotEpicOutcome.NotEpic` and
    // costs the caller nothing beyond that one read. An epic reads each child's own links in turn (bounded by the
    // epic's own sub count — never large) to build the depends-on order and to check for a nested epic, refreshes
    // `mergeChecker` once, then walks the order asking it whether each sub is already delivered
    // before gating the first one that is not.
    public static async Task<AutopilotEpicOutcome> ResolveAsync(
        ITrackerProvider provider,
        AutopilotRun clicked,
        string executableStage,
        IEpicSubMergeChecker mergeChecker,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<TrackerLinkedIssue> links;
        try
        {
            links = await provider.GetLinkedIssuesAsync(clicked.IssueId, cancellationToken);
        }
        catch (Exception)
        {
            // A read failure here must not be mistaken for "genuinely has no children" — that would plan the epic
            // itself instead of a sub, quietly bypassing the whole epic-runner for as long as the tracker misbehaves.
            return AutopilotEpicOutcome.Paused(null, $"Autopilot could not read {clicked.IssueId}'s links, so it could not tell whether this is an epic. Try again once the tracker is reachable.");
        }

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
        var nestedEpics = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var childId in byId.Keys)
        {
            IReadOnlyList<TrackerLinkedIssue> childLinks;
            try
            {
                childLinks = await provider.GetLinkedIssuesAsync(childId, cancellationToken);
            }
            catch (Exception)
            {
                return AutopilotEpicOutcome.Paused(childId, $"Autopilot could not read {childId}'s links while resolving this epic's chain. Try again once the tracker is reachable.");
            }

            // A "depends on" link is read from the depending issue's own side (its target — YouTrack reports it
            // INWARD there; the source side sees the mirrored "is required for" name instead). Confirmed against a
            // real YouTrack instance: querying the depending sub's own links returns "depends on" under Direction ==
            // Inward, never Outward — the combination this used to filter on (Outward) does not occur for this link
            // type at all, which silently made the whole depends-on ordering a no-op.
            dependsOn[childId] = childLinks
                .Where(link => link.Direction == TrackerLinkDirection.Inward
                    && string.Equals(link.LinkType, DependsOnLinkType, StringComparison.OrdinalIgnoreCase)
                    && childIds.Contains(link.IssueId))
                .Select(link => link.IssueId)
                .ToList();

            // A sub that itself has "parent for" children is a nested epic — see the class doc for why this pauses
            // rather than being unrolled or handed unchanged into the single-issue pipeline.
            if (childLinks.Any(link => link.Direction == TrackerLinkDirection.Outward && string.Equals(link.LinkType, ChildLinkType, StringComparison.OrdinalIgnoreCase)))
            {
                nestedEpics.Add(childId);
            }
        }

        var order = EpicSubTopologicalOrder.Resolve([.. byId.Keys], dependsOn);

        // One refresh for the whole resolve pass (AC-346 review): the original shape fetched origin/main once per sub,
        // inside the loop below — up to one fetch-and-timeout per sub, serially, in the click handler.
        await mergeChecker.RefreshAsync(cancellationToken);

        foreach (var subId in order)
        {
            switch (mergeChecker.IsMerged(subId))
            {
                case true:
                    continue;
                case null:
                    // Cannot tell — never treat that as "not merged" and re-run a sub that may already be delivered.
                    return AutopilotEpicOutcome.Paused(subId, $"Autopilot could not determine whether {subId} is already merged into origin/main (no working directory it could check, or a git error). Resolve that and try again.");
            }

            if (nestedEpics.Contains(subId))
            {
                return AutopilotEpicOutcome.Paused(subId, $"{subId} itself has subtasks (a nested epic) — Autopilot's epic-runner does not unroll nested epics. Resolve {subId}'s own subtasks first, or flatten it under the epic directly.");
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
