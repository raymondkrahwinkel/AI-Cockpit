namespace Cockpit.Infrastructure.Mcp;

/// <summary>
/// The transport-verified identity of the session behind the MCP request currently being handled (AC-89). Set by
/// <see cref="McpAuthMiddleware"/> from the request's per-session bearer, before the tool runs; read by the consent
/// broker so it scopes remember decisions on this — the session the request actually came from — rather than on the
/// <c>session</c> value the agent declared. An <see cref="System.Threading.AsyncLocal{T}"/>, so it flows down the
/// async call chain from the middleware through the tool into the broker, and is null off that path (the in-process
/// tool loop, the app's own UI-side consent) — where the broker keeps its previous behaviour.
/// </summary>
public static class McpRequestContext
{
    private static readonly AsyncLocal<string?> Current = new();

    /// <summary>The verified pane id of the current MCP request, or null when there is no verified session in scope.</summary>
    public static string? CurrentPaneId => Current.Value;

    /// <summary>Sets the verified pane id for the duration of the current request's async flow.</summary>
    internal static void Set(string? paneId) => Current.Value = paneId;
}
