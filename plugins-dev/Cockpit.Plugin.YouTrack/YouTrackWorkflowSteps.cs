using Cockpit.Plugins.Abstractions.Workflows;

namespace Cockpit.Plugin.YouTrack;

/// <summary>
/// What YouTrack lends the workflow editor (#69, #75): the ticket half of the flow Raymond described — a branch is
/// cut, the ticket goes to In Progress, work happens, the ticket moves on. The cockpit's workflow plugin knows
/// nothing about tickets and should not; it knows that someone offers a step called <c>youtrack.start</c>.
/// <para>
/// A step names its instance the way a person would — by the host it points at — rather than by an index into a list
/// whose order the operator never sees. With one instance configured, the field can be left empty.
/// </para>
/// </summary>
internal static class YouTrackWorkflowSteps
{
    public static IEnumerable<IWorkflowStep> All(YouTrackSettings settings) =>
    [
        new StartIssueStep(settings),
        new MoveIssueStep(settings),
    ];

    // The instance a step works against: the only one, or the one whose URL contains what the operator wrote. A
    // guess between two instances is a ticket moved on the wrong board, which is not a mistake a flow should make
    // quietly.
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

    /// <summary>Start a ticket: move it to the state the board itself calls "in progress", and assign it to you.</summary>
    private sealed class StartIssueStep(YouTrackSettings settings) : IWorkflowStep
    {
        public string TypeId => "youtrack.start";

        public string Name => "Start a ticket";

        public string Description => "Move a ticket to the state its own board calls in progress, and assign it to you. Refuses rather than guesses when the board has no such state.";

        public string Icon => "▶";

        public string Category => "YouTrack";

        public IReadOnlyList<string> Parameters => ["Ticket", "Instance"];

        public IReadOnlyDictionary<string, string> Produces => new Dictionary<string, string>
        {
            ["ticket"] = "EVE-14",
            ["state"] = "In Progress",
            ["summary"] = "Fix the login redirect",
            ["branch"] = "eve-14-fix-the-login-redirect",
        };

        public async Task<WorkflowStepResult> RunAsync(WorkflowStepContext context, CancellationToken cancellationToken)
        {
            var ticket = context.Parameter("Ticket").Trim();
            if (ticket.Length == 0)
            {
                throw new InvalidOperationException("This step has no ticket. Open it and write one, e.g. EVE-14 — or {ticket} to take it from the step before.");
            }

            var instance = _Instance(settings, context.Parameter("Instance"));
            var client = new YouTrackClient();

            var issue = await client.GetIssueAsync(instance.InstanceUrl, instance.Token, ticket, cancellationToken);
            var fields = await client.GetIssueFieldsAsync(instance.InstanceUrl, instance.Token, issue, cancellationToken);

            if (fields.State is not { } state || YouTrackWorkflow.FindStartTarget(state) is not { } target)
            {
                // The board says which moves exist. Inventing "In Progress" on a board that has no such column is
                // how a flow reports success for a transition YouTrack refused.
                throw new InvalidOperationException($"{issue.IdReadable}'s board has no state that means \"in progress\", so this step cannot start it.");
            }

            var said = await new YouTrackWorkflow(client).StartAsync(instance, issue, fields, target, cancellationToken);

            // The branch name comes along because it is the next thing the flow will want, and this is the only step
            // that knows both the ticket's id and its summary.
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
                said);
        }
    }

    /// <summary>Move a ticket to any state its board allows — the end of the flow, or any stop along it.</summary>
    private sealed class MoveIssueStep(YouTrackSettings settings) : IWorkflowStep
    {
        public string TypeId => "youtrack.move";

        public string Name => "Move a ticket";

        public string Description => "Move a ticket to a state you name — Review, Done. Only states the board itself allows: a transition YouTrack refuses fails the step rather than passing silently.";

        public string Icon => "↦";

        public string Category => "YouTrack";

        public IReadOnlyList<string> Parameters => ["Ticket", "State", "Instance"];

        public IReadOnlyDictionary<string, string> Produces => new Dictionary<string, string>
        {
            ["ticket"] = "EVE-14",
            ["state"] = "Done",
        };

        public async Task<WorkflowStepResult> RunAsync(WorkflowStepContext context, CancellationToken cancellationToken)
        {
            var ticket = context.Parameter("Ticket").Trim();
            var target = context.Parameter("State").Trim();

            if (ticket.Length == 0 || target.Length == 0)
            {
                throw new InvalidOperationException("This step needs a ticket and a state to move it to, e.g. {ticket} and Done.");
            }

            var instance = _Instance(settings, context.Parameter("Instance"));
            var client = new YouTrackClient();

            var issue = await client.GetIssueAsync(instance.InstanceUrl, instance.Token, ticket, cancellationToken);
            var fields = await client.GetIssueFieldsAsync(instance.InstanceUrl, instance.Token, issue, cancellationToken);

            if (fields.State is not { } state)
            {
                throw new InvalidOperationException($"{issue.IdReadable} has no status field, so it cannot be moved.");
            }

            // What the board allows is read, not assumed — a workflow-governed field offers events, an ordinary one
            // values, and a name that is on neither list would be refused by YouTrack anyway. Saying so here says it
            // better.
            var allowed = state.AvailableTargets;
            var chosen = allowed.FirstOrDefault(available => string.Equals(available, target, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException(
                    allowed.Count == 0
                        ? $"{issue.IdReadable}'s board offers no moves this token may make."
                        : $"{issue.IdReadable} cannot go to '{target}'. Its board allows: {string.Join(", ", allowed)}.");

            await client.SetStateAsync(instance.InstanceUrl, instance.Token, issue, state, chosen, cancellationToken);

            return new WorkflowStepResult(
                [
                    new Dictionary<string, string>
                    {
                        ["ticket"] = issue.IdReadable,
                        ["state"] = chosen,
                    },
                ],
                $"{issue.IdReadable} → {chosen}");
        }
    }
}
