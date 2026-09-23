namespace Cockpit.Plugin.Docker.Security;

// Which Docker MCP tools count as a read versus a change, for the ReadFree/AllFree consent modes (AC-1348). A
// tool absent from this table counts as a change — an unlisted tool (one added later, say) never goes quiet by
// omission.
internal static class DockerToolAccess
{
    private static readonly HashSet<string> _ReadTools = new(StringComparer.Ordinal)
    {
        "daemon_info", "list_containers", "logs", "list_images", "inspect", "stats", "top",
        "list_volumes", "list_networks", "compose_config", "compose_logs", "compose_ps",
    };

    public static bool IsRead(string toolName) => _ReadTools.Contains(toolName);
}
