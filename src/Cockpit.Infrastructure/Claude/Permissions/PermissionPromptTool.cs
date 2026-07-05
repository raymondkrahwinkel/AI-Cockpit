using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using Cockpit.Core.Abstractions.Claude;
using Cockpit.Core.Claude.Permissions;

namespace Cockpit.Infrastructure.Claude.Permissions;

/// <summary>
/// The single MCP tool the cockpit exposes as <c>mcp__cockpit__permission_prompt</c> and passes
/// to every session via <c>--permission-prompt-tool</c>. The CLI calls it (over HTTP) whenever a
/// tool genuinely needs permission; it hands the request to <see cref="IPermissionCoordinator"/>,
/// waits for the operator's decision, and returns the <c>behavior</c> contract the CLI expects.
/// </summary>
/// <remarks>
/// Argument shape verified against claude.exe 2.1.197: the <c>tools/call</c> arguments are
/// <c>{ tool_name, input, tool_use_id }</c> (no <c>session_id</c>), which the SDK binds to the
/// three parameters below.
/// </remarks>
internal sealed class PermissionPromptTool
{
    private readonly IPermissionCoordinator _coordinator;

    public PermissionPromptTool(IPermissionCoordinator coordinator)
    {
        _coordinator = coordinator;
    }

    [McpServerTool(Name = "permission_prompt")]
    [Description("Asks the cockpit operator to allow or deny a proposed tool call.")]
    public async Task<string> PermissionPromptAsync(
        [Description("The name of the tool Claude wants to run.")] string tool_name,
        [Description("The proposed input for the tool call.")] JsonElement input,
        [Description("The tool_use id correlating this prompt to the session's stream.")] string tool_use_id,
        CancellationToken cancellationToken)
    {
        var proposedInputJson = input.ValueKind == JsonValueKind.Undefined ? "{}" : input.GetRawText();

        var decision = await _coordinator
            .RequestDecisionAsync(tool_use_id, tool_name, proposedInputJson, cancellationToken)
            .ConfigureAwait(false);

        return PermissionPromptResponse.Serialize(decision, proposedInputJson);
    }
}
