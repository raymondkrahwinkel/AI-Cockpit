using Cockpit.Plugins.Abstractions;

namespace Cockpit.Plugin.Kind;

// Registers a fresh cluster with the Kubernetes plugin, if that one is installed at all (AC-1083). Addressed by
// manifest id and an agreed action string, which is how plugin intents work — so nothing here references that
// plugin's types, and a cockpit without it registers nothing and says so, rather than failing.
internal static class KubernetesClusterGate
{
    private const string PluginId = "kubernetes";
    private const string RegisterAction = "cluster.register";
    private const string UnregisterAction = "cluster.unregister";

    // What kind_create should tell the agent on top of "the cluster is up", or null when there is nothing to add.
    // Checked at the moment of the call, never from Initialize: a plugin that loads after this one has not
    // registered its handler yet when ours runs.
    public static async Task<string?> RegisterAsync(ICockpitHost? host, string name, string kubeconfigPath)
    {
        if (host is null || !host.CanSendIntent(PluginId, RegisterAction))
        {
            return $"The Kubernetes plugin is not installed, so the cluster was not registered with it. Reach it with kubeconfig {kubeconfigPath}, context kind-{name}.";
        }

        var answer = await host.SendIntent(PluginId, RegisterAction, new Dictionary<string, string>
        {
            ["id"] = RegistrationId(name),
            ["label"] = name,
            ["context"] = $"kind-{name}",
            ["kubeconfigPath"] = kubeconfigPath,
        });

        return answer?.GetValueOrDefault("notice") is { Length: > 0 } notice ? notice : null;
    }

    // Best-effort: the kind cluster is gone either way, and a stale registration is the operator's to remove.
    public static async Task UnregisterAsync(ICockpitHost? host, string name)
    {
        if (host is not null && host.CanSendIntent(PluginId, UnregisterAction))
        {
            await host.SendIntent(PluginId, UnregisterAction, new Dictionary<string, string> { ["id"] = RegistrationId(name) });
        }
    }

    public static string RegistrationId(string name) => $"kind-{name}";
}
