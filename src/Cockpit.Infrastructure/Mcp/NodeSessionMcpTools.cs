using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using Cockpit.Core.Abstractions.Assistant;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Abstractions.Profiles;
using Cockpit.Core.Mcp;

namespace Cockpit.Infrastructure.Mcp;

// The `cockpit-node` MCP tools (AC-795): what a paired controller may do with the sessions on this machine — see
// them, start one, stop one. The last sub of AC-742, and the first thing on the node listener a remote caller can
// actually reach.
//
// *Why this is a server of its own and not four more tools somewhere.* Every endpoint AC-790 exposes on the node
// listener already carries tools, and a remote caller can call none of them: they key on the transport-verified
// pane (`McpRequestContext.CurrentPaneId`), a controller has no pane, and so each one fails closed — deliberately,
// per AC-791. That property is worth keeping exactly as it is. So the tools a controller *may* call are a separate
// server whose whole subject is the remote case, rather than an exception carved into tools whose current
// guarantee is that they have none.
//
// *The mount rule, and why it is the inverse of the assistant's.* `AssistantAgentMcpTools` is `Internal` — hosted,
// but reaching only a launch that names it. This one is the mirror image: registered with `IsEnabled` returning
// false, so `CockpitMcpEndpointHost` hosts it (which is what binds the node listener in front of it) while no local
// session is ever told it exists. One endpoint, reachable from the network and from nowhere on this machine.
//
// *And, like there, the mount is not the gate.* `_RefuseIfNotTheController` runs first in every tool and returns
// before any gateway is touched, because a mount is configuration and configuration widens by accident — an
// `IsEnabled` that someone makes conditional, a fan-out that stops reading the flag. What is checked instead is
// the pane `McpAuthMiddleware` stamped from the node's own shared secret, which no argument on any tool can move.
//
// *What stands in for the operator's click.* Nothing here raises a consent card, and that is the point rather than
// an omission. A card would appear on a machine the epic exists precisely so that nobody has to be standing at
// (AC-742: "taken offloaden naar een laptop"), where it would sit unanswered until it timed out — a gate that
// cannot be answered is not a gate, it is an outage. The grant AC-794 put on the pairing is the consent: the node's
// operator ticked these profiles and projects ahead of time, on this machine, and unticking one takes effect on the
// next call rather than the next restart. So every start below is checked against `INodePairingBroker`, which
// answers false for everything while unpaired.
//
// *A session started here outlives the controller.* Raymond's decision on the epic's open question (2026-08-15):
// this is offloading, not remote control. There is therefore no teardown path in this file and none anywhere else
// — a controller that closes, crashes or unpairs leaves what it started running, and `stop_node_agent` is the only
// thing that ends one. `NodeSessionsOutliveTheControllerTests` pins that, because the tempting "tidy up on unpair"
// is exactly the well-meant addition that would silently make it the other thing.
internal sealed class NodeSessionMcpTools(
    IAssistantReadGateway read,
    IAssistantAgentGateway gateway,
    INodePairingBroker pairing,
    ISessionProfileStore profiles)
{
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = false };

    // What a caller that did not come in over the node listener is told. One sentence and no detail, the same
    // posture `AssistantAgentMcpTools` takes: a local session learns that this is not for it, and nothing else.
    private const string NotTheController =
        "This tool belongs to the cockpit that is paired to this one as its controller. It is not available to a session on this machine.";

    [McpServerTool(Name = "list_node_sessions")]
    [Description("Lists the AI sessions running on this node — the machine you are paired to, not your own. IT IS NOT EVERYTHING RUNNING THERE: you see the sessions running under a profile that machine's operator has allowed you, and nothing else, so never report this as \"the node is idle\" — say what you can see. THESE ARE NOT YOUR SESSIONS AND THEIR IDS ARE NOT YOURS: a pane id from this list means nothing to stop_agent, and a pane id from your own list_sessions means nothing to stop_node_agent, so never carry one across. When you tell the operator what is running, say which machine each session is on — two sessions can carry the same name on two machines, and the whole risk here is stopping the one you did not mean.")]
    public async Task<string> ListNodeSessionsAsync()
    {
        try
        {
            if (_RefuseIfNotTheController() is { } refusal)
            {
                return refusal;
            }

            return _Serialize(new
            {
                ok = true,
                node = Environment.MachineName,
                sessions = (await _VisibleSessionsAsync().ConfigureAwait(false)).Select(session => new
                {
                    paneId = session.PaneId,
                    name = session.Name,
                    profile = session.Profile,
                    statusline = session.Statusline,
                    status = session.Status,
                    needsYou = session.NeedsYou,
                }),
            });
        }
        catch (Exception exception)
        {
            return _Serialize(new { ok = false, error = exception.Message });
        }
    }

    [McpServerTool(Name = "list_node_profiles")]
    [Description("Lists the profiles this node's operator has allowed you to run here — never all of them. An empty list is the normal state of a fresh pairing and is not a fault: it means they have not ticked anything in Options → Security on that machine yet, and until they do, start_node_agent refuses everything. Say that rather than reporting the node as broken. WHAT YOU GET IS DELIBERATELY THIN: a label, a provider and the operator's own note. There is no model, no folder, no system prompt and no settings — a profile carries the node operator's own configuration and that is not yours to read.")]
    public async Task<string> ListNodeProfilesAsync()
    {
        try
        {
            if (_RefuseIfNotTheController() is { } refusal)
            {
                return refusal;
            }

            var known = await profiles.LoadAsync().ConfigureAwait(false);

            // `NodeScopedProfileSummary` is AC-794's allow-list of what may cross this boundary, and this is the
            // call it was written for. Constructing it from the three named fields rather than mapping a profile
            // wholesale is the whole of its guarantee: a field added to `SessionProfile` later does not arrive here
            // by default, it has to be put here on purpose, with that type's secrecy test updated to say so.
            return _Serialize(new
            {
                ok = true,
                node = Environment.MachineName,
                profiles = known
                    .Where(profile => pairing.IsProfileAllowed(profile.Label))
                    .Select(profile => new NodeScopedProfileSummary(profile.Label, profile.Provider, profile.Purpose))
                    // Written out field by field rather than serialized as the record: the provider has to cross as
                    // its name and not as whatever number the enum happens to have, or the two machines agree only
                    // for as long as nobody inserts a value into `SessionProvider`.
                    .Select(summary => new { label = summary.Label, provider = summary.Provider.ToString(), purpose = summary.Purpose }),
            });
        }
        catch (Exception exception)
        {
            return _Serialize(new { ok = false, error = exception.Message });
        }
    }

    [McpServerTool(Name = "list_node_projects")]
    [Description("Lists the projects this node's operator has allowed you to start work on here. Same as the profiles: empty is the ordinary state of a fresh pairing, not a failure. Take a project id from here and hand it to start_node_agent to have the session come up with that project's own folder, default profile and settings; an id that is not in this list is refused there.")]
    public async Task<string> ListNodeProjectsAsync()
    {
        try
        {
            if (_RefuseIfNotTheController() is { } refusal)
            {
                return refusal;
            }

            var known = await read.ListProjectsAsync().ConfigureAwait(false);
            return _Serialize(new
            {
                ok = true,
                node = Environment.MachineName,
                projects = known
                    .Where(project => pairing.IsProjectAllowed(project.Id))
                    .Select(project => new { id = project.Id, name = project.Name, description = project.Description }),
            });
        }
        catch (Exception exception)
        {
            return _Serialize(new { ok = false, error = exception.Message });
        }
    }

    [McpServerTool(Name = "start_node_agent")]
    [Description("Starts an AI session on the node — on that machine, under that machine's own account, spending that machine's own budget. THIS IS NOT YOUR COCKPIT: you cannot see the session's screen, and the operator of this cockpit may not be sitting at the other one. Say plainly which machine you are starting something on before you do it, and read the node name back off the result. THE PROFILE MUST BE ONE list_node_profiles REPORTED: anything else is refused, because the node's operator ticked those and only those. THE SAME GOES FOR projectId — take it from list_node_projects or leave it out. YOU DO NOT PICK A DESK OR A FOLDER: the session lands on whatever desk that machine is showing and runs where its profile or project says, so there is nothing here to name and nothing for you to guess. IF YOU WANT TO HEAR HOW IT WENT, ASK FOR IT IN THE prompt — you have no inbox on that machine and it has none on yours, so a session there cannot notify you and you will not be told when it finishes. WHAT YOU START KEEPS RUNNING: closing this cockpit, losing the network or unpairing does not stop it — it goes on spending until somebody stops it, here with stop_node_agent or there by hand. Never describe this as borrowing the machine for a moment.")]
    public async Task<string> StartNodeAgentAsync(
        [Description("The profile to run under, exactly as list_node_profiles reports its label. Required — there is no default, and an unknown or unticked label is refused rather than swapped for something that would run.")] string profile,
        [Description("The project on the node to work on, by its id from list_node_projects. Optional; given, the session comes up with that project's folder, settings and — where you named no profile of your own — its default profile. An id the operator has not allowed is refused.")] string? projectId = null,
        [Description("The first message to hand the session once it is up. Write it as a brief for an agent on another machine that cannot see this conversation and cannot reply to you: it gets this text and nothing else, and there is no route back. Say what to do and what to leave alone.")] string? prompt = null,
        [Description("What to call the pane on the node, so its operator can see what it is. Say what the work is and that it came from here — \"AC-795 tests (from the controller)\" beats the profile name and the clock.")] string? name = null)
    {
        try
        {
            if (_RefuseIfNotTheController() is { } refusal)
            {
                return refusal;
            }

            // Both checks before anything is started, and both against the live grant rather than a copy read at
            // pairing time: unticking a row on the node's Security tab has to stop covering the next call, which is
            // the promise `INodePairingBroker.SetScopeAsync` makes. An unpaired node answers false to both, so a
            // controller whose pairing has just been revoked is refused here as well as at the door.
            //
            // The label is resolved to a real profile *first*, with the same case-insensitive comparison the spawn
            // itself will use, and the grant is then checked against what that resolved to. Checking the string as
            // it arrived would leave a gap wherever two profiles differ only in case: "Foo" is ticked, "foo" is not,
            // and a request naming "Foo" passes a grant check the spawn then answers with either of them.
            if (await _ResolveAllowedProfileAsync(profile).ConfigureAwait(false) is not { } allowedProfile)
            {
                return _Serialize(new
                {
                    ok = false,
                    error = $"This node's operator has not allowed the profile '{profile}'. Call list_node_profiles for the ones they have, and ask them to tick it on that machine if the one you want is missing.",
                });
            }

            if (projectId is { Length: > 0 } project && !pairing.IsProjectAllowed(project))
            {
                return _Serialize(new
                {
                    ok = false,
                    error = $"This node's operator has not allowed the project '{project}'. Call list_node_projects for the ones they have.",
                });
            }

            if (await _ActiveWorkspaceIdAsync().ConfigureAwait(false) is not { } workspaceId)
            {
                return _Serialize(new { ok = false, error = "This node has no desk that can hold a session just now." });
            }

            var result = await gateway.SpawnAsync(new AgentSpawnRequest(
                // The third door on `SpawnTarget`, and the desk it names was read here rather than received: see
                // that type's remarks for why a caller's own workspace id must never reach `NamedByTheAssistant`.
                SpawnTarget.RequestedByThePairedController(workspaceId),
                // The label as this machine spells it, not as the request spelled it — the one the grant was
                // actually checked against.
                allowedProfile,
                projectId,
                prompt,
                // Deliberately no working directory and no provider options over the wire. A path means nothing on
                // a machine whose filesystem this caller has never seen, and the options a session runs under are
                // the node operator's configuration — the grant is over *which* profile, never over what it is.
                WorkingDirectory: null,
                SessionName: name)).ConfigureAwait(false);

            return result.Ok
                ? _Serialize(new
                {
                    ok = true,
                    node = Environment.MachineName,
                    paneId = result.PaneId,
                    name = result.SessionName,
                    resolvedProfile = result.ResolvedProfileLabel,
                    promptDelivered = result.PromptDelivered,
                })
                : _Serialize(new { ok = false, error = result.Error });
        }
        catch (Exception exception)
        {
            return _Serialize(new { ok = false, error = exception.Message });
        }
    }

    [McpServerTool(Name = "stop_node_agent")]
    [Description("Closes a session running on the node, named by its pane id from list_node_sessions. TAKE THE ID FROM THAT LIST AND FROM NOWHERE ELSE: a pane id off your own list_sessions names a session on this machine, and the two lists can hold the same names — read the id and the machine back to the operator before you use it. You can only stop what that list showed you, which is the work running under a profile you were allowed; anything else on that machine is not yours to end and is refused. A refusal is normal (a session that has already ended, one that is not an agent), so read the reason out and carry on. This ends the session for good on that machine; nobody there is asked first, because the operator here was given that authority when the two cockpits were paired.")]
    public async Task<string> StopNodeAgentAsync(
        [Description("The pane id of the session on the node, exactly as list_node_sessions reports it.")] string paneId)
    {
        try
        {
            if (_RefuseIfNotTheController() is { } refusal)
            {
                return refusal;
            }

            // Stopping is bounded by exactly what listing showed, and for the same reason: the grant is what this
            // controller was given, and a session the node's operator started under a profile they never ticked is
            // their work, not this caller's. Without this a fresh pairing with nothing ticked could still end every
            // agent on the machine — able to break everything while allowed to start nothing.
            var visible = await _VisibleSessionsAsync().ConfigureAwait(false);
            if (!visible.Any(session => string.Equals(session.PaneId, paneId, StringComparison.Ordinal)))
            {
                return _Serialize(new
                {
                    ok = false,
                    error = $"There is no session '{paneId}' on this node that you may stop. Call list_node_sessions for the ones you can see; anything else there is running outside what this node's operator allowed you.",
                });
            }

            var result = await gateway.StopAsync(paneId, SpawnCaller.Controller, NodeCallerIdentity.PaneId).ConfigureAwait(false);
            return result.Ok
                ? _Serialize(new { ok = true, node = Environment.MachineName, paneId = result.PaneId, name = result.SessionName })
                : _Serialize(new { ok = false, error = result.Error });
        }
        catch (Exception exception)
        {
            return _Serialize(new { ok = false, error = exception.Message });
        }
    }

    // The sessions this controller may see, which is the same set it may stop: the ones running under a profile
    // its grant covers. One method rather than a filter at each call site, because "what you can see" and "what you
    // can end" drifting apart is precisely the hole this closes.
    //
    // A session whose profile is no longer ticked disappears from the list and stops being stoppable, live — the
    // same posture every other grant check here takes. Sessions the node's operator started themselves are visible
    // when they run under a shared profile, which is deliberate: the grant is over the profile, and two machines
    // sharing one is what "offload to the laptop" means.
    private async Task<IReadOnlyList<AssistantSessionRow>> _VisibleSessionsAsync()
    {
        var sessions = await read.ListSessionsAsync().ConfigureAwait(false);
        return [.. sessions.Where(session => pairing.IsProfileAllowed(session.Profile))];
    }

    // The profile this label names, if the grant covers it — or null, which is the only other answer callers need.
    // Compared the way the spawn path compares (`AssistantAgentGateway`: OrdinalIgnoreCase), so the profile checked
    // here is the profile that would run.
    private async Task<string?> _ResolveAllowedProfileAsync(string label)
    {
        var known = await profiles.LoadAsync().ConfigureAwait(false);
        var match = known.FirstOrDefault(candidate => string.Equals(candidate.Label, label.Trim(), StringComparison.OrdinalIgnoreCase));
        return match is not null && pairing.IsProfileAllowed(match.Label) ? match.Label : null;
    }

    // The desk a controller's session lands on: the one this machine is showing. Derived here and never named by
    // the caller — a controller has never seen this cockpit's desks, so any id it sent would be a guess. Falls back
    // to the first desk that can hold a session, because "the active one" is a workspace type away from being able
    // to (a terminal-only desk is active just as often as any other).
    private async Task<string?> _ActiveWorkspaceIdAsync()
    {
        var workspaces = await gateway.ListWorkspacesAsync().ConfigureAwait(false);
        var usable = workspaces.Where(workspace => workspace.CanHostSessions).ToList();
        return (usable.FirstOrDefault(workspace => workspace.IsActive) ?? usable.FirstOrDefault())?.Id;
    }

    private static string? _RefuseIfNotTheController() =>
        string.Equals(McpRequestContext.CurrentPaneId, NodeCallerIdentity.PaneId, StringComparison.Ordinal)
            ? null
            : JsonSerializer.Serialize(new { ok = false, error = NotTheController }, SerializerOptions);

    private static string _Serialize(object payload) => JsonSerializer.Serialize(payload, SerializerOptions);
}
