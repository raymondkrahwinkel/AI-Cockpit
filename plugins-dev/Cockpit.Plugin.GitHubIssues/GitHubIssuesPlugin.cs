using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Cockpit.Plugin.GitHubIssues.Contracts;
using Cockpit.Plugins.Abstractions;

namespace Cockpit.Plugin.GitHubIssues;

// Example plugin (#14) proving the contract end-to-end: the backend part. It registers the tracker provider, the
// project field, the session resources, the workflow steps/templates and the Autopilot templates, keeps which issue
// each session is linked to, and answers what the UI part (GitHubIssuesUi — the settings view, the left-menu dialog
// listing open issues and the session header) asks over the plugin's channel. Its settings live in the host's
// per-plugin storage, so `ConfigureServices` is empty.
//
// AC-1396: before this split, the dialog, picker and header created the gh/HTTP clients and resolved the project's
// repository themselves; they now ask this part, which is the only one that talks to GitHub or to ICockpitHost.
public sealed class GitHubIssuesPlugin : ICockpitPlugin
{
    private readonly GitHubGhClient _gh = new();
    private readonly GitHubIssuesClient _http = new();
    private readonly GitHubWorkflowClient _workflow = new();
    private readonly List<IDisposable> _handlers = [];

    public PluginMetadata Metadata { get; } = new(
        Id: "github-issues",
        DisplayName: "GitHub Issues",
        Author: "Cockpit",
        Description: "Browse open GitHub issues across your repos (via the gh CLI) or one repo, with an \"Assigned to me\" filter, and drop a prompt asking the agent to open and review one. Link a cockpit project to a repository in the project editor, picked from the list gh can see, and the dialog opens on it instead of on every repository you have. Linking an issue to a session you already have open labels that session: it says what it is working on, and takes the issue as its name unless you gave it one yourself. The prompt template is editable in settings.");

    public void ConfigureServices(IServiceCollection services)
    {
    }

    public void Initialize(ICockpitHost host)
    {
        var settings = new GitHubIssuesSettings(host.Storage);

        // The writing half (AC-154): a consumer (Autopilot) posts evidence and labels an issue back through this,
        // tracker-neutrally. A GitHub issue has no status field, so its stage-equivalent is a label.
        host.AddTrackerProvider(new GitHubTrackerProvider());

        // Which repository a cockpit project lives in (AC-317), picked from the owner's own list in the project
        // editor. Read back below, where the issues dialog opens on it.
        host.AddProjectField(GitHubRepositoryField.Registration(settings, _gh));

        // AC-165: and carried into the sessions that project starts, so a `gh` command the agent runs is about the
        // repository the project is linked to rather than whatever its working directory happens to be.
        host.AddSessionResourceProvider(new GitHubRepositorySessionResources(host));

        // Shared by the dialog (which links an issue to the active session) and the header items (each of which
        // shows the issue linked to its own session) — see SessionIssueLinks. AC-1396: both reach it over the
        // channel, and hear about a change through the LinkChanged event.
        var links = new SessionIssueLinks(host);
        links.Changed += (_, paneId) => host.Channel.Publish(
            GitHubIssuesChannel.LinkChanged,
            JsonSerializer.SerializeToElement(new GitHubIssuesLinkChanged(paneId, links.For(paneId)), GitHubIssuesChannel.Json));

        _HandleChannel(host, settings, links);

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
        foreach (var handler in _handlers)
        {
            handler.Dispose();
        }

        _handlers.Clear();
    }

