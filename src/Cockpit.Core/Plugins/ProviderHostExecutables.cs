namespace Cockpit.Core.Plugins;

/// <summary>
/// Which PATH command <see cref="HostExecutableProbe"/> should look for on behalf of each AI-provider store
/// plugin (AC-510[b] criterion 1) — a CLI provider's own manifest names the binary it wraps (e.g. cli-agent-
/// provider's says "Requires the codex CLI"), but that text is free-form prose nobody has checked, so the actual
/// command to probe is kept here instead of parsed out of it. A provider not listed here has nothing local to
/// find — <c>gemini-provider</c> and <c>github-models-provider</c> are cloud endpoints behind an API key entered
/// after install, not a CLI on this machine — and the first-run provider step shows those without a found/not-
/// found claim rather than guessing.
/// <para>
/// Deliberately a lookup by plugin id, not a manifest field: the probe only ever answers "is a file with this
/// name on PATH", never "does it work" (see <see cref="HostExecutableProbe"/>'s own remarks) — adding a field
/// that a plugin author fills in would let a plugin claim a probe result about itself, which is exactly the kind
/// of unverified claim criterion 1 exists to not repeat.
/// </para>
/// </summary>
public static class ProviderHostExecutables
{
    private static readonly IReadOnlyDictionary<string, string> _CommandsByPluginId = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["claude-provider"] = "claude",
        ["cli-agent-provider"] = "codex",
        ["kimi-provider"] = "kimi",
    };

    /// <summary>The PATH command to probe for the given store plugin id, or null when this provider has no local CLI to find.</summary>
    public static string? CommandFor(string pluginId) =>
        _CommandsByPluginId.TryGetValue(pluginId, out var command) ? command : null;
}
