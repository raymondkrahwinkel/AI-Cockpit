using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Claude;
using Cockpit.Core.Claude.Permissions;

namespace Cockpit.Infrastructure.Claude.Permissions;

/// <summary>
/// Shared, in-process correlation between the single MCP permission-prompt server and the many
/// sessions, keyed on <c>tool_use_id</c> (see <see cref="IPermissionCoordinator"/> for why).
/// One instance for the whole app (singleton) since one MCP server serves every session.
/// </summary>
internal sealed class PermissionCoordinator : IPermissionCoordinator, ISingletonService
{
    private readonly ILogger<PermissionCoordinator> _logger;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<PermissionDecision>> _pending = new();
    private readonly ConcurrentDictionary<string, IPermissionRuleChecker> _ruleCheckers = new();

    // Allow/Deny answers that arrived before the CLI's MCP call registered the pending request (#28):
    // the operator can click on the prompt — derived from the stream tool_use — before the permission
    // tool is invoked. Holding the decision here rather than dropping it means whichever side arrives
    // first, the other picks it up, so the prompt never hangs.
    private readonly ConcurrentDictionary<string, PermissionDecision> _earlyDecisions = new();

    // Ids whose request already completed, so a late/duplicate Resolve is rejected rather than held as
    // an early answer for a request that will never come.
    private readonly ConcurrentDictionary<string, byte> _completed = new();

    public PermissionCoordinator(ILogger<PermissionCoordinator> logger)
    {
        _logger = logger;
    }

    public void RegisterToolUse(string toolUseId, IPermissionRuleChecker? ruleChecker)
    {
        if (ruleChecker is not null)
        {
            _ruleCheckers[toolUseId] = ruleChecker;
        }
    }

    public async Task<PermissionDecision> RequestDecisionAsync(
        string toolUseId,
        string toolName,
        string proposedInputJson,
        CancellationToken cancellationToken = default)
    {
        if (_ruleCheckers.TryGetValue(toolUseId, out var ruleChecker)
            && ruleChecker.IsAlwaysAllowed(toolName, proposedInputJson))
        {
            _ruleCheckers.TryRemove(toolUseId, out _);
            _logger.LogInformation(
                "Permission auto-allowed by a saved rule for {ToolName} (tool_use_id={ToolUseId})",
                toolName,
                toolUseId);
            return PermissionDecision.Allow();
        }

        // The operator already answered before this MCP call arrived (#28) — honour it, don't re-prompt.
        if (_earlyDecisions.TryRemove(toolUseId, out var earlyDecision))
        {
            _ruleCheckers.TryRemove(toolUseId, out _);
            _logger.LogInformation(
                "Permission {Outcome} for tool_use_id={ToolUseId} (answered before the request arrived)",
                earlyDecision.IsAllowed ? "allowed" : "denied",
                toolUseId);
            return earlyDecision;
        }

        var completion = new TaskCompletionSource<PermissionDecision>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(toolUseId, completion))
        {
            // A retry/duplicate for the same id: the first request owns the decision, so reuse it
            // rather than racing a second prompt for the same tool call.
            completion = _pending[toolUseId];
        }

        _logger.LogInformation("Permission requested for {ToolName} (tool_use_id={ToolUseId})", toolName, toolUseId);

        await using var registration = cancellationToken.Register(
            static state =>
            {
                var (source, id) = ((TaskCompletionSource<PermissionDecision>, string))state!;
                source.TrySetResult(PermissionDecision.Deny($"Permission request {id} was cancelled before the operator responded."));
            },
            (completion, toolUseId));

        try
        {
            return await completion.Task.ConfigureAwait(false);
        }
        finally
        {
            _pending.TryRemove(toolUseId, out _);
            _ruleCheckers.TryRemove(toolUseId, out _);
            _earlyDecisions.TryRemove(toolUseId, out _);
            _completed[toolUseId] = 0;
        }
    }

    public bool Resolve(string toolUseId, PermissionDecision decision)
    {
        if (!_pending.TryGetValue(toolUseId, out var completion))
        {
            if (_completed.ContainsKey(toolUseId))
            {
                // A late/duplicate answer for a request that already finished — ignore it.
                _logger.LogWarning("No pending permission request for tool_use_id={ToolUseId} to resolve", toolUseId);
                return false;
            }

            // The request hasn't been registered yet — the operator answered first (#28). Hold the
            // decision so the MCP call picks it up when it arrives, instead of dropping it and hanging.
            _earlyDecisions[toolUseId] = decision;
            _logger.LogInformation(
                "Permission answered for tool_use_id={ToolUseId} before its request arrived — held for the pending MCP call",
                toolUseId);
            return true;
        }

        var resolved = completion.TrySetResult(decision);
        if (resolved)
        {
            _logger.LogInformation("Permission {Outcome} for tool_use_id={ToolUseId}", decision.IsAllowed ? "allowed" : "denied", toolUseId);
        }

        return resolved;
    }

    public void DenyPending(IEnumerable<string> toolUseIds, string reason)
    {
        foreach (var toolUseId in toolUseIds)
        {
            _ruleCheckers.TryRemove(toolUseId, out _);
            _earlyDecisions.TryRemove(toolUseId, out _);
            if (_pending.TryGetValue(toolUseId, out var completion))
            {
                completion.TrySetResult(PermissionDecision.Deny(reason));
            }
        }
    }
}
