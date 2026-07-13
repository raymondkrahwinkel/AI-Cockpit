using Microsoft.Extensions.DependencyInjection;
using Cockpit.Plugins.Abstractions;

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
        Version: "1.5.0",
        Author: "Cockpit",
        Description: "Browse open GitHub issues across your repos (via the gh CLI) or one repo, with an \"Assigned to me\" filter, and drop a prompt asking the agent to open and review one. The prompt template is editable in settings.");

    public void ConfigureServices(IServiceCollection services)
    {
    }

    public void Initialize(ICockpitHost host)
    {
        var settings = new GitHubIssuesSettings(host.Storage);

        host.AddSettings(() => new GitHubIssuesSettingsControl(settings));
        host.AddSideMenuButton(
            "GitHub Issues",
            () => _ = host.ShowDialogAsync("GitHub Issues", () => new GitHubIssuesDialogControl(settings, host.Actions), 1040, 700));

        // The issue this session is working on, in its own header — and, before one is picked, the way to pick it.
        var links = new SessionIssueLinks();
        host.AddSessionHeaderItem(session => new GitHubSessionHeaderControl(host, session, links, settings));

        // What a flow can do with an issue (#77). A GitHub issue has no status, so there is no "move to In Progress"
        // here — starting one means assigning it to yourself and, if your repo uses one, labelling it.
        foreach (var step in GitHubWorkflowSteps.All(settings))
        {
            host.AddWorkflowStep(step);
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
                ["branch"] = GitHubBranchName.From(picked.Issue.Number, picked.Issue.Title),
                ["directory"] = picked.WorkingDirectory ?? string.Empty,
            });
    }

    public void Dispose()
    {
    }
}
