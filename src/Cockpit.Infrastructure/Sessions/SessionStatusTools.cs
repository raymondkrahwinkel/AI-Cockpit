using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Infrastructure.Mcp;

namespace Cockpit.Infrastructure.Sessions;

/// <summary>
/// The MCP tools a session uses to say what it is working on (#AC-13), exposed as <c>mcp__cockpit-session__*</c>.
/// Deliberately its own server, separate from the orchestrator: setting your own status is a capability every
/// session should have — including a delegated sub-agent, which is denied the orchestrator (delegation) tools to
/// stop it delegating further, yet still needs to report what it is doing. Thin by design: it only routes to
/// <see cref="ISessionLabelSink"/>, which the App implements over its session view-models.
/// <para>
/// Status and name travel on one tool because they are one act — "I picked this up" — and because the workflow node
/// that does the same job (<c>cockpit.set-status</c>) already carries both. They are not equally binding: the
/// statusline is the agent's to write, the name is only proposed (#AC-312).
/// </para>
/// </summary>
internal sealed class SessionStatusTools
{
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = false };

    private readonly ISessionLabelSink _labels;

    public SessionStatusTools(ISessionLabelSink labels)
    {
        _labels = labels;
    }

    [McpServerTool(Name = "set_status")]
    [Description("Sets your session's statusline — the short line shown under the session's name in the cockpit (its header and the sidebar), saying what you are working on right now: a ticket you picked up ('AC-13'), a phase, whatever the operator would want to see at a glance across their sessions. Pass the value of the COCKPIT_PANE_ID environment variable in this session as `session`, so the status lands on your own session and not another. An empty status clears the line. Optionally propose a `name` for the session too. Set it when you pick up a piece of work, and update or clear it as you move on.")]
    public async Task<string> SetStatusAsync(
        [Description("Your session id — the value of the COCKPIT_PANE_ID environment variable in this session.")] string session,
        [Description("The status to show, e.g. 'AC-13' or 'reviewing the diff'. An empty string clears it.")] string status,
        [Description("Optional. A name to propose for this session — the ticket you just picked up, say. It is taken only while the session still carries a name the cockpit made up for it; a name the operator gave it stays, and the reply says so with `renamed: false`. Leave it out to keep the current name.")] string? name = null)
    {
        // Key on the transport-verified pane (AC-89/AC-128), not the agent-declared `session`: an agent must not be
        // able to spoof or clear another session's statusline by naming its id (confused deputy). Falls back to
        // `session` off the verified path (the in-process tool loop / tests), where there is no middleware to trust.
        var caller = McpRequestContext.CurrentPaneId ?? session;
        var applied = await _labels.SetStatuslineAsync(caller, status ?? string.Empty);
        if (!applied)
        {
            return JsonSerializer.Serialize(
                new { ok = false, error = "No session matched that id — pass the COCKPIT_PANE_ID from this session's own environment as `session`." },
                SerializerOptions);
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            return JsonSerializer.Serialize(new { ok = true, status = status ?? string.Empty }, SerializerOptions);
        }

        // The same verified pane, so a name cannot be pushed onto a session an agent merely names either.
        var renamed = await _labels.SuggestNameAsync(caller, name);
        return JsonSerializer.Serialize(new { ok = true, status = status ?? string.Empty, renamed }, SerializerOptions);
    }
}
