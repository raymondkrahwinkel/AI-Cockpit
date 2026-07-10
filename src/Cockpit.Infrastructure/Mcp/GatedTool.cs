using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Cockpit.Infrastructure.Mcp;

/// <summary>
/// Wraps an MCP tool (any <see cref="AIFunction"/>) so it is only executed after the operator approves it
/// (#26). On invocation it asks the <see cref="IToolApprovalGate"/> — which raises the session's
/// PermissionRequested flow and awaits the decision — and runs the underlying tool only on approval; a
/// denial becomes the tool result rather than an execution. This keeps the agentic
/// <c>UseFunctionInvocation</c> loop intact while gating every call, mirroring the Claude permission flow.
/// </summary>
internal sealed class GatedTool(AIFunction inner, IToolApprovalGate gate) : DelegatingAIFunction(inner)
{
    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        var toolUseId = Guid.NewGuid().ToString("N");
        var inputJson = _SerializeArguments(arguments);

        var approved = await gate.RequestApprovalAsync(toolUseId, Name, inputJson, cancellationToken).ConfigureAwait(false);
        if (!approved)
        {
            const string refusal = "Tool call was denied by the user.";
            gate.ReportToolResult(toolUseId, refusal, isError: true);
            return refusal;
        }

        try
        {
            var result = await base.InvokeCoreAsync(arguments, cancellationToken).ConfigureAwait(false);
            gate.ReportToolResult(toolUseId, result?.ToString() ?? string.Empty, isError: false);
            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Return the failure as the tool's result rather than rethrowing: a single tool error (a bad
            // path, an unreachable server) must not abort the whole turn — the model should see the error and
            // be able to recover or explain it, exactly as it would a normal tool result.
            var message = $"Tool call failed: {ex.Message}";
            gate.ReportToolResult(toolUseId, message, isError: true);
            return message;
        }
    }

    private static string _SerializeArguments(AIFunctionArguments arguments)
    {
        try
        {
            return JsonSerializer.Serialize(arguments.ToDictionary(pair => pair.Key, pair => pair.Value));
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            return "{}";
        }
    }
}
