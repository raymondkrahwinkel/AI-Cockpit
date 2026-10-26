using System.Text.Json;
using Cockpit.Plugins.Abstractions.Channels;

namespace Cockpit.Plugin.Browser;

// AC-1713: The actions the browser UI half calls for the agent profile. JSON in and out, so the same calls work
// when the UI is in another process than the backend.
internal static class AgentChannel
{
    public static void Register(IPluginBackendChannel channel, AgentBrowserTabs tabs)
    {
        channel.Handle("agents.tabs", async (_, cancellationToken) => Json(new { ok = true, tabs = await tabs.ListAsync(cancellationToken).ConfigureAwait(false) }));
        channel.Handle("agents.watch", async (payload, cancellationToken) =>
            Done(await tabs.WatchAsync(Text(payload, "paneId"), Text(payload, "targetId"), cancellationToken).ConfigureAwait(false), "That tab is gone"));
        channel.Handle("agents.unwatch", (_, _) =>
        {
            tabs.Unwatch();
            return Task.FromResult(Done(true, string.Empty));
        });
        channel.Handle("agents.takeover", (payload, _) => Task.FromResult(Done(tabs.TakeOver(Text(payload, "paneId")), "That agent browser is gone")));
        channel.Handle("agents.release", (payload, _) => Task.FromResult(Done(tabs.GiveBack(Text(payload, "paneId")), "That agent browser is gone")));
        channel.Handle("agents.stop", (payload, _) => Task.FromResult(Done(tabs.Stop(Text(payload, "paneId")), "That agent browser is gone")));
        channel.Handle("agents.input", async (payload, _) =>
        {
            var error = await tabs.InputAsync(Text(payload, "paneId"), Text(payload, "targetId"), Text(payload, "method"), payload.GetProperty("params")).ConfigureAwait(false);
            return Done(error is null, error ?? string.Empty);
        });
    }

    private static string Text(JsonElement payload, string name) =>
        payload.TryGetProperty(name, out var value) && value.GetString() is { } text ? text : string.Empty;

    private static JsonElement Done(bool ok, string error) => Json(new { ok, error = ok ? null : error });

    private static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value);
}
