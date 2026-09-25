using System.Text.Json;
using Cockpit.Plugin.GitHubIssues.Contracts;
using Cockpit.Plugins.Abstractions.UI;

namespace Cockpit.Plugin.GitHubIssues.UI;

// AC-1396: one typed round trip over the plugin's channel, so the dialog, picker and header do not each repeat the
// serialize/invoke/deserialize dance for every action they ask the backend part.
internal static class GitHubIssuesChannelCalls
{
    public static async Task<TResult?> AskAsync<TResult>(this IPluginUiChannel channel, string action, object request, CancellationToken cancellationToken = default)
    {
        var payload = JsonSerializer.SerializeToElement(request, request.GetType(), GitHubIssuesChannel.Json);
        var answer = await channel.InvokeAsync(action, payload, cancellationToken);
        return answer.Deserialize<TResult>(GitHubIssuesChannel.Json);
    }
}
