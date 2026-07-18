namespace Cockpit.Plugin.Kubernetes.Mcp;

/// <summary>
/// A Kubernetes <c>apiVersion</c> split into its group and version, the way the generic client wants them: core
/// resources are group <c>""</c> (e.g. <c>v1</c> → group <c>""</c>, version <c>v1</c>), grouped ones carry the
/// group (e.g. <c>apps/v1</c> → group <c>apps</c>, version <c>v1</c>).
/// </summary>
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
