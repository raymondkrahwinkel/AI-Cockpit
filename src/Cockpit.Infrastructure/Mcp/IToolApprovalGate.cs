namespace Cockpit.Infrastructure.Mcp;

/// <summary>
/// The approval seam a <see cref="GatedTool"/> asks before running an MCP tool call (#26). The session
/// driver implements it by raising the cockpit's <c>PermissionRequested</c> event and awaiting the
/// operator's Allow/Deny — the same human-in-the-loop flow Claude tool calls use — so local-model tool
/// use is never executed without consent.
/// </summary>
internal interface IToolApprovalGate
{
    /// <summary>Returns true to run the tool call, false to refuse it. Consults any always-allow rule first, otherwise prompts and awaits the decision.</summary>
    Task<bool> RequestApprovalAsync(string toolUseId, string toolName, string inputJson, CancellationToken cancellationToken);
}