    private void _HandleChannel(ICockpitHost host, GitHubIssuesSettings settings, SessionIssueLinks links)
    {
        _Handle<GitHubIssuesSearchRequest, GitHubIssuesSearchResult>(host, GitHubIssuesChannel.SearchIssues, (request, cancellationToken) =>
            _SearchAsync(settings, request, cancellationToken));

        // AC-548/AC-940: every repository the session's project is linked to, not only the owner's whole set.
        // Sent as `--repo` flags (see GitHubGhClient.SearchArguments) — never a `repo:` term, which ANDs. The
        // truncation signal (AC-519) is a dialog-only concern: the picker has never warned about a capped page.
        _Handle<GitHubIssuesPickerRequest, IReadOnlyList<GitHubIssue>>(host, GitHubIssuesChannel.PickerIssues, async (request, cancellationToken) =>
        {
            var linkedRepositories = await GitHubRepositoryField.ResolvePreferredRepositoriesAsync(host, request.PaneId, cancellationToken);
            var (issues, _) = await _gh.SearchOpenIssuesAsync(
                settings.GhOwner,
                request.AssignedToMe,
                forceRefresh: false,
                cancellationToken,
                string.IsNullOrWhiteSpace(settings.PickerTerms) ? null : settings.PickerTerms,
                linkedRepositories.Count > 0 ? linkedRepositories : null);
            return issues;
        });

        _Handle<GitHubIssuesPaneRequest, IReadOnlyList<string>>(host, GitHubIssuesChannel.ListLabels, (_, cancellationToken) =>
            settings.UseGitHubCli
                ? _gh.ListRepositoryLabelsAsync(settings.GhOwner, cancellationToken)
                : _http.GetRepositoryLabelsAsync(settings.Owner, settings.Repo, settings.Token, cancellationToken));

        // gh mode has its own repository source (GitHubRepositoryField uses the same one for the project editor's
        // repository field); HTTP mode has only ever had the one repository the settings name.
        _Handle<GitHubIssuesPaneRequest, IReadOnlyList<string>>(host, GitHubIssuesChannel.ListRepositories, (_, cancellationToken) =>
            settings.UseGitHubCli
                ? _gh.ListRepositoriesAsync(settings.GhOwner, cancellationToken)
                : Task.FromResult<IReadOnlyList<string>>(string.IsNullOrWhiteSpace(settings.Owner) || string.IsNullOrWhiteSpace(settings.Repo)
                    ? []
                    : [$"{settings.Owner}/{settings.Repo}"]));

        _Handle<GitHubIssuesPaneRequest, string?>(host, GitHubIssuesChannel.LinkedRepository, (request, cancellationToken) =>
            GitHubRepositoryField.ResolvePreferredRepositoryAsync(host, request.PaneId, cancellationToken));

        _Handle<GitHubIssuesLinkRequest, object?>(host, GitHubIssuesChannel.Link, async (request, _) =>
        {
            await links.LinkAsync(request.PaneId, request.Issue, request.WorkingDirectory);
            return null;
        });

        _Handle<GitHubIssuesPaneRequest, object?>(host, GitHubIssuesChannel.Unlink, (request, _) =>
        {
            links.Unlink(request.PaneId ?? string.Empty);
            return Task.FromResult<object?>(null);
        });

        _Handle<GitHubIssuesPaneRequest, GitHubIssue?>(host, GitHubIssuesChannel.LinkedIssue, (request, _) =>
            Task.FromResult(links.For(request.PaneId ?? string.Empty)));

        _Handle<GitHubIssueRequest, object?>(host, GitHubIssuesChannel.AssignToMe, async (request, cancellationToken) =>
        {
            await _workflow.AssignToMeAsync(new GitHubIssueReference(request.Repository, request.Number), cancellationToken);
            return null;
        });

        _Handle<GitHubIssueLabelRequest, object?>(host, GitHubIssuesChannel.AddLabel, async (request, cancellationToken) =>
        {
            await _workflow.AddLabelAsync(new GitHubIssueReference(request.Repository, request.Number), request.Label, cancellationToken);
            return null;
        });

        _Handle<GitHubIssueCloseRequest, object?>(host, GitHubIssuesChannel.Close, async (request, cancellationToken) =>
        {
            await _workflow.CloseAsync(new GitHubIssueReference(request.Repository, request.Number), request.Reason, string.Empty, cancellationToken);
            return null;
        });
    }

    // Which route the dialog's list takes is the settings' call: gh across the owner's repositories, or the one
    // repository over HTTP. A label narrows the fetch itself (AC-519) — gh's "label:x" term or the REST labels= param.
    private async Task<GitHubIssuesSearchResult> _SearchAsync(GitHubIssuesSettings settings, GitHubIssuesSearchRequest request, CancellationToken cancellationToken)
    {
        if (settings.UseGitHubCli)
        {
            var (issues, truncated) = await _gh.SearchOpenIssuesAsync(
                settings.GhOwner,
                request.AssignedToMe,
                request.ForceRefresh,
                cancellationToken,
                request.Label is null ? null : GitHubGhClient.LabelSearchTerm(request.Label));
            return new GitHubIssuesSearchResult(issues, truncated, GitHubGhClient.IssueSearchLimit);
        }

        var (pageIssues, pageTruncated) = await _http.GetOpenIssuesAsync(
            settings.Owner, settings.Repo, settings.Token, request.AssignedToMe, cancellationToken, request.Label);
        return new GitHubIssuesSearchResult(pageIssues, pageTruncated, GitHubIssuesClient.IssuePageLimit);
    }

    // Every action takes a request record, even one that needs nothing from it, so one handler shape covers them all.
    private void _Handle<TRequest, TResult>(ICockpitHost host, string action, Func<TRequest, CancellationToken, Task<TResult>> answer)
        where TRequest : class =>
        _handlers.Add(host.Channel.Handle(action, async (payload, cancellationToken) =>
        {
            var request = payload.Deserialize<TRequest>(GitHubIssuesChannel.Json)
                ?? throw new ArgumentException($"The '{action}' request is empty.", nameof(payload));
            return JsonSerializer.SerializeToElement(await answer(request, cancellationToken), GitHubIssuesChannel.Json);
        }));
}
