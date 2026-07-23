using Cockpit.Plugins.Abstractions;

namespace Cockpit.Plugin.YouTrack;

/// <summary>
/// The Autopilot goal/brief templates this plugin contributes (AC-189): sensible starting points for the two kinds of
/// work a YouTrack issue most often is — a bug fix and a feature — so the operator picks one in the plan flow instead of
/// writing the brief from scratch. Each carries <c>{{issue.*}}</c> placeholders Autopilot fills from the triggering
/// issue at run time. Registered on every start through <see cref="ICockpitHost.RegisterAutopilotTemplate"/>; the host
/// stamps this plugin as their owner (Origin=Plugin), which makes them editable-as-override but never deletable.
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
            """,
            ["issue.id", "issue.title"]),

        new PluginAutopilotTemplate(
            "youtrack.feature",
            "Feature",
            """
            Build the feature from YouTrack issue {{issue.id}}: "{{issue.title}}".

            {{issue.description}}

            Design it to fit the existing code and conventions, cover it with tests, and update any docs the change
            touches. Keep the scope to what the issue asks for. Link: {{issue.url}}
            """,
            ["issue.id", "issue.title"]),
    ];
}
