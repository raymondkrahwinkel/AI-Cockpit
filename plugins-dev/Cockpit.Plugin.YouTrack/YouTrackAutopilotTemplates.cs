using Cockpit.Plugins.Abstractions;

namespace Cockpit.Plugin.YouTrack;

/// <summary>
/// The Autopilot goal/brief templates this plugin contributes (AC-189): sensible starting points for the kinds of work a
/// YouTrack issue most often is — a bug fix, a feature, and an epic (AC-217) — so the operator picks one in the plan flow
/// instead of writing the brief from scratch. Each carries <c>{{issue.*}}</c> placeholders Autopilot fills from the
/// triggering issue at run time. Registered on every start through <see cref="ICockpitHost.RegisterAutopilotTemplate"/>;
/// the host stamps this plugin as their owner (Origin=Plugin), which makes them editable-as-override but never deletable.
/// </summary>
internal static class YouTrackAutopilotTemplates
{
    public static IReadOnlyList<PluginAutopilotTemplate> All { get; } =
    [
        new PluginAutopilotTemplate(
            "youtrack.bugfix",
            "Bug fix",
            """
            Fix YouTrack issue {{issue.id}}: "{{issue.title}}".

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
            "youtrack.feature",
            "Feature",
            """
            Build the feature from YouTrack issue {{issue.id}}: "{{issue.title}}".

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

        new PluginAutopilotTemplate(
            "youtrack.epic",
            "Epic",
            """
            Plan and deliver YouTrack epic {{issue.id}}: "{{issue.title}}" as one coherent run.

            {{issue.description}}

            This issue is an epic — its real work is its child issues, linked as "parent for", not the description above.
            Before you plan, read the epic issue with the tracker's read tools and pull its child issues (follow the epic's
            "parent for" / child links); those child issues are the sub-items to build. Do NOT reconstruct the sub-items
            from the description — take them from the links.

            Fold every child issue into ONE plan, not a run per child: a step (or a few) per child issue, ordered so
            dependencies come first, all sharing one worktree so the work accumulates on a single branch. End with the
            standard gates — a code review then a separate security review. The whole epic lands as ONE pull request:
            record the epic id ({{issue.id}}) and every child issue id you built in the plan and in the PR description, so
            the PR names exactly which issues it closes. Link: {{issue.url}}
            """,
            ["issue.id", "issue.title"],
            DeliversPullRequest: true),
    ];
}
