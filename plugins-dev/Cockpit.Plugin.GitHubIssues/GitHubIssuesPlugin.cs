using Microsoft.Extensions.DependencyInjection;
using Material.Icons;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Plugin.GitHubIssues;

/// <summary>
/// Example plugin (#14) proving the contract end-to-end: it registers a settings view (opened from the
/// plugin manager's gear — GitHub CLI vs single-repo, and the editable prompt template) and a left-menu
/// button that opens a dialog listing open issues (across all your repos via <c>gh</c>, or one repo over
/// HTTP), where selecting one injects the rendered template into the active session so the agent opens and
/// reviews it. Its settings live in the host's per-plugin storage, so <see cref="ConfigureServices"/> is empty.
/// </summary>
public sealed class GitHubIssuesPlugin : ICockpitPlugin
{
    public PluginMetadata Metadata { get; } = new(
        Id: "github-issues",
        DisplayName: "GitHub Issues",
        Author: "Cockpit",
        Description: "Browse open GitHub issues across your repos (via the gh CLI) or one repo, with an \"Assigned to me\" filter, and drop a prompt asking the agent to open and review one. Link a cockpit project to a repository in the project editor, picked from the list gh can see, and the dialog opens on it instead of on every repository you have. The prompt template is editable in settings.");

    public void ConfigureServices(IServiceCollection services)
    {
    }

    public void Initialize(ICockpitHost host)
    {
        var settings = new GitHubIssuesSettings(host.Storage);

        host.AddSettings(() => new GitHubIssuesSettingsControl(settings));

        // The writing half (AC-154): a consumer (Autopilot) posts evidence and labels an issue back through this,
        // tracker-neutrally. A GitHub issue has no status field, so its stage-equivalent is a label.
        host.AddTrackerProvider(new GitHubTrackerProvider());

        // Which repository a cockpit project lives in (AC-317), picked from the owner's own list in the project
        // editor. Read back below, where the issues dialog opens on it.
        host.AddProjectField(GitHubRepositoryField.Registration(settings, new GitHubGhClient()));

        // Shared by the dialog (which links an issue to the active session) and the header items (each of which
        // shows the issue linked to its own session) — see SessionIssueLinks.
        var links = new SessionIssueLinks(host);

        // 1280×860, up from 1040×700 — the chip, fixed action toolbar and rendered description all want more
        // room than the old size gave them, the same reasoning as the YouTrack dialog's resize. PluginDialogHost
        // clamps this against the cockpit's own window size, so a smaller screen still gets a dialog that fits.
        host.AddSideMenuButton(
            "GitHub Issues",
            () => _ = host.ShowDialogAsync("GitHub Issues", () => new GitHubIssuesDialogControl(settings, host, links), 1280, 860));

        // The issue this session is working on, in its own header — and, before one is picked, the way to pick it.
        host.AddSessionHeaderItem(session => new GitHubSessionHeaderControl(host, session, links, settings));

        host.AddSessionHeaderAction(new PluginSessionAction(
            "Track a GitHub issue…",
            "",
            session => GitHubSessionHeaderControl.Pick(host, session, links, settings))
        {
            IconKind = MaterialIconKind.Github,
        });

        // What a flow can do with an issue (#77). A GitHub issue has no status, so there is no "move to In Progress"
        // here — starting one means assigning it to yourself and, if your repo uses one, labelling it.
        foreach (var step in GitHubWorkflowSteps.All(settings))
        {
            host.AddWorkflowStep(step);
        }

        foreach (var template in GitHubWorkflowTemplates.All)
        {
            host.AddWorkflowTemplate(template);
        }

        // The Autopilot goal/brief templates this plugin contributes (AC-189): a Bug fix and a Feature starting point,
        // with {{issue.*}} placeholders Autopilot fills from the triggering issue. Re-registered on every start (the host
        // keeps them in memory, stamped with this plugin as their owner); the operator picks one in the Autopilot plan flow.
        foreach (var template in GitHubAutopilotTemplates.All)
        {
            host.RegisterAutopilotTemplate(template);
        }

        // And the trigger is fired by the act it names: you picked an issue for a session.
        links.Picked += (_, picked) => host.RaiseWorkflowTrigger(
            GitHubWorkflowSteps.PickedTrigger,
            new Dictionary<string, string>
            {
                ["issue"] = picked.Issue.Number.ToString(),
                ["repository"] = picked.Issue.Repository,
                ["title"] = picked.Issue.Title,
                ["url"] = picked.Issue.Url,
                ["branch"] = GitHubBranchName.From(picked.Issue.Number, picked.Issue.Title, settings.BranchPattern),
                ["directory"] = picked.WorkingDirectory ?? string.Empty,
            });
    }

    public void Dispose()
    {
    }
}
