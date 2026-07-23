using Cockpit.Plugins.Abstractions;

namespace Cockpit.Plugin.GitHubIssues;

/// <summary>
/// The Autopilot goal/brief templates this plugin contributes (AC-189): starting points for the two kinds of work a
/// GitHub issue most often is — a bug fix and a feature — so the operator picks one in the plan flow instead of writing
/// the brief from scratch. Each carries <c>{{issue.*}}</c> placeholders Autopilot fills from the triggering issue at run
/// time. Registered on every start through <see cref="ICockpitHost.RegisterAutopilotTemplate"/>; the host stamps this
/// plugin as their owner (Origin=Plugin), which makes them editable-as-override but never deletable.
/// </summary>
internal static class GitHubAutopilotTemplates
{
    public static IReadOnlyList<PluginAutopilotTemplate> All { get; } =
    [
        new PluginAutopilotTemplate(
            "github-issues.bugfix",
            "Bug fix",
            """
            Fix GitHub issue {{issue.id}}: "{{issue.title}}".

            {{issue.description}}

            Reproduce the bug first, then fix the root cause — not just the symptom. Add or extend a test that fails
            before the fix and passes after it, and keep the change tight. Link: {{issue.url}}

            This is a code run: commit your work on the run's branch with a clear, conventional message and push the
            branch — the run must end with a merge-ready pull request for review. Do not merge it yourself; a human does
            the final merge. Keep the commits clean: no "Co-Authored-By" trailer and no mention of an AI, agent or
            assistant anywhere in a commit message.
            """,
            ["issue.id", "issue.title"],
            DeliversPullRequest: true),

        new PluginAutopilotTemplate(
            "github-issues.feature",
            "Feature",
            """
            Build the feature from GitHub issue {{issue.id}}: "{{issue.title}}".

            {{issue.description}}

            Design it to fit the existing code and conventions, cover it with tests, and update any docs the change
            touches. Keep the scope to what the issue asks for. Link: {{issue.url}}

            This is a code run: commit your work on the run's branch with a clear, conventional message and push the
            branch — the run must end with a merge-ready pull request for review. Do not merge it yourself; a human does
            the final merge. Keep the commits clean: no "Co-Authored-By" trailer and no mention of an AI, agent or
            assistant anywhere in a commit message.
            """,
            ["issue.id", "issue.title"],
            DeliversPullRequest: true),
    ];
}
