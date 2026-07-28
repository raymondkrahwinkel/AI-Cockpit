using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using Cockpit.Core.Abstractions.Agents;
using Cockpit.Infrastructure.Mcp;

namespace Cockpit.Infrastructure.Agents;

/// <summary>
/// The <c>cockpit-agents</c> MCP tools (AC-391): the foundation of an agent-to-agent communication line. Today it
/// only carries <c>list_agents</c>, which lets a session see the other agents sharing its own workspace — the
/// desk/tab the operator put it on. Claiming a piece of work and opting in to being woken by another agent are later
/// tickets; this tool already reserves a place for both in its result so a future one only has to fill it in.
/// <para>
/// The workspace is never something an agent names: it is derived, host-side, from the transport-verified pane the
/// request actually came from (<see cref="McpRequestContext.CurrentPaneId"/>), through <see cref="IWorkspaceAgentGateway"/>.
/// <c>list_agents</c> takes no session/pane argument at all — the same defence <c>cockpit-verify</c> uses
/// (<c>VerifyMcpTools.VerifyAsync</c>) — so there is nothing an agent could declare to reach another workspace's
/// roster or stamp the roster with another pane's id. A request that carries no verified pane — the shared
/// app-lifetime key path (the in-process tool loop, or a session <c>McpAuthMiddleware</c> authorized without naming
/// a pane) — is refused outright rather than given something to name instead.
/// </para>
/// </summary>
internal sealed class AgentsMcpTools(IWorkspaceAgentGateway workspaces, IWorkspaceAgentCoordinator coordinator)
{
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = false };

    [McpServerTool(Name = "list_agents")]
    [Description("Lists the other agent sessions sharing your workspace — the tab/desk the operator put you on — so you can see who else is working alongside you. Each entry has the pane id, its name, the profile it runs under, and its statusline (whatever it last set with cockpit-session__set_status). A pane the workspace holds but that has never called a cockpit-agents tool shows enrolled=false with a short note instead of being left off the list — silently missing is worse than visibly not-yet-checked-in. Calling this also enrolls you on the roster, so the next agent to call it sees you. Claims and a wake opt-in are reserved fields for later — empty for now. It runs for the session you call it from — you do not name one.")]
    public async Task<string> ListAgentsAsync()
    {
        try
        {
            // Identity comes only from the transport-verified pane (AC-89) — there is no argument to trust instead,
            // because there is no argument at all. A request with no verified pane cannot be attributed to any
            // session and is refused, exactly as cockpit-verify refuses one.
            if (McpRequestContext.CurrentPaneId is not { } caller)
            {
                return _Serialize(new { ok = false, error = "This request could not be attributed to a session." });
            }

            if (await workspaces.GetWorkspaceSnapshotAsync(caller).ConfigureAwait(false) is not { } snapshot)
            {
                return _Serialize(new { ok = false, error = "This session is not one the cockpit can place in a workspace — list_agents works on an interactive agent session sharing a desk with others." });
            }

            // Calling list_agents is itself the announcement: a pane that asks who else is here is, from this moment,
            // one of the panes the roster knows about.
            coordinator.Enroll(caller);

            var agents = snapshot.Panes.Select(pane =>
            {
                var enrolled = coordinator.IsEnrolled(pane.PaneId);
                return new
                {
                    paneId = pane.PaneId,
                    name = pane.Name,
                    profile = pane.Profile,
                    statusline = pane.Statusline,
                    enrolled,
                    // Deliberately not diagnosed further than this: the roster only ever learns about a pane by that
                    // pane calling in, so a neighbour that has simply never called list_agents yet looks identical to
                    // one whose MCP injection silently failed (AC-156) or that does not have this server mounted at
                    // all — this host has no cheap way to tell those apart from here, and the very first agent to
                    // call list_agents in a workspace will see every one of its (healthy) neighbours this way. Naming
                    // one specific cause would be a diagnosis this cannot actually make.
                    gap = enrolled
                        ? null
                        : "This pane is in the workspace but has never called a cockpit-agents tool. That can mean it simply has not looked yet, that cockpit-agents is not mounted for it, or that the MCP injection failed silently (AC-156) — there is no way to tell which from here. Absence here would look like nothing is wrong; this is the visible alternative.",
                    claims = Array.Empty<object>(),
                    wakeOptIn = (object?)null,
                };
            });

            return _Serialize(new { ok = true, workspaceId = snapshot.WorkspaceId, agents });
        }
        catch (Exception exception)
        {
            // A tool result, never an MCP protocol error: an unexpected failure here (a race on a closing session,
            // say) must not look to the calling CLI like the transport itself broke.
            return _Serialize(new { ok = false, error = exception.Message });
        }
    }

    private static string _Serialize(object value) => JsonSerializer.Serialize(value, SerializerOptions);
}
