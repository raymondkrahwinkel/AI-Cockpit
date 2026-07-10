namespace Cockpit.Infrastructure.Mcp;

/// <summary>
/// The approval seam a <see cref="GatedTool"/> asks before running an MCP tool call (#26). The session
/// driver implements it by raising the cockpit's <c>PermissionRequested</c> event and awaiting the
/// operator's Allow/Deny — the same human-in-the-loop flow Claude tool calls use — so local-model tool
/// use is never executed without consent.
/// </summary>
internal interface IToolApprovalGate
{
    /// <summary>
    /// Surfaces the pending tool call (a ToolUse + PermissionRequested on the session) and returns true to
    /// run it, false to refuse — after consulting any always-allow rule and otherwise awaiting the decision.
    /// </summary>
    Task<bool> RequestApprovalAsync(string toolUseId, string toolName, string inputJson, CancellationToken cancellationToken);

    /// <summary>Reports the outcome of a tool call (its result text or a denial/error), so the session shows it under the tool row.</summary>
    void ReportToolResult(string toolUseId, string content, bool isError);
}
