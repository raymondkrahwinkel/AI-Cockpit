using Material.Icons;
using Cockpit.Plugins.Abstractions.Workflows;

namespace Cockpit.Plugin.YouTrack;

/// <summary>
/// What YouTrack lends the workflow editor (#69, #75): two steps, and no more.
/// <para>
/// A <b>trigger</b> — you picked a ticket for a session — and an <b>action</b> that sets a ticket's status. The old
/// "Start a ticket" was the second one with a state guessed for you, which is a node that exists because a flow could
/// not say "In Progress" out loud. It can. Fewer nodes that each do one thing beat more nodes that overlap.
/// </para>
/// <para>
/// The trigger is what makes the rest of the flow possible: it hands over the ticket, its summary, the branch name and
/// the directory the session works in, which is everything the next steps (cut a branch, move it to In Progress, put
/// an agent on it) need.
/// </para>
/// </summary>
internal static class YouTrackWorkflowSteps
{
    /// <summary>The trigger's type id, fired when an issue is linked to a session — see <see cref="SessionIssueLinks"/>.</summary>
    public const string PickedTrigger = "youtrack.picked";

    /// <summary>The type id of the trigger fired when a ticket's status is moved from the cockpit — see <see cref="IssueStateChanges"/>.</summary>
    public const string StatusChangedTrigger = "youtrack.status-changed";

    public static IEnumerable<IWorkflowStep> All(YouTrackSettings settings) =>
    [
        new TicketPickedTrigger(),
        new TicketStatusChangedTrigger(),
        new SetStatusStep(settings),
    ];

    /// <summary>Fires when you pick a ticket for a session. The cockpit's own act — nothing is polled, nothing is guessed.</summary>
    private sealed class TicketPickedTrigger : IWorkflowStep
    {
        public string TypeId => PickedTrigger;

        public string Name => "Ticket picked for a session";

        public string Description => "Fires when you pick a YouTrack issue to track in a session — the moment work on it actually starts.";

        public string Icon => "";

        public MaterialIconKind? IconKind => MaterialIconKind.TicketOutline;

        public string Category => "YouTrack";

        public bool IsTrigger => true;

        public IReadOnlyList<string> Parameters => [];

        public IReadOnlyDictionary<string, string> Produces => new Dictionary<string, string>
        {
            ["ticket"] = "EVE-14",
            ["summary"] = "Fix the login redirect",
            ["branch"] = "eve-14-fix-the-login-redirect",
            ["state"] = "Backlog",
            ["directory"] = "/home/raymond/RiderProjects/Eveworkbench",
        };
    }

    /// <summary>
    /// Fires when you move a ticket's status from the cockpit — the issues dialog, or a session's header. It hands over
    /// where the ticket came from as well as where it went, because a flow that runs "when a ticket reaches Review"
    /// needs to know it is Review; a flow that runs on any move at all is a flow that runs all day.
    /// </summary>
    private sealed class TicketStatusChangedTrigger : IWorkflowStep
    {
        public string TypeId => StatusChangedTrigger;

        public string Name => "Ticket status changed";

        public string Description => "Fires when you move a YouTrack ticket to another status from the cockpit. Produces the status it moved to and the one it came from, so a flow can act on the move that matters (a ticket reaching Review, say) rather than on every move. A status set by the \"Set ticket status\" step does not fire it — a flow does not trigger itself.";

        public string Icon => "";

        public MaterialIconKind? IconKind => MaterialIconKind.Refresh;

        public string Category => "YouTrack";

        public bool IsTrigger => true;

        public IReadOnlyList<string> Parameters => [];

        public IReadOnlyDictionary<string, string> Produces => new Dictionary<string, string>
        {
            ["ticket"] = "EVE-14",
            ["summary"] = "Fix the login redirect",
            ["state"] = "Review",
            ["previous_state"] = "In Progress",
            ["branch"] = "eve-14-fix-the-login-redirect",
            ["directory"] = "/home/raymond/RiderProjects/Eveworkbench",
        };
    }

    /// <summary>Sets a ticket's status to one its board allows — In Progress, Review, Done. The one node that moves a ticket.</summary>
    private sealed class SetStatusStep(YouTrackSettings settings) : IWorkflowStep
    {
        public string TypeId => "youtrack.status";

        // Moves a real ticket on a real board (and can assign it) with your token, so gated (#AC-38).
        public WorkflowStepConsent? RequiredConsent => WorkflowStepConsent.Dangerous;

        public string Name => "Set ticket status";

        public string Description => "Move a ticket to a status its own board allows. Write \"forward\" or \"back\" to follow the board's own order, a status by name, or leave it empty for whatever that board calls in progress. Optionally assigns it to you.";

        public string Icon => "";

        public MaterialIconKind? IconKind => MaterialIconKind.ArrowRightThick;

        public string Category => "YouTrack";

        public IReadOnlyList<string> Parameters => ["Ticket", "Status", "Assign to me", "Instance"];

