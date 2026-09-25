using System.Text.Json;
using Cockpit.Plugin.Kubernetes.Cluster;
using Cockpit.Plugin.Kubernetes.Contracts;
using Cockpit.Plugin.Kubernetes.Model;

namespace Cockpit.Plugin.Kubernetes.Settings;

// What the settings view (UI/KubernetesSettingsControl, UI/ClusterRowControl) loads and saves over the plugin's
// channel (AC-1394): the UI part never references ClusterRegistration or KubernetesSettings directly — both stay
// backend-only, crossing the channel as the wire-shaped Contracts records instead. A separate class from
// KubernetesPlugin so a test can exercise it against a real KubernetesSettings without the plugin's other
// dependencies (the MCP tools, the managed CLI, the intent handlers).
internal sealed class ClusterSettingsChannel(KubernetesSettings settings)
{
    public Task<JsonElement> LoadAsync() =>
        Task.FromResult(JsonSerializer.SerializeToElement(_Snapshot(), KubernetesChannel.Json));

    public Task<JsonElement> SaveAsync(JsonElement payload)
    {
        var request = payload.Deserialize<ClusterSettingsSaveRequest>(KubernetesChannel.Json)
            ?? throw new ArgumentException("The request named no cluster edits.", nameof(payload));

        var originalIds = settings.Clusters.Select(cluster => cluster.Id).ToHashSet(StringComparer.Ordinal);
        var registrations = new List<ClusterRegistration>();
        foreach (var edit in request.Clusters)
        {
            var pasted = edit.PastedKubeconfig.Trim();
            if (!string.IsNullOrEmpty(edit.KubeconfigPath))
            {
                // The path model owns the source — drop any stored secret so a later cleared path cannot silently
                // revive a stale kubeconfig.
                settings.ClearKubeconfig(edit.Id);
            }
            else if (pasted.Length > 0)
            {
                settings.SetKubeconfig(edit.Id, pasted);
            }

            var pastedToken = edit.PastedArgoToken.Trim();
            if (pastedToken.Length > 0)
            {
                settings.SetArgoToken(edit.Id, pastedToken);
            }

            // attach is model+gate-ready but has no meaningful non-interactive MCP tool yet, so it stays off.
            var registration = new ClusterRegistration(
                edit.Id,
                edit.Label,
                edit.ContextName,
                edit.AllowedNamespaces,
                edit.AllowClusterScoped,
                edit.AllowExec,
                edit.AllowPortForward,
                AllowAttach: false,
                KubeconfigPath: edit.KubeconfigPath).WithConsentMode((ClusterConsentMode)edit.ConsentMode);

            // Detect exec-auth on the effective kubeconfig (the file at the path, or the pasted/stored content) so
            // the row can warn that connecting will run an external process.
            var content = pasted.Length > 0 ? pasted : settings.GetKubeconfig(edit.Id);
            var effectiveKubeconfig = KubeconfigInspector.ReadYaml(registration.KubeconfigPath, content);
            if (effectiveKubeconfig is { Length: > 0 })
            {
                registration = registration with { UsesExecAuth = KubeconfigInspector.Inspect(effectiveKubeconfig, registration.ContextName).UsesExecAuth };
            }

            registrations.Add(registration);
        }

        // Clear the stored kubeconfig of any cluster that is no longer saved — removed, or emptied out until the
        // row counted as blank — so an orphaned secret does not linger.
        var savedIds = registrations.Select(registration => registration.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var goneId in originalIds.Where(id => !savedIds.Contains(id)))
        {
            settings.ClearKubeconfig(goneId);
            settings.ClearArgoToken(goneId);
        }

        settings.Clusters = registrations;
        settings.McpEnabled = request.McpEnabled;

        return Task.FromResult(JsonSerializer.SerializeToElement(_Snapshot(), KubernetesChannel.Json));
    }

    private ClusterSettingsSnapshot _Snapshot() => new(
        settings.Clusters.Select(cluster => new ClusterSnapshot(
            cluster.Id,
            cluster.Label,
            cluster.ContextName,
            cluster.AllowedNamespaces,
            cluster.AllowClusterScoped,
            cluster.AllowExec,
            cluster.AllowPortForward,
            cluster.UsesExecAuth,
            cluster.KubeconfigPath,
            (int)cluster.EffectiveConsentMode(),
            settings.GetKubeconfig(cluster.Id) is not null,
            settings.GetArgoToken(cluster.Id) is not null)).ToList(),
        settings.McpEnabled);
}
