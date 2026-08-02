using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using Cockpit.Core.Abstractions.Assistant;
using Cockpit.Core.Assistant;
using Cockpit.Core.Consent;
using Cockpit.Infrastructure.Agents;
using Cockpit.Infrastructure.Consent;
using Cockpit.Infrastructure.Mcp;
using Cockpit.Plugins.Abstractions.Consent;

namespace Cockpit.Infrastructure.Assistant;

/// <summary>
/// The <c>cockpit-assistant-agents</c> MCP tools (AC-545): the voice assistant's acting path — starting a session on
/// a named desk and stopping one again. The read half is <see cref="AssistantReadMcpTools"/>, and the two are
/// deliberately not one server; <see cref="AssistantIdentity.ActMcpServerName"/> says why.
/// </summary>
/// <remarks>
/// <b>The mount rule is copied, not re-invented.</b> Both gates are the ones AC-544 already has, and both are here
/// for the same reasons written out on the read server:
/// <para>
/// <b>1. It is not handed out.</b> The endpoint is registered <c>Internal</c> (AC-204), so it is in no picker and in
/// no fan-out, and reaches only a launch that names it — <c>AssistantSessionHost.McpSelection</c> being the one
/// place in the codebase that does.
/// </para>
/// <para>
/// <b>2. It is not answered.</b> <see cref="_RefuseIfNotTheAssistant"/> runs first in every tool and returns
/// <em>before</em> the gateway is touched. That is the gate that holds: the mount is a fact about configuration and
/// configuration widens later by accident — an endpoint made non-internal, a profile that names the server, a spawn
/// path that copies a selection it did not read. When that happens these tools sit in a session's context and still
/// answer nobody, because what is checked is the pane <see cref="McpAuthMiddleware"/> stamped from the request's own
/// per-session bearer, and no argument on any tool here can move it.
/// </para>
/// <para>
/// <b>Why the stakes are higher here than on the read server.</b> These tools spend money and start processes. The
/// second gate is therefore not the last one: on the defaults, a call raises the SDK permission prompt, which the
/// chat window renders as an Allow/Deny row showing the literal profile, desk and folder. Nothing in this file is
/// that gate and nothing here may become it — a spoken "yes" is a sentence in a transcript, and the only thing that
/// resolves a permission is a click.
/// </para>
/// <para>
/// <b>"On the defaults" carries that whole sentence, and the tool descriptions have to say so.</b> The assistant
/// starts on <c>SessionOptionCatalog.DefaultPermissionMode</c> only as a floor: the Assistant Profile's own
/// permission mode overrides it (<c>AssistantSessionHost._LaunchOptions</c>), so an operator who put
/// <c>bypassPermissions</c> on that profile gets no row at all — and for the two tools that raise a cockpit card of
/// their own, AC-575's per-source switch does the same one layer down, before the card is ever built
/// (<c>ConsentService.RequestConsentAsync</c>). A description that promises a click unconditionally has the
/// assistant announcing a card nobody will ever see, which is the same defect as promising no click, pointing the
/// other way. <see cref="AskingCanBeSwitchedOff"/> is the one sentence that says it, written once because five
/// copies of it are five places for it to stop being true.
/// </para>
/// <para>
/// <b>This server scopes nothing.</b> The workspace is a required parameter rather than something derived, because
/// the assistant sits on no desk to derive one from — see <see cref="SpawnTarget"/>, whose two factories are the two
/// scoping rules, and whose remarks explain why a coordinator's stricter rule must not be built as a check bolted
/// onto this one.
/// </para>
/// <para>
/// <b>Two of these ask on their own account.</b> <c>send_message</c> and <c>send_prompt</c> reach into a session the
/// assistant did not start, so they raise a cockpit consent card as well — under two separate sources
/// (<see cref="ConsentSourceCatalog.AssistantMessage"/> and <see cref="ConsentSourceCatalog.AssistantPrompt"/>), so
/// that an operator who lets the assistant leave notes unasked has not thereby let it start work unasked.
/// </para>
/// </remarks>
internal sealed class AssistantAgentMcpTools(IAssistantAgentGateway gateway, IConsentBroker? consent = null)
{
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = false };

    /// <summary>
    /// What a caller that is not the assistant is told. One sentence, and no detail about what it would have got —
    /// the same wording the read server uses, so a session that reaches either one learns the same nothing.
    /// </summary>
    private const string NotTheAssistant =
        "This tool is the cockpit assistant's own. It is not available to an agent session.";

    /// <summary>
    /// The caveat every "it needs their click" sentence on this server carries, spliced into each description rather
    /// than retyped in it. See the class remarks for why it exists: the click is the default, not a guarantee, and
    /// the operator can switch the asking off ahead of time by two separate levers.
    /// </summary>
    private const string AskingCanBeSwitchedOff =
        " THE CLICK IS THE DEFAULT AND NOT A PROMISE: the operator can switch the asking off ahead of time — for this"
        + " kind of request in Options → Voice, or by giving the Assistant Profile a permission mode that bypasses"
        + " prompts — and then no row appears and the call simply goes through. You cannot tell which it will be"
        + " before you call, so announce that something is waiting on their screen only while it actually is, and"
        + " once the result is back, report what happened rather than what you expected to be asked.";

    [McpServerTool(Name = "start_agent")]
    [Description("Starts an AI session on a workspace and leaves it running there as an ordinary pane — the same kind of pane the operator's own New-session dialog makes, with its own transcript and its own approvals. YOU MUST NAME THE WORKSPACE. You sit on no desk yourself, so there is nothing for the cockpit to infer one from; call list_workspaces to turn the desk the operator named into an id, and ask them which one if what they said matches nothing there. IF THEY NAMED NO DESK IN THIS INSTRUCTION, DO NOT CARRY ONE OVER FROM EARLIER IN THE CONVERSATION: they may well have moved on since. The desk they are looking at right now is the one list_workspaces reports as isActive, and that is what \"here\" means — use it, and say which desk you used. YOU MUST NAME THE PROFILE, and it is the field that decides what this costs: the profile picks the provider and the model, so starting something on a large model because no smaller one was named is a bill nobody agreed to. If the operator did not say which, call list_profiles: when exactly one fits what they asked for, take it and say so, and otherwise ask, naming only the ones that fit. THE OPERATOR STILL HAS TO APPROVE IT: this call raises an Allow/Deny row in the cockpit's chat window showing the profile, the desk and the folder, and nothing starts until it is clicked. Say out loud that permission is waiting on their screen — they may be looking somewhere else — and never treat a spoken \"yes\" as the approval, because it is not one and cannot become one." + AskingCanBeSwitchedOff + " A REFUSAL IS NORMAL: if this comes back with ok false, read the reason out in a sentence and carry on with whatever you are still allowed to do, rather than treating it as the end of the conversation. WHAT THIS CANNOT DO: a delegated task (delegate_task) has no pane, so it is not something this tool can start, is not in any list, and cannot be stopped here. If you are asked about that kind of work, say it is invisible from where you are standing instead of reporting an absence as a fact.")]
    public async Task<string> StartAgentAsync(
        [Description("The id of the workspace the session is to appear on — the desk, not its tab label. Required, and never guessed: get it from list_workspaces, which shows every desk including the empty ones, or from list_sessions, where each session reports the desk it sits on. If neither turns up the desk the operator meant, ask them which one rather than picking a plausible id.")] string workspaceId,
        [Description("The profile to run under, by its label exactly as the cockpit knows it. Required. This is what decides provider, model and therefore cost — an unknown label is refused rather than quietly swapped for a default, because the default might be the expensive one.")] string profile,
        [Description("The first message to hand the session once it is up, in the words the work should be described in. Left out, the session comes up waiting for someone to type in it. Write it as a brief for an agent that cannot hear the conversation you are having — it gets this text and nothing else.")] string? prompt = null,
        [Description("The folder to run in. Left out, the profile's own default folder is used. Give a full path; a relative one means nothing here, since you are not standing in any directory.")] string? workingDirectory = null,
        [Description("What to call the pane, so the operator can find it in the sidebar. Left out, the profile and the clock name it. A name that says what the work is (\"AC-545 tests\") is worth far more than one that says what it runs on.")] string? name = null,
        [Description("Which route to start on: \"tty\" for the provider's own terminal, \"sdk\" for the chat/SDK session. LEAVE THIS OUT unless the operator actually said which — the profile is already set to one and that is nearly always the right answer. It is here for exactly one request: \"the same profile, but as an SDK session\", which is a thing they can pick in the New-session dialog too. It is not a way to start work by another route: everything you can start goes through this tool, appears as a pane, and is written down.")] string? kind = null)
    {
        try
        {
            if (_RefuseIfNotTheAssistant() is { } refusal)
            {
                return refusal;
            }

            var result = await gateway.SpawnAsync(new AgentSpawnRequest(
                // The assistant's door on SpawnTarget, named after its rule: the workspace was named by the caller,
                // because the caller has no desk to derive one from. Constructed here rather than in the gateway so
                // the authority a spawn was made under travels with the request into the audit trail.
                SpawnTarget.NamedByTheAssistant(workspaceId),
                profile,
                prompt,
                workingDirectory,
                name,
                kind)).ConfigureAwait(false);

            return result.Ok
                ? _Serialize(new
                {
                    ok = true,
                    paneId = result.PaneId,
                    name = result.SessionName,
                    workingDirectory = result.WorkingDirectory,
                    workspaceId,
                    profile,
                })
                : _Serialize(new { ok = false, error = result.Error });
        }
        catch (Exception exception)
        {
            // A tool result, never an MCP protocol error — the same choice the read server and cockpit-agents make.
            // The caller is a model whose next sentence is spoken to the operator, and "the tool failed" is the one
            // answer that helps nobody.
            return _Serialize(new { ok = false, error = exception.Message });
        }
    }

    [McpServerTool(Name = "stop_agent")]
    [Description("Closes a running AI session, named by its pane id — on any desk, not just one. Take the pane id from list_sessions; there is no lookup by name here, because two sessions can carry the same one and stopping the wrong session loses work that was in progress. LIKE STARTING, THIS NEEDS THE OPERATOR'S CLICK: an Allow/Deny row appears in the chat window naming what is about to be closed, and nothing happens until it is answered. Say out loud that it is waiting on their screen." + AskingCanBeSwitchedOff + " A REFUSAL IS NORMAL — a pane that is already gone, one that is a plain terminal rather than an agent, one that runs inside a workspace's own surface rather than as a pane, or your own session, which you do not get to end mid-sentence — so read the reason out and carry on. WHAT THIS CANNOT DO: a delegated task (delegate_task) runs without a pane, so it cannot be stopped here and never appears in any list you can see. Say so rather than reporting that there was nothing to stop.")]
    public async Task<string> StopAgentAsync(
        [Description("The pane id of the session to close, exactly as list_sessions reports it. Read it back to the operator before you ask for it, together with the session's name — a pane id is not something anyone can check by ear once it is gone.")] string paneId)
    {
        try
        {
            if (_RefuseIfNotTheAssistant() is { } refusal)
            {
                return refusal;
            }

            var result = await gateway.StopAsync(paneId).ConfigureAwait(false);
            return result.Ok
                ? _Serialize(new { ok = true, paneId = result.PaneId, name = result.SessionName })
                : _Serialize(new { ok = false, error = result.Error });
        }
        catch (Exception exception)
        {
            return _Serialize(new { ok = false, error = exception.Message });
        }
    }

    [McpServerTool(Name = "rename_session")]
    [Description("Renames a running session — the name in its header and in the sidebar, which is the one thing the operator finds it back by. TAKE THE PANE ID FROM list_sessions AND NEVER RENAME BY NAME: two sessions can carry the same one, and renaming the wrong session relabels work somebody is in the middle of. Read the pane id back together with the session's current name before you ask, because a pane id cannot be checked by ear. THE NAME YOU SET IS THE OPERATOR'S OWN: nothing overwrites it afterwards — not a ticket a plugin links to that session later, not a restart — so use the words they said rather than a tidier version of them. LIKE EVERYTHING ON THIS SERVER IT NEEDS THEIR CLICK: an Allow/Deny row appears in the cockpit's chat window, and nothing changes until it is answered. Say out loud that it is waiting on their screen. A REFUSAL IS NORMAL — a pane that has since closed, one that runs inside a workspace's own surface rather than as a pane, my own session, or an empty name — so read the reason out in a sentence and carry on. WHAT THIS CANNOT DO: it does not rename the desk the session sits on (that is rename_workspace), and a delegated task (delegate_task) has no pane, so it cannot be renamed here and is in no list you can see.")]
    public async Task<string> RenameSessionAsync(
        [Description("The pane id of the session to rename, exactly as list_sessions reports it. Never a name and never a guess.")] string paneId,
        [Description("What the session should be called, in the operator's own words. A name that says what the work is (\"AC-592 tests\") is worth far more than one that says what it runs on.")] string name)
    {
        try
        {
            if (_RefuseIfNotTheAssistant() is { } refusal)
            {
                return refusal;
            }

            var result = await gateway.RenameSessionAsync(paneId, name).ConfigureAwait(false);
            return result.Ok
                ? _Serialize(new { ok = true, paneId, name = result.Name })
                : _Serialize(new { ok = false, error = result.Error });
        }
        catch (Exception exception)
        {
            return _Serialize(new { ok = false, error = exception.Message });
        }
    }

    [McpServerTool(Name = "rename_workspace")]
    [Description("Renames a desk — the tab label the operator reads and says out loud. Take the id from list_workspaces and never guess one from a name, because two desks may be called the same thing. THIS ONE DOES NOT BRING THEM ANYWHERE: renaming a desk is not walking to it, so whatever is on their screen stays there, including when the desk you are renaming is not the one they are looking at. The name is taken exactly as given and is not made unique, so read it back before you ask for it. LIKE STARTING A SESSION, IT NEEDS THEIR CLICK on the Allow/Deny row in the chat window — say out loud that it is waiting. A refusal is normal (an id that names no desk, an empty name); read it out and carry on. Renaming a desk changes nothing about what runs on it.")]
    public async Task<string> RenameWorkspaceAsync(
        [Description("The id of the workspace to rename, from list_workspaces. The desk, not its current tab label.")] string workspaceId,
        [Description("What the desk should be called, in the operator's own words — it becomes the tab label they will read and say. Keep it short enough to fit a tab.")] string name)
    {
        try
        {
            if (_RefuseIfNotTheAssistant() is { } refusal)
            {
                return refusal;
            }

            var result = await gateway.RenameWorkspaceAsync(workspaceId, name).ConfigureAwait(false);
            return result.Ok
                ? _Serialize(new { ok = true, workspaceId, name = result.Name })
                : _Serialize(new { ok = false, error = result.Error });
        }
        catch (Exception exception)
        {
            return _Serialize(new { ok = false, error = exception.Message });
        }
    }

    [McpServerTool(Name = "list_workspaces")]
    [Description("Lists every desk the cockpit has open, with the id a spawn needs, the name the operator says out loud, and how many agent sessions are on each. THIS IS HOW YOU TURN A SPOKEN NAME INTO AN ID: the operator says \"the release desk\", this says which id that is. Call it before start_agent whenever you were given a name rather than an id, and never guess an id from a name — two desks may be called the same thing. Unlike list_sessions, this also shows the EMPTY desks, which is the half a session roster cannot tell you about. canHostSessions false means a session cannot be placed there at all (a dashboard would run it invisibly); do not offer it as a target, offer to make a new desk instead. You are on none of these yourself — you sit on no desk, which is exactly why the one you spawn onto must always be named.")]
    public async Task<string> ListWorkspacesAsync()
    {
        try
        {
            if (_RefuseIfNotTheAssistant() is { } refusal)
            {
                return refusal;
            }

            var workspaces = await gateway.ListWorkspacesAsync().ConfigureAwait(false);
            return _Serialize(new { ok = true, workspaces });
        }
        catch (Exception exception)
        {
            return _Serialize(new { ok = false, error = exception.Message });
        }
    }

    [McpServerTool(Name = "list_profiles")]
    [Description("Lists the profiles a session can be started under, each with the provider it runs on and the model it pins. CALL THIS BEFORE ASKING WHICH PROFILE — a question you cannot offer the answers to makes the operator do your work. USE THE PROVIDER FIELD, NOT THE LABEL'S WORDING: if they said \"two Claude agents\", the ones that count are the ones whose provider is Claude, whatever they happen to be called. IF EXACTLY ONE MATCHES WHAT THEY ASKED FOR, JUST USE IT and say which one you took. If several match, name only those — reciting profiles they have already ruled out is noise. If none match, say so and read out what there is. This is the field that decides the model and therefore the cost, which is why it is never guessed and never defaulted.")]
    public async Task<string> ListProfilesAsync()
    {
        try
        {
            if (_RefuseIfNotTheAssistant() is { } refusal)
            {
                return refusal;
            }

            var profiles = await gateway.ListProfilesAsync().ConfigureAwait(false);
            return _Serialize(new { ok = true, profiles });
        }
        catch (Exception exception)
        {
            return _Serialize(new { ok = false, error = exception.Message });
        }
    }

    [McpServerTool(Name = "create_workspace")]
    [Description("Makes a new, empty Sessions desk with the name given and returns its id, ready to be spawned onto. Use it when the operator asks for somewhere new to put work, or when the desk they named does not exist and they would rather have it made than pick another. THIS ONE DOES BRING THEM THERE: an empty new desk has nothing on it to interrupt, and asking for a desk to be made is asking to be shown it — say so, because their screen will change. LIKE STARTING A SESSION, IT NEEDS THEIR CLICK on the Allow/Deny row in the chat window." + AskingCanBeSwitchedOff + " The name is taken exactly as given and is not made unique, so read it back before you ask for it. Making a desk does not put anything on it — that is still a separate start_agent, with its own approval.")]
    public async Task<string> CreateWorkspaceAsync(
        [Description("What the desk is to be called, in the operator's own words — it becomes the tab label they will read and say. Keep it short enough to fit a tab.")] string name)
    {
        try
        {
            if (_RefuseIfNotTheAssistant() is { } refusal)
            {
                return refusal;
            }

            var created = await gateway.CreateWorkspaceAsync(name).ConfigureAwait(false);
            return created is null
                ? _Serialize(new { ok = false, error = "A workspace needs a name; that one was empty." })
                : _Serialize(new { ok = true, workspace = created });
        }
        catch (Exception exception)
        {
            return _Serialize(new { ok = false, error = exception.Message });
        }
    }

    [McpServerTool(Name = "remove_workspace")]
    [Description("Closes an empty SESSIONS desk and takes its tab away — the counterpart of create_workspace. ONLY A SESSIONS DESK: the operator's own ✕ closes any desk, this closes only the kind that holds sessions, so it is the narrower of the two. THE DESK HAS TO BE EMPTY FIRST: this is refused for as long as anything is still on it, and the reason says how many. That is the design and not a shortcoming — closing a desk would stop everything on it in one go, and each of those sessions is a stop the operator gets to approve on its own. So do it in that order: list_workspaces for the count, stop_agent per session, then this. If they asked for all of it in one breath (\"stop everything on Henk and then get rid of it\"), that is an ordinary request — carry it out as several calls, and say where you are, rather than reporting the desk gone while it is still there. YOU MUST NAME THE WORKSPACE BY ITS ID, never by its label: two desks can be called the same thing and this one does not come back. Take the id from list_workspaces and read the desk's NAME back to the operator before you ask, because an id is not something anyone can check by ear. LIKE STARTING A SESSION, IT NEEDS THEIR CLICK on the Allow/Deny row in the chat window, and nothing goes until it is answered — say out loud that it is waiting on their screen, and never treat a spoken \"yes\" as the approval." + AskingCanBeSwitchedOff + " A REFUSAL IS NORMAL: a desk that is not a sessions desk, sessions still on it, the only desk left (the cockpit always needs one to show), or the projects overview, which is a fixture and never closes. Read the reason out in a sentence and carry on. WHAT THIS CANNOT DO: it closes only sessions desks. A dashboard, the projects overview and any desk a plugin brought are all out of reach here, whatever is or is not on them — what they hold is not sessions, so there is nothing for you to count, nothing for you to stop, and the approval row could not have named what closing one would throw away. The operator closes those from the tab itself, where the app tells them what goes; say that, rather than trying another id. And it stops nothing: it does not close the sessions on a desk for you, does not move them anywhere, and does not empty the desk on its way out — emptying it is stop_agent's job, one session at a time, each with its own approval. It does not touch a delegated task either: that runs without a pane, so it is on no desk, and closing a desk neither ends it nor tells you it was there. And it cannot be undone: the tab, the arrangement on it and its place in the strip are gone, so the name you read back is the last chance anyone has to say no.")]
    public async Task<string> RemoveWorkspaceAsync(
        [Description("The id of the desk to close, exactly as list_workspaces reports it — the desk, not its tab label. Required and never guessed: closing the wrong desk is not something an apology fixes.")] string workspaceId)
    {
        try
        {
            if (_RefuseIfNotTheAssistant() is { } refusal)
            {
                return refusal;
            }

            var result = await gateway.RemoveWorkspaceAsync(workspaceId).ConfigureAwait(false);
            return result.Ok
                ? _Serialize(new { ok = true, workspaceId, name = result.Name })
                : _Serialize(new { ok = false, error = result.Error });
        }
        catch (Exception exception)
        {
            return _Serialize(new { ok = false, error = exception.Message });
        }
    }

    [McpServerTool(Name = "send_message")]
    [Description("Leaves a message in a running agent session's inbox — the same inbox the agents on a desk use to talk to each other, so the recipient reads yours exactly as it reads theirs. This TELLS an agent something; it does not make it do anything. The recipient decides what to do with what you wrote, and anything that needs the operator's approval still needs it — so use this for what an agent would want to know (\"the operator changed their mind about the branch\", \"another session is about to touch that worktree\"), and use send_prompt when the operator actually wants work started. Address it with a pane id from list_sessions, never by name: two sessions can be called the same thing. IT NEEDS THE OPERATOR'S CLICK: an Allow/Deny row appears in the chat window showing your message word for word and which session gets it, and nothing is delivered until it is answered — say out loud that it is waiting on their screen, and never treat a spoken \"yes\" as the approval." + AskingCanBeSwitchedOff + " A REFUSAL IS NORMAL — a pane that has closed, a terminal pane with no agent on it, your own session, or a recipient whose inbox is full — so read the reason out and carry on. The reply says whether the message will reach the recipient on its own with its next turn (deliversAtTurnStart) or only when that session next calls read_inbox; when it is false, do not tell the operator the agent has been told, because it has not been yet. Sending the identical message twice while the first is still unread adds nothing and comes back deduplicated. WHAT THIS CANNOT DO: it cannot reach your own session, cannot reach a pane that is not an agent session (a plain terminal has a pane id and nobody reading it), and cannot reach a delegated task (delegate_task), which runs with no pane and is invisible from where you are standing — say that rather than reporting an absence as a fact. It also does not interrupt: nothing is woken, nobody is pulled off what they are doing, and delivery is at the recipient's next turn at the earliest. If the operator needs something to happen now, this is the wrong tool and you should say so.")]
    public async Task<string> SendMessageAsync(
        [Description("The pane id of the agent session to write to, exactly as list_sessions reports it. Read the session's NAME back to the operator before you ask — a pane id is not something anyone can check by ear.")] string paneId,
        [Description("A short label for what this is, at most 100 characters, e.g. 'heads-up', 'question', 'handover'. The recipient sees it as your label, not as anything the cockpit vouches for.")] string kind,
        [Description("The message itself, at most 2000 characters, and shown to the operator word for word on the approval row — so write what you actually mean to send, not a summary of it. Write it as information for another agent, not as an order.")] string body)
    {
        try
        {
            if (_RefuseIfNotTheAssistant() is { } refusal)
            {
                return refusal;
            }

            // Bounded and stripped of terminal control sequences before the operator is shown anything, so the card
            // and the inbox carry the same bytes. Showing the raw argument and delivering the cleaned one would make
            // the card a description of the message rather than the message.
            var label = AgentMessageContent.Normalize(kind, out var strippedKind);
            var text = AgentMessageContent.Normalize(body, out var strippedBody);
            var addressee = AgentMessageContent.Normalize(paneId, out _);
            if (AgentMessageContent.Reject(addressee, label, text) is { } rejection)
            {
                return _Serialize(new { ok = false, error = rejection });
            }

            if (await _ApprovedAsync(
                    "The assistant wants to put a message in another session's inbox",
                    $"Send to session {addressee}\nkind: {label}\n\n{text}",
                    ConsentSourceCatalog.AssistantMessage,
                    "assistant.message",
                    ConsentRisk.LowRisk).ConfigureAwait(false) is { } denial)
            {
                return denial;
            }

            var result = await gateway.SendMessageAsync(addressee, label, text).ConfigureAwait(false);
            return result.Ok
                ? _Serialize(new
                {
                    ok = true,
                    paneId = result.PaneId,
                    name = result.SessionName,
                    messageId = result.MessageId,
                    result.Deduplicated,
                    // Said here and not only in list_sessions, because this is the moment the assistant forms the
                    // sentence it speaks: "delivered" on a pane with no passive delivery means the message is
                    // waiting, not that anybody has been told.
                    deliversAtTurnStart = result.DeliversAtTurnStart,
                    // True means what was delivered is not byte-for-byte what was passed: terminal control sequences
                    // were removed. Reported rather than done quietly.
                    sanitized = strippedKind || strippedBody,
                })
                : _Serialize(new { ok = false, error = result.Error });
        }
        catch (Exception exception)
        {
            return _Serialize(new { ok = false, error = exception.Message });
        }
    }

    [McpServerTool(Name = "send_prompt")]
    [Description("Hands a running agent session a turn: the text goes into that session and is SENT, so the agent starts working on it straight away. This is not a message — it is you typing into someone else's session on the operator's behalf, and whatever the session is allowed to do, it will now do without being asked again. Use it when the operator wants work started or steered in a session that is already open (\"tell the release worker to run the tests\"); use send_message when they only want an agent told something. Address it with a pane id from list_sessions, never by name. IT NEEDS THE OPERATOR'S CLICK, EVERY SINGLE TIME: an Allow/Deny row appears in the chat window showing the prompt word for word and which session receives it, it is never remembered, and nothing is sent until it is answered — so say out loud that it is waiting on their screen, and never treat a spoken \"yes\" as the approval, because it is not one and cannot become one." + AskingCanBeSwitchedOff + " Read the prompt back to the operator before you ask, in the words you are about to send: they are approving those words, and the row is where they will check them. A REFUSAL IS NORMAL — a pane that has closed, a terminal pane, your own session, or the operator simply saying no — so read the reason out and carry on. The reply's delivered field says whether the turn went in on the spot or is being held because the session is still coming up; while it is false the agent has not started, so do not report that it has. DO NOT SEND IT AGAIN WHILE IT IS BEING HELD: a session coming up holds exactly one turn, the one it was given first, and a second call is refused rather than replacing it — so a held turn is not lost and needs nothing from you but patience. Wait, or tell the operator it is still starting. WHAT THIS CANNOT DO: it cannot hand a turn to your own session, cannot reach a pane that is not an agent session (a plain terminal has a pane id and no agent on the other end), and cannot reach a delegated task (delegate_task), which runs with no pane and is invisible from where you are standing — say that rather than reporting an absence as a fact. It also cannot take a turn back: once the row is clicked the words are in that session's own transcript and its agent is acting on them.")]
    public async Task<string> SendPromptAsync(
        [Description("The pane id of the agent session to hand the turn to, exactly as list_sessions reports it. Read the session's NAME back to the operator before you ask — a pane id is not something anyone can check by ear, and the wrong one starts work in the wrong place.")] string paneId,
        [Description("The turn to submit, in the exact words that will be sent — the operator reads this verbatim on the approval row and is agreeing to these words, not to your description of them.")] string prompt)
    {
        try
        {
            if (_RefuseIfNotTheAssistant() is { } refusal)
            {
                return refusal;
            }

            if (string.IsNullOrWhiteSpace(prompt))
            {
                return _Serialize(new { ok = false, error = "A turn needs something to say; that prompt was empty." });
            }

            if (await _ApprovedAsync(
                    "The assistant wants to submit a turn in another session",
                    $"Send to session {paneId}, as the operator:\n\n{prompt}",
                    ConsentSourceCatalog.AssistantPrompt,
                    "assistant.prompt",
                    ConsentRisk.Dangerous).ConfigureAwait(false) is { } denial)
            {
                return denial;
            }

            var result = await gateway.SendPromptAsync(paneId, prompt).ConfigureAwait(false);
            return result.Ok
                ? _Serialize(new { ok = true, paneId = result.PaneId, name = result.SessionName, result.Delivered })
                : _Serialize(new { ok = false, error = result.Error });
        }
        catch (Exception exception)
        {
            return _Serialize(new { ok = false, error = exception.Message });
        }
    }

    /// <summary>
    /// Asks the operator, and returns the tool result to hand back when they said no — or null when they said yes.
    /// </summary>
    /// <remarks>
    /// <paramref name="action"/> is passed straight through to <see cref="ConsentRequest.Action"/>, which is rendered
    /// verbatim, and is composed at each call site out of the literal arguments rather than out of a sentence about
    /// them. That is the rule the type states and the reason it states it: the assistant's words are supplied by a
    /// model that can be argued into supplying different ones, so a card showing a friendly description of a hostile
    /// message is a card that approves the message. The pane id is shown rather than the session's name for the same
    /// reason — the id is what the cockpit will act on, and a name would be the assistant's rendering of it.
    /// <para>
    /// No broker means no operator to ask, and these two tools deliver into someone else's session, so the answer is
    /// no. <c>AllowRemember</c> is deliberately left off both: the operator's lever for "stop asking me about this"
    /// is the per-source bypass in Options (AC-575), which is per source and switchable back off, rather than a
    /// per-call promise made on a row that was about one particular message.
    /// </para>
    /// </remarks>
    private async Task<string?> _ApprovedAsync(string title, string action, string sourceLabel, string scope, ConsentRisk risk)
    {
        if (consent is null)
        {
            return _Serialize(new { ok = false, error = "This needs the operator's approval, and there is nobody here to ask." });
        }

        var decision = await consent.RequestConsentAsync(
            new ConsentRequest(title, action, new ConsentSource(AssistantIdentity.PaneId, null, sourceLabel), scope, risk))
            .ConfigureAwait(false);

        return decision.IsApproved
            ? null
            : _Serialize(new { ok = false, error = "The operator did not approve this." });
    }

    /// <summary>
    /// The gate, in one place so every tool on this server is covered by the same sentence rather than by its own
    /// copy of it. Returns the refusal to hand straight back, or null when the caller really is the assistant.
    /// </summary>
    /// <remarks>
    /// A request with no verified pane is refused too, and not because it might be an impostor: it is the shared
    /// app-lifetime key path (the in-process tool loop), which cannot be attributed to any session at all. There is
    /// no identity to check, so there is no way to establish this one — and the safe answer to "I cannot tell who
    /// this is" on a tool that starts processes in any workspace is no.
    /// <para>
    /// Deliberately a second copy of <c>AssistantReadMcpTools._RefuseIfNotTheAssistant</c> rather than a shared
    /// helper the two servers call. What would be shared is four lines; what would be gained is one place to weaken.
    /// Both copies compare against the same <see cref="AssistantIdentity.PaneId"/>, which is the constant that must
    /// not drift — and it already lives in Core for exactly that reason.
    /// </para>
    /// </remarks>
    private static string? _RefuseIfNotTheAssistant() =>
        string.Equals(McpRequestContext.CurrentPaneId, AssistantIdentity.PaneId, StringComparison.Ordinal)
            ? null
            : _Serialize(new { ok = false, error = NotTheAssistant });

    private static string _Serialize(object value) => JsonSerializer.Serialize(value, SerializerOptions);
}
