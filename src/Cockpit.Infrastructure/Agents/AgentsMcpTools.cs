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
/// An agent in workspace X therefore cannot see or affect workspace Y's roster by declaring a different <c>session</c>
/// — the same confused-deputy defence <c>cockpit-verify</c> and <c>cockpit-terminal</c> already rely on.
/// </para>
/// </summary>
internal sealed class AgentsMcpTools(IWorkspaceAgentGateway workspaces, IWorkspaceAgentCoordinator coordinator)
{
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = false };

    [McpServerTool(Name = "list_agents")]
    [Description("Lists the other agent sessions sharing your workspace — the tab/desk the operator put you on — so you can see who else is working alongside you. Each entry has the pane id, its name, the profile it runs under, and its statusline (whatever it last set with cockpit-session__set_status). A pane the workspace holds but that has never called a cockpit-agents tool shows enrolled=false with a short reason instead of being left off the list — silently missing is worse than visibly not-yet-checked-in. Calling this also enrolls you on the roster, so the next agent to call it sees you. Claims and a wake opt-in are reserved fields for later — empty for now.")]
    public string ListAgents(
        [Description("Your session id — the value of the COCKPIT_PANE_ID environment variable in this session.")] string session)
    {
        // Identity comes from the transport-verified pane (AC-89), never from the agent-declared `session`: an agent
        // must not be able to see another workspace's roster, or stamp the roster with another pane's id, by naming
        // it in the argument. `session` is only the fallback for the in-process tool loop and tests, which run off
        // that verified path entirely.
        var caller = McpRequestContext.CurrentPaneId ?? session;

        if (workspaces.GetWorkspaceSnapshot(caller) is not { } snapshot)
        {
            return _Serialize(new { ok = false, error = "This request could not be attributed to a live session in a workspace." });
        }

        // Calling list_agents is itself the announcement: a pane that asks who else is here is, from this moment,
        // one of the panes the roster knows about.
        coordinator.Enroll(snapshot.WorkspaceId, caller);

        var agents = snapshot.Panes.Select(pane =>
        {
            var enrolled = coordinator.IsEnrolled(snapshot.WorkspaceId, pane.PaneId);
            return new
            {
                paneId = pane.PaneId,
                name = pane.Name,
                profile = pane.Profile,
                statusline = pane.Statusline,
                enrolled,
                gap = enrolled
                    ? null
                    : "This pane is in the workspace but has never called a cockpit-agents tool — it may not have this server mounted, or the MCP injection failed silently (AC-156). Absence here would look like nothing is wrong; this is the visible alternative.",
                claims = Array.Empty<object>(),
                wakeOptIn = (object?)null,
            };
        });

        return _Serialize(new { ok = true, workspaceId = snapshot.WorkspaceId, agents });
    }

    private static string _Serialize(object value) => JsonSerializer.Serialize(value, SerializerOptions);
}
