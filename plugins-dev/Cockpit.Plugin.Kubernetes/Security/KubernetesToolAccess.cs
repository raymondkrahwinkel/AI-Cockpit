namespace Cockpit.Plugin.Kubernetes.Security;

// Which k8s MCP tools count as a read, a sensitive read or a change, for the ReadFree/AllFree consent modes
// (AC-1349). A tool absent from this table counts as a change — one added later never goes quiet by omission.
internal static class KubernetesToolAccess
{
    // helm_list/helm_status/helm_history decode the release secret but return metadata only (AC-1347 decision 4B).
    private static readonly HashSet<string> _ReadTools = new(StringComparer.Ordinal)
    {
        "list_resources", "get_resource", "pod_logs", "argo_apps", "argo_app", "argo_history", "argo_last_sync",
        "helm_list", "helm_status", "helm_history",
    };

    private static readonly HashSet<string> _SensitiveTools = new(StringComparer.Ordinal)
    {
        "helm_values", "helm_manifest",
    };

    // `sensitiveResource` is `ResourceScope.IsSensitive` at the call site: a read tool on a secret is sensitive.
    public static ToolAccess Classify(string toolName, bool sensitiveResource = false)
    {
        if (_ReadTools.Contains(toolName))
        {
            return sensitiveResource ? ToolAccess.Sensitive : ToolAccess.Read;
        }

        return _SensitiveTools.Contains(toolName) ? ToolAccess.Sensitive : ToolAccess.Change;
    }
}

internal enum ToolAccess
{
    Read,
    Sensitive,
    Change,
}
