using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Cockpit.Plugin.GitStatus.Contracts;
using Cockpit.Plugins.Abstractions;

namespace Cockpit.Plugin.GitStatus;

// Git status (#1): the backend part. It adds the git workflow steps and answers what the UI part (GitStatusUi,
// the per-session header badge and its settings) asks git over the plugin's channel (AC-1390). Everything it
// needs lives in the host's services already, so `ConfigureServices` is empty.
//
// AC-522 removed the plugin's other half — a left-menu button opening a dialog over a manually configured
// repository list, for watching a repo with no session open. Raymond judged that overbuilt for what the
// per-session indicator already covers; the workflow steps below are unrelated and stayed.
public sealed class GitStatusPlugin : ICockpitPlugin
{
    private readonly GitStatusReader _reader = new();
    private readonly List<IDisposable> _handlers = [];

    public PluginMetadata Metadata { get; } = new(
        Id: "git-status",
        DisplayName: "Git status",
        Author: "Cockpit",
        Description: "An inline panel that follows the active session — the branch / uncommitted / unpushed status of the repo it is working in, refreshing when the session switches or runs a git command. Click to drop the status summary into the session.");

    public void ConfigureServices(IServiceCollection services)
    {
    }

    public void Initialize(ICockpitHost host)
    {
        _handlers.Add(host.Channel.Handle(GitStatusChannel.Status, (payload, cancellationToken) =>
            _AnswerAsync(payload, async directory => _Badge(await _reader.ReadAsync(directory, cancellationToken)))));
        _handlers.Add(host.Channel.Handle(GitStatusChannel.Branch, (payload, cancellationToken) =>
            _AnswerAsync(payload, directory => GitCommand.CurrentBranchAsync(directory, cancellationToken))));
        _handlers.Add(host.Channel.Handle(GitStatusChannel.HeadFile, (payload, cancellationToken) =>
            _AnswerAsync(payload, directory => GitHeadLocator.ResolveHeadFileAsync(directory, cancellationToken))));

        // What a flow can do with git (#69): cut a branch, commit, push. Nothing that can throw away work — no force,
        // no reset, no deleting branches.
        foreach (var step in GitWorkflowSteps.All())
        {
            host.AddWorkflowStep(step);
        }
    }

    public void Dispose()
    {
        foreach (var handler in _handlers)
        {
            handler.Dispose();
        }

        _handlers.Clear();
    }

    private static async Task<JsonElement> _AnswerAsync<T>(JsonElement payload, Func<string, Task<T>> answer)
    {
        var request = payload.Deserialize<GitStatusRequest>(GitStatusChannel.Json)
            ?? throw new ArgumentException("The request names no working directory.", nameof(payload));
        return JsonSerializer.SerializeToElement(await answer(request.WorkingDirectory), GitStatusChannel.Json);
    }

    private static GitStatusBadge _Badge(GitRepoStatus status) =>
        new(status.Error, status.Branch, status.IsClean, $"{status.Name} · {status.Branch}\n{GitStatusSummary.Describe(status)}");
}
