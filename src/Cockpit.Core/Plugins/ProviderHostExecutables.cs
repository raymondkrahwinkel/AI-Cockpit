namespace Cockpit.Core.Plugins;

// AC-1013: PATH command `HostExecutableProbe` looks for per AI-provider plugin (AC-510[b] criterion
// 1), kept here rather than parsed from a manifest's free-form prose, and deliberately a lookup by
// plugin id rather than a self-reported manifest field, so a plugin cannot claim its own probe result.
public static class ProviderHostExecutables
{
    private static readonly IReadOnlyDictionary<string, string> _CommandsByPluginId = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["claude-provider"] = "claude",
        ["cli-agent-provider"] = "codex",
        ["kimi-provider"] = "kimi",
    };

    // The PATH command to probe for the given store plugin id, or null when this provider has no local CLI to find.
    public static string? CommandFor(string pluginId) =>
        _CommandsByPluginId.TryGetValue(pluginId, out var command) ? command : null;
}
