using Cockpit.Plugins.Abstractions.Workflows;
using Material.Icons;

namespace Cockpit.Plugin.GitHubIssues;

/// <summary>
/// What GitHub lends the workflow editor (#77). The research that shapes all of it: <b>a GitHub issue has no
/// status.</b> There is open, closed, and a reason for the closing. No In Progress, no board column, nothing to move a
/// ticket to the way YouTrack does.
/// <para>
/// So: a trigger (you picked an issue for a session), and the three things people actually do in place of a status —
/// pick it up (assign to yourself, and label it if your repo uses a label for that), say something on it, close it.
/// The label is named in the plugin's settings, because GitHub enforces no convention and every repo calls it
/// something else, or nothing.
/// </para>
/// <para>
/// Projects v2 is the real status field and is deliberately absent: it needs an extra OAuth scope, a project that
/// exists, and three ids resolved before anything can be set. Worth building when a flow actually wants it.
/// </para>
/// </summary>
internal static class GitHubWorkflowSteps
{
    /// <summary>The trigger's type id, fired when an issue is picked for a session.</summary>
    public const string PickedTrigger = "github.picked";

    public static IEnumerable<IWorkflowStep> All(GitHubIssuesSettings settings) =>
    [
        new IssuePickedTrigger(),
        new PickUpIssueStep(settings),
        new CommentStep(),
        new CloseIssueStep(),
    ];

    private sealed class IssuePickedTrigger : IWorkflowStep
    {
        public string TypeId => PickedTrigger;

        public string Name => "Issue picked for a session";

        public string Description => "Fires when you pick a GitHub issue to track in a session — the moment work on it actually starts.";

        public string Icon => "";

        public MaterialIconKind? IconKind => MaterialIconKind.Github;

        public string Category => "GitHub";

        public bool IsTrigger => true;

        public IReadOnlyList<string> Parameters => [];

        public IReadOnlyDictionary<string, string> Produces => new Dictionary<string, string>
        {
            ["issue"] = "42",
            ["repository"] = "raymondkrahwinkel/AI-Cockpit",
            ["title"] = "Fix the login redirect",
            ["url"] = "https://github.com/raymondkrahwinkel/AI-Cockpit/issues/42",
            ["branch"] = "42-fix-the-login-redirect",
            ["directory"] = "/home/raymond/RiderProjects/AI-Cockpit",
        };
    }

    /// <summary>Picks the issue up: assigns it to you, and puts on your repo's in-progress label when you have named one.</summary>
    private sealed class PickUpIssueStep(GitHubIssuesSettings settings) : IWorkflowStep
    {
        public string TypeId => "github.start";

        // Assigns and labels a real GitHub issue with your account, so gated (#AC-38).
        public WorkflowStepConsent? RequiredConsent => WorkflowStepConsent.Dangerous;

        public string Name => "Pick up an issue";

        public string Description => "Assign a GitHub issue to yourself and, when you have named an in-progress label in the plugin's settings, put it on. Issues have no status field, so this is what starting one means.";

        public string Icon => "";

        public MaterialIconKind? IconKind => MaterialIconKind.Play;

        public string Category => "GitHub";

        public IReadOnlyList<string> Parameters => ["Issue", "Repository"];

        public IReadOnlyDictionary<string, string> Produces => new Dictionary<string, string>
        {
            ["issue"] = "42",
            ["title"] = "Fix the login redirect",
            ["url"] = "https://github.com/raymondkrahwinkel/AI-Cockpit/issues/42",
            ["branch"] = "42-fix-the-login-redirect",
        };