        /// <summary>
        /// The statuses a board actually has, read from YouTrack rather than typed from memory: "In Progres" is a flow
        /// that fails at run time over a letter, and nothing on the canvas would say why. Read from the open issues of
        /// the configured instance — the states they are in are the states this board uses — plus the two words this
        /// step understands that are not statuses at all.
        /// <para>
        /// Suggestions, not a closed list: the value is as often <c>{state}</c> from the step before as it is one of
        /// these. And a failure is silence — an unconfigured instance leaves a field you can still type in.
        /// </para>
        /// </summary>
        public async Task<IReadOnlyList<string>> SuggestAsync(string parameter, CancellationToken cancellationToken = default)
        {
            if (!string.Equals(parameter, "Status", StringComparison.Ordinal))
            {
                return [];
            }

            var configured = settings.Instances.FirstOrDefault(instance =>
                instance.InstanceUrl.Length > 0 && instance.Token.Length > 0);

            if (configured is null)
            {
                return [];
            }

            var issues = await new YouTrackClient().GetOpenIssuesAsync(
                configured.InstanceUrl,
                configured.Token,
                projectTag: null,
                extraFilter: null,
                assignedToMe: false,
                top: 100,
                cancellationToken);

            var states = issues
                .Select(issue => issue.State)
                .Where(state => !string.IsNullOrWhiteSpace(state))
                .Select(state => state!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(state => state, StringComparer.OrdinalIgnoreCase);

            // "forward" and "back" follow the board's own order — they are what this step understands besides a name.
            return ["forward", "back", .. states];
        }

        public IReadOnlyDictionary<string, string> Produces => new Dictionary<string, string>
        {
            ["ticket"] = "EVE-14",
            ["state"] = "In Progress",
        };

        public async Task<WorkflowStepResult> RunAsync(WorkflowStepContext context, CancellationToken cancellationToken)
        {
            var ticket = context.Parameter("Ticket").Trim();
            if (ticket.Length == 0)
            {
                throw new InvalidOperationException("This step has no ticket. Write one (EVE-14), or {ticket} to take it from the step before.");
            }

            var instance = _Instance(settings, context.Parameter("Instance"));
            var client = new YouTrackClient();

            var issue = await client.GetIssueAsync(instance.InstanceUrl, instance.Token, ticket, cancellationToken);
            var fields = await client.GetIssueFieldsAsync(instance.InstanceUrl, instance.Token, issue, cancellationToken);

            if (fields.State is not { } state)
            {
                throw new InvalidOperationException($"{issue.IdReadable} has no status field, so it cannot be moved.");
            }

            var target = _Target(context.Parameter("Status").Trim(), issue, state);

            await client.SetStateAsync(instance.InstanceUrl, instance.Token, issue, state, target, cancellationToken);

            var said = $"{issue.IdReadable} → {target}";

            if (_Yes(context.Parameter("Assign to me")))
            {
                if (fields.AssigneeFieldName is not { } assigneeField)
                {
                    said += " (this project has no assignee field)";
                }
                else
                {
                    await client.AssignToMeAsync(instance.InstanceUrl, instance.Token, issue, assigneeField, cancellationToken);
                    said += ", assigned to you";
                }
            }

            return new WorkflowStepResult(
                [
                    new Dictionary<string, string>
                    {
                        ["ticket"] = issue.IdReadable,
                        ["state"] = target,
                        ["summary"] = issue.Summary,
                        ["branch"] = BranchName.From(issue.IdReadable, issue.Summary, settings.BranchPattern),
                    },
                ],
                said + ".");
        }

        // Four ways to say where it goes. Empty means "whatever this board calls in progress" — the thing every flow
        // starts with. "forward" and "back" follow the board's own column order, so a flow written once works on a
        // board whose columns are called something else. A name must be one the board actually allows: YouTrack would
        // refuse anything else anyway, and saying so here says it better.
        private static string _Target(string wanted, YouTrackIssue issue, YouTrackStateField state)
        {
            if (wanted.Length == 0)
            {
                return YouTrackWorkflow.FindStartTarget(state)
                    ?? throw new InvalidOperationException($"{issue.IdReadable}'s board has no status that means \"in progress\", so this step cannot guess one. Name the status you want.");
            }

            if (wanted.Equals("forward", StringComparison.OrdinalIgnoreCase))
            {
                return StateFlow.Forward(state)
                    ?? throw new InvalidOperationException($"{issue.IdReadable} is at the end of its board ({state.CurrentValue}), so there is nowhere forward to go.");
            }

            if (wanted.Equals("back", StringComparison.OrdinalIgnoreCase))
            {
                return StateFlow.Back(state)
                    ?? throw new InvalidOperationException($"{issue.IdReadable} is at the start of its board ({state.CurrentValue}), so there is nowhere back to go.");
            }

            var allowed = state.AvailableTargets;

            return allowed.FirstOrDefault(available => string.Equals(available, wanted, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException(
                    allowed.Count == 0
                        ? $"{issue.IdReadable}'s board offers no moves this token may make."
                        : $"{issue.IdReadable} cannot go to '{wanted}'. Its board allows: {string.Join(", ", allowed)}.");
        }

        private static bool _Yes(string value) =>
            value.Trim() is "yes" or "true" or "1" or "y";
    }

    // The instance a step works against: the only one, or the one whose URL contains what the operator wrote. A guess
    // between two instances is a ticket moved on the wrong board, which is not a mistake a flow should make quietly.
    private static YouTrackInstance _Instance(YouTrackSettings settings, string named)
    {
        var configured = settings.Instances
            .Where(instance => instance.InstanceUrl.Length > 0 && instance.Token.Length > 0)
            .ToList();

        if (configured.Count == 0)
        {
            throw new InvalidOperationException("No YouTrack instance is configured. Open the plugin's settings first.");
        }

        if (string.IsNullOrWhiteSpace(named))
        {
            return configured.Count == 1
                ? configured[0]
                : throw new InvalidOperationException($"There are {configured.Count} YouTrack instances configured, so this step must say which one: put part of its URL in the Instance field.");
        }

        var matches = configured
            .Where(instance => instance.InstanceUrl.Contains(named.Trim(), StringComparison.OrdinalIgnoreCase))
            .ToList();

        return matches.Count switch
        {
            1 => matches[0],
            0 => throw new InvalidOperationException($"No configured YouTrack instance matches '{named}'."),
            _ => throw new InvalidOperationException($"'{named}' matches {matches.Count} configured instances. Be more specific."),
        };
    }
}
