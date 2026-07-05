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
        }
    }

    public bool Resolve(string toolUseId, PermissionDecision decision)
    {
        if (!_pending.TryGetValue(toolUseId, out var completion))
        {
            _logger.LogWarning("No pending permission request for tool_use_id={ToolUseId} to resolve", toolUseId);
            return false;
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
            if (_pending.TryGetValue(toolUseId, out var completion))
            {
                completion.TrySetResult(PermissionDecision.Deny(reason));
            }
        }
    }
}
