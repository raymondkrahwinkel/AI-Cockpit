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

// The `cockpit-assistant-agents` MCP tools (AC-545): the voice assistant's acting path — starting a session on
// a named desk and stopping one again. The read half is `AssistantReadMcpTools`, and the two are
// deliberately not one server; `AssistantIdentity.ActMcpServerName` says why.
// *The mount rule is copied, not re-invented.* Both gates are the ones AC-544 already has, and both are here
// for the same reasons written out on the read server:
//
// *1. It is not handed out.* The endpoint is registered `Internal` (AC-204), so it is in no picker and in
// no fan-out, and reaches only a launch that names it — `AssistantSessionHost.McpSelection` being the one
// place in the codebase that does.
//
// *2. It is not answered.* `_RefuseIfNotTheAssistant` runs first in every tool and returns
// *before* the gateway is touched. That is the gate that holds: the mount is a fact about configuration and
// configuration widens later by accident — an endpoint made non-internal, a profile that names the server, a spawn
// path that copies a selection it did not read. When that happens these tools sit in a session's context and still
// answer nobody, because what is checked is the pane `McpAuthMiddleware` stamped from the request's own
// per-session bearer, and no argument on any tool here can move it.
//
// *Why the stakes are higher here than on the read server.* These tools spend money and start processes. The
// second gate is therefore not the last one: on the defaults, a call raises the SDK permission prompt, which the
// chat window renders as an Allow/Deny row showing the literal profile, desk and folder. Nothing in this file is
// that gate and nothing here may become it — a spoken "yes" is a sentence in a transcript, and the only thing that
// resolves a permission is a click.
//
// *"On the defaults" carries that whole sentence, and the tool descriptions have to say so.* The assistant
// starts on `SessionOptionCatalog.DefaultPermissionMode` only as a floor: the Assistant Profile's own
// permission mode overrides it (`AssistantSessionHost._LaunchOptions`), so an operator who put
// `bypassPermissions` on that profile gets no row at all — and for the two tools that raise a cockpit card of
// their own, AC-575's per-source switch does the same one layer down, before the card is ever built
// (`ConsentService.RequestConsentAsync`). A description that promises a click unconditionally has the
// assistant announcing a card nobody will ever see, which is the same defect as promising no click, pointing the
// other way.
//
// *And whichever it was, it is over before the assistant hears about it (AC-768).* Every call here blocks on its
// own gate: the SDK prompt is answered, the consent card is answered, or neither was ever raised. A tool result is
// therefore always a decision already made, so telling the assistant to announce that permission is waiting on the
// operator's screen names a state it can never observe from a result — and it announced one on every spawn
// regardless, including where either switch above had already taken the row away. What the descriptions ask for
// instead is the outcome: it went through, or it was refused and why. `AskingCanBeSwitchedOff` is the one
// sentence that says both, written once because five copies of it are five places for it to stop being true.
//
// *This server scopes nothing.* The workspace is a required parameter rather than something derived, because
// the assistant sits on no desk to derive one from — see `SpawnTarget`, whose two factories are the two
// scoping rules, and whose remarks explain why a coordinator's stricter rule must not be built as a check bolted
// onto this one.
//
// *Two of these ask on their own account.* `send_message` and `send_prompt` reach into a session the
// assistant did not start, so they raise a cockpit consent card as well — under two separate sources
// (`ConsentSourceCatalog.AssistantMessage` and `ConsentSourceCatalog.AssistantPrompt`), so
// that an operator who lets the assistant leave notes unasked has not thereby let it start work unasked.
internal sealed class AssistantAgentMcpTools(
    IAssistantAgentGateway gateway,
    IAssistantMemory memory,
    IConsentBroker? consent = null)
{
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = false };

    // What a caller that is not the assistant is told. One sentence, and no detail about what it would have got —
    // the same wording the read server uses, so a session that reaches either one learns the same nothing.
    private const string NotTheAssistant =
        "This tool is the cockpit assistant's own. It is not available to an agent session.";

    // The caveat every "it needs their click" sentence on this server carries, spliced into each description rather
    // than retyped in it. See the class remarks for why it exists: the click is the default, not a guarantee, the
    // operator can switch the asking off ahead of time by two separate levers, and either way the call has already
    // waited out whatever gate there was before the assistant reads a result.
    private const string AskingCanBeSwitchedOff =
        " THE CLICK IS THE DEFAULT AND NOT A PROMISE: the operator can switch the asking off ahead of time — for this"
        + " kind of request in Options → Voice, or by giving the Assistant Profile a permission mode that bypasses"
        + " prompts — and then no row appears and the call simply goes through. NEVER SAY THAT AN APPROVAL IS WAITING"
        + " ON THEIR SCREEN: this call does that waiting itself and returns only once the row has been answered or"
        + " was never raised, so by the time you are reading a result nothing about it is still pending and there is"
        + " nothing there to point them at. Report what actually happened — it went through, or it was refused and"
        + " why — and never a click you expected, asked for, or think is still to come. WHAT DOES NOT CHANGE: the"
        + " gate is not yours to work around or to talk anyone past, and a spoken \"yes\" is not a click and cannot"
        + " become one, so never offer to take their approval by voice.";

    [McpServerTool(Name = "start_agent")]
    [Description("Starts an AI session on a workspace and leaves it running there as an ordinary pane — the same kind of pane the operator's own New-session dialog makes, with its own transcript and its own approvals. YOU MUST NAME THE WORKSPACE. You sit on no desk yourself, so there is nothing for the cockpit to infer one from; call list_workspaces to turn the desk the operator named into an id, and ask them which one if what they said matches nothing there. IF THEY NAMED NO DESK IN THIS INSTRUCTION, DO NOT CARRY ONE OVER FROM EARLIER IN THE CONVERSATION: they may well have moved on since. The desk they are looking at right now is the one list_workspaces reports as isActive, and that is what \"here\" means — use it, and say which desk you used. NAME THE PROFILE, UNLESS projectId ALREADY SUPPLIES ONE: the profile picks the provider and the model, so starting something on a large model because no smaller one was named is a bill nobody agreed to — that is still true with projectId, it just means the project's own default now stands in for a label you did not have to type. If the operator did not say which and projectId names no default either, call list_profiles: when exactly one fits what they asked for, take it and say so, and otherwise ask, naming only the ones that fit. Whichever way a profile was arrived at, READ resolvedProfile BACK OFF THE RESULT AND SAY WHICH ONE RAN — never assume, because a project's default can change out from under a call that named none. BY DEFAULT THE OPERATOR STILL HAS TO APPROVE IT: this call raises an Allow/Deny row in the cockpit's chat window showing the profile, the desk and the folder, and nothing starts until it is clicked. The call itself waits that out, so what comes back is the answer and never a question still open; never treat a spoken \"yes\" as the approval, because it is not one and cannot become one." + AskingCanBeSwitchedOff + " A REFUSAL IS NORMAL: if this comes back with ok false, read the reason out in a sentence and carry on with whatever you are still allowed to do, rather than treating it as the end of the conversation. IF YOU GAVE A prompt, CHECK promptDelivered — true means it went in as a submitted turn; false means the pane exists but the hosted CLI was not yet reading input, so the brief is being held and will go out on its own the moment it can, exactly once. Do not call this again to retry it and do not send the same brief through send_prompt while it is false — that would be a second turn, not a longer one. null means no prompt was given. WHAT THIS CANNOT DO: a delegated task (delegate_task) has no pane, so it is not something this tool can start, is not in any list, and cannot be stopped here. If you are asked about that kind of work, say it is invisible from where you are standing instead of reporting an absence as a fact.")]
    public async Task<string> StartAgentAsync(
        [Description("The id of the workspace the session is to appear on — the desk, not its tab label. Required, and never guessed: get it from list_workspaces, which shows every desk including the empty ones, or from list_sessions, where each session reports the desk it sits on. If neither turns up the desk the operator meant, ask them which one rather than picking a plausible id.")] string workspaceId,
        [Description("The profile to run under, by its label exactly as the cockpit knows it. Required unless projectId names a project with its own default profile — leave it out then and that default is used, and read resolvedProfile off the result to see which one. Naming one here always wins over a project's default. This is what decides provider, model and therefore cost — an unknown label is refused rather than quietly swapped for a default, because the default might be the expensive one.")] string? profile = null,
        [Description("The project this session works on, by its id from list_projects. Given, this is the one call that applies everything that project carries — its own working directory, its default profile when none was named, its worktree isolation setting, its behaviour prompt, its memory/resources and its MCP selection — instead of you reading cockpit.json's project record yourself and retyping pieces of it into workingDirectory/prompt. Left out, the folder start_agent lands in still gets matched to a project automatically when it happens to be one of its folders (unchanged, AC-682); this argument is only for making that match explicit and reliable rather than a guess from the folder. An id that names no project is refused, never silently ignored.")] string? projectId = null,
        [Description("The first message to hand the session once it is up, in the words the work should be described in. Left out, the session comes up waiting for someone to type in it. Write it as a brief for an agent that cannot hear the conversation you are having — it gets this text and nothing else. IF YOU WANT TO HEAR BACK, ASK FOR IT HERE AND GIVE YOUR ADDRESS: you are on every desk's roster as the pane id `cockpit-assistant`, so tell the agent to notify that id when it is done, blocked, or about to touch something another session is holding. A message sent there reaches you on your next turn or your next tool call, with nobody having to pass it on — without asking, the only news you get is what you go looking for.")] string? prompt = null,
        [Description("The folder to run in. Left out, the profile's own default folder is used. Give a full path; a relative one means nothing here, since you are not standing in any directory.")] string? workingDirectory = null,
        [Description("What to call the pane, so the operator can find it in the sidebar. Left out, the profile and the clock name it. A name that says what the work is (\"AC-545 tests\") is worth far more than one that says what it runs on.")] string? name = null,
        [Description("Which route to start on: \"tty\" for the provider's own terminal, \"sdk\" for the chat/SDK session. LEAVE THIS OUT unless the operator actually said which — the profile is already set to one and that is nearly always the right answer. It is here for exactly one request: \"the same profile, but as an SDK session\", which is a thing they can pick in the New-session dialog too. It is not a way to start work by another route: everything you can start goes through this tool, appears as a pane, and is written down.")] string? kind = null,
        [Description("Provider options to start this one session with, as key/value — \"that profile, but at low effort\". LEAVE IT OUT unless the operator asked for something the profile is not set to; the profile's own values are the right answer nearly every time. ONLY THE KEYS YOU NAME CHANGE: everything else stays exactly what the profile says, so this never resets anything you did not mention. USE THE PROVIDER'S OWN KEYS, WHICH list_profiles SHOWS YOU under `Options` for that profile — a key that provider does not declare is refused with a reason, and so is a value it does not take, because Codex has no idea what `effort` means. PERMISSION-MODE IS NEVER YOURS TO SET, and neither is Codex's `sandbox`: what a session is allowed to do to the machine is whatever the profile was deliberately configured with, and naming it here is refused outright, not asked about. If a session needs to run differently in that respect, the answer is a different profile.")] Dictionary<string, string>? options = null,
        [Description("Whether this session runs in its own git worktree rather than the operator's real checkout. LEAVE THIS OUT — omitted, it inherits whatever the folder's project is set to (or runs unisolated when there is none or it is not set), which is the right answer nearly every time. true asks to isolate even where the project does not. false IS REFUSED, ALWAYS: asking to run unisolated where isolation would otherwise apply is not something you get to decide — it would put the session in the operator's own working tree, and that choice is theirs, made on the project, not yours to make per spawn.")] bool? isolate = null)
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
                projectId,
                prompt,
                workingDirectory,
                name,
                kind,
                options,
                isolate)).ConfigureAwait(false);

            return result.Ok
                ? _Serialize(new
                {
                    ok = true,
                    paneId = result.PaneId,
                    name = result.SessionName,
                    workingDirectory = result.WorkingDirectory,
                    workspaceId,
                    projectId,
                    // The profile actually used (AC-773) — the label given, or, when none was, the resolved
                    // project's own default. Replaces the old `profile` field, which only ever echoed the raw
                    // argument back and said nothing when that argument was left out.
                    resolvedProfile = result.ResolvedProfileLabel,
                    promptDelivered = result.PromptDelivered,
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
    [Description("Closes a running AI session, named by its pane id — on any desk, not just one. Take the pane id from list_sessions; there is no lookup by name here, because two sessions can carry the same one and stopping the wrong session loses work that was in progress. LIKE STARTING, THIS BY DEFAULT NEEDS THE OPERATOR'S CLICK: an Allow/Deny row appears in the chat window naming what is about to be closed, and nothing happens until it is answered — by the call, which returns with the outcome and not with a question." + AskingCanBeSwitchedOff + " A REFUSAL IS NORMAL — a pane that is already gone, one that is a plain terminal rather than an agent, one that runs inside a workspace's own surface rather than as a pane, or your own session, which you do not get to end mid-sentence — so read the reason out and carry on. WHAT THIS CANNOT DO: a delegated task (delegate_task) runs without a pane, so it cannot be stopped here and never appears in any list you can see. Say so rather than reporting that there was nothing to stop.")]
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
    [Description("Renames a running session — the name in its header and in the sidebar, which is the one thing the operator finds it back by. TAKE THE PANE ID FROM list_sessions AND NEVER RENAME BY NAME: two sessions can carry the same one, and renaming the wrong session relabels work somebody is in the middle of. Read the pane id back together with the session's current name before you ask, because a pane id cannot be checked by ear. THE NAME YOU SET IS THE OPERATOR'S OWN: nothing overwrites it afterwards — not a ticket a plugin links to that session later, not a restart — so use the words they said rather than a tidier version of them. LIKE EVERYTHING ON THIS SERVER IT BY DEFAULT NEEDS THEIR CLICK: an Allow/Deny row appears in the cockpit's chat window, and nothing changes until it is answered — which the call waits for on your behalf." + AskingCanBeSwitchedOff + " A REFUSAL IS NORMAL — a pane that has since closed, one that runs inside a workspace's own surface rather than as a pane, my own session, or an empty name — so read the reason out in a sentence and carry on. WHAT THIS CANNOT DO: it does not rename the desk the session sits on (that is rename_workspace), and a delegated task (delegate_task) has no pane, so it cannot be renamed here and is in no list you can see.")]
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
    [Description("Renames a desk — the tab label the operator reads and says out loud. Take the id from list_workspaces and never guess one from a name, because two desks may be called the same thing. THIS ONE DOES NOT BRING THEM ANYWHERE: renaming a desk is not walking to it, so whatever is on their screen stays there, including when the desk you are renaming is not the one they are looking at. The name is taken exactly as given and is not made unique, so read it back before you ask for it. LIKE STARTING A SESSION, IT BY DEFAULT NEEDS THEIR CLICK on the Allow/Deny row in the chat window, which the call waits for before it comes back." + AskingCanBeSwitchedOff + " A refusal is normal (an id that names no desk, an empty name); read it out and carry on. Renaming a desk changes nothing about what runs on it.")]
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
    [Description("Lists the profiles a session can be started under, each with the provider it runs on, the model it pins, and `Options` — what that profile is actually configured to run at, in its provider's own words. READ `Options` BEFORE YOU PICK ONE: it is where a profile says it runs in a bypass permission mode, or on the expensive model at the highest effort. PROVIDERS DO NOT SHARE A SHAPE. A Claude profile has permission-mode/model/effort; a Codex one has a sandbox and no effort at all; a local model declares none of them, and an empty `Options` means exactly that — not that its settings are hidden somewhere else. Each entry carries the provider's own `Key` and `Label`, the `Value` in force with a readable `ValueLabel`, and `SetOnProfile`: false means nobody chose it and the provider's own default applies. CALL THIS BEFORE ASKING WHICH PROFILE — a question you cannot offer the answers to makes the operator do your work. USE THE PROVIDER FIELD, NOT THE LABEL'S WORDING: if they said \"two Claude agents\", the ones that count are the ones whose provider is Claude, whatever they happen to be called. IF EXACTLY ONE MATCHES WHAT THEY ASKED FOR, JUST USE IT and say which one you took. If several match, name only those — reciting profiles they have already ruled out is noise. If none match, say so and read out what there is. This is the field that decides the model and therefore the cost, which is why it is never guessed and never defaulted.")]
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
    [Description("Makes a new, empty Sessions desk with the name given and returns its id, ready to be spawned onto. Use it when the operator asks for somewhere new to put work, or when the desk they named does not exist and they would rather have it made than pick another. THIS ONE DOES BRING THEM THERE: an empty new desk has nothing on it to interrupt, and asking for a desk to be made is asking to be shown it — say so, because their screen will change. LIKE STARTING A SESSION, IT BY DEFAULT NEEDS THEIR CLICK on the Allow/Deny row in the chat window." + AskingCanBeSwitchedOff + " The name is taken exactly as given and is not made unique, so read it back before you ask for it. Making a desk does not put anything on it — that is still a separate start_agent, with its own approval.")]
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
    [Description("Closes an empty SESSIONS desk and takes its tab away — the counterpart of create_workspace. ONLY A SESSIONS DESK: the operator's own ✕ closes any desk, this closes only the kind that holds sessions, so it is the narrower of the two. THE DESK HAS TO BE EMPTY FIRST: this is refused for as long as anything is still on it, and the reason says how many. That is the design and not a shortcoming — closing a desk would stop everything on it in one go, and each of those sessions is a stop the operator gets to approve on its own. So do it in that order: list_workspaces for the count, stop_agent per session, then this. If they asked for all of it in one breath (\"stop everything on Henk and then get rid of it\"), that is an ordinary request — carry it out as several calls, and say where you are, rather than reporting the desk gone while it is still there. YOU MUST NAME THE WORKSPACE BY ITS ID, never by its label: two desks can be called the same thing and this one does not come back. Take the id from list_workspaces and read the desk's NAME back to the operator before you ask, because an id is not something anyone can check by ear. LIKE STARTING A SESSION, IT BY DEFAULT NEEDS THEIR CLICK on the Allow/Deny row in the chat window, and nothing goes until it is answered — the call waits for that answer and hands you the outcome, so never treat a spoken \"yes\" as the approval." + AskingCanBeSwitchedOff + " A REFUSAL IS NORMAL: a desk that is not a sessions desk, sessions still on it, the only desk left (the cockpit always needs one to show), or the projects overview, which is a fixture and never closes. Read the reason out in a sentence and carry on. WHAT THIS CANNOT DO: it closes only sessions desks. A dashboard, the projects overview and any desk a plugin brought are all out of reach here, whatever is or is not on them — what they hold is not sessions, so there is nothing for you to count, nothing for you to stop, and the approval row could not have named what closing one would throw away. The operator closes those from the tab itself, where the app tells them what goes; say that, rather than trying another id. And it stops nothing: it does not close the sessions on a desk for you, does not move them anywhere, and does not empty the desk on its way out — emptying it is stop_agent's job, one session at a time, each with its own approval. It does not touch a delegated task either: that runs without a pane, so it is on no desk, and closing a desk neither ends it nor tells you it was there. And it cannot be undone: the tab, the arrangement on it and its place in the strip are gone, so the name you read back is the last chance anyone has to say no.")]
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
    [Description("Leaves a message in a running agent session's inbox — the same inbox the agents on a desk use to talk to each other, so the recipient reads yours exactly as it reads theirs. This TELLS an agent something; it does not make it do anything. The recipient decides what to do with what you wrote, and anything that needs the operator's approval still needs it — so use this for what an agent would want to know (\"the operator changed their mind about the branch\", \"another session is about to touch that worktree\"), and use send_prompt when the operator actually wants work started. Address it with a pane id from list_sessions, never by name: two sessions can be called the same thing. BY DEFAULT IT NEEDS THE OPERATOR'S CLICK: an Allow/Deny row appears in the chat window showing your message word for word and which session gets it, and nothing is delivered until it is answered — the call waits for that answer and hands you the outcome, so never treat a spoken \"yes\" as the approval." + AskingCanBeSwitchedOff + " A REFUSAL IS NORMAL — a pane that has closed, a terminal pane with no agent on it, your own session, or a recipient whose inbox is full — so read the reason out and carry on. The reply says whether the message will reach the recipient on its own with its next turn (deliversAtTurnStart) or only when that session next calls read_inbox; when it is false, do not tell the operator the agent has been told, because it has not been yet. Sending the identical message twice while the first is still unread adds nothing and comes back deduplicated. WHAT THIS CANNOT DO: it cannot reach your own session, cannot reach a pane that is not an agent session (a plain terminal has a pane id and nobody reading it), and cannot reach a delegated task (delegate_task), which runs with no pane and is invisible from where you are standing — say that rather than reporting an absence as a fact. It also does not interrupt: nothing is woken, nobody is pulled off what they are doing, and delivery is at the recipient's next turn at the earliest. If the operator needs something to happen now, this is the wrong tool and you should say so.")]
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

            var approval = await _ApprovedAsync(
                "The assistant wants to put a message in another session's inbox",
                $"Send to session {addressee}\nkind: {label}\n\n{text}",
                ConsentSourceCatalog.AssistantMessage,
                "assistant.message",
                ConsentRisk.LowRisk).ConfigureAwait(false);
            if (!approval.Ok)
            {
                return _Serialize(new { ok = false, error = approval.Error });
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
                    // "asked" | "bypassed" | "remembered" (AC-759) — what the consent check above actually did,
                    // so the assistant reports that rather than assuming a click is still coming.
                    approval = approval.Label,
                })
                : _Serialize(new { ok = false, error = result.Error });
        }
        catch (Exception exception)
        {
            return _Serialize(new { ok = false, error = exception.Message });
        }
    }

    [McpServerTool(Name = "send_prompt")]
    [Description("Hands a running agent session a turn: the text goes into that session and is SENT, so the agent starts working on it straight away. This is not a message — it is you typing into someone else's session on the operator's behalf, and whatever the session is allowed to do, it will now do without being asked again. Use it when the operator wants work started or steered in a session that is already open (\"tell the release worker to run the tests\"); use send_message when they only want an agent told something. Address it with a pane id from list_sessions, never by name. BY DEFAULT IT NEEDS THE OPERATOR'S CLICK, AND NEVER REMEMBERS ONE: an Allow/Deny row appears in the chat window showing the prompt word for word and which session receives it, it is never remembered even when it does appear, and nothing is sent until it is answered — the call waits for that answer, so a result in your hands is a decision already made; never treat a spoken \"yes\" as the approval, because it is not one and cannot become one." + AskingCanBeSwitchedOff + " Read the prompt back to the operator before you ask, in the words you are about to send: they are approving those words, and the row is where they will check them. A REFUSAL IS NORMAL — a pane that has closed, a terminal pane, your own session, or the operator simply saying no — so read the reason out and carry on. The reply's delivered field says whether the turn went in on the spot or is being held because the session is still coming up; while it is false the agent has not started, so do not report that it has. DO NOT SEND IT AGAIN WHILE IT IS BEING HELD: a session coming up holds exactly one turn, the one it was given first, and a second call is refused rather than replacing it — so a held turn is not lost and needs nothing from you but patience. Wait, or tell the operator it is still starting. WHAT THIS CANNOT DO: it cannot hand a turn to your own session, cannot reach a pane that is not an agent session (a plain terminal has a pane id and no agent on the other end), and cannot reach a delegated task (delegate_task), which runs with no pane and is invisible from where you are standing — say that rather than reporting an absence as a fact. It also cannot take a turn back: once the row is clicked the words are in that session's own transcript and its agent is acting on them.")]
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

            var approval = await _ApprovedAsync(
                "The assistant wants to submit a turn in another session",
                $"Send to session {paneId}, as the operator:\n\n{prompt}",
                ConsentSourceCatalog.AssistantPrompt,
                "assistant.prompt",
                ConsentRisk.Dangerous).ConfigureAwait(false);
            if (!approval.Ok)
            {
                return _Serialize(new { ok = false, error = approval.Error });
            }

            var result = await gateway.SendPromptAsync(paneId, prompt).ConfigureAwait(false);
            return result.Ok
                ? _Serialize(new
                {
                    ok = true,
                    paneId = result.PaneId,
                    name = result.SessionName,
                    result.Delivered,
                    // "asked" | "bypassed" | "remembered" (AC-759) — see send_message for why this is reported
                    // rather than assumed. Never "remembered" here in practice: a Dangerous request is never
                    // offered it (ConsentService), but the label still comes from the decision rather than being
                    // hand-picked here, so that stays true by construction and not by this call site remembering it.
                    approval = approval.Label,
                })
                : _Serialize(new { ok = false, error = result.Error });
        }
        catch (Exception exception)
        {
            return _Serialize(new { ok = false, error = exception.Message });
        }
    }

    [McpServerTool(Name = "watch_session")]
    [Description("Asks the cockpit to tell you when something happens in another session, so you stop calling list_sessions to find out. Arm it right after start_agent and then leave the session alone: the cockpit watches it for you and puts a message in your inbox when one of the events below happens, which reaches you on your next turn or your next tool result. Nothing is watched until you say so, and a watch costs nothing while nothing happens. THE FIVE EVENTS, and what each is actually for: `busy-to-idle` = it stopped working — which is either finished or a question waiting for an answer, and the transcript lines the message carries are how you tell those apart, so read them before reporting either. `needs-attention` = it is stopped on a permission nobody has clicked; this is the one an agent can never tell you itself, because it cannot call a tool while it waits. `gone` = the pane disappeared without ever having reported finishing or asking — the fell-over-quietly case — and the watch is dropped with it. `stuck` = it has written nothing for a while; counted in transcript rows and never in status, so it is the one that still fires when the status field itself is wrong. `pattern` = a line matching your regular expression appeared, reported every time a fresh one does. EVERY MESSAGE CARRIES THE LAST FEW TRANSCRIPT LINES, so you rarely need read_transcript afterwards — say what the session actually said, not that it 'changed state'. This starts nothing and changes nothing, so it needs no approval and nothing appears on the operator's screen. A REFUSAL IS NORMAL: a pane id that resolves to nothing, `stuck` or `pattern` on a terminal-route session (it keeps no transcript here), a pattern that is not a valid regular expression, or an event name that is not one of the five. Read the reason and carry on. Arming a pane again replaces what was armed on it, rather than adding a second watch.")]
    public async Task<string> WatchSessionAsync(
        [Description("The pane id of the session to watch, exactly as list_sessions or start_agent reports it. Never a name — two sessions can be called the same thing.")] string paneId,
        [Description("Which of the five to watch for, one or more of: busy-to-idle, needs-attention, gone, stuck, pattern. Arming what you actually want to hear about is the whole point — a watch on all five for a session you only want the end of is noise you will have to read.")] string[] events,
        [Description("For `stuck` only: how many minutes without a new transcript row counts as stuck. Left out, a sensible default applies. Pick it from the work — a long build writes nothing for minutes at a time and is not stuck.")] int? afterMinutes = null,
        [Description("For `pattern` only: the regular expression matched against each NEW transcript line, .NET syntax. An error signature, a ticket number, a phrase an agent uses when it is blocked. Refused here and now if it does not compile.")] string? pattern = null)
    {
        try
        {
            if (_RefuseIfNotTheAssistant() is { } refusal)
            {
                return refusal;
            }

            var result = await gateway.WatchSessionAsync(paneId, events, afterMinutes, pattern).ConfigureAwait(false);
            return result.Ok
                ? _Serialize(new { ok = true, paneId, name = result.Name, watching = events })
                : _Serialize(new { ok = false, error = result.Error });
        }
        catch (Exception exception)
        {
            return _Serialize(new { ok = false, error = exception.Message });
        }
    }

    [McpServerTool(Name = "unwatch_session")]
    [Description("Stops watching a session you armed with watch_session — no further messages about it from the moment this returns. Call it when you stop a session, and when you have had the answer you were waiting for and the rest would only be noise. A pane the cockpit finds gone unwatches itself, so you never have to clean one of those up. `wasWatching` false means nothing was armed on that pane: not an error, and worth reading rather than reporting a stop that never happened. This changes nothing about the session itself — it keeps running, you simply stop being told about it.")]
    public async Task<string> UnwatchSessionAsync(
        [Description("The pane id whose watch is to be dropped, exactly as it was armed.")] string paneId)
    {
        try
        {
            if (_RefuseIfNotTheAssistant() is { } refusal)
            {
                return refusal;
            }

            var wasWatching = await gateway.UnwatchSessionAsync(paneId).ConfigureAwait(false);
            return _Serialize(new { ok = true, paneId, wasWatching });
        }
        catch (Exception exception)
        {
            return _Serialize(new { ok = false, error = exception.Message });
        }
    }

    [McpServerTool(Name = "worktree_handover")]
    [Description("Hands a worktree you (the assistant) made with worktree_create over to a running agent session, so that session owns it from then on — exactly as if it had made the worktree itself: released and cleaned up when that session closes, never left stuck on you. Use it for a worktree you made ahead of a later start_agent, for one you want to give to a session that is already running, or to re-own a worktree of yours left over from before this tool existed. Nothing is asked of the operator: no file is touched, no worktree is removed, only who owns it changes. REFUSED, HARD, NOT BEST-EFFORT: the worktree at `path` is not yours to give away (it belongs to a different session, or is not a worktree the cockpit manages — call worktree_list for the current paths), `paneId` names no running agent session (already closed, or a plain terminal), or `paneId` is your own.")]
    public async Task<string> WorktreeHandoverAsync(
        [Description("The worktree's path, as returned by worktree_create or worktree_list. Must currently be owned by you (the assistant) — never a worktree a session made or is running in.")] string path,
        [Description("The pane id of the running agent session to hand the worktree to, exactly as list_sessions or start_agent reports it. Never your own.")] string paneId)
    {
        try
        {
            if (_RefuseIfNotTheAssistant() is { } refusal)
            {
                return refusal;
            }

            var result = await gateway.HandoverWorktreeAsync(path, paneId).ConfigureAwait(false);
            return result.Ok
                ? _Serialize(new { ok = true, path = result.Path, branch = result.Branch, paneId, name = result.SessionName })
                : _Serialize(new { ok = false, error = result.Error });
        }
        catch (Exception exception)
        {
            return _Serialize(new { ok = false, error = exception.Message });
        }
    }

    [McpServerTool(Name = "bind_shared_project")]
    [Description("Adds a project a colleague shares — one that list_shared_projects reported — to this machine, so it becomes an ordinary project sessions can be started on. Exactly what the operator's own \"Add to my projects…\" card does on the Projects page, and it produces the same project. CALL list_shared_projects FIRST AND USE AN ID FROM IT: the id says which connection the project comes from, and a project that is already added is refused rather than added twice. THREE THINGS ARE NOT SHARED AND YOU MUST ASK FOR THEM, because they are facts about this machine that nobody else's definition can carry: the FOLDER the project lives in here, the PROFILE its sessions run under, and — only when the definition names any — a local path for each resource row it lists. NEVER INVENT ANY OF THE THREE: a guessed folder points the project at somebody else's work, and a guessed profile decides the model and the bill. If one is missing the call is refused and the reason says which, in words you can put straight to the operator. THE NAME, THE BEHAVIOUR, THE MCP CHOICE AND THE MEMORY COME WITH THE PROJECT — do not ask about those and do not offer to change them; they are the colleague's, and this step only fills in what is yours. IT DOES NOT CLONE: the folder has to exist already, so if they have not checked the project out yet, say so and let them clone it (or point at where it is) rather than looking for another way. BY DEFAULT IT NEEDS THEIR CLICK: an Allow/Deny row appears in the cockpit's chat window showing the project id, THE FOLDER and the profile, and nothing is written until it is answered — the call waits that out, so what comes back is a decision already made." + AskingCanBeSwitchedOff + " A REFUSAL IS NORMAL — an id no connection offers, a project already added, a folder that is not there, a profile that does not exist, or a connection that has gone unreachable or lost the project since you listed it — so read the reason out and carry on. ADDING IT STARTS NOTHING: no session runs and no work begins, which is still a separate start_agent with its own approval.")]
    public async Task<string> BindSharedProjectAsync(
        [Description("The shared project's id, exactly as list_shared_projects reports it. Never a name and never a guess: the id carries the connection it came from, so a made-up one names no source at all.")] string sharedProjectId,
        [Description("The full path of the folder on this machine that holds this project. Required, asked of the operator, and never invented — it must already exist, because this does not clone. A relative path means nothing here; you are standing in no directory.")] string sourceDirectory,
        [Description("The profile this project's sessions default to, by its label exactly as the cockpit knows it. Required — a shared project brings no profile of its own — and it is what decides provider, model and therefore cost, so call list_profiles and let the operator choose rather than picking one that sounds close.")] string profile,
        [Description("A local path for each resource row the shared definition names but cannot carry a value for, in the order the refusal listed them. LEAVE IT OUT ON THE FIRST CALL: most projects name none, and if this one does the call comes back refused with each row spelled out in order, which is what you take to the operator. One entry per row, no blanks.")] string[]? resources = null)
    {
        try
        {
            if (_RefuseIfNotTheAssistant() is { } refusal)
            {
                return refusal;
            }

            // The card below is three labelled lines, and it is rendered verbatim — so a newline inside one of these
            // three arguments writes a `folder:` line of the caller's own choosing under a folder nobody approved.
            // Refused rather than escaped: a project id, a path and a profile label have no legitimate shape with a
            // control character in it, so there is nothing here to salvage.
            if (_RefuseIfNotOneLine(("project id", sharedProjectId), ("folder", sourceDirectory), ("profile", profile)) is { } malformed)
            {
                return malformed;
            }

            // Asked before the definition is read, so the operator is never made to wait on a connection for a card
            // they were going to deny. The cost of that order is a project whose machine-specific resource rows are
            // only discovered afterwards — the retry then raises a second card. Worth it: those rows are the rare
            // case (see `SharedProjectBindingDialogViewModel.ResourceRows`), a card the operator denied is the
            // common one, and the alternative is reaching a colleague's server on the strength of a call nobody has
            // agreed to yet.
            //
            // LowRisk, and deliberately not Dangerous: what goes through is a registration in `cockpit.json` of a
            // definition somebody on the operator's team already wrote, pointed at a folder shown on the card.
            // Nothing runs, nothing is written outside it, and a second call cannot overwrite or duplicate it (the
            // gateway refuses an id already added). Starting anything on it is `start_agent`, with its own gate.
            var approval = await _ApprovedAsync(
                "The assistant wants to add a shared project to this machine",
                $"Add shared project {sharedProjectId}\nfolder: {sourceDirectory}\nprofile: {profile}",
                ConsentSourceCatalog.AssistantProjectBinding,
                "assistant.bind-shared-project",
                ConsentRisk.LowRisk).ConfigureAwait(false);
            if (!approval.Ok)
            {
                return _Serialize(new { ok = false, error = approval.Error });
            }

            var result = await gateway.BindSharedProjectAsync(sharedProjectId, sourceDirectory, profile, resources)
                .ConfigureAwait(false);

            return result.Ok
                ? _Serialize(new
                {
                    ok = true,
                    projectId = result.ProjectId,
                    name = result.Name,
                    sourceName = result.SourceName,
                    sourceDirectory = result.SourceDirectory,
                    approval = approval.Label,
                })
                : _Serialize(new { ok = false, error = result.Error });
        }
        catch (Exception exception)
        {
            return _Serialize(new { ok = false, error = exception.Message });
        }
    }

    [McpServerTool(Name = "create_project")]
    [Description("Creates a brand-new local project — not one shared by a colleague, that is bind_shared_project — and returns its id. Exactly what \"New project\" on the Projects page does for the same answers: same name rule, same save — but not the dialog's full field set: this sets name, description, folder, default profile, behaviour prompt, worktree isolation, MCP selection, category and plugin fields, and nothing past those, so memory/resources, a logo, a git URL and the free-form \"additional info\" box still need the operator to open the dialog themselves. CALL list_shared_projects FIRST WHEN THE NAME MIGHT ALREADY BE SHARED: a name matching a project a connection already shares is refused rather than quietly duplicated next to it — bind_shared_project is very likely the door meant instead. ONLY name IS REQUIRED. Every other field is optional and, left out, the project simply has no opinion on it: no folder (an administrative project is a perfectly good project), every MCP server offered, no worktree isolation, no behaviour prompt. FOUR FIELDS DECIDE HOW EVERY SESSION ON THIS PROJECT RUNS, NOT MERELY HOW IT IS LABELLED — sourceDirectory, enabledMcpServerNames, isolateInWorktreeByDefault and behaviorPrompt — and that is what separates this tool from create_workspace, which only ever opens an empty tab: read these four back to the operator before you ask, because they are what start_agent will use on every session here afterwards, without asking again. sourceDirectory, WHEN GIVEN, MUST BE A FULL PATH THAT ALREADY EXISTS: this tool does not clone or create a folder, so ask the operator for one that is already checked out, or leave it out. pluginFields KEYS MUST BE ONES A PLUGIN REGISTERED (list_projects shows what an existing project is already linked under) — a made-up key is refused rather than stored where no plugin will ever read it. BY DEFAULT THE OPERATOR STILL HAS TO APPROVE IT: an Allow/Deny row appears in the cockpit's chat window showing the name and the four session-behaviour fields above, and nothing is written until it is answered." + AskingCanBeSwitchedOff + " A REFUSAL IS NORMAL: a blank name, a name already shared elsewhere, a folder that is not there, or an unknown plugin field key — read the reason out and carry on. THIS STARTS NOTHING: no session runs and no work begins, which is still a separate start_agent with its own approval. IT DOES NOT TAKE A PASSWORD OR A SHARED-SOURCE NAME — those belong to a project already bound to a shared definition (bind_shared_project), not a fresh local one.")]
    public async Task<string> CreateProjectAsync(
        [Description("The project's display name. Required — the one thing every other surface shows it by. Free to collide with another local project's name, but refused if a connection already shares a project under this exact name; call list_shared_projects to check first if that seems likely.")] string name,
        [Description("Free-text note on what this project is, shown under its name in the launcher and the manager. Left out, none.")] string? description = null,
        [Description("The folder its sessions start in — one of the four fields that decide how every session here runs. Give a full path that already exists; this tool does not clone. Left out, this is an administrative project with no folder of its own.")] string? sourceDirectory = null,
        [Description("The profile its sessions start under, by label exactly as the cockpit knows it. Left out, a session started here falls back to whatever it would otherwise use.")] string? defaultProfileLabel = null,
        [Description("Appended to every session's system prompt here, on top of whatever its profile already says — one of the four fields that decide how every session here runs. Left out, nothing is appended.")] string? behaviorPrompt = null,
        [Description("Whether new sessions here isolate in their own git worktree by default — one of the four fields that decide how every session here runs. Left out, false: sessions run in the operator's real checkout unless told otherwise per spawn.")] bool isolateInWorktreeByDefault = false,
        [Description("Names of MCP servers this project's sessions start ticked — one of the four fields that decide how every session here runs. Left out, every offered server starts ticked, following the registry as it changes.")] string[]? enabledMcpServerNames = null,
        [Description("Which group this project sits under in the manager's list. Left out, it groups under \"Uncategorized\".")] string? category = null,
        [Description("What this project is called elsewhere, keyed by the field a plugin registered — e.g. {\"youtrack.project\": \"AC\"}, the same shape list_projects reports. A key no installed plugin registered is refused.")] Dictionary<string, string>? pluginFields = null)
    {
        try
        {
            if (_RefuseIfNotTheAssistant() is { } refusal)
            {
                return refusal;
            }

            // The card below renders labelled lines verbatim, so a newline in a single-line field would forge one
            // nobody approved — same reason `bind_shared_project` refuses rather than escapes. `behaviorPrompt`/
            // `description` are legitimately multi-line and are normalised and bounded, not refused, below.
            // `category` is deliberately not checked here (AC-799 review finding 11): unlike the fields below it
            // never reaches the card, so the one thing this guard exists for — a newline forging a line under a
            // value nobody approved — cannot happen through it.
            var lineChecks = new List<(string Name, string Value)> { ("name", name) };
            if (sourceDirectory is not null)
            {
                lineChecks.Add(("sourceDirectory", sourceDirectory));
            }

            if (defaultProfileLabel is not null)
            {
                lineChecks.Add(("defaultProfileLabel", defaultProfileLabel));
            }

            foreach (var serverName in enabledMcpServerNames ?? [])
            {
                lineChecks.Add(("an entry in enabledMcpServerNames", serverName));
            }

            if (_RefuseIfNotOneLine([.. lineChecks]) is { } malformed)
            {
                return malformed;
            }

            var normalizedDescription = AgentMessageContent.Normalize(description, out _);
            var normalizedBehaviorPrompt = AgentMessageContent.Normalize(behaviorPrompt, out _);
            if (normalizedDescription.Length > AgentMessageContent.MaxBodyLength)
            {
                return _Serialize(new
                {
                    ok = false,
                    error = $"`description` is {normalizedDescription.Length} characters and the limit is {AgentMessageContent.MaxBodyLength}. Shorten it, or leave it out.",
                });
            }

            if (normalizedBehaviorPrompt.Length > AgentMessageContent.MaxBodyLength)
            {
                return _Serialize(new
                {
                    ok = false,
                    error = $"`behaviorPrompt` is {normalizedBehaviorPrompt.Length} characters and the limit is {AgentMessageContent.MaxBodyLength}. Shorten it, or leave it out.",
                });
            }

            // An empty array and no argument at all mean the same thing — "every server, following the registry"
            // (`ProjectMcpOverlay.IsSelectedByDefault` reads a non-null empty list as "nothing is selected", the
            // opposite of what the card below would otherwise say) — so this collapses `[]` to `null` once, before
            // either the card or the gateway sees it (AC-799 review finding 1). Without this the two disagreed: the
            // card would say "every server" while what got stored selected none.
            var normalizedEnabledMcpServerNames = enabledMcpServerNames is { Length: 0 } ? null : enabledMcpServerNames;

            // LowRisk — weighed against `ConsentRisk`'s own text, not borrowed from `bind_shared_project`
            // (AC-799 review finding 4). Its bar is "an idempotent, low-consequence action", and this does not
            // clear the first half on its own: every call makes a fresh `Project.Create` id, so a repeated or
            // "remembered" call is a second project, not a no-op the way re-adding an already-bound shared project
            // is. Two things narrow that gap rather than close it: (a) `ConsentService` keys "remember" on the
            // whole `Action` string, so a remembered approval only ever covers a byte-identical repeat — varying
            // one field defeats it, it does not ride on it; (b) this card is the only place the operator ever sees
            // `behaviorPrompt` at all — `start_agent` folds a project's `BehaviorPrompt` into the system prompt via
            // `ComposeAsync` without showing it on its own card, so scrutinising it loosely here leaves it
            // unscrutinised everywhere. What still holds unconditionally is the low-consequence half: this writes a
            // project record and starts nothing, and a session only actually runs behind `start_agent`'s own
            // separate approval. LowRisk on the strength of (a) and (b), not because non-idempotence stopped mattering.
            var approval = await _ApprovedAsync(
                "The assistant wants to create a new project",
                $"Create project '{name}'\n"
                + $"folder: {(string.IsNullOrWhiteSpace(sourceDirectory) ? "(none)" : sourceDirectory)}\n"
                + $"MCP servers: {(normalizedEnabledMcpServerNames is { Length: > 0 } names ? string.Join(", ", names) : "(every server, following the registry)")}\n"
                + $"isolate in worktree by default: {isolateInWorktreeByDefault}\n\n"
                + $"behaviour prompt: {(normalizedBehaviorPrompt.Length == 0 ? "(none)" : normalizedBehaviorPrompt)}",
                ConsentSourceCatalog.AssistantProjectCreate,
                "assistant.create-project",
                ConsentRisk.LowRisk).ConfigureAwait(false);
            if (!approval.Ok)
            {
                return _Serialize(new { ok = false, error = approval.Error });
            }

            var result = await gateway.CreateProjectAsync(
                name,
                normalizedDescription.Length == 0 ? null : normalizedDescription,
                sourceDirectory,
                defaultProfileLabel,
                normalizedBehaviorPrompt.Length == 0 ? null : normalizedBehaviorPrompt,
                isolateInWorktreeByDefault,
                normalizedEnabledMcpServerNames,
                category,
                pluginFields).ConfigureAwait(false);

            return result.Ok
                ? _Serialize(new { ok = true, projectId = result.ProjectId, name = result.Name, approval = approval.Label })
                : _Serialize(new { ok = false, error = result.Error });
        }
        catch (Exception exception)
        {
            return _Serialize(new { ok = false, error = exception.Message });
        }
    }

    [McpServerTool(Name = "remember")]
    [Description("Writes one thing down where you will still have it in your next conversation. Everything else you know about this operator arrives with your instructions and is gone when this conversation ends — this is the only way something they said today reaches you tomorrow. USE IT WHEN THEY TELL YOU SOMETHING THAT IS MEANT TO LAST: what to call them or yourself, how they want you to answer, what a word of theirs means (\"prod is the release desk\"), a standing rule about what to do without asking. Say that you have noted it, in passing — one clause, not an announcement. WHAT DOES NOT BELONG HERE: what is happening right now (that is note_state), anything you worked out yourself rather than were told, and anything you are merely guessing they would want kept. WRITE IT AS A FACT THAT STILL READS IN A MONTH: \"the operator is called Raymond\", not \"he said his name\". One thing per call — two facts in one line cannot be pruned apart later. This does not ask for permission and nothing shows on their screen, so it is on you not to fill it with things nobody asked you to keep: there is no tool to take a line back, and the only way to clear one is the operator opening the file themselves.")]
    public async Task<string> RememberAsync(
        [Description("The one thing to remember, in a full sentence that will still make sense on its own with no conversation around it.")] string text)
    {
        try
        {
            if (_RefuseIfNotTheAssistant() is { } refusal)
            {
                return refusal;
            }

            await memory.RememberAsync(text).ConfigureAwait(false);
            return _Serialize(new { ok = true, remembered = text.Trim() });
        }
        catch (Exception exception)
        {
            return _Serialize(new { ok = false, error = exception.Message });
        }
    }

    [McpServerTool(Name = "note_state")]
    [Description("Leaves a note to yourself about where this conversation stands, for the version of you that comes after a restart. YOU RUN ALL DAY AND YOUR CONTEXT DOES NOT: when it is nearly full the cockpit starts you again on an empty one, and everything said since this morning is gone except your instructions, the memory file and this note. Keep it current — after anything that changes what is going on, not on a timer: what the operator is working on, what they last asked you, what you are waiting for, what you promised to come back to. EACH CALL REPLACES THE LAST, so write the whole picture every time rather than the newest line; three sentences that stand on their own, no transcript. WRITE IT AS A NOTE TO A COLLEAGUE WHO WAS NOT HERE: \"we are on AC-592, the release desk is running the tests, they asked me to say when it goes green\" — not \"as I said\", and not anything that only makes sense next to a message you can still see. THIS IS NOT THE MEMORY: things meant to last go in remember, and this one is wiped clean by the next call. Nothing shows on their screen and nobody approves it. On the other side of a restart you will be handed this note with a heading saying it may be out of date — so do not write it as if it were still happening.")]
    public async Task<string> NoteStateAsync(
        [Description("Where the conversation stands, in a few sentences that will still make sense to someone who cannot see any of it.")] string state)
    {
        try
        {
            if (_RefuseIfNotTheAssistant() is { } refusal)
            {
                return refusal;
            }

            await memory.NoteCurrentStateAsync(state).ConfigureAwait(false);
            return _Serialize(new { ok = true, state = state.Trim() });
        }
        catch (Exception exception)
        {
            return _Serialize(new { ok = false, error = exception.Message });
        }
    }

    [McpServerTool(Name = "export_assistant_memory")]
    [Description("Writes the assistant's own memory (what the operator asked it to remember) and its current-state note to a single .zip archive at a path the operator chooses — separate from, and much lighter than, a full cockpit backup: no settings, no plugins, no secrets scrubbing, just these two files, whichever of them exist. Use it when the operator wants to carry the assistant's memory to another machine or keep a copy before clearing it out. THE PATH IS THE OPERATOR'S OWN CHOICE, never guessed — ask where they want it written, a full path, and say that it overwrites whatever file is already there." + AskingCanBeSwitchedOff + " A REFUSAL IS NORMAL: nothing to export yet (the assistant has never remembered anything), or a path it cannot write to — read the reason out and carry on.")]
    public async Task<string> ExportAssistantMemoryAsync(
        [Description("Full path to write the archive to, e.g. \"C:\\Users\\operator\\Desktop\\assistant-memory.zip\". Chosen by the operator, never guessed. Overwrites whatever file is already there.")] string path)
    {
        try
        {
            if (_RefuseIfNotTheAssistant() is { } refusal)
            {
                return refusal;
            }

            var approval = await _ApprovedAsync(
                "The assistant wants to export its memory to a file",
                $"Write assistant-memory.md and assistant-state.md to {path}",
                ConsentSourceCatalog.AssistantMemoryExport,
                "assistant.memory-backup.export",
                ConsentRisk.LowRisk).ConfigureAwait(false);
            if (!approval.Ok)
            {
                return _Serialize(new { ok = false, error = approval.Error });
            }

            var written = await memory.ExportAsync(path).ConfigureAwait(false);
            return _Serialize(new { ok = true, path, files = written, approval = approval.Label });
        }
        catch (Exception exception)
        {
            return _Serialize(new { ok = false, error = exception.Message });
        }
    }

    [McpServerTool(Name = "import_assistant_memory")]
    [Description("Puts the assistant's memory and current-state note back from an archive export_assistant_memory made — its own restore, separate from a full cockpit restore. Whatever is currently at assistant-memory.md and assistant-state.md is copied aside with a timestamp first, never deleted — an import that turns out wrong is not a memory that is simply gone. Only whichever of the two files the archive actually carries is restored. Takes effect the next time this session restarts or a fresh one starts; it does not rewrite the memory already in this conversation." + AskingCanBeSwitchedOff + " A REFUSAL IS NORMAL: a path that is not a zip this tool made, or one that carries neither file — read the reason out and carry on.")]
    public async Task<string> ImportAssistantMemoryAsync(
        [Description("Full path to the archive to restore from, as export_assistant_memory wrote it. Chosen by the operator, never guessed.")] string path)
    {
        try
        {
            if (_RefuseIfNotTheAssistant() is { } refusal)
            {
                return refusal;
            }

            var approval = await _ApprovedAsync(
                "The assistant wants to replace its memory from a file",
                $"Restore assistant-memory.md and assistant-state.md from {path}",
                ConsentSourceCatalog.AssistantMemoryImport,
                "assistant.memory-backup.import",
                ConsentRisk.Dangerous).ConfigureAwait(false);
            if (!approval.Ok)
            {
                return _Serialize(new { ok = false, error = approval.Error });
            }

            var restored = await memory.ImportAsync(path).ConfigureAwait(false);
            return _Serialize(new { ok = true, path, files = restored, approval = approval.Label });
        }
        catch (Exception exception)
        {
            return _Serialize(new { ok = false, error = exception.Message });
        }
    }

    // Asks the operator, and returns the whole decision — never just whether it was approved (AC-759). A caller
    // that only learns "yes" cannot tell a card the operator actually clicked from one that never appeared because
    // they switched the asking off ahead of time (AC-575), and the tool descriptions promise a click only "by
    // default" now precisely because that difference is real; the result has to be able to say which one happened.
    // `action` is passed straight through to `ConsentRequest.Action`, which is rendered
    // verbatim, and is composed at each call site out of the literal arguments rather than out of a sentence about
    // them. That is the rule the type states and the reason it states it: the assistant's words are supplied by a
    // model that can be argued into supplying different ones, so a card showing a friendly description of a hostile
    // message is a card that approves the message. The pane id is shown rather than the session's name for the same
    // reason — the id is what the cockpit will act on, and a name would be the assistant's rendering of it.
    //
    // No broker means no operator to ask, and these two tools deliver into someone else's session, so the answer is
    // no. `AllowRemember` is deliberately left off both: the operator's lever for "stop asking me about this"
    // is the per-source bypass in Options (AC-575), which is per source and switchable back off, rather than a
    // per-call promise made on a row that was about one particular message.
    private async Task<_Approval> _ApprovedAsync(string title, string action, string sourceLabel, string scope, ConsentRisk risk)
    {
        if (consent is null)
        {
            return new _Approval(null, "This needs the operator's approval, and there is nobody here to ask.");
        }

        var decision = await consent.RequestConsentAsync(
            new ConsentRequest(title, action, new ConsentSource(AssistantIdentity.PaneId, null, sourceLabel), scope, risk))
            .ConfigureAwait(false);

        return decision.IsApproved
            ? new _Approval(decision.Bypassed ? "bypassed" : decision.Remembered ? "remembered" : "asked", null)
            : new _Approval(null, "The operator did not approve this.");
    }

    // What a consent check came back with: the label to report on a success payload, or the error to hand back on
    // a refusal — never both, which is the same rule `ok:false` payloads keep everywhere on this server (a refusal
    // carries its reason, not a half-formed field about a decision that never happened).
    private readonly record struct _Approval(string? Label, string? Error)
    {
        public bool Ok => Error is null;
    }

    // Refuses an argument that would land on a consent card as more than the one line it is meant to be. The card's
    // `Action` is composed of literal arguments precisely so it says what will happen rather than what the assistant
    // claims will happen — and that only holds while an argument cannot write a line of its own. `send_message`
    // strips control characters for the same reason; here they are refused instead, because unlike a message body
    // there is no version of these three fields worth delivering once one is in there.
    private static string? _RefuseIfNotOneLine(params (string Name, string Value)[] fields)
    {
        foreach (var (name, value) in fields)
        {
            if (value is not null && value.Any(char.IsControl))
            {
                return _Serialize(new
                {
                    ok = false,
                    error = $"That {name} carries a line break or a control character, which nothing legitimate does. Send it as a single plain line.",
                });
            }
        }

        return null;
    }

    // The gate, in one place so every tool on this server is covered by the same sentence rather than by its own
    // copy of it. Returns the refusal to hand straight back, or null when the caller really is the assistant.
    // A request with no verified pane is refused too, and not because it might be an impostor: it is the shared
    // app-lifetime key path (the in-process tool loop), which cannot be attributed to any session at all. There is
    // no identity to check, so there is no way to establish this one — and the safe answer to "I cannot tell who
    // this is" on a tool that starts processes in any workspace is no.
    //
    // Deliberately a second copy of `AssistantReadMcpTools._RefuseIfNotTheAssistant` rather than a shared
    // helper the two servers call. What would be shared is four lines; what would be gained is one place to weaken.
    // Both copies compare against the same `AssistantIdentity.PaneId`, which is the constant that must
    // not drift — and it already lives in Core for exactly that reason.
    private static string? _RefuseIfNotTheAssistant() =>
        string.Equals(McpRequestContext.CurrentPaneId, AssistantIdentity.PaneId, StringComparison.Ordinal)
            ? null
            : _Serialize(new { ok = false, error = NotTheAssistant });

    private static string _Serialize(object value) => JsonSerializer.Serialize(value, SerializerOptions);
}
