using System.Collections.Concurrent;
using Cockpit.Core.Sessions.Permissions;

namespace Cockpit.Infrastructure.Mcp;

// AC-964: the one decision point every host-run tool loop goes through — auto-approve, an always-allow rule, a
// delegated ceiling, or the operator's answer — shared rather than copied since a mistake here is a permission hole.
internal sealed class SessionToolApprovalGate(
    Action<string, string, string> reportToolUse,
    Action<string, string, string> askPermission,
    Action<string, string, bool> reportResult) : IToolApprovalGate
{
    private readonly ConcurrentDictionary<string, TaskCompletionSource<bool>> _pendingApprovals = new();
    private readonly ConcurrentDictionary<string, byte> _alwaysAllowedTools = new();
    private volatile bool _autoApproveTools;

    // Non-interactive delegated gate (AC-79): when a ceiling is set, this session has no human to prompt, so a
    // tool call is decided against the ceiling + allow-list rather than raising PermissionRequested. Null for an
    // ordinary interactive session.
    private volatile string? _delegatedGateCeiling;
    private volatile IReadOnlySet<string>? _delegatedGateAllowList;

    // Each tool's permission class as the connected servers annotated it, consulted by the delegated decision.
    // Set once the tool session is up; an unset map grades every tool Unknown, which is the restrictive end.
    public IReadOnlyDictionary<string, ToolPermissionClass> ToolClasses { get; set; } =
        new Dictionary<string, ToolPermissionClass>(StringComparer.Ordinal);

    public async Task<ToolApprovalResult> RequestApprovalAsync(string toolUseId, string toolName, string inputJson, CancellationToken cancellationToken)
    {
        // Surface the call in the transcript, then either auto-allow (an always-allow rule this session), decide
        // it non-interactively (a delegated session), or prompt and await the operator's decision.
        reportToolUse(toolUseId, toolName, inputJson);

        // Auto-approve mode (the session's "allow all tools" toggle) or a per-tool always-allow rule runs the
        // call without prompting — the tool row is still emitted above, so it stays visible either way.
        if (_autoApproveTools || _alwaysAllowedTools.ContainsKey(toolName))
        {
            return ToolApprovalResult.Allow;
        }

        // A delegated session has no human to answer a prompt (AC-79): decide non-interactively against the
        // profile's permission ceiling and tool allow-list instead of raising PermissionRequested. A denial
        // carries its reason to the model (via GatedTool) and never blocks — no PermissionRequested is emitted.
        if (_delegatedGateCeiling is { } ceiling)
        {
            var toolClass = ToolClasses.GetValueOrDefault(toolName, ToolPermissionClass.Unknown);
            var onAllowList = _delegatedGateAllowList?.Contains(toolName) == true;
            var decision = DelegatedToolPermissionPolicy.Decide(ceiling, toolClass, toolName, onAllowList);
            return decision.IsAllowed ? ToolApprovalResult.Allow : ToolApprovalResult.Deny(decision.DenyMessage);
        }

        var pending = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingApprovals[toolUseId] = pending;
        askPermission(toolUseId, toolName, inputJson);

        using (cancellationToken.Register(() => pending.TrySetResult(false)))
        {
            var approved = await pending.Task.ConfigureAwait(false);
            return approved ? ToolApprovalResult.Allow : ToolApprovalResult.Deny(null);
        }
    }

    public void ReportToolResult(string toolUseId, string content, bool isError)
    {
        _pendingApprovals.TryRemove(toolUseId, out _);
        reportResult(toolUseId, content, isError);
    }

    // The operator's answer to one prompt. False when no prompt is outstanding under that id, which is how a
    // caller that also has another permission source (a plugin driver's own) knows to pass it on instead.
    public bool Respond(string toolUseId, bool allow)
    {
        if (!_pendingApprovals.TryRemove(toolUseId, out var decision))
        {
            return false;
        }

        decision.TrySetResult(allow);
        return true;
    }

    public bool AllowAlways(string toolUseId, string toolName)
    {
        _alwaysAllowedTools.TryAdd(toolName, 0);
        return Respond(toolUseId, allow: true);
    }

    public void SetAutoApprove(bool enabled)
    {
        _autoApproveTools = enabled;

        // Flipping it on frees any prompt already waiting, so the operator does not have to answer a prompt
        // they just chose to stop seeing.
        if (!enabled)
        {
            return;
        }

        foreach (var pending in _pendingApprovals.Values)
        {
            pending.TrySetResult(true);
        }
    }

    public void SetDelegatedGate(string ceiling, IReadOnlyList<string> allowedTools)
    {
        // Set the allow-list before the ceiling, since a non-null ceiling is what arms the gate; coerce null to
        // empty so a caller always gets it armed, never falling through to a prompt that hangs a headless session.
        _delegatedGateAllowList = new HashSet<string>(allowedTools, StringComparer.Ordinal);
        _delegatedGateCeiling = ceiling ?? string.Empty;
    }

    // Refuses every waiting prompt, so a disposed session's in-flight tool calls end rather than hang.
    public void CancelPending()
    {
        foreach (var pending in _pendingApprovals.Values)
        {
            pending.TrySetResult(false);
        }
    }
}
