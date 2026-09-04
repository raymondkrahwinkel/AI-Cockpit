using Cockpit.Plugins.Abstractions;

namespace Cockpit.Plugin.Kind.Settings;

// The plugin's settings, persisted through the host's per-plugin `IPluginStorage`. Read fresh on every access, so a
// change made in the settings view takes effect on the next call without a restart.
internal sealed class KindSettings(IPluginStorage storage)
{
    // AC-179: the kind-cluster registry — a kind cluster's kubeconfig is a plain file path on disk, not a pasted
    // secret, so it lives in the record itself rather than the secret layer.
    public IReadOnlyList<KindClusterRecord> KindClusters
    {
        get => storage.Get<List<KindClusterRecord>>("kindClusters") ?? [];
        set => storage.Set("kindClusters", value.ToList());
    }

    // The TTL backstop (AC-179 criterion 11), next to criterion 8's live-session sweep rather than instead of it.
    // Four hours covers a normal working session without leaving a forgotten cluster's 632 MiB idle overnight.
    public TimeSpan KindClusterMaxLifetime
    {
        get => TimeSpan.FromHours(storage.Get<double?>("kindClusterMaxLifetimeHours") ?? 4.0);
        set => storage.Set("kindClusterMaxLifetimeHours", value.TotalHours);
    }

    // Whether the kind MCP server is offered to sessions. On by default until the operator turns it off.
    public bool McpEnabled
    {
        get => storage.Get<bool?>("mcpEnabled") ?? true;
        set => storage.Set("mcpEnabled", value);
    }
}
