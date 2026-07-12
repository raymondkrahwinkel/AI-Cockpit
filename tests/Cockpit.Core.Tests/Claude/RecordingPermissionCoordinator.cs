using System.Collections.Concurrent;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Sessions.Permissions;

namespace Cockpit.Core.Tests.Claude;

/// <summary>
/// In-memory <see cref="IPermissionCoordinator"/> test double: records what sessions resolve/deny
/// and lets a test complete a pending request, without a real MCP server. A pending
/// <see cref="RequestDecisionAsync"/> completes when the matching <see cref="Resolve"/> (or
/// <see cref="DenyPending"/>) lands, mirroring the real correlation-on-tool_use_id contract.
/// </summary>
internal sealed class RecordingPermissionCoordinator : IPermissionCoordinator
{
    private readonly ConcurrentDictionary<string, TaskCompletionSource<PermissionDecision>> _pending = new();

    public List<(string ToolUseId, PermissionDecision Decision)> Resolved { get; } = [];

    public List<(string ToolUseId, string Reason)> Denied { get; } = [];

    public List<(string ToolUseId, IPermissionRuleChecker? RuleChecker)> Registered { get; } = [];

    public void RegisterToolUse(string toolUseId, IPermissionRuleChecker? ruleChecker) =>
        Registered.Add((toolUseId, ruleChecker));

    public Task<PermissionDecision> RequestDecisionAsync(
        string toolUseId,
        string toolName,
        string proposedInputJson,
        CancellationToken cancellationToken = default)
    {
        var completion = _pending.GetOrAdd(
            toolUseId,
            _ => new TaskCompletionSource<PermissionDecision>(TaskCreationOptions.RunContinuationsAsynchronously));
        return completion.Task;
    }

    public bool Resolve(string toolUseId, PermissionDecision decision)
    {
        Resolved.Add((toolUseId, decision));
        if (_pending.TryGetValue(toolUseId, out var completion))
        {
            return completion.TrySetResult(decision);
        }

        return false;
    }

    public void DenyPending(IEnumerable<string> toolUseIds, string reason)
    {
        foreach (var toolUseId in toolUseIds)
        {
            Denied.Add((toolUseId, reason));
            if (_pending.TryGetValue(toolUseId, out var completion))
            {
                completion.TrySetResult(PermissionDecision.Deny(reason));
            }
        }
    }
}
