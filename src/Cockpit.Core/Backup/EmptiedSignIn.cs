using System.Text.Json.Nodes;

namespace Cockpit.Core.Backup;

// A sign-in that came back from a restore with nothing in it (AC-1503). `PluginId` names the plugin whose own settings
// hold the Sign in; it is null for a sign-in no plugin owns, which the operator is still told about.
public sealed record EmptiedSignIn(string Label, string? PluginId)
{
    // What a restore of `incoming` leaves empty: the fields the backup took out (`removed`, JSON paths) and the stored
    // OAuth tokens that hold no access token. A plugin's field counts only when that plugin was restored too.
    public static IReadOnlyList<EmptiedSignIn> From(JsonObject? incoming, IReadOnlyList<string> removed, RestoreOptions options)
    {
        var pluginIds = (incoming?["Plugins"] as JsonObject)?.Select(plugin => plugin.Key).ToList() ?? [];
        var found = new Dictionary<string, EmptiedSignIn>(StringComparer.OrdinalIgnoreCase);

        foreach (var path in removed)
        {
            var pluginId = pluginIds.FirstOrDefault(id => path.StartsWith($"Plugins.{id}.", StringComparison.Ordinal));

            if (pluginId is null)
            {
                // The tokens are named below by their server, which reads better than their path.
                if (options.Settings && !path.StartsWith("McpOAuthTokens[", StringComparison.Ordinal))
                {
                    found.TryAdd(path, new EmptiedSignIn(path, null));
                }
            }
            else if (options.Settings || options.Includes(pluginId))
            {
                found.TryAdd(pluginId, new EmptiedSignIn(pluginId, pluginId));
            }
        }

        if (options.Settings && incoming?["McpOAuthTokens"] is JsonArray tokens)
        {
            foreach (var token in tokens.OfType<JsonObject>().Where(token => string.IsNullOrEmpty(token["AccessToken"]?.GetValue<string>())))
            {
                var name = token["ServerName"]?.GetValue<string>() is { Length: > 0 } serverName ? serverName : token["ServerId"]?.GetValue<string>() ?? "MCP server";
                found.TryAdd($"mcp:{name}", new EmptiedSignIn(name, null));
            }
        }

        return [.. found.Values];
    }
}