        public async Task<WorkflowStepResult> RunAsync(WorkflowStepContext context, CancellationToken cancellationToken)
        {
            var reference = GitHubIssueReference.Parse(context.Parameter("Issue"), context.Parameter("Repository"));
            var client = new GitHubWorkflowClient();

            var issue = await client.GetIssueAsync(reference, cancellationToken);
            await client.AssignToMeAsync(reference, cancellationToken);

            var said = $"#{issue.Number} assigned to you";

            if (settings.InProgressLabel is { Length: > 0 } label)
            {
                await client.AddLabelAsync(reference, label, cancellationToken);
                said += $", labelled '{label}'";
            }

            return new WorkflowStepResult(
                [
                    new Dictionary<string, string>
                    {
                        ["issue"] = issue.Number.ToString(),
                        ["title"] = issue.Title,
                        ["url"] = issue.Url,
                        ["branch"] = GitHubBranchName.From(issue.Number, issue.Title, settings.BranchPattern),
                    },
                ],
                said + ".");
        }
    }

    private sealed class CommentStep : IWorkflowStep
    {
        public string TypeId => "github.comment";

        // Posts a public comment to a real GitHub issue under your account — egress, so gated (#AC-38).
        public WorkflowStepConsent? RequiredConsent => WorkflowStepConsent.Dangerous;

        public string Name => "Comment on an issue";

        public string Description => "Leave a comment on a GitHub issue — what a flow did, what it found, what it is waiting for.";

        public string Icon => "";

        public MaterialIconKind? IconKind => MaterialIconKind.ChatOutline;

        public string Category => "GitHub";

        public IReadOnlyList<string> Parameters => ["Issue", "Repository", "Comment"];

        public async Task<WorkflowStepResult> RunAsync(WorkflowStepContext context, CancellationToken cancellationToken)
        {
            var comment = context.Parameter("Comment").Trim();
            if (comment.Length == 0)
            {
                throw new InvalidOperationException("This step has nothing to say. Write the comment — {output} puts what the step before produced in it.");
            }

            var reference = GitHubIssueReference.Parse(context.Parameter("Issue"), context.Parameter("Repository"));
            await new GitHubWorkflowClient().CommentAsync(reference, comment, cancellationToken);

            return WorkflowStepResult.Done($"Commented on #{reference.Number}.");
        }
    }

    private sealed class CloseIssueStep : IWorkflowStep
    {
        public string TypeId => "github.close";

        // Closes a real GitHub issue under your account, so gated (#AC-38).
        public WorkflowStepConsent? RequiredConsent => WorkflowStepConsent.Dangerous;

        public string Name => "Close an issue";

        public string Description => "Close a GitHub issue as completed or not planned. Those two are the only reasons GitHub records — there is no Done column to move it to.";

        public string Icon => "";

        public MaterialIconKind? IconKind => MaterialIconKind.Check;

        public string Category => "GitHub";

        public IReadOnlyList<string> Parameters => ["Issue", "Repository", "Reason", "Comment"];

        public IReadOnlyDictionary<string, string> Produces => new Dictionary<string, string>
        {
            ["issue"] = "42",
            ["state"] = "closed",
        };

        public async Task<WorkflowStepResult> RunAsync(WorkflowStepContext context, CancellationToken cancellationToken)
        {
            var reference = GitHubIssueReference.Parse(context.Parameter("Issue"), context.Parameter("Repository"));

            var reason = context.Parameter("Reason").Trim();
            var resolved = reason.Length == 0 || reason.Equals("completed", StringComparison.OrdinalIgnoreCase)
                ? "completed"
                : reason.Replace(' ', '-').Equals("not-planned", StringComparison.OrdinalIgnoreCase)
                    ? "not planned"
                    : throw new InvalidOperationException($"GitHub closes an issue as 'completed' or 'not planned'. It does not know '{reason}'.");

            await new GitHubWorkflowClient().CloseAsync(reference, resolved, context.Parameter("Comment").Trim(), cancellationToken);

            return new WorkflowStepResult(
                [
                    new Dictionary<string, string>
                    {
                        ["issue"] = reference.Number.ToString(),
                        ["state"] = "closed",
                    },
                ],
                $"Closed #{reference.Number} as {resolved}.");
        }
    }
}
