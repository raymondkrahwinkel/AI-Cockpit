using Material.Icons;
using Cockpit.Plugins.Abstractions.Workflows;

namespace Cockpit.Plugin.GitStatus;

/// <summary>
/// What git lends the workflow editor (#69): the four things a flow does around a piece of work — cut a branch,
/// commit, push, and go back to where you came from.
/// <para>
/// Every one of them names a working directory, and the flow's trigger hands it over ({directory}), so a step never
/// has to guess which repository it is in. Guessing there would be a commit in the wrong repo, which is a mistake
/// nobody notices until it is pushed.
/// </para>
/// <para>
/// What is <b>not</b> here, on purpose: force-pushing, resetting, deleting branches, rewriting history. A workflow
/// that can throw away work you did not know it had is not a convenience.
/// </para>
/// </summary>
internal static class GitWorkflowSteps
{
    public static IEnumerable<IWorkflowStep> All() =>
    [
        new SwitchBranchStep(),
        new CommitStep(),
        new PushStep(),
    ];

    /// <summary>Switches to a branch, creating it when it is not there yet — which is what "start working on this ticket" means in git.</summary>
    private sealed class SwitchBranchStep : IWorkflowStep
    {
        public string TypeId => "git.branch";

        // Switches/creates branches with your git rights — a mutation of a real repo, so gated (#AC-38).
        public WorkflowStepConsent? RequiredConsent => WorkflowStepConsent.Dangerous;

        public string Name => "Switch to a branch";

        public string Description => "Switch to a branch, creating it from the current one when it does not exist yet. Refuses when the working tree is dirty rather than dragging your changes onto another branch.";

        public string Icon => "";

        public MaterialIconKind? IconKind => MaterialIconKind.SourceBranch;

        public string Category => "Git";

        public IReadOnlyList<string> Parameters => ["Branch", "Working directory", "Create from"];

        public IReadOnlyDictionary<string, string> Produces => new Dictionary<string, string>
        {
            ["branch"] = "eve-14-fix-the-login-redirect",
            ["created"] = "true",
            ["directory"] = "/home/raymond/RiderProjects/Eveworkbench",
        };

        public async Task<WorkflowStepResult> RunAsync(WorkflowStepContext context, CancellationToken cancellationToken)
        {
            var branch = context.Parameter("Branch").Trim();
            if (branch.Length == 0)
            {
                throw new InvalidOperationException("This step has no branch. Write one, or {branch} to take the name from the step before.");
            }

            var directory = context.Parameter("Working directory").Trim();

            // A dirty tree is not a reason to stop what you were doing to it — but it *is* a reason not to be moved
            // to another branch by a machine. git would carry the changes across; this refuses instead.
            if (await GitCommand.HasChangesAsync(directory, cancellationToken))
            {
                throw new InvalidOperationException(
                    $"There are uncommitted changes in {directory}, so this step will not switch branches. Commit or stash them first.");
            }

            var known = await _ExistsAsync(directory, branch, cancellationToken);

            if (known)
            {
                await GitCommand.RunAsync(directory, ["switch", branch], cancellationToken);
            }
            else
            {
                var from = context.Parameter("Create from").Trim();

                await GitCommand.RunAsync(
                    directory,
                    from.Length == 0 ? ["switch", "-c", branch] : ["switch", "-c", branch, from],
                    cancellationToken);
            }

            return new WorkflowStepResult(
                [
                    new Dictionary<string, string>
                    {
                        ["branch"] = branch,
                        ["created"] = known ? "false" : "true",
                        ["directory"] = directory,
                    },
                ],
                known ? $"Switched to {branch}." : $"Created {branch} and switched to it.");
        }

        private static async Task<bool> _ExistsAsync(string directory, string branch, CancellationToken cancellationToken)
        {
            var branches = await GitCommand.RunAsync(directory, ["branch", "--list", branch], cancellationToken);

            return branches.Length > 0;
        }
    }

    /// <summary>Commits what is there. Stages everything by default, because a flow that committed a subset nobody chose would be the surprising thing.</summary>
    private sealed class CommitStep : IWorkflowStep
    {
        public string TypeId => "git.commit";

