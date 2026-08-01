using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using Cockpit.Core.Abstractions.Assistant;
using Cockpit.Core.Assistant;
using Cockpit.Infrastructure.Mcp;

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
/// second gate is therefore not the last one: every call the assistant makes raises the SDK permission prompt (its
/// session runs on <c>SessionOptionCatalog.DefaultPermissionMode</c> with nothing pre-approved), which the chat
/// window renders as an Allow/Deny row showing the literal profile, desk and folder. Nothing in this file is that
/// gate and nothing here may become it — a spoken "yes" is a sentence in a transcript, and the only thing that
/// resolves a permission is a click.
/// </para>
/// <para>
/// <b>This server scopes nothing.</b> The workspace is a required parameter rather than something derived, because
/// the assistant sits on no desk to derive one from — see <see cref="SpawnTarget"/>, whose two factories are the two
/// scoping rules, and whose remarks explain why a coordinator's stricter rule must not be built as a check bolted
/// onto this one.
/// </para>
/// </remarks>
internal sealed class AssistantAgentMcpTools(IAssistantAgentGateway gateway)
{
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = false };

    /// <summary>
    /// What a caller that is not the assistant is told. One sentence, and no detail about what it would have got —
    /// the same wording the read server uses, so a session that reaches either one learns the same nothing.
    /// </summary>
    private const string NotTheAssistant =
        "This tool is the cockpit assistant's own. It is not available to an agent session.";

    [McpServerTool(Name = "start_agent")]
    [Description("Starts an AI session on a workspace and leaves it running there as an ordinary pane — the same kind of pane the operator's own New-session dialog makes, with its own transcript and its own approvals. YOU MUST NAME THE WORKSPACE. You sit on no desk yourself, so there is nothing for the cockpit to infer one from; call list_workspaces to turn the desk the operator named into an id, and ask them which one if what they said matches nothing there. IF THEY NAMED NO DESK IN THIS INSTRUCTION, DO NOT CARRY ONE OVER FROM EARLIER IN THE CONVERSATION: they may well have moved on since. The desk they are looking at right now is the one list_workspaces reports as isActive, and that is what \"here\" means — use it, and say which desk you used. YOU MUST NAME THE PROFILE, and it is the field that decides what this costs: the profile picks the provider and the model, so starting something on a large model because no smaller one was named is a bill nobody agreed to. If the operator did not say which, call list_profiles: when exactly one fits what they asked for, take it and say so, and otherwise ask, naming only the ones that fit. THE OPERATOR STILL HAS TO APPROVE IT: this call raises an Allow/Deny row in the cockpit's chat window showing the profile, the desk and the folder, and nothing starts until it is clicked. Say out loud that permission is waiting on their screen — they may be looking somewhere else — and never treat a spoken \"yes\" as the approval, because it is not one and cannot become one. A REFUSAL IS NORMAL: if this comes back with ok false, read the reason out in a sentence and carry on with whatever you are still allowed to do, rather than treating it as the end of the conversation. WHAT THIS CANNOT DO: a delegated task (delegate_task) has no pane, so it is not something this tool can start, is not in any list, and cannot be stopped here. If you are asked about that kind of work, say it is invisible from where you are standing instead of reporting an absence as a fact.")]
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
    [Description("Closes a running AI session, named by its pane id — on any desk, not just one. Take the pane id from list_sessions; there is no lookup by name here, because two sessions can carry the same one and stopping the wrong session loses work that was in progress. LIKE STARTING, THIS NEEDS THE OPERATOR'S CLICK: an Allow/Deny row appears in the chat window naming what is about to be closed, and nothing happens until it is answered. Say out loud that it is waiting on their screen. A REFUSAL IS NORMAL — a pane that is already gone, one that is a plain terminal rather than an agent, one that runs inside a workspace's own surface rather than as a pane, or your own session, which you do not get to end mid-sentence — so read the reason out and carry on. WHAT THIS CANNOT DO: a delegated task (delegate_task) runs without a pane, so it cannot be stopped here and never appears in any list you can see. Say so rather than reporting that there was nothing to stop.")]
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
    [Description("Makes a new, empty Sessions desk with the name given and returns its id, ready to be spawned onto. Use it when the operator asks for somewhere new to put work, or when the desk they named does not exist and they would rather have it made than pick another. THIS ONE DOES BRING THEM THERE: an empty new desk has nothing on it to interrupt, and asking for a desk to be made is asking to be shown it — say so, because their screen will change. LIKE STARTING A SESSION, IT NEEDS THEIR CLICK on the Allow/Deny row in the chat window. The name is taken exactly as given and is not made unique, so read it back before you ask for it. Making a desk does not put anything on it — that is still a separate start_agent, with its own approval.")]
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
