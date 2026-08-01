using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using Cockpit.Core.Abstractions.Assistant;
using Cockpit.Core.Assistant;
using Cockpit.Infrastructure.Mcp;

namespace Cockpit.Infrastructure.Assistant;

/// <summary>
/// The <c>cockpit-assistant</c> MCP tools (AC-544): the voice assistant's read path over every session in every
/// workspace. Reading only — acting is <c>[c]</c>'s, and nothing here changes anything.
/// </summary>
/// <remarks>
/// <b>Why this is a separate server and not two more tools on <c>cockpit-agents</c>.</b> That server's tools are
/// workspace-scoped by construction: they derive the caller's desk host-side from the transport-verified pane and
/// refuse a request they cannot place on one. The assistant is placed on no desk at all, so the cheapest way to
/// make it work there would be to relax that derivation — and the derivation is not a filter on one tool, it is the
/// reason no agent anywhere can reach another workspace's roster. Loosening it to serve one privileged caller
/// removes the protection for every other caller at the same time, silently, and it compiles.
/// <para>
/// So the broad reach lives here instead, behind two independent gates:
/// </para>
/// <para>
/// <b>1. It is not handed out.</b> The endpoint is registered <c>Internal</c> (AC-204), which keeps it out of every
/// user-facing MCP picker <em>and</em> out of the no-selection fan-out, so it reaches only a launch that names it —
/// and the assistant's own start is the one place in the codebase that does.
/// </para>
/// <para>
/// <b>2. It is not answered.</b> Every tool here refuses any caller whose verified pane is not
/// <see cref="AssistantIdentity.PaneId"/>. That is the gate that actually holds, and it is the one worth having:
/// the mount is a fact about configuration, and configuration is exactly the kind of thing that widens later by
/// accident — an endpoint made non-internal, a profile that names the server, a spawn path that copies a selection
/// it did not read. When that happens the tools are in a session's context and still answer nobody. The pane is
/// stamped by <see cref="McpAuthMiddleware"/> from the request's own per-session bearer and no argument on any tool
/// here can move it, so "I am the assistant" is not a sentence a session can say.
/// </para>
/// <para>
/// <b>Where that stops</b> is where AC-89's per-session tokens stop, and no further: every session runs as the same
/// OS user, so an agent with a shell can read a neighbour's <c>COCKPIT_MCP_KEY</c> out of its environment and send
/// as it. That is a property the whole cockpit shares — the consent broker and the agent line included — and it is
/// not fixable from here. What this design buys is that reaching these tools takes deliberate theft off the
/// filesystem rather than a tool argument or an unticked checkbox.
/// </para>
/// </remarks>
internal sealed class AssistantReadMcpTools(IAssistantReadGateway gateway)
{
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = false };

    /// <summary>
    /// What a caller that is not the assistant is told. One sentence, and no detail about what it would have got:
    /// the refusal is the whole answer, and there is nothing here for an ordinary session to learn from.
    /// </summary>
    private const string NotTheAssistant =
        "This tool is the cockpit assistant's own. It is not available to an agent session.";

    [McpServerTool(Name = "list_sessions")]
    [Description("Lists every AI session the cockpit is running right now, across all workspaces — not just one desk. Each entry has the pane id, the session's name, the profile it runs under, the workspace it sits on (id and the tab label the operator sees), and its statusline: whatever that session last set for itself with cockpit-session__set_status. Use it to answer questions like \"what is the status of AC-223\" or \"what is everyone working on\". IMPORTANT about what a statusline is and is not: it is a convention, not a record. A session says what it is working on because it was asked to, so a statusline mentioning a ticket is good evidence that session is on it — but a ticket appearing nowhere means only that no running session has written that ticket into its own status line. It does NOT mean nobody is working on it: a session may never have set a status, may have set a stale one, or may be doing the work under a different description. Report the difference rather than turning an absence of evidence into an answer.")]
    public async Task<string> ListSessionsAsync()
    {
        try
        {
            if (_RefuseIfNotTheAssistant() is { } refusal)
            {
                return refusal;
            }

            var sessions = await gateway.ListSessionsAsync().ConfigureAwait(false);
            return _Serialize(new
            {
                ok = true,
                count = sessions.Count,
                sessions = sessions.Select(session => new
                {
                    paneId = session.PaneId,
                    name = session.Name,
                    profile = session.Profile,
                    workspaceId = session.WorkspaceId,
                    workspaceName = session.WorkspaceName,
                    statusline = session.Statusline,
                    // Said in the row rather than left for the reader to infer from an empty string. An empty
                    // statusline and a session working quietly look identical from here, and the field that says so
                    // is cheaper than the mistake it prevents.
                    hasStatusline = session.Statusline.Length > 0,
                }),
            });
        }
        catch (Exception exception)
        {
            // A tool result, never an MCP protocol error — the same choice cockpit-agents makes, so an unexpected
            // failure here does not look to the assistant's runtime like the transport itself broke.
            return _Serialize(new { ok = false, error = exception.Message });
        }
    }

    /// <summary>
    /// The gate, in one place so every tool on this server is covered by the same sentence rather than by its own
    /// copy of it. Returns the refusal to hand straight back, or null when the caller really is the assistant.
    /// </summary>
    /// <remarks>
    /// A request with no verified pane is refused too, and not because it might be an impostor: it is the shared
    /// app-lifetime key path (the in-process tool loop), which cannot be attributed to any session at all. There is
    /// no identity to check, so there is no way to establish this one — and the safe answer to "I cannot tell who
    /// this is" on a tool that reads every workspace is no.
    /// </remarks>
    private static string? _RefuseIfNotTheAssistant() =>
        string.Equals(McpRequestContext.CurrentPaneId, AssistantIdentity.PaneId, StringComparison.Ordinal)
            ? null
            : _Serialize(new { ok = false, error = NotTheAssistant });

    private static string _Serialize(object value) => JsonSerializer.Serialize(value, SerializerOptions);
}
