using System.Text.Json;
using System.Text.Json.Nodes;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Plugin.ClaudeProvider;

internal static class ClaudeMcpPermissionRules
{
    public static void Apply(PluginTtyLaunchContext context, string? configDir, string userHome, List<string> arguments)
    {
        if (context.TranslateMcpPermissionRule is not { } translate)
        {
            return;
        }
        var state = ClaudeConfigPaths.ResolveStateDirectory(configDir,
            context.BaseEnvironment.GetValueOrDefault(ClaudeConfigPaths.EnvironmentVariable), userHome);
        var permissions = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal)
        {
            ["allow"] = [], ["deny"] = [], ["ask"] = [],
        };
        foreach (var directory in new[] { state, Path.Combine(context.WorkingDirectory, ".claude") })
        {
            foreach (var file in new[] { "settings.json", "settings.local.json" })
            {
                var path = Path.Combine(directory, file);
                if (!File.Exists(path))
                {
                    continue;
                }
                JsonObject? settings;
                try
                {
                    settings = JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
                }
                catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
                {
                    continue;
                }
                foreach (var (kind, rules) in permissions)
                {
                    if (settings?["permissions"] is not JsonObject savedPermissions || savedPermissions[kind] is not JsonArray saved)
                    {
                        continue;
                    }
                    foreach (var entry in saved)
                    {
                        if (entry is JsonValue value && value.TryGetValue<string>(out var rule) && rule.StartsWith("mcp__", StringComparison.Ordinal))
                        {
                            rules.UnionWith(translate(rule));
                        }
                    }
                }
            }
        }
        if (permissions["allow"].Count > 0)
        {
            arguments.Add("--allowedTools");
            arguments.Add(string.Join(",", permissions["allow"]));
        }
        // Keep deny/ask precedence when an old allow rule is copied to the gateway namespace.
        var overlay = new JsonObject();
        foreach (var kind in new[] { "deny", "ask" })
        {
            if (permissions[kind].Count > 0)
            {
                overlay[kind] = new JsonArray(permissions[kind].Select(rule => (JsonNode?)JsonValue.Create(rule)).ToArray());
            }
        }
        if (overlay.Count > 0)
        {
            var index = arguments.IndexOf("--settings");
            var settings = index >= 0 ? JsonNode.Parse(arguments[index + 1]) as JsonObject ?? new() : new JsonObject();
            settings["permissions"] = overlay;
            if (index >= 0)
            {
                arguments[index + 1] = settings.ToJsonString();
            }
            else
            {
                arguments.Add("--settings");
                arguments.Add(settings.ToJsonString());
            }
        }
    }
}
