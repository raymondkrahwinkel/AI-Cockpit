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

    public static IEnumerable<IWorkflowStep> All(YouTrackSettings settings) =>
    [
        new TicketPickedTrigger(),
        new SetStatusStep(settings),
    ];

    /// <summary>Fires when you pick a ticket for a session. The cockpit's own act — nothing is polled, nothing is guessed.</summary>
    private sealed class TicketPickedTrigger : IWorkflowStep
    {
        public string TypeId => PickedTrigger;

        public string Name => "Ticket picked for a session";

        public string Description => "Fires when you pick a YouTrack issue to track in a session — the moment work on it actually starts.";

        public string Icon => "🎫";

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

    /// <summary>Sets a ticket's status to one its board allows — In Progress, Review, Done. The one node that moves a ticket.</summary>
    private sealed class SetStatusStep(YouTrackSettings settings) : IWorkflowStep
    {
        public string TypeId => "youtrack.status";

        public string Name => "Set ticket status";

        public string Description => "Move a ticket to a status its own board allows. Write \"forward\" or \"back\" to follow the board's own order, a status by name, or leave it empty for whatever that board calls in progress. Optionally assigns it to you.";

        public string Icon => "↦";

        public string Category => "YouTrack";

        public IReadOnlyList<string> Parameters => ["Ticket", "Status", "Assign to me", "Instance"];

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
                        ["branch"] = BranchName.From(issue.IdReadable, issue.Summary),
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
