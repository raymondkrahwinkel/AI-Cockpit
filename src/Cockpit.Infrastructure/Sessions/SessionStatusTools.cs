using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using Cockpit.Core.Abstractions.Sessions;

namespace Cockpit.Infrastructure.Sessions;

/// <summary>
/// The MCP tools a session uses to say what it is working on (#AC-13), exposed as <c>mcp__cockpit-session__*</c>.
/// Deliberately its own server, separate from the orchestrator: setting your own status is a capability every
/// session should have — including a delegated sub-agent, which is denied the orchestrator (delegation) tools to
/// stop it delegating further, yet still needs to report what it is doing. Thin by design: it only routes to
/// <see cref="ISessionStatuslineSink"/>, which the App implements over its session view-models.
/// </summary>
internal sealed class SessionStatusTools
{
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = false };

    private readonly ISessionStatuslineSink _statuslineSink;

    public SessionStatusTools(ISessionStatuslineSink statuslineSink)
    {
        _statuslineSink = statuslineSink;
    }

    [McpServerTool(Name = "set_status")]
    [Description("Sets your session's statusline — the short line shown under the session's name in the cockpit (its header and the sidebar), saying what you are working on right now: a ticket you picked up ('AC-13'), a phase, whatever the operator would want to see at a glance across their sessions. Pass the value of the COCKPIT_PANE_ID environment variable in this session as `session`, so the status lands on your own session and not another. An empty status clears the line. Set it when you pick up a piece of work, and update or clear it as you move on.")]
    public async Task<string> SetStatusAsync(
        [Description("Your session id — the value of the COCKPIT_PANE_ID environment variable in this session.")] string session,
        [Description("The status to show, e.g. 'AC-13' or 'reviewing the diff'. An empty string clears it.")] string status)
    {
        var applied = await _statuslineSink.SetStatuslineAsync(session, status ?? string.Empty);
        return applied
            ? JsonSerializer.Serialize(new { ok = true, status = status ?? string.Empty }, SerializerOptions)
            : JsonSerializer.Serialize(new { ok = false, error = "No session matched that id — pass the COCKPIT_PANE_ID from this session's own environment as `session`." }, SerializerOptions);
    }
}
