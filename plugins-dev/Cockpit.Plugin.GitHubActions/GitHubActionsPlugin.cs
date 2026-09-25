using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Cockpit.Plugin.GitHubActions.Contracts;
using Cockpit.Plugins.Abstractions;

namespace Cockpit.Plugin.GitHubActions;

// GitHub Actions CI status (AC-52): the backend part. Answers the UI part's GitHubActionsChannel.RecentRuns
// requests — the branch's recent workflow runs, read via the local `gh` CLI. Uses the machine's existing `gh`
// login; no local state, so `ConfigureServices` is empty.
//
// AC-1394: before this split, the UI controls shelled out to `gh`/`git` themselves; this plugin had no backend
// behaviour at all. CiWorkflowRunClient (the actual `gh run list` / `git rev-parse` logic) moved here unchanged.
public sealed class GitHubActionsPlugin : ICockpitPlugin
{
    private readonly CiWorkflowRunClient _client = new();
    private readonly List<IDisposable> _handlers = [];

    public PluginMetadata Metadata { get; } = new(
        Id: "github-actions",
        DisplayName: "GitHub Actions",
        Author: "Cockpit",
        Description: "Shows the GitHub Actions status of the branch a session is working in, in that session's header: "
            + "a coloured icon (green pass, red fail, amber running) for the latest workflow run on the current branch, "
            + "with the workflow/event/time on hover — click to open the run on GitHub. Also offers a Dashboard widget "
            + "and a dock-rail panel, each listing the branch's recent runs with the same colours plus duration, kept "
            + "open next to your work; read-only, with its own per-instance run count (1–20). Requires the gh CLI "
            + "installed and authenticated on the machine running Cockpit.");

    public void ConfigureServices(IServiceCollection services)
    {
        // No local state or background services — every answer reads gh on demand.
    }

    public void Initialize(ICockpitHost host)
    {
        _handlers.Add(host.Channel.Handle(GitHubActionsChannel.RecentRuns, _AnswerRecentRunsAsync));
    }

    public void Dispose()
    {
        foreach (var handler in _handlers)
        {
            handler.Dispose();
        }

        _handlers.Clear();
    }

    private async Task<JsonElement> _AnswerRecentRunsAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        var request = payload.Deserialize<GitHubActionsRunsRequest>(GitHubActionsChannel.Json)
            ?? throw new ArgumentException("The request names no working directory.", nameof(payload));
        var runs = await _client.GetRecentRunsAsync(request.WorkingDirectory, request.Limit, cancellationToken);
        return JsonSerializer.SerializeToElement(runs, GitHubActionsChannel.Json);
    }
}
