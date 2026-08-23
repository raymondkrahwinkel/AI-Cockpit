namespace Cockpit.Infrastructure.Mcp;

// AC-89: transport-verified session identity for the current MCP request, set by McpAuthMiddleware and read by
// the consent broker via AsyncLocal; null off that path (in-process tool loop, UI-side consent).
public static class McpRequestContext
{
    private static readonly AsyncLocal<string?> Current = new();

    // The verified pane id of the current MCP request, or null when there is no verified session in scope.
    public static string? CurrentPaneId => Current.Value;

    // Sets the verified pane id for the duration of the current request's async flow.
    internal static void Set(string? paneId) => Current.Value = paneId;
}
