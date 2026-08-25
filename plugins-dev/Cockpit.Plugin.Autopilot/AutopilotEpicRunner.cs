using Cockpit.Plugins.Abstractions.Tracking;

namespace Cockpit.Plugin.Autopilot;

// Which of the shapes `AutopilotEpicRunner.ResolveAsync` can end in.
internal enum AutopilotEpicOutcomeKind
{
    // The clicked item has no children — an ordinary issue. The caller runs its existing, unchanged path.
    NotEpic,

    // The next executable sub was found and Ready; `AutopilotEpicOutcome.Run` is what to plan.
    Ready,

    // The chain cannot proceed right now and pauses — `PausedSubId`/`Reason` say why (not Ready, a nested epic,
    // merge status undetermined, or link structure unreadable) — rather than skipping ahead or re-running
    // something that may already be done.
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

// The AC-346 orchestration layer: starting Autopilot on an epic instead of a single item. `ResolveAsync`
// returns "not an epic" or the one next `AutopilotRun` to plan, gated and stopping after the first Ready sub
// (the ticket's "stop at merge-ready"). A nested epic pauses rather than being absorbed by AC-217 CEO behaviour.
internal static class AutopilotEpicRunner
{
    // Exactly the link-type strings YouTrack (and any future tracker) reports for these two relationships, resolved
    // from the queried issue's own side (see ITrackerProvider.GetLinkedIssuesAsync). Case-insensitive comparison below
    // — a tracker's exact casing of its own link-type name is not part of the contract.
    private const string ChildLinkType = "parent for";
    private const string DependsOnLinkType = "depends on";

    // Resolves what a "plan" intent on `clicked` should actually run. No `"parent for"` children costs the caller
    // just the one link read (`NotEpic`); otherwise reads each child's links to build the depends-on order and
    // detect nested epics, then walks the order to gate the first sub not already delivered.
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

            // Read from the depending issue's own side: YouTrack reports "depends on" as Inward there (the source
            // side sees the mirrored "is required for" instead). Confirmed against a real instance — filtering on
            // Outward never matches this link type and silently made the whole ordering a no-op.
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
