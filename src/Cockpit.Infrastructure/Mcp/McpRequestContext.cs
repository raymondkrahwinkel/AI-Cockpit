namespace Cockpit.Infrastructure.Mcp;

// AC-89: transport-verified session identity for the current MCP request, set by McpAuthMiddleware and read by
// the consent broker via AsyncLocal; null off that path (in-process tool loop, UI-side consent).
public static class McpRequestContext
{
    private static readonly AsyncLocal<string?> Current = new();

    private static readonly AsyncLocal<NodeCaller?> CurrentNode = new();

    private static readonly AsyncLocal<bool> BackendApi = new();

    // The verified pane id of the current MCP request, or null when there is no verified session in scope.
    public static string? CurrentPaneId => Current.Value;

    // AC-1351: which credential a node-listener caller used and what it may do; null for every other caller.
    internal static NodeCaller? CurrentNodeCaller => CurrentNode.Value;

    // AC-1383: whether the current request is for the backend API (`/api/…`), which never holds the assistant.
    internal static bool IsBackendApiRequest => BackendApi.Value;

    // Sets the verified pane id (and, for a node-listener caller, its credential) for the current request's async flow.
    internal static void Set(string? paneId, NodeCaller? nodeCaller = null)
    {
        Current.Value = paneId;
        CurrentNode.Value = nodeCaller;
    }

    internal static void MarkBackendApi(bool isBackendApi) => BackendApi.Value = isBackendApi;
}
