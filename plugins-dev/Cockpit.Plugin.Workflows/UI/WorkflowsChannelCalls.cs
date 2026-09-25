using System.Text.Json;
using Cockpit.Plugin.Workflows.Contracts;
using Cockpit.Plugins.Abstractions.UI;

namespace Cockpit.Plugin.Workflows.UI;

// AC-1399: one typed round trip over the plugin's channel, so the dialog, editor and entry do not each repeat the
// serialize/invoke/deserialize dance for every question they ask the backend part.
internal static class WorkflowsChannelCalls
{
    public static async Task<TResult?> AskAsync<TResult>(this IPluginUiChannel channel, string action, object? request = null, CancellationToken cancellationToken = default)
    {
        var answer = await channel.InvokeAsync(action, _Payload(request), cancellationToken);
        return answer.Deserialize<TResult>(WorkflowsChannel.Json);
    }

    public static Task TellAsync(this IPluginUiChannel channel, string action, object request) =>
        channel.InvokeAsync(action, _Payload(request));

    private static JsonElement _Payload(object? request) =>
        request is null
            ? JsonSerializer.SerializeToElement(new { }, WorkflowsChannel.Json)
            : JsonSerializer.SerializeToElement(request, request.GetType(), WorkflowsChannel.Json);
}
