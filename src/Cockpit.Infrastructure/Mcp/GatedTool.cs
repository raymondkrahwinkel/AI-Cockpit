using System.Text.Json;
using Microsoft.Extensions.AI;
using Cockpit.Infrastructure.Formatting;

namespace Cockpit.Infrastructure.Mcp;

// #26: wraps an MCP tool so it only runs after the operator approves it via IToolApprovalGate; a denial becomes
// the tool result instead of an execution.
internal sealed class GatedTool(AIFunction inner, IToolApprovalGate gate) : DelegatingAIFunction(inner)
{
    // [measured, 2026-09-02] Of 9,970 persisted tool-result rows, 446 exceeded 16k but only 8 exceeded 131k
    // (seven shell, one read_many). This prevents one pathological result filling a context window; it cannot
    // protect a conversation that was already nearly full.
    private const int MaxResultLength = 131_072;

    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        var toolUseId = Guid.NewGuid().ToString("N");
        var inputJson = _SerializeArguments(arguments);

        var approval = await gate.RequestApprovalAsync(toolUseId, Name, inputJson, cancellationToken).ConfigureAwait(false);
        if (!approval.Approved)
        {
            // The gate owns the reason: an operator's deny is generic, a delegated policy deny explains the ceiling
            // so the model can adapt instead of retrying. Reported once, here, for both the UI and the model.
            var refusal = approval.DenyReason ?? "Tool call was denied.";
            return _ReportResult(toolUseId, refusal, isError: true);
        }

        try
        {
            var result = await base.InvokeCoreAsync(arguments, cancellationToken).ConfigureAwait(false);
            var content = result?.ToString() ?? string.Empty;
            var visibleContent = _ReportResult(toolUseId, content, isError: false);
            return visibleContent == content ? result : visibleContent;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Return the failure as the tool's result rather than rethrowing: a single tool error (a bad
            // path, an unreachable server) must not abort the whole turn — the model should see the error and
            // be able to recover or explain it, exactly as it would a normal tool result.
            var message = $"Tool call failed: {ex.Message}";
            return _ReportResult(toolUseId, message, isError: true);
        }
    }

    private string _ReportResult(string toolUseId, string content, bool isError)
    {
        var visibleContent = BoundedText.ToolResult(content, MaxResultLength);
        gate.ReportToolResult(toolUseId, visibleContent, isError);
        return visibleContent;
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
