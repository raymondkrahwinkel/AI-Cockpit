using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using Cockpit.Core.Abstractions.Agents;
using Cockpit.Core.Abstractions.Assistant;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Abstractions.Profiles;
using Cockpit.Core.Assistant;
using Cockpit.Core.Mcp;
using Cockpit.Core.Profiles;
using Cockpit.Infrastructure.Assistant;

namespace Cockpit.Infrastructure.Mcp;

// AC-795 (last sub of AC-742): the cockpit-node MCP tools — see, start, stop sessions on this machine. No
// consent card (an unattended laptop) — AC-794's pairing grant is the consent; a session outlives the
// controller by design (2026-08-15), only stop_node_agent ends one.
internal sealed class NodeSessionMcpTools(
    IAssistantReadGateway read,
    IAssistantAgentGateway gateway,
    INodePairingBroker pairing,
    ISessionProfileStore profiles,
    NodeDiscoveryId discoveryId,
    IAgentMessageInbox inbox,
    // AC-1329: this machine's own memory — the same behaviour/machine split its own assistant reads at every
    // start. Never merged with the controller's: what these two tools hand over is read there and let go, per
    // Raymond's rule that a controller's memory must not be scrambled by the memory of the machine it is working on.
    IAssistantMemory memory,
    // AC-1351: null where nothing verifies connect keys (a test's own host), and then the key tools refuse.
    ConnectKeyVerifier? connectKeys = null)
{
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = false };

    // AC-1322: the most one poll hands over — the same bound `read_inbox` puts on a local read, for the same
    // reason: the recipient's context is the recipient's to spend.
    internal const int MaxMessagesPerRead = 25;

    // What a caller that did not come in over the node listener is told. One sentence and no detail, the same
    // posture `AssistantAgentMcpTools` takes: a local session learns that this is not for it, and nothing else.
    private const string NotTheController =
        "This tool belongs to the cockpit that is paired to this one as its controller. It is not available to a session on this machine.";

    private const string NoConnectKeys = "Connect keys are not available on this node.";

    internal const string AdminRefusal = "Managing connect keys needs a connect key with the admin capability.";

    internal const string PermissionsRefusal = "This connect key may not answer permission prompts: it does not have the mayAnswerPermissions grant.";

    private const string ProfilesParameter = "The profile labels the key may use. Leave out for all of them, including ones added later.";

    private const string ProjectsParameter = "The project ids the key may use. Leave out for all of them, including ones added later.";

    private const string BypassParameter = "Whether the key may start a profile that skips its approvals. Defaults to false.";

    private const string PermissionsParameter = "Whether the key may answer a session's permission prompts. Defaults to true.";

    [McpServerTool(Name = "list_node_sessions", ReadOnly = true)]
    [Description("Lists the AI sessions running on this node — the machine you are paired to, not your own. IT IS NOT EVERYTHING RUNNING THERE: you see the sessions running under a profile that machine's operator has allowed you, and nothing else, so never report this as \"the node is idle\" — say what you can see. THESE ARE NOT YOUR SESSIONS AND THEIR IDS ARE NOT YOURS: a pane id from this list means nothing to stop_agent, and a pane id from your own list_sessions means nothing to stop_node_agent, so never carry one across. When you tell the operator what is running, say which machine each session is on — two sessions can carry the same name on two machines, and the whole risk here is stopping the one you did not mean. hasOutstandingWork can be true under any status: it means something of that session's own — a backgrounded shell such as a build or a test run — is still going even though the session stopped talking, because that work deliberately does not hold the status. A session reading Idle or Done with hasOutstandingWork true is not finished; say so. pendingPermissions lists the Allow/Deny questions a session is stopped on; they are for the operator's own screen on the controller, never for you to answer.")]
    public async Task<string> ListNodeSessionsAsync()
    {
        try
        {
            if (_RefuseIfNotTheController() is { } refusal)
            {
                return refusal;
            }

            var visible = await _VisibleSessionsAsync().ConfigureAwait(false);
            // AC-1324: the open questions ride on the same read as the sessions they belong to, so needsYou and the
            // rows the controller draws for it can never come from two different moments.
            var pending = (await read.ListPendingPermissionsAsync().ConfigureAwait(false)).ToLookup(permission => permission.PaneId, StringComparer.Ordinal);
            return _Serialize(new
            {
                ok = true,
                node = Environment.MachineName,
                // AC-1320: the origin the controller's assistant stamps on every row it lists from here — a name
                // can be shared by two machines, this id cannot.
                discoveryId = discoveryId.Value,
                sessions = visible.Select(session => new
                {
                    paneId = session.PaneId,
                    name = session.Name,
                    profile = session.Profile,
                    statusline = session.Statusline,
                    status = session.Status,
                    needsYou = session.NeedsYou,
                    // AC-1311: the second wire copy of this field — easiest one to forget, since it lives
                    // beside the assistant's own list_sessions rather than in it.
                    hasOutstandingWork = session.HasOutstandingWork,
                    pendingPermissions = pending[session.PaneId].Select(permission => new
                    {
                        toolUseId = permission.ToolUseId,
                        tool = permission.ToolName,
                        input = AssistantReadMcpTools.Bounded(permission.InputJson).Text,
                        sinceUtc = permission.SinceUtc,
                    }),
                }),
            });
        }
        catch (Exception exception)
        {
            return _Serialize(new { ok = false, error = exception.Message });
        }
    }

    [McpServerTool(Name = "list_node_profiles", ReadOnly = true)]
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

            // AC-794's allow-list of what may cross this boundary — built from three named fields rather than
            // mapping a profile wholesale, so a field added to SessionProfile later doesn't arrive here by default.
            return _Serialize(new
            {
                ok = true,
                node = Environment.MachineName,
                // AC-1367: a bypass profile this caller may not start is not shown either — seeing is starting.
                profiles = known
                    .Where(profile => _IsProfileAllowed(profile.Label) && (_Caller().MayStartBypass || !UnsupervisedProfile.SkipsApprovals(profile.Defaults)))
                    .Select(profile => new NodeScopedProfileSummary(profile.Label, profile.Provider, profile.Purpose, UnsupervisedProfile.SkipsApprovals(profile.Defaults)))
                    // Written out field by field rather than serialized as the record: the provider has to cross as
                    // its name and not as whatever number the enum happens to have, or the two machines agree only
                    // for as long as nobody inserts a value into `SessionProvider`.
                    .Select(summary => new { label = summary.Label, provider = summary.Provider.ToString(), purpose = summary.Purpose, skipsApprovals = summary.SkipsApprovals }),
            });
        }
        catch (Exception exception)
        {
            return _Serialize(new { ok = false, error = exception.Message });
        }
    }

    [McpServerTool(Name = "list_node_projects", ReadOnly = true)]
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
                    .Where(project => _IsProjectAllowed(project.Id))
                    .Select(project => new { id = project.Id, name = project.Name, description = project.Description }),
            });
        }
        catch (Exception exception)
        {
            return _Serialize(new { ok = false, error = exception.Message });
        }
    }

    [McpServerTool(Name = "read_node_inbox", ReadOnly = false, Destructive = false)]
    [Description("Collects the messages agents on this node sent to their assistant while you were its controller — those reach you, not the assistant here. Pass the id of the last message you already hold as afterMessageId: everything up to and including it is dropped on the node, and what follows comes back, oldest first. Leave it out to read from the start. A read that fails on your side drops nothing here, so the next one with the same id gets the same messages again — that is how nothing is lost and nothing is doubled. At most 25 per call; `remaining` says how many still wait. The sender's pane id is this machine's, not yours: reply to it with send_node_message, not notify.")]
    public Task<string> ReadNodeInboxAsync(
        [Description("The id of the last message you already collected from this node, or null to read from the start. The node drops everything up to and including it before answering.")] string? afterMessageId = null)
    {
        try
        {
            if (_RefuseIfNotTheController() is { } refusal)
            {
                return Task.FromResult(refusal);
            }

            // A look, not a take: what was already held stays until the cursor says it arrived. Taking it in flight
            // and returning it is the peek the inbox interface does not offer directly.
            var held = inbox.TakeForDelivery(AssistantIdentity.ControllerInboxPaneId, MaxMessagesPerRead);
            var acknowledged = afterMessageId is null
                ? -1
                : held.Messages.ToList().FindIndex(message => string.Equals(message.Id, afterMessageId, StringComparison.Ordinal));
            var ids = held.Messages.Select(message => message.Id).ToList();
            inbox.ConfirmDelivered(AssistantIdentity.ControllerInboxPaneId, ids[..(acknowledged + 1)]);
            inbox.ReturnUndelivered(AssistantIdentity.ControllerInboxPaneId, ids[(acknowledged + 1)..]);

            return Task.FromResult(_Serialize(new
            {
                ok = true,
                node = Environment.MachineName,
                discoveryId = discoveryId.Value,
                messages = held.Messages.Skip(acknowledged + 1).Select(message => new
                {
                    id = message.Id,
                    fromPaneId = message.FromPaneId,
                    kind = message.Kind,
                    body = message.Body,
                    sentAtUtc = message.SentAtUtc,
                }),
                remaining = held.Remaining,
            }));
        }
        catch (Exception exception)
        {
            return Task.FromResult(_Serialize(new { ok = false, error = exception.Message }));
        }
    }

    [McpServerTool(Name = "read_node_memory", ReadOnly = true)]
    [Description("Reads one of THIS machine's own memory files for its controller — never a pane, so the only gate is being the controller at all. \"behaviour\" is the rule set this machine's own operator gave its own assistant; \"machine\" is what this machine's own assistant has learned about itself by doing — a path, a quirk, a command that failed here. Raymond's rule for why this exists: the controller needs to see this while it is steering work here, but what it reads must never travel back into its own memory — that would be exactly the scrambling he ruled out (its memory travels with it, stays its own, and is never merged with a laptop's). scope is required and must be \"behaviour\" or \"machine\"; anything else is refused.")]
    public async Task<string> ReadNodeMemoryAsync(
        [Description("Either \"behaviour\" or \"machine\".")] string scope)
    {
        try
        {
            if (_RefuseIfNotTheController() is { } refusal)
            {
                return refusal;
            }

            if (_ParseMemoryScope(scope) is not { } memoryScope)
            {
                return _ScopeRefusal();
            }

            var text = await memory.ReadAsync(memoryScope).ConfigureAwait(false);
            return _Serialize(new { ok = true, node = Environment.MachineName, scope, text });
        }
        catch (Exception exception)
        {
            return _Serialize(new { ok = false, error = exception.Message });
        }
    }

    [McpServerTool(Name = "remember_on_node", ReadOnly = false, Destructive = false)]
    [Description("Appends one fact to THIS machine's own memory — the same file its own assistant reads at every start — in the scope named. USE THIS FOR MACHINE KNOWLEDGE LEARNED WHILE WORKING HERE (a path, a quirk, a command that only fails on this box): the controller's own remember tool already reaches this tool by itself for a behaviour rule, since Raymond's rule is that anything not machine/OS-specific goes on every memory at once, and only what is specific to a machine is written to that machine alone. scope is required and must be \"behaviour\" or \"machine\"; anything else is refused and nothing is written.")]
    public async Task<string> RememberOnNodeAsync(
        [Description("The one thing to remember, as a full sentence that will still make sense with no conversation around it.")] string text,
        [Description("Either \"behaviour\" or \"machine\" — which of this machine's own memory files the line is appended to.")] string scope)
    {
        try
        {
            if (_RefuseIfNotTheController() is { } refusal)
            {
                return refusal;
            }

            if (_ParseMemoryScope(scope) is not { } memoryScope)
            {
                return _ScopeRefusal();
            }

            await memory.RememberAsync(text, memoryScope).ConfigureAwait(false);
            return _Serialize(new { ok = true, node = Environment.MachineName, remembered = text.Trim(), scope });
        }
        catch (Exception exception)
        {
            return _Serialize(new { ok = false, error = exception.Message });
        }
    }

    [McpServerTool(Name = "start_node_agent", ReadOnly = false, Destructive = false)]
    [Description("Starts an AI session on the node — on that machine, under that machine's own account, spending that machine's own budget. THIS IS NOT YOUR COCKPIT: you cannot see the session's screen, and the operator of this cockpit may not be sitting at the other one. Say plainly which machine you are starting something on before you do it, and read the node name back off the result. THE PROFILE MUST BE ONE list_node_profiles REPORTED: anything else is refused, because the node's operator ticked those and only those. THE SAME GOES FOR projectId — take it from list_node_projects or leave it out. YOU DO NOT PICK A DESK OR A FOLDER: the session lands on whatever desk that machine is showing and runs where its profile or project says, so there is nothing here to name and nothing for you to guess. IF YOU WANT TO HEAR HOW IT WENT, ASK FOR IT IN THE prompt: a session there reaches you only by notifying its own assistant (cockpit-assistant), which is queued for you while you hold the line and arrives with your inbox as `<node> · <paneId>` — and only from a profile the node's operator shared. Nothing tells you it finished unless it says so itself. WHAT YOU START KEEPS RUNNING: closing this cockpit, losing the network or unpairing does not stop it — it goes on spending until somebody stops it, here with stop_node_agent or there by hand. Never describe this as borrowing the machine for a moment.")]
    public async Task<string> StartNodeAgentAsync(
        [Description("The profile to run under, exactly as list_node_profiles reports its label. Required — there is no default, and an unknown or unticked label is refused rather than swapped for something that would run.")] string profile,
        [Description("The project on the node to work on, by its id from list_node_projects. Optional; given, the session comes up with that project's folder, settings and — where you named no profile of your own — its default profile. An id the operator has not allowed is refused.")] string? projectId = null,
        [Description("The first message to hand the session once it is up. Write it as a brief for an agent on another machine that cannot see this conversation: it gets this text and nothing else, and the only route back is a notify to its own assistant. Say what to do, what to leave alone, and whether to report.")] string? prompt = null,
        [Description("What to call the pane on the node, so its operator can see what it is. Say what the work is and that it came from here — \"AC-795 tests (from the controller)\" beats the profile name and the clock.")] string? name = null)
    {
        try
        {
            if (_RefuseIfNotTheController() is { } refusal)
            {
                return refusal;
            }

            // Both checks against the live grant, not a copy read at pairing time, so unticking a row takes
            // effect on the next call. The label is resolved to a real profile first, with the same
            // case-insensitive comparison the spawn uses, so "Foo"/"foo" can't pass a grant check on the wrong one.
            if (await _ResolveAllowedProfileAsync(profile).ConfigureAwait(false) is not { } allowedProfile)
            {
                return _Serialize(new
                {
                    ok = false,
                    error = _Caller().ByConnectKey
                        ? $"The scope of this connect key does not include the profile '{profile}'. Call list_node_profiles for the ones it does."
                        : $"This node's operator has not allowed the profile '{profile}'. Call list_node_profiles for the ones they have, and ask them to tick it on that machine if the one you want is missing.",
                });
            }

            // AC-1367: in scope is not enough for a profile that skips its approvals — that takes its own grant.
            if (!_Caller().MayStartBypass && UnsupervisedProfile.SkipsApprovals(allowedProfile.Defaults))
            {
                return _Serialize(new
                {
                    ok = false,
                    error = $"The profile '{allowedProfile.Label}' skips its approvals, and this connect key does not have the mayStartBypassProfiles grant to start such a profile.",
                });
            }

            if (projectId is { Length: > 0 } project && !_IsProjectAllowed(project))
            {
                return _Serialize(new
                {
                    ok = false,
                    error = _Caller().ByConnectKey
                        ? $"The scope of this connect key does not include the project '{project}'. Call list_node_projects for the ones it does."
                        : $"This node's operator has not allowed the project '{project}'. Call list_node_projects for the ones they have.",
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
                allowedProfile.Label,
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

    [McpServerTool(Name = "stop_node_agent", ReadOnly = false, Destructive = true)]
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

            // Stopping is bounded by exactly what listing showed — a session under an unticked profile is the
            // node operator's work, not this caller's, else a fresh empty pairing could still end every agent.
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

    [McpServerTool(Name = "send_node_prompt", ReadOnly = false, Destructive = true)]
    [Description("Hands a session on the node a turn: the text goes into that session and is SENT, so its agent starts on it straight away — what send_prompt does on your own machine. The pane id is the node's, from list_node_sessions; you can only reach what that list showed you, which is the work running under a profile you were allowed, and anything else is refused. Nobody on the node is asked first: the operator there gave you this authority when the two cockpits were paired, so read the prompt back to your own operator before you send it. `delivered` false means the session is still coming up and holds the turn — do not send it again.")]
    public async Task<string> SendNodePromptAsync(
        [Description("The pane id of the session on the node, exactly as list_node_sessions reports it.")] string paneId,
        [Description("The turn to submit, in the exact words that will be sent.")] string prompt)
    {
        try
        {
            if ((_RefuseIfNotTheController() ?? await _RefuseIfNotVisibleAsync(paneId).ConfigureAwait(false)) is { } refusal)
            {
                return refusal;
            }

            var result = await gateway.SendPromptAsync(paneId, prompt).ConfigureAwait(false);
            return result.Ok
                ? _Serialize(new { ok = true, node = Environment.MachineName, paneId = result.PaneId, name = result.SessionName, result.Delivered })
                : _Serialize(new { ok = false, error = result.Error });
        }
        catch (Exception exception)
        {
            return _Serialize(new { ok = false, error = exception.Message });
        }
    }

    [McpServerTool(Name = "send_node_message", ReadOnly = false, Destructive = false)]
    [Description("Leaves a message in the inbox of a session on the node, as this machine's assistant — while you control this node, that is you, so the agent's reply (notify cockpit-assistant) comes back to you through read_node_inbox. The pane id is the node's, from list_node_sessions; only a session that list showed you can be written to. This tells the agent something; it does not make it do anything — use send_node_prompt for that.")]
    public async Task<string> SendNodeMessageAsync(
        [Description("The pane id of the session on the node, exactly as list_node_sessions reports it.")] string paneId,
        [Description("A short label for what this is, at most 100 characters.")] string kind,
        [Description("The message itself, at most 2000 characters.")] string body)
    {
        try
        {
            if ((_RefuseIfNotTheController() ?? await _RefuseIfNotVisibleAsync(paneId).ConfigureAwait(false)) is { } refusal)
            {
                return refusal;
            }

            var result = await gateway.SendMessageAsync(paneId, kind, body).ConfigureAwait(false);
            return result.Ok
                ? _Serialize(new
                {
                    ok = true,
                    node = Environment.MachineName,
                    paneId = result.PaneId,
                    name = result.SessionName,
                    messageId = result.MessageId,
                    result.Deduplicated,
                    deliversAtTurnStart = result.DeliversAtTurnStart,
                })
                : _Serialize(new { ok = false, error = result.Error });
        }
        catch (Exception exception)
        {
            return _Serialize(new { ok = false, error = exception.Message });
        }
    }

    [McpServerTool(Name = "rename_node_session", ReadOnly = false, Destructive = false)]
    [Description("Renames a session on the node — the name its operator sees in the sidebar there. The pane id is the node's, from list_node_sessions; only a session that list showed you can be renamed.")]
    public async Task<string> RenameNodeSessionAsync(
        [Description("The pane id of the session on the node, exactly as list_node_sessions reports it.")] string paneId,
        [Description("What the session should be called.")] string name)
    {
        try
        {
            if ((_RefuseIfNotTheController() ?? await _RefuseIfNotVisibleAsync(paneId).ConfigureAwait(false)) is { } refusal)
            {
                return refusal;
            }

            var result = await gateway.RenameSessionAsync(paneId, name).ConfigureAwait(false);
            return result.Ok
                ? _Serialize(new { ok = true, node = Environment.MachineName, paneId, name = result.Name })
                : _Serialize(new { ok = false, error = result.Error });
        }
        catch (Exception exception)
        {
            return _Serialize(new { ok = false, error = exception.Message });
        }
    }

    [McpServerTool(Name = "read_node_transcript", ReadOnly = true)]
    [Description("Reads the last rows of a session's transcript on the node, oldest first, in the same shape read_transcript gives you for your own sessions. The pane id is the node's, from list_node_sessions; only a session that list showed you can be read. `found` false means the node has no such session. Each row is cut to 2000 characters and marked truncated when it was; totalEntries says how long the whole transcript is.")]
    public async Task<string> ReadNodeTranscriptAsync(
        [Description("The pane id of the session on the node, exactly as list_node_sessions reports it.")] string paneId,
        [Description("How many of the most recent entries to return; capped at 100.")] int count = AssistantReadMcpTools.DefaultEntryCount)
    {
        try
        {
            if ((_RefuseIfNotTheController() ?? await _RefuseIfNotVisibleAsync(paneId).ConfigureAwait(false)) is { } refusal)
            {
                return refusal;
            }

            var transcript = await read.ReadTranscriptAsync(paneId, Math.Clamp(count, 1, AssistantReadMcpTools.MaxEntryCount)).ConfigureAwait(false);
            if (transcript is null)
            {
                return _Serialize(new { ok = true, node = Environment.MachineName, found = false });
            }

            return _Serialize(new
            {
                ok = true,
                node = Environment.MachineName,
                found = true,
                paneId = transcript.PaneId,
                name = transcript.Name,
                totalEntries = transcript.TotalEntries,
                entries = transcript.Entries.Select(entry =>
                {
                    var (text, textTruncated) = AssistantReadMcpTools.Bounded(entry.Text);
                    var (result, resultTruncated) = AssistantReadMcpTools.Bounded(entry.ToolResult);
                    return new { kind = entry.Kind, text, toolResult = entry.ToolResult is null ? null : result, truncated = textTruncated || resultTruncated };
                }),
            });
        }
        catch (Exception exception)
        {
            return _Serialize(new { ok = false, error = exception.Message });
        }
    }

    [McpServerTool(Name = "answer_node_permission", ReadOnly = false, Destructive = true)]
    [Description("Places the operator's Allow or Deny on one open permission question of a session on the node — the click on the row the controller's screen drew for it. THIS IS THE OPERATOR'S CLICK CARRIED OVER THE LINE, NOT A DECISION OF YOURS: an assistant never answers a permission, on any machine. The pane id is the node's, from list_node_sessions, and the tool-use id is the one that list reported under pendingPermissions. `answered` false means the question was no longer open there — answered on the node itself, or the session gone — and nothing was done.")]
    public async Task<string> AnswerNodePermissionAsync(
        [Description("The pane id of the session on the node, exactly as list_node_sessions reports it.")] string paneId,
        [Description("The tool-use id of the open question, exactly as list_node_sessions reports it.")] string toolUseId,
        [Description("True to allow the call, false to deny it.")] bool allow)
    {
        try
        {
            if ((_RefuseIfNotTheController() ?? _RefuseIfMayNotAnswerPermissions() ?? await _RefuseIfNotVisibleAsync(paneId).ConfigureAwait(false)) is { } refusal)
            {
                return refusal;
            }

            var answered = await gateway.RespondToPermissionAsync(paneId, toolUseId, allow).ConfigureAwait(false);
            return _Serialize(new
            {
                ok = true,
                node = Environment.MachineName,
                paneId,
                toolUseId,
                answered,
                outcome = answered ? allow ? "Allowed" : "Denied" : "That question is no longer open on this node; nothing was done.",
            });
        }
        catch (Exception exception)
        {
            return _Serialize(new { ok = false, error = exception.Message });
        }
    }

    [McpServerTool(Name = "issue_connect_key", ReadOnly = false, Destructive = false)]
    [Description("Issues a new connect key for this node and returns it ONCE — it is stored only as a hash, so a key that is lost cannot be shown again, only replaced. Needs a connect key with the admin capability. capability is \"operate\" (the node tools) or \"admin\" (those plus managing keys). Every issued key expires; expiresInDays defaults to the node's policy. holdsAssistant turns off this node's assistant while you call. The scope defaults to every profile and project, with answering permission prompts allowed and starting profiles that skip their approvals not; profiles and projects narrow it, and set_connect_key_scope changes it later. To rotate, issue the new key, move the controller to it, then revoke the old one. After first setup, issue your own key with an expiry and revoke the bootstrap key. The key lands in the transcript of whoever calls this, so hand it to the operator's connect dialog rather than calling this from an assistant.")]
    public async Task<string> IssueConnectKeyAsync(
        [Description("A name for the operator: which controller or machine this key is for.")] string label,
        [Description("\"operate\" or \"admin\".")] string capability,
        [Description("Days until the key expires. Leave out for the node's default.")] int? expiresInDays = null,
        [Description("Turns off this node's assistant while you call. Defaults to false.")] bool holdsAssistant = false,
        [Description(ProfilesParameter)] string[]? profiles = null,
        [Description(ProjectsParameter)] string[]? projects = null,
        [Description(BypassParameter)] bool mayStartBypassProfiles = false,
        [Description(PermissionsParameter)] bool mayAnswerPermissions = true)
    {
        try
        {
            if ((_RefuseIfNotTheController() ?? _RefuseIfNotAdmin()) is { } refusal)
            {
                return refusal;
            }

            if (connectKeys is null || McpRequestContext.CurrentNodeCaller is not { } caller)
            {
                return _Serialize(new { ok = false, error = NoConnectKeys });
            }

            if (!Enum.TryParse<ConnectKeyCapability>(capability, ignoreCase: true, out var parsed) || !Enum.IsDefined(parsed))
            {
                return _Serialize(new { ok = false, error = "capability must be \"operate\" or \"admin\"." });
            }

            if (string.IsNullOrWhiteSpace(label))
            {
                return _Serialize(new { ok = false, error = "label is required, so the operator can tell this key from the others." });
            }

            var scope = _ScopeOf(profiles, projects, mayStartBypassProfiles, mayAnswerPermissions);
            var (key, secret) = await connectKeys.IssueAsync(label, parsed, expiresInDays, caller, holdsAssistant, scope).ConfigureAwait(false);
            return _Serialize(new
            {
                ok = true,
                key = secret,
                prefix = key.Prefix,
                label = key.Label,
                capability = key.Capability.ToString().ToLowerInvariant(),
                expiresAt = key.ExpiresAt,
                scope = _ScopeJson(key.EffectiveScope()),
                note ="This is the only time the key is shown. Store it now.",
            });
        }
        catch (Exception exception)
        {
            return _Serialize(new { ok = false, error = exception.Message });
        }
    }

    [McpServerTool(Name = "revoke_connect_key", ReadOnly = false, Destructive = true)]
    [Description("Revokes a connect key by its prefix, at once: its next call is refused and its open connections are closed. The bootstrap key can be revoked too, and stays revoked when the node restarts with the same secret. Needs a connect key with the admin capability. Revoking the key you are calling with ends this connection.")]
    public async Task<string> RevokeConnectKeyAsync(
        [Description("The key's prefix, exactly as list_connect_keys reports it.")] string prefix)
    {
        try
        {
            if ((_RefuseIfNotTheController() ?? _RefuseIfNotAdmin()) is { } refusal)
            {
                return refusal;
            }

            if (connectKeys is null || McpRequestContext.CurrentNodeCaller is not { } caller)
            {
                return _Serialize(new { ok = false, error = NoConnectKeys });
            }

            var revoked = await connectKeys.RevokeAsync(prefix, caller).ConfigureAwait(false);
            return revoked
                ? _Serialize(new { ok = true, prefix, revoked })
                : _Serialize(new { ok = false, error = $"There is no live connect key with prefix '{prefix}'. Call list_connect_keys for the ones there are." });
        }
        catch (Exception exception)
        {
            return _Serialize(new { ok = false, error = exception.Message });
        }
    }

    [McpServerTool(Name = "set_connect_key_scope", ReadOnly = false, Destructive = true)]
    [Description("Replaces the scope of a connect key by its prefix, from its next call on — a controller already connected with it is held to the new scope without reconnecting. The whole scope is replaced: what you leave out takes the default issue_connect_key gives it. The bootstrap key keeps its full scope and is refused. Needs a connect key with the admin capability.")]
    public async Task<string> SetConnectKeyScopeAsync(
        [Description("The key's prefix, exactly as list_connect_keys reports it.")] string prefix,
        [Description(ProfilesParameter)] string[]? profiles = null,
        [Description(ProjectsParameter)] string[]? projects = null,
        [Description(BypassParameter)] bool mayStartBypassProfiles = false,
        [Description(PermissionsParameter)] bool mayAnswerPermissions = true)
    {
        try
        {
            if ((_RefuseIfNotTheController() ?? _RefuseIfNotAdmin()) is { } refusal)
            {
                return refusal;
            }

            if (connectKeys is null || McpRequestContext.CurrentNodeCaller is not { } caller)
            {
                return _Serialize(new { ok = false, error = NoConnectKeys });
            }

            var scope = _ScopeOf(profiles, projects, mayStartBypassProfiles, mayAnswerPermissions);
            return await connectKeys.UpdateScopeAsync(prefix, scope, caller).ConfigureAwait(false)
                ? _Serialize(new { ok = true, prefix, scope = _ScopeJson(scope) })
                : _Serialize(new { ok = false, error = $"There is no live connect key with prefix '{prefix}'. Call list_connect_keys for the ones there are." });
        }
        catch (Exception exception)
        {
            return _Serialize(new { ok = false, error = exception.Message });
        }
    }

    [McpServerTool(Name = "list_connect_keys", ReadOnly = true)]
    [Description("Lists this node's connect keys — prefix, label, capability, holdsAssistant, scope (null profiles or projects means all of them), when each was created, expires, was revoked and was last used — never the keys themselves. holdsAssistant turns off this node's assistant while you call. Needs a connect key with the admin capability. lastUsedAt covers this run of the node only.")]
    public async Task<string> ListConnectKeysAsync()
    {
        try
        {
            if ((_RefuseIfNotTheController() ?? _RefuseIfNotAdmin()) is { } refusal)
            {
                return refusal;
            }

            if (connectKeys is null)
            {
                return _Serialize(new { ok = false, error = NoConnectKeys });
            }

            var keys = await connectKeys.ListAsync().ConfigureAwait(false);
            return _Serialize(new
            {
                ok = true,
                keys = keys.Select(entry => new
                {
                    prefix = entry.Key.Prefix,
                    label = entry.Key.Label,
                    capability = entry.Key.Capability.ToString().ToLowerInvariant(),
                    holdsAssistant = entry.Key.HoldsAssistant,
                    scope = _ScopeJson(entry.Key.EffectiveScope()),
                    isBootstrap = entry.Key.IsBootstrap,
                    createdAt = entry.Key.CreatedAt,
                    expiresAt = entry.Key.ExpiresAt,
                    revokedAt = entry.Key.RevokedAt,
                    lastUsedAt = entry.LastUsedAt,
                }),
            });
        }
        catch (Exception exception)
        {
            return _Serialize(new { ok = false, error = exception.Message });
        }
    }

    // AC-1323: the four controls share stop's bound — a session outside what listing showed is the node operator's
    // own, whatever the controller wants to do with it.
    private async Task<string?> _RefuseIfNotVisibleAsync(string paneId)
    {
        var visible = await _VisibleSessionsAsync().ConfigureAwait(false);
        return visible.Any(session => string.Equals(session.PaneId, paneId, StringComparison.Ordinal))
            ? null
            : _Serialize(new
            {
                ok = false,
                error = $"There is no session '{paneId}' on this node that you may reach. Call list_node_sessions for the ones you can see; anything else there is running outside what this node's operator allowed you.",
            });
    }

    // The sessions this controller may see, the same set it may stop — one method rather than a filter per call
    // site, closing the gap where "see" and "end" could drift apart. Live against the grant: an unticked profile
    // disappears from the list immediately, and the node operator's own sessions are visible under a shared profile.
    private async Task<IReadOnlyList<AssistantSessionRow>> _VisibleSessionsAsync()
    {
        var sessions = await read.ListSessionsAsync().ConfigureAwait(false);
        return [.. sessions.Where(session => _IsProfileAllowed(session.Profile))];
    }

    // The profile this label names, if the grant covers it — or null, which is the only other answer callers need.
    // Compared the way the spawn path compares (`AssistantAgentGateway`: OrdinalIgnoreCase), so the profile checked
    // here is the profile that would run.
    private async Task<SessionProfile?> _ResolveAllowedProfileAsync(string label)
    {
        var known = await profiles.LoadAsync().ConfigureAwait(false);
        var match = known.FirstOrDefault(candidate => string.Equals(candidate.Label, label.Trim(), StringComparison.OrdinalIgnoreCase));
        return match is not null && _IsProfileAllowed(match.Label) ? match : null;
    }

    // The desk a controller's session lands on, derived here and never named by the caller — a controller has
    // never seen this cockpit's desks. Falls back to the first desk that can hold a session.
    private async Task<string?> _ActiveWorkspaceIdAsync()
    {
        var workspaces = await gateway.ListWorkspacesAsync().ConfigureAwait(false);
        var usable = workspaces.Where(workspace => workspace.CanHostSessions).ToList();
        return (usable.FirstOrDefault(workspace => workspace.IsActive) ?? usable.FirstOrDefault())?.Id;
    }

    // AC-1329: the same two-value parse `remember` uses on the controller, kept local rather than shared — every
    // MCP tool class here already carries its own `_Serialize`/refusal helpers rather than a common base.
    private static AssistantMemoryScope? _ParseMemoryScope(string? scope) => scope switch
    {
        "behaviour" => AssistantMemoryScope.Behaviour,
        "machine" => AssistantMemoryScope.Machine,
        _ => null,
    };

    private static string _ScopeRefusal() =>
        _Serialize(new { ok = false, error = "scope is required and must be \"behaviour\" or \"machine\"." });

    // AC-1351 (B3): the key tools need an admin key. The pairing secret counts as operate, so a paired laptop
    // cannot mint itself a key.
    private static string? _RefuseIfNotAdmin() =>
        McpRequestContext.CurrentNodeCaller is { Capability: ConnectKeyCapability.Admin }
            ? null
            : _Serialize(new { ok = false, error = AdminRefusal });

    // AC-1367: pass-throughs to `NodeCaller`, the one check the backend API shares. A connect key is held to its
    // own scope, a pairing caller to what its operator ticked.
    private bool _IsProfileAllowed(string profileLabel) => _Caller().AllowsProfile(profileLabel, pairing);

    private bool _IsProjectAllowed(string projectId) => _Caller().AllowsProject(projectId, pairing);

    // No caller stamped is the pairing's: the node tools are driven that way outside the listener's door.
    private static NodeCaller _Caller() => McpRequestContext.CurrentNodeCaller ?? NodeCaller.ForPairing("");

    private static string? _RefuseIfMayNotAnswerPermissions() =>
        _Caller().MayAnswerPermissions ? null : _Serialize(new { ok = false, error = PermissionsRefusal });

    private static ConnectKeyScope _ScopeOf(string[]? profiles, string[]? projects, bool mayStartBypassProfiles, bool mayAnswerPermissions) => new()
    {
        AllowAllProfiles = profiles is null,
        AllowedProfileLabels = [.. (profiles ?? []).Select(label => label.Trim())],
        AllowAllProjects = projects is null,
        AllowedProjectIds = [.. (projects ?? []).Select(id => id.Trim())],
        MayStartBypassProfiles = mayStartBypassProfiles,
        MayAnswerPermissions = mayAnswerPermissions,
    };

    // Null profiles or projects means all of them, the way the tools take it in.
    private static object _ScopeJson(ConnectKeyScope scope) => new
    {
        profiles = scope.AllowAllProfiles ? null : scope.AllowedProfileLabels,
        projects = scope.AllowAllProjects ? null : scope.AllowedProjectIds,
        mayStartBypassProfiles = scope.MayStartBypassProfiles,
        mayAnswerPermissions = scope.MayAnswerPermissions,
    };

    private static string? _RefuseIfNotTheController() =>
        string.Equals(McpRequestContext.CurrentPaneId, NodeCallerIdentity.PaneId, StringComparison.Ordinal)
            ? null
            : JsonSerializer.Serialize(new { ok = false, error = NotTheController }, SerializerOptions);

    private static string _Serialize(object payload) => JsonSerializer.Serialize(payload, SerializerOptions);
}
