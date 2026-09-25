using System.Text.Json;

namespace Cockpit.Plugin.Kubernetes.Contracts;

// AC-1394: what the backend part answers the UI part over the plugin's channel. Compiled into both assemblies as
// a linked source file rather than shared as an assembly, so the UI part never references the backend part — in
// particular, never Model.ClusterRegistration or Settings.KubernetesSettings, which the backend's own business
// logic (the MCP tools, the access gate, the connection factory) still uses directly and pervasively. The records
// below are that data's wire shape, kept as separate types so the two never collide when something references
// both assemblies at once (a test project does).
internal static class KubernetesChannel
{
    // Payload: KubeconfigContextsRequest. Answers a KubeconfigContextsAnswer, or null when neither a kubeconfig
    // file nor pasted content resolves to anything.
    public const string KubeconfigContexts = "kubeconfig-contexts";

    // Payload: none. Answers a ClusterSettingsSnapshot: every registered cluster plus the MCP toggle.
    public const string LoadClusterSettings = "load-cluster-settings";

    // Payload: ClusterSettingsSaveRequest. Stores the edited clusters (kubeconfig/token secrets included,
    // exec-auth detected fresh) and the MCP toggle. Answers a ClusterSettingsSnapshot of what was actually saved.
    public const string SaveClusterSettings = "save-cluster-settings";

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
}

internal sealed record KubeconfigContextsRequest(string KubeconfigPath, string PastedKubeconfig);

// The context names declared in the kubeconfig the request named.
internal sealed record KubeconfigContextsAnswer(IReadOnlyList<string> Names);

// One registered cluster's non-secret fields, as the settings view displays them — the wire shape of
// Model.ClusterRegistration. ConsentMode is already the cluster's *effective* mode (Model.ClusterRegistration.
// EffectiveConsentMode), as a plain int matching the settings view's ComboBox.SelectedIndex.
internal sealed record ClusterSnapshot(
    string Id,
    string Label,
    string ContextName,
    IReadOnlyList<string> AllowedNamespaces,
    bool AllowClusterScoped,
    bool AllowExec,
    bool AllowPortForward,
    bool UsesExecAuth,
    string KubeconfigPath,
    int ConsentMode,
    bool HasStoredKubeconfig,
    bool HasStoredArgoToken);

internal sealed record ClusterSettingsSnapshot(IReadOnlyList<ClusterSnapshot> Clusters, bool McpEnabled);

// One row's edits on save. PastedKubeconfig/PastedArgoToken are empty when the operator left the box blank —
// that means "keep the stored secret", same as before the split.
internal sealed record ClusterEdit(
    string Id,
    string Label,
    string ContextName,
    IReadOnlyList<string> AllowedNamespaces,
    bool AllowClusterScoped,
    bool AllowExec,
    bool AllowPortForward,
    string KubeconfigPath,
    int ConsentMode,
    string PastedKubeconfig,
    string PastedArgoToken);

internal sealed record ClusterSettingsSaveRequest(IReadOnlyList<ClusterEdit> Clusters, bool McpEnabled);
