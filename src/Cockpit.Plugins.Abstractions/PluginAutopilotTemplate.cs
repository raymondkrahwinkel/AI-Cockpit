namespace Cockpit.Plugins.Abstractions;

/// <summary>
/// An Autopilot goal/brief template a plugin contributes (AC-189), offered as a starting point for a run — the same
/// idea as <see cref="Workflows.WorkflowTemplate"/> for flows. A plugin that knows a recurring kind of work ("triage
/// this issue", "cut a release") ships the brief so the operator picks it instead of writing it from scratch. The
/// <see cref="Body"/> is the brief text and may carry <c>{{placeholder}}</c> tokens (<c>{{issue.title}}</c>,
/// <c>{{input.branch}}</c>) that Autopilot fills in from the triggering issue and the operator's input at run time.
/// <para>
/// This is what a plugin <em>registers</em>; the Autopilot plugin turns it into its own richer template (with an
/// origin, edit/delete rules and any operator override applied). Registrations live only in memory — a plugin
/// re-registers on every start — so a plugin never persists templates itself.
/// </para>
/// </summary>
/// <param name="Id">Stable identity ("autopilot.triage"), so a registration is recognised across versions and an operator override can be keyed to it.</param>
/// <param name="Name">What the template picker shows.</param>
/// <param name="Body">The goal/brief text, with optional <c>{{placeholder}}</c> tokens resolved at run time.</param>
/// <param name="RequiredPlaceholders">The placeholder names the brief cannot do without, so the surface can warn before a run is started with one unfilled. Optional.</param>
/// <param name="DeliversPullRequest">
/// Whether a run started from this template is a <em>code</em> run that must end with a merge-ready pull request
/// (AC-216) — the template-level signal that decides the run's finalization, not a global switch. A code template
/// ("Bug fix", "Feature") sets it <see langword="true"/>: its brief tells the agents to commit and push, and the run's
/// merge-ready finalizer pushes the run branch and opens a PR (or, when it cannot — no <c>gh</c>, no remote — reports a
/// clear outcome and leaves the work on its branch). An administrative template leaves it <see langword="false"/>: the
/// run settles merge-ready with no PR expectation and no error for the missing PR. Defaults to <see langword="false"/>
/// so a template that says nothing is treated as administrative, and older plugin builds keep compiling untouched.
/// </param>
public sealed record PluginAutopilotTemplate(
    string Id,
    string Name,
    string Body,
    IReadOnlyList<string>? RequiredPlaceholders = null,
    bool DeliversPullRequest = false);
