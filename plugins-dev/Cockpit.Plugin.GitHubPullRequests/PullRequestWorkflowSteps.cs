using System.Diagnostics;
using System.Text.Json;
using Cockpit.Plugins.Abstractions.Workflows;

namespace Cockpit.Plugin.GitHubPullRequests;

/// <summary>
/// Opening a pull request from a flow (#69). The end of the working day the other steps describe: a branch was cut, a
/// ticket moved, work was committed and pushed — and this is what turns that into something a person can review.
/// <para>
/// It goes through <c>gh</c>, like the rest of this plugin, so it reuses the login the operator already has. The
/// repository is the one the working directory belongs to: a pull request opened against a repo nobody named is a
/// pull request in the wrong place, and gh already knows which one it is standing in.
/// </para>
/// </summary>
internal static class PullRequestWorkflowSteps
{
    public static IEnumerable<IWorkflowStep> All() =>
    [
        new OpenPullRequestStep(),
    ];

    private sealed class OpenPullRequestStep : IWorkflowStep
    {
        public string TypeId => "github.pr.open";

        public string Name => "Open a pull request";

        public string Description => "Open a pull request for the branch a repository is on. Draft unless you say otherwise, because a flow that asks four people to review something you have not looked at yet is not a favour.";

        public string Icon => "⇵";

        public string Category => "GitHub";

        public IReadOnlyList<string> Parameters => ["Title", "Body", "Working directory", "Base branch", "Draft"];

        public IReadOnlyDictionary<string, string> Produces => new Dictionary<string, string>
        {
            ["url"] = "https://github.com/raymondkrahwinkel/AI-Cockpit/pull/12",
            ["number"] = "12",
            ["branch"] = "eve-14-fix-the-login-redirect",
        };

        public async Task<WorkflowStepResult> RunAsync(WorkflowStepContext context, CancellationToken cancellationToken)
        {
            var title = context.Parameter("Title").Trim();
            if (title.Length == 0)
            {
                throw new InvalidOperationException("This step has no title. Write one — {summary} or {branch} put what the step before produced in it.");
            }

            var directory = context.Parameter("Working directory").Trim();
            if (directory.Length == 0 || !Directory.Exists(directory))
            {
                throw new InvalidOperationException(
                    directory.Length == 0
                        ? "This step has no working directory. Write one, or {directory} to take it from the step before."
                        : $"There is no directory '{directory}'.");
            }

            var arguments = new List<string> { "pr", "create", "--title", title, "--body", context.Parameter("Body") };

            if (context.Parameter("Base branch").Trim() is { Length: > 0 } baseBranch)
            {
                arguments.Add("--base");
                arguments.Add(baseBranch);
            }

            // Draft unless the operator says no. An automation that opens a review-ready pull request is an
            // automation that asks other people for their afternoon.
            if (!_No(context.Parameter("Draft")))
            {
                arguments.Add("--draft");
            }

            var url = (await _RunAsync(directory, arguments, cancellationToken))
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .LastOrDefault(line => line.Contains("/pull/", StringComparison.Ordinal))
                ?.Trim()
                ?? string.Empty;

            var number = url.Split('/').LastOrDefault() ?? string.Empty;
            var branch = await _BranchAsync(directory, cancellationToken);

            return new WorkflowStepResult(
                [
                    new Dictionary<string, string>
                    {
                        ["url"] = url,
                        ["number"] = number,
                        ["branch"] = branch,
                    },
                ],
                url.Length > 0 ? $"Opened {url}" : "Opened a pull request.");
        }

        private static bool _No(string value) => value.Trim() is "no" or "false" or "0" or "n";

        private static async Task<string> _BranchAsync(string directory, CancellationToken cancellationToken)
        {
            try
            {
                var json = await _RunAsync(directory, ["pr", "view", "--json", "headRefName"], cancellationToken);
                using var document = JsonDocument.Parse(json);

                return document.RootElement.TryGetProperty("headRefName", out var branch) ? branch.GetString() ?? string.Empty : string.Empty;
            }
            catch (Exception)
            {
                // The branch is a courtesy for the next step; failing to read it back must not fail a pull request
                // that was actually opened.
                return string.Empty;
            }
        }

        private static async Task<string> _RunAsync(string directory, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
        {
            var startInfo = new ProcessStartInfo("gh")
            {
                WorkingDirectory = directory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            foreach (var argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            using var process = new Process { StartInfo = startInfo };

            try
            {
                process.Start();
            }
            catch (Exception exception)
            {
                throw new InvalidOperationException($"Could not run 'gh' — is the GitHub CLI installed and on PATH? ({exception.Message})", exception);
            }

            var stdout = await process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderr = await process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);

            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(stderr.Trim() is { Length: > 0 } said ? said : $"gh exited with {process.ExitCode}.");
            }

            // gh prints the pull request's URL on stdout, and everything it wants to tell you about it on stderr.
            return $"{stdout}\n{stderr}";
        }
    }
}
