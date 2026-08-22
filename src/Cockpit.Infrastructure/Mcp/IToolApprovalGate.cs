namespace Cockpit.Infrastructure.Mcp;

/// <summary>
/// The approval seam a <see cref="GatedTool"/> asks before running an MCP tool call (#26). The session driver
/// raises the cockpit's <c>PermissionRequested</c> event and awaits Allow/Deny — the same human-in-the-loop
/// flow Claude tool calls use — so local-model tool use is never executed without consent.
/// </summary>
internal interface IToolApprovalGate
{
    /// <summary>
    /// Surfaces the pending tool call (a ToolUse + PermissionRequested) and resolves to allow or refuse it with a
    /// reason — after consulting any always-allow rule, or for a delegated session deciding non-interactively
    /// against the ceiling + allow-list (AC-79). The refusal reason feeds back to the model so it can adapt.
    /// </summary>
    Task<ToolApprovalResult> RequestApprovalAsync(string toolUseId, string toolName, string inputJson, CancellationToken cancellationToken);

    /// <summary>Reports the outcome of a tool call (its result text or a denial/error), so the session shows it under the tool row.</summary>
    void ReportToolResult(string toolUseId, string content, bool isError);
}

// The outcome of a gate decision: run the tool, or refuse it with a reason for the model's tool result.
internal readonly record struct ToolApprovalResult(bool Approved, string? DenyReason)
{
    // Allow the call to run.
    public static ToolApprovalResult Allow { get; } = new(true, null);

    // Refuse the call; `reason` becomes the tool-result error the model sees (null falls back to a generic message).
    public static ToolApprovalResult Deny(string? reason) => new(false, reason);
}
