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
    /// Surfaces the pending tool call (a ToolUse + PermissionRequested on the session) and resolves to allow it
    /// or refuse it with a reason — after consulting any always-allow rule and otherwise awaiting the decision, or,
    /// for a delegated session, deciding it non-interactively against the ceiling + allow-list (AC-79). The reason
    /// on a refusal is the tool result fed back to the model, so it can adapt rather than blindly retry.
    /// </summary>
    Task<ToolApprovalResult> RequestApprovalAsync(string toolUseId, string toolName, string inputJson, CancellationToken cancellationToken);

    /// <summary>Reports the outcome of a tool call (its result text or a denial/error), so the session shows it under the tool row.</summary>
    void ReportToolResult(string toolUseId, string content, bool isError);
}

/// <summary>The outcome of a gate decision: run the tool, or refuse it with a reason for the model's tool result.</summary>
internal readonly record struct ToolApprovalResult(bool Approved, string? DenyReason)
{
    /// <summary>Allow the call to run.</summary>
    public static ToolApprovalResult Allow { get; } = new(true, null);

    /// <summary>Refuse the call; <paramref name="reason"/> becomes the tool-result error the model sees (null falls back to a generic message).</summary>
    public static ToolApprovalResult Deny(string? reason) => new(false, reason);
}
