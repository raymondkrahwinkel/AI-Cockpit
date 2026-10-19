using System.Text.Json.Nodes;

namespace Cockpit.Core.Backup;

// A sign-in that came back from a restore with nothing in it (AC-1503). `PluginId` names the plugin whose own settings
// hold the Sign in; it is null for a sign-in no plugin owns, which the operator is still told about.
public sealed record EmptiedSignIn(string Label, string? PluginId)
{
    // The sentence the next start shows for one of them, named by the plugin or connection the operator will recognise.
    public static string Ask(string name) => $"This connection asks for a new sign-in after the restore: {name}.";

    // What a restore of `incoming` leaves empty: the fields the backup took out (`removed`, JSON paths) and the stored
    // OAuth tokens that hold no access token. A plugin's field counts only when that plugin's registration was restored.
    public static IReadOnlyList<EmptiedSignIn> From(JsonObject? incoming, IReadOnlyList<string> removed, RestoreOptions options)
    {
        var pluginIds = (incoming?["Plugins"] as JsonObject)?.Select(plugin => plugin.Key).ToList() ?? [];
        var found = new Dictionary<string, EmptiedSignIn>(StringComparer.OrdinalIgnoreCase);

        foreach (var path in removed)
        {
            var pluginId = pluginIds.OrderByDescending(id => id.Length).FirstOrDefault(id => path.StartsWith($"Plugins.{id}.", StringComparison.Ordinal));

            if (pluginId is null)
            {
                // The tokens are named below by their server, and a restore keeps this machine's own Provisioning section.
                if (options.Settings && !path.StartsWith("McpOAuthTokens[", StringComparison.Ordinal) && !path.StartsWith("Provisioning.", StringComparison.Ordinal))
                {
                    found.TryAdd(path, new EmptiedSignIn(_Named(incoming, path), null));
                }
            }
            else if (options.Includes(pluginId))
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

    // "Providers[2].ApiKey" reads as "Work (Providers[2].ApiKey)" when the entry holding the field names itself.
    private static string _Named(JsonObject? incoming, string path)
    {
        JsonNode? node = incoming;
        var steps = System.Text.RegularExpressions.Regex.Matches(path, @"[^.\[\]]+|\[(\d+)\]");

        foreach (var step in steps.Take(steps.Count - 1))
        {
            node = step.Groups[1].Success ? (node as JsonArray)?[int.Parse(step.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)] : (node as JsonObject)?[step.Value];
        }

        var name = (node as JsonObject)?.Select(property => property).FirstOrDefault(property => property.Key is "Name" or "Label" or "DisplayName").Value?.ToString();

        return string.IsNullOrEmpty(name) ? path : $"{name} ({path})";
    }
}
