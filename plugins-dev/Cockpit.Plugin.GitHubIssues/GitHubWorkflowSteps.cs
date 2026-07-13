using Cockpit.Plugins.Abstractions.Workflows;

namespace Cockpit.Plugin.GitHubIssues;

/// <summary>
/// What GitHub lends the workflow editor (#77). The research that shapes all of this: <b>a GitHub issue has no
/// status.</b> There is <c>open</c> and <c>closed</c> and a reason for the closing, and that is the whole of it —
/// no In Progress, no board column, nothing to move a ticket to the way YouTrack does.
/// <para>
/// So these steps do not pretend otherwise. "Start" is what people actually do in place of a status: assign the issue
/// to yourself, and optionally put the label your repo uses to mean "someone is on this". The label is named by you,
/// because there is no convention GitHub enforces and every repo calls it something else — <c>in progress</c>,
/// <c>status: in progress</c>, or nothing at all. A label that does not exist on the repo fails the step and says so,
/// rather than being created behind your back: labels are shared vocabulary, and inventing one from an automation is
/// how a repo ends up with three of them.
/// </para>
/// <para>
/// Projects v2 is the real status field, and it is deliberately not here yet: it needs an extra OAuth scope, a project
/// that exists, and three ids resolved before anything can be set. Worth doing when a flow actually wants it —
/// not worth guessing at now.
/// </para>
/// </summary>
internal static class GitHubWorkflowSteps
{
    public static IEnumerable<IWorkflowStep> All() =>
    [
        new StartIssueStep(),
        new CommentStep(),
        new CloseIssueStep(),
    ];

    /// <summary>Picks the issue up: assigns it to you, and puts on the label your repo uses for work in flight.</summary>
    private sealed class StartIssueStep : IWorkflowStep
    {
        public string TypeId => "github.start";

        public string Name => "Start an issue";

        public string Description => "Assign a GitHub issue to yourself and, if your repo uses one, put its in-progress label on it. GitHub issues have no status field, so this is what starting one actually means.";

        public string Icon => "▶";

        public string Category => "GitHub";

        public IReadOnlyList<string> Parameters => ["Issue", "Repository", "Label"];

        public IReadOnlyDictionary<string, string> Produces => new Dictionary<string, string>
        {
            ["issue"] = "42",
            ["title"] = "Fix the login redirect",
            ["url"] = "https://github.com/raymondkrahwinkel/AI-Cockpit/issues/42",
            ["branch"] = "42-fix-the-login-redirect",
        };

        public async Task<WorkflowStepResult> RunAsync(WorkflowStepContext context, CancellationToken cancellationToken)
        {
            var issue = GitHubIssueReference.Parse(context.Parameter("Issue"), context.Parameter("Repository"));
            var client = new GitHubWorkflowClient();

            var found = await client.GetIssueAsync(issue, cancellationToken);

            await client.AssignToMeAsync(issue, cancellationToken);

            var label = context.Parameter("Label").Trim();
            var said = $"#{found.Number} assigned to you";

            if (label.Length > 0)
            {
                await client.AddLabelAsync(issue, label, cancellationToken);
                said += $", labelled '{label}'";
            }

            return new WorkflowStepResult(
                [
                    new Dictionary<string, string>
                    {
                        ["issue"] = found.Number.ToString(),
                        ["title"] = found.Title,
                        ["url"] = found.Url,
                        ["branch"] = GitHubBranchName.From(found.Number, found.Title),
                    },
                ],
                said + ".");
        }
    }

    /// <summary>Says something on the issue — the closest thing GitHub has to a status change that a human will read.</summary>
    private sealed class CommentStep : IWorkflowStep
    {
        public string TypeId => "github.comment";

        public string Name => "Comment on an issue";

        public string Description => "Leave a comment on a GitHub issue — what a flow did, what it found, what it is waiting for.";

        public string Icon => "💬";

        public string Category => "GitHub";

        public IReadOnlyList<string> Parameters => ["Issue", "Repository", "Comment"];

        public async Task<WorkflowStepResult> RunAsync(WorkflowStepContext context, CancellationToken cancellationToken)
        {
            var comment = context.Parameter("Comment").Trim();
            if (comment.Length == 0)
            {
                throw new InvalidOperationException("This step has nothing to say. Open it and write the comment — {output} puts what the step before produced in it.");
            }

            var issue = GitHubIssueReference.Parse(context.Parameter("Issue"), context.Parameter("Repository"));
            await new GitHubWorkflowClient().CommentAsync(issue, comment, cancellationToken);

            return WorkflowStepResult.Done($"Commented on #{issue.Number}.");
        }
    }

    /// <summary>Closes it — and says why, because <c>state_reason</c> is the only thing GitHub keeps besides open/closed.</summary>
    private sealed class CloseIssueStep : IWorkflowStep
    {
        public string TypeId => "github.close";

        public string Name => "Close an issue";

        public string Description => "Close a GitHub issue as completed or not planned. Those two are the only reasons GitHub records — there is no Done column to move it to.";

        public string Icon => "✔";

        public string Category => "GitHub";

        public IReadOnlyList<string> Parameters => ["Issue", "Repository", "Reason", "Comment"];

        public IReadOnlyDictionary<string, string> Produces => new Dictionary<string, string>
        {
            ["issue"] = "42",
            ["state"] = "closed",
        };

        public async Task<WorkflowStepResult> RunAsync(WorkflowStepContext context, CancellationToken cancellationToken)
        {
            var issue = GitHubIssueReference.Parse(context.Parameter("Issue"), context.Parameter("Repository"));

            // "completed" unless you say otherwise, which is what closing an issue means nine times out of ten.
            var reason = context.Parameter("Reason").Trim();
            var resolved = reason.Length == 0 || reason.Equals("completed", StringComparison.OrdinalIgnoreCase)
                ? "completed"
                : reason.Replace(' ', '-').Equals("not-planned", StringComparison.OrdinalIgnoreCase)
                    ? "not planned"
                    : throw new InvalidOperationException($"GitHub closes an issue as 'completed' or 'not planned'. It does not know '{reason}'.");

            await new GitHubWorkflowClient().CloseAsync(issue, resolved, context.Parameter("Comment").Trim(), cancellationToken);

            return new WorkflowStepResult(
                [
                    new Dictionary<string, string>
                    {
                        ["issue"] = issue.Number.ToString(),
                        ["state"] = "closed",
                    },
                ],
                $"Closed #{issue.Number} as {resolved}.");
        }
    }
}