        // Writes a commit into a real repo with your git rights, so gated (#AC-38).
        public WorkflowStepConsent? RequiredConsent => WorkflowStepConsent.Dangerous;

        public string Name => "Commit";

        public string Description => "Commit the changes in a repository. Stages everything unless you say otherwise. Says so and does nothing when there is nothing to commit — an empty commit is noise in a history someone has to read.";

        public string Icon => "";

        public MaterialIconKind? IconKind => MaterialIconKind.Pencil;

        public string Category => "Git";

        public IReadOnlyList<string> Parameters => ["Message", "Working directory", "Stage everything"];

        public IReadOnlyDictionary<string, string> Produces => new Dictionary<string, string>
        {
            ["commit"] = "a1b2c3d",
            ["branch"] = "eve-14-fix-the-login-redirect",
            ["directory"] = "/home/raymond/RiderProjects/Eveworkbench",
        };

        public async Task<WorkflowStepResult> RunAsync(WorkflowStepContext context, CancellationToken cancellationToken)
        {
            var message = context.Parameter("Message").Trim();
            if (message.Length == 0)
            {
                throw new InvalidOperationException("This step has no commit message. Write one — {output} puts what the step before produced in it.");
            }

            var directory = context.Parameter("Working directory").Trim();

            if (!await GitCommand.HasChangesAsync(directory, cancellationToken))
            {
                // Not a failure: there was nothing to do. An empty commit would be a lie about what happened.
                return WorkflowStepResult.Done("Nothing to commit.");
            }

            if (!_No(context.Parameter("Stage everything")))
            {
                await GitCommand.RunAsync(directory, ["add", "-A"], cancellationToken);
            }

            await GitCommand.RunAsync(directory, ["commit", "-m", message], cancellationToken);

            var commit = await GitCommand.RunAsync(directory, ["rev-parse", "--short", "HEAD"], cancellationToken);
            var branch = await GitCommand.CurrentBranchAsync(directory, cancellationToken);

            return new WorkflowStepResult(
                [
                    new Dictionary<string, string>
                    {
                        ["commit"] = commit,
                        ["branch"] = branch,
                        ["directory"] = directory,
                    },
                ],
                $"Committed {commit} on {branch}.");
        }

        private static bool _No(string value) => value.Trim() is "no" or "false" or "0" or "n";
    }

    /// <summary>Pushes the current branch, setting its upstream the first time — never with force, and never a branch you did not name.</summary>
    private sealed class PushStep : IWorkflowStep
    {
        public string TypeId => "git.push";

        // Pushes to a remote — egress that changes what other people see, so gated (#AC-38).
        public WorkflowStepConsent? RequiredConsent => WorkflowStepConsent.Dangerous;

        public string Name => "Push";

        public string Description => "Push the current branch, setting its upstream the first time. Never forces: a workflow that can overwrite a colleague's commits is not a convenience.";

        public string Icon => "";

        public MaterialIconKind? IconKind => MaterialIconKind.ArrowUp;

        public string Category => "Git";

        public IReadOnlyList<string> Parameters => ["Working directory", "Remote"];

        public IReadOnlyDictionary<string, string> Produces => new Dictionary<string, string>
        {
            ["branch"] = "eve-14-fix-the-login-redirect",
            ["remote"] = "origin",
            ["directory"] = "/home/raymond/RiderProjects/Eveworkbench",
        };

        public async Task<WorkflowStepResult> RunAsync(WorkflowStepContext context, CancellationToken cancellationToken)
        {
            var directory = context.Parameter("Working directory").Trim();
            var remote = context.Parameter("Remote").Trim() is { Length: > 0 } named ? named : "origin";

            var branch = await GitCommand.CurrentBranchAsync(directory, cancellationToken);
            if (branch.Length == 0)
            {
                throw new InvalidOperationException($"{directory} is not on a branch (detached HEAD), so there is nothing to push.");
            }

            var said = await GitCommand.RunAsync(directory, ["push", "--set-upstream", remote, branch], cancellationToken);

            return new WorkflowStepResult(
                [
                    new Dictionary<string, string>
                    {
                        ["branch"] = branch,
                        ["remote"] = remote,
                        ["directory"] = directory,
                    },
                ],
                said.Length > 0 ? said : $"Pushed {branch} to {remote}.");
        }
    }
}
