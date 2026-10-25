namespace Cockpit.Core.Plugins;

public sealed record ProviderHostExecutable(string Command, IReadOnlyList<string> UserProfileRelativeDirectories);

// AC-1013: executable data `HostExecutableProbe` uses per AI-provider plugin (AC-510[b] criterion 1),
// kept here rather than parsed from a manifest's free-form prose. A plugin cannot claim its own result.
public static class ProviderHostExecutables
{
    private static readonly IReadOnlyDictionary<string, ProviderHostExecutable> _ByPluginId = new Dictionary<string, ProviderHostExecutable>(StringComparer.Ordinal)
    {
        ["claude-provider"] = new("claude", [".local/bin"]),
        ["cli-agent-provider"] = new("codex", []),
        ["kimi-provider"] = new("kimi", []),
    };

    public static ProviderHostExecutable? For(string pluginId) =>
        _ByPluginId.TryGetValue(pluginId, out var executable) ? executable : null;
}
