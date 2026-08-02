namespace Cockpit.Plugin.Kubernetes.Mcp;

// A Kubernetes `apiVersion` split into its group and version, the way the generic client wants them: core
// resources are group `""` (e.g. `v1` → group `""`, version `v1`), grouped ones carry the
// group (e.g. `apps/v1` → group `apps`, version `v1`).
internal readonly record struct ApiVersionRef(string Group, string Version)
{
    public static ApiVersionRef Parse(string apiVersion)
    {
        var slash = apiVersion.IndexOf('/');
        return slash < 0
            ? new ApiVersionRef(string.Empty, apiVersion.Trim())
            : new ApiVersionRef(apiVersion[..slash].Trim(), apiVersion[(slash + 1)..].Trim());
    }
}
