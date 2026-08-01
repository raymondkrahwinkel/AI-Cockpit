namespace Cockpit.Core.Assistant;

/// <summary>
/// Who the assistant is, in the two names the mount rule is built out of (AC-544, criterion 2). Both live here, in
/// Core, because the two halves of that rule are written in two assemblies that cannot see each other: the App
/// starts the assistant and names the server it is to be given, and Infrastructure hosts the tools and refuses
/// every caller that is not that pane. A constant copied into both would be a rule that can drift, and the drift
/// would be silent — a renamed pane id does not fail to compile, it just quietly stops matching, and what stops
/// matching is a guardrail.
/// </summary>
public static class AssistantIdentity
{
    /// <summary>
    /// The pane id the assistant is always known by, and the only identity the broad read tools answer to.
    /// <para>
    /// It is not a secret and it does not have to be: it is checked against
    /// <c>McpRequestContext.CurrentPaneId</c>, which is stamped host-side from the request's own per-session
    /// bearer (AC-89) and cannot be moved by any argument on any tool. An ordinary session naming this string
    /// gets nothing, because nothing reads a string the caller supplied.
    /// </para>
    /// </summary>
    public const string PaneId = "cockpit-assistant";

    /// <summary>
    /// The MCP server the broad read tools are hosted under. Registered as an internal endpoint, so it never fans
    /// out to a session that did not name it — and the assistant's own launch is the only place in the codebase
    /// that names it.
    /// </summary>
    public const string McpServerName = "cockpit-assistant";
}
