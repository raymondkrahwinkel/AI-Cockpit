using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using Cockpit.Core.Abstractions.Assistant;
using Cockpit.Core.Abstractions.Delegation;
using Cockpit.Core.Assistant;
using Cockpit.Core.Delegation;
using Cockpit.Infrastructure.Agents;
using Cockpit.Infrastructure.Formatting;
using Cockpit.Infrastructure.Mcp;

namespace Cockpit.Infrastructure.Assistant;

// The `cockpit-assistant` MCP tools (AC-544): the voice assistant's read path over every session in every
// workspace. Reading only — acting is `[c]`'s, and nothing here changes anything.
// *Why this is a separate server and not two more tools on `cockpit-agents`.* That server's tools are
// workspace-scoped by construction: they derive the caller's desk host-side from the transport-verified pane and
// refuse a request they cannot place on one. The assistant is placed on no desk at all, so the cheapest way to
// make it work there would be to relax that derivation — and the derivation is not a filter on one tool, it is the
// reason no agent anywhere can reach another workspace's roster. Loosening it to serve one privileged caller
// removes the protection for every other caller at the same time, silently, and it compiles.
//
// So the broad reach lives here instead, behind two independent gates:
//
// *1. It is not handed out.* The endpoint is registered `Internal` (AC-204), which keeps it out of every
// user-facing MCP picker *and* out of the no-selection fan-out, so it reaches only a launch that names it —
// and the assistant's own start is the one place in the codebase that does.
//
// *2. It is not answered.* Every tool here refuses any caller whose verified pane is not
// `AssistantIdentity.PaneId`. That is the gate that actually holds, and it is the one worth having:
// the mount is a fact about configuration, and configuration is exactly the kind of thing that widens later by
// accident — an endpoint made non-internal, a profile that names the server, a spawn path that copies a selection
// it did not read. When that happens the tools are in a session's context and still answer nobody. The pane is
// stamped by `McpAuthMiddleware` from the request's own per-session bearer and no argument on any tool
// here can move it, so "I am the assistant" is not a sentence a session can say.
//
// *Where that stops* is where AC-89's per-session tokens stop, and no further: every session runs as the same
// OS user, so an agent with a shell can read a neighbour's `COCKPIT_MCP_KEY` out of its environment and send
// as it. That is a property the whole cockpit shares — the consent broker and the agent line included — and it is
// not fixable from here. What this design buys is that reaching these tools takes deliberate theft off the
// filesystem rather than a tool argument or an unticked checkbox.
internal sealed class AssistantReadMcpTools(IAssistantReadGateway gateway, IDelegationService delegation)
{
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = false };

    // What a caller that is not the assistant is told. One sentence, and no detail about what it would have got:
    // the refusal is the whole answer, and there is nothing here for an ordinary session to learn from.
    private const string NotTheAssistant =
        "This tool is the cockpit assistant's own. It is not available to an agent session.";

    [McpServerTool(Name = "list_sessions")]
    [Description("Lists every AI session the cockpit is running right now, across all workspaces — not just one desk. Each entry has the pane id, the session's name, the profile it runs under, the workspace it sits on (id and the tab label the operator sees), its statusline (whatever that session last set for itself with cockpit-session__set_status), its status — Idle, Busy, WorkingBackground, Done or NeedsAttention — needsYou, and ready. Use it to answer questions like \"what is the status of AC-223\" or \"what is everyone working on\". NEEDSYOU IS THE ONE TO VOLUNTEER: it means that session is stopped on a permission nobody has answered, so it is not working and will not start again until the operator clicks. If any session has it, say so out loud and name it, even when the question was about something else — a stalled session reads exactly like a finished one from a statusline, and nobody goes looking for a question they were never told about. Status and statusline answer different things: the statusline is what a session chose to write down, the status is whether it is doing anything at all. READY IS A THIRD THING AGAIN: status Idle covers both a session that never came up and one that finished cleanly and is sitting there able to take a prompt — ready is what tells those two apart, true only for the second one. IMPORTANT about what a statusline is and is not: it is a convention, not a record. A session says what it is working on because it was asked to, so a statusline mentioning a ticket is good evidence that session is on it — but a ticket appearing nowhere means only that no running session has written that ticket into its own status line. It does NOT mean nobody is working on it: a session may never have set a status, may have set a stale one, or may be doing the work under a different description. There is also one whole class of worker this list cannot see at all — a delegated task (delegate_task) runs without a pane and therefore without a statusline, so it never appears here however busy it is. Report the difference rather than turning an absence of evidence into an answer.")]
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
                    status = session.Status,
                    needsYou = session.NeedsYou,
                    // Idle covers both "never came up" and "finished a turn" — status alone cannot tell them apart,
                    // ready is the field that does.
                    ready = session.Ready,
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

    // How much of a transcript one `read_transcript` hands over when the caller does not say.
    //
    // Thirty rows, because a row is not a turn: one turn of an agent is typically a user line, a thinking block, a
    // handful of tool calls and a closing paragraph, so thirty rows is the last few turns — which is the span that
    // answers what this ticket asks a transcript for ("what is it doing", "where did it get stuck", "did it ever
    // hear me"). It is a default for a spoken question, and a spoken question is nearly always about the recent
    // end. The bound exists because the alternative is not "a longer answer" but a whole session pulled into every
    // turn of the assistant's own context, priced per token, for a question that wanted the last thing that
    // happened.
    internal const int DefaultEntryCount = 30;

    // The ceiling `count` cannot be raised past, however large a number is passed.
    //
    // The parameter exists for the case the default really is too narrow — "read further back, it started before
    // that" — and the ceiling exists because that request arrives as a number chosen by a model, which has no way to
    // know what it costs. Clamped rather than refused: a caller asking for a thousand wants as much as it can have,
    // and `omitted` in the reply already tells it what it did not get, so a refusal would only cost a
    // round-trip to arrive at the same hundred rows.
    internal const int MaxEntryCount = 100;

    // The most of any single transcript row that is repeated into the assistant's context.
    //
    // Bounding the row *count* does not bound the byte count, and on a transcript the gap is not theoretical:
    // one tool result — a file read, a build log, a `git diff` — is routinely larger than every other row put
    // together, and nothing stops it being ten megabytes. Thirty rows of that is a session-ending read of a tool
    // whose entire purpose is to answer a question about somebody else's session. The same 2000 characters an agent
    // message body is held to (`AgentMessageContent.MaxBodyLength`), for the same reason and with the
    // same arithmetic. The limit is applied to a row's text and to its coupled tool result *separately*, so
    // the worst case for a whole read is twice this per row: about 120 000 characters at the default of thirty rows
    // and 400 000 at the ceiling of a hundred. Written out because the tempting arithmetic — rows times this
    // constant — is the one that is wrong, and a bound nobody can compute correctly is a bound nobody will notice
    // growing. Characters rather than bytes, too: a row of astral-plane text is up to four bytes each, so these
    // numbers are a ceiling on what is repeated, not on what it weighs on the wire.
    //
    // Truncated rather than refused, unlike a message body — there is nobody to hand the refusal to who could
    // shorten it, and the first 2000 characters of a build log is the half that says what failed.
    internal const int MaxEntryTextLength = 2000;

    [McpServerTool(Name = "list_projects")]
    [Description("Lists the projects this cockpit knows: name, what the operator wrote about each, the folder its work lives in, the profile its sessions default to, and links — what a plugin calls this project elsewhere, keyed by field, e.g. {\"youtrack.project\": \"AC\"}. That key is the ticket prefix: an issue named AC-555 belongs to whichever project links \"youtrack.project\" to \"AC\", which is how \"pick up AC-555\" is assembled from list_projects, YouTrack's own get_issue, list_workspaces and start_agent rather than needing a tool of its own. A LINK'S VALUE CAN NAME SEVERAL PREFIXES, comma-separated, e.g. {\"youtrack.project\": \"EWB, AT, EJ\"} for one Cockpit project tracked under several YouTrack projects at once — an issue named AT-42 belongs to that project exactly as EWB-1 does; check every comma-separated item, not just the first. A PROJECT IS NOT A DESK AND NOT A SESSION — it is the operator's own idea of a body of work, it outlives every session, and asking \"which projects do we have\" is this tool and never list_workspaces. A project with no folder is an ordinary project, not a broken one: administrative work is work. The folder is also the honest answer to \"start something for that project\": it is where that project's sessions are meant to run, so pass it as the working directory rather than guessing a path. A PROJECT CAN DECLARE MORE THAN ONE REPOSITORY (AC-938) — a web repo and an android repo, say, neither nested in the other, kept as one project: repositories lists all of them, each with its path and an optional label the operator gave it (\"web\", \"android\"); sourceDirectory is always repositories[0].path. A session runs in exactly one repository at a time — pick the one you mean and pass its path as the working directory, rather than assuming the first is the one wanted. NEVER GUESS A LINK: if two projects' comma-separated lists under the same key share a prefix, or a prefix matches no project's list at all, that is a question for the operator, not a coin flip — say what you found (or that two projects claim it) and ask which one, rather than picking either.")]
    public async Task<string> ListProjectsAsync()
    {
        try
        {
            if (_RefuseIfNotTheAssistant() is { } refusal)
            {
                return refusal;
            }

            var projects = await gateway.ListProjectsAsync().ConfigureAwait(false);
            return _Serialize(new { ok = true, projects });
        }
        catch (Exception exception)
        {
            return _Serialize(new { ok = false, error = exception.Message });
        }
    }

    [McpServerTool(Name = "list_shared_projects")]
    [Description("Lists shared projects this machine can see but has not bound to a local project yet, grouped by the source that offers them (e.g. \"Depot — Work\"). Call this before create_project: a project that looks new to this machine may already be shared here under a different name, and binding it is one step instead of creating a duplicate. Each source reports succeeded and, when it did not, an error explaining why (not signed in, unreachable) — one broken source never costs another source's rows, so check succeeded per source rather than assuming an empty projects list means nothing is shared there. A project already bound on this machine is left out, since binding it again is not what this tool is for.")]
    public async Task<string> ListSharedProjectsAsync()
    {
        try
        {
            if (_RefuseIfNotTheAssistant() is { } refusal)
            {
                return refusal;
            }

            var sources = await gateway.ListSharedProjectsAsync().ConfigureAwait(false);
            return _Serialize(new
            {
                ok = true,
                sources = sources.Select(source => new
                {
                    sourceName = source.SourceName,
                    succeeded = source.Succeeded,
                    error = source.Error,
                    projects = source.Projects.Select(project => new
                    {
                        id = project.Id,
                        name = project.Name,
                        description = project.Description,
                        role = project.Role,
                    }),
                }),
            });
        }
        catch (Exception exception)
        {
            return _Serialize(new { ok = false, error = exception.Message });
        }
    }

    [McpServerTool(Name = "list_delegated_tasks")]
    [Description("Lists the delegated tasks this cockpit is running — the background work a session started with delegate_task — newest first, across every owner pane. THIS IS THE HALF list_sessions CANNOT SEE: a delegated task runs without a pane, so it has no row there and no statusline however busy it is; a session that fanned its work out further looks idle in one list and is doing five things in the other. Each entry has the task id, the profile it runs under, its label and task type, its status (Queued, Running, Completed, Failed or Stopped), when it was created/started/finished, how many turns it has taken, its result or its error, and ownerPaneId — the session that started it, which is how you attribute background work to the agent you spawned. A null ownerPaneId means the task was started off the verified path (the operator or the cockpit itself), not that nobody owns it. Each entry also carries permission — what the task was allowed to do, read-only unless its caller asked for more — and changedPaths, the paths the cockpit itself found changed in its working directory, which is what answers 'who wrote that' about work no pane did. A null changedPaths means the cockpit could not establish it (no working directory, or not a git checkout), never that nothing changed. Reading only: starting, stopping or following up on a task is not available here. Turn count is progress, not success — a task with turns and no result is still working, and one that is Failed says why in error.")]
    public string ListDelegatedTasks(
        [Description("Only tasks in this state: Queued, Running, Completed, Failed or Stopped. Omit it for every task. An unrecognised value is refused rather than quietly listing everything — a filter nobody applied reads exactly like nothing matching it.")] string? status = null)
    {
        try
        {
            if (_RefuseIfNotTheAssistant() is { } refusal)
            {
                return refusal;
            }

            DelegatedTaskStatus? filter = null;
            if (!string.IsNullOrWhiteSpace(status))
            {
                if (!Enum.TryParse<DelegatedTaskStatus>(status, ignoreCase: true, out var parsed))
                {
                    return _Serialize(new
                    {
                        ok = false,
                        error = $"'{status}' is not a task status. Use one of: "
                            + string.Join(", ", Enum.GetNames<DelegatedTaskStatus>()) + ", or omit it for every task.",
                    });
                }

                filter = parsed;
            }

            // The null caller is the point of this tool: the assistant owns no tasks, so the scoped read every
            // other caller gets would only ever return nothing.
            var tasks = delegation.ListTasks(filter, callerPaneId: null);
            return _Serialize(new
            {
                ok = true,
                count = tasks.Count,
                // Projected rather than handed over as the view: this file's serializer writes properties as
                // declared and enums as numbers, and `"status": 3` is not something the assistant can read out.
                tasks = tasks.Select(task => new
                {
                    taskId = task.TaskId,
                    profileLabel = task.ProfileLabel,
                    label = task.Label,
                    taskType = task.TaskType,
                    status = task.Status.ToString(),
                    createdAt = task.CreatedAt,
                    startedAt = task.StartedAt,
                    finishedAt = task.FinishedAt,
                    turnCount = task.TurnCount,
                    result = task.Result,
                    error = task.Error,
                    ownerPaneId = task.OwnerPaneId,
                    // AC-971: what the task was allowed to do, and what the cockpit itself saw it change. Read out
                    // together they answer the question this tool exists for — who did that, and what did they touch.
                    permission = task.Permission,
                    changedPaths = task.ChangedPaths,
                }),
            });
        }
        catch (Exception exception)
        {
            return _Serialize(new { ok = false, error = exception.Message });
        }
    }

    [McpServerTool(Name = "read_transcript")]
    [Description("Reads the raw transcript of one AI session, named by its pane id — any session in any workspace, not just one desk. Take the pane id from list_sessions. Returns the entries as they happened, oldest first: each has a kind (UserText, AssistantText, ToolUse, ToolResult, Thinking, Question, Error, TurnCompleted), the text of the row, and — on a tool call — the result that call returned. It is passed through raw and unedited, exactly as the operator's own screen shows it; reading it, making sense of it and saying what it means in a sentence is your job, not the cockpit's. BOUNDED: by default you get the last 30 entries, not the whole session, which is the recent end where nearly every spoken question is actually pointed. The reply always says totalEntries and omitted, so you can tell a short session from a long one you only saw the tail of — never report a session as having started with what is simply the first line you were given. Ask for more with count (up to 100) only when the question really is about earlier on, e.g. \"what did it try before that\". A single very long entry is cut to 2000 characters and marked truncated: that is a shortened tool result, not a complete one.")]
    public async Task<string> ReadTranscriptAsync(
        [Description("The pane id of the session to read, exactly as list_sessions reports it. There is no name lookup here: find the session with list_sessions first, then read the pane it names.")] string paneId,
        [Description("How many of the most recent entries to return. Defaults to 30 and is capped at 100 — a larger number is quietly clamped, not refused. Zero or a negative number is clamped up to 1 rather than returning nothing, so a miscounted argument still answers something instead of looking like an empty session. Raise it only when the question is about earlier in the session; a wider read costs context on every turn that follows it.")] int count = DefaultEntryCount)
    {
        try
        {
            if (_RefuseIfNotTheAssistant() is { } refusal)
            {
                return refusal;
            }

            var transcript = await gateway.ReadTranscriptAsync(paneId, Math.Clamp(count, 1, MaxEntryCount))
                .ConfigureAwait(false);
            if (transcript is null)
            {
                // Said plainly, and without guessing at what was meant: this tool takes a pane id, and a pane id that
                // matches nothing is either a session that has since closed or one of the cockpit's plain terminals,
                // which has no agent behind it to have a transcript. Neither is worth a search over session names —
                // list_sessions is right there, and it is the half that knows what the operator calls things.
                return _Serialize(new
                {
                    ok = false,
                    error = $"No AI session is running on pane '{paneId}'. It may have closed, or the pane may be a "
                        + "plain terminal rather than an agent. Call list_sessions for the panes that exist now.",
                });
            }

            var entries = transcript.Entries.Select(entry =>
            {
                var (text, textTruncated) = _Bounded(entry.Text);
                var (result, resultTruncated) = _Bounded(entry.ToolResult);
                return new
                {
                    kind = entry.Kind,
                    text,
                    toolResult = entry.ToolResult is null ? null : result,
                    // Per entry rather than once for the whole read: "something in here was shortened" would leave the
                    // reader unable to tell which tool result it may quote as complete.
                    truncated = textTruncated || resultTruncated,
                };
            }).ToArray();

            return _Serialize(new
            {
                ok = true,
                paneId = transcript.PaneId,
                name = transcript.Name,
                count = entries.Length,
                totalEntries = transcript.TotalEntries,
                // What was left out in front of this slice. A capped read has to say so, or a tail is indistinguishable
                // from a whole session and the assistant reports a beginning that is not one — the same field, and the
                // same reasoning, as read_inbox's `remaining`.
                omitted = transcript.TotalEntries - entries.Length,
                more = transcript.TotalEntries > entries.Length
                    ? $"This is the last {entries.Length} of {transcript.TotalEntries} entries — {transcript.TotalEntries - entries.Length} earlier ones were not read. Ask again with a larger count (up to {MaxEntryCount}) if the question is about earlier on."
                    : null,
                entries,
            });
        }
        catch (Exception exception)
        {
            return _Serialize(new { ok = false, error = exception.Message });
        }
    }

    // One transcript row as the assistant may be shown it: terminal control sequences stripped and cut to
    // `MaxEntryTextLength`, with whether the cut actually happened.
    // The stripping is not cosmetic and it is not this tool's invention — it is the same
    // `AgentMessageContent.Normalize` every agent-authored line already passes through before it is
    // repeated into another session's context. A transcript is the most agent-authored text there is, and it ends up
    // in a reply the assistant's own runtime prints, so a tool result full of ANSI would otherwise be able to
    // reposition a cursor or overwrite what the cockpit wrote above it. Reused rather than rewritten: a second
    // implementation of "which control characters are dangerous" is a second one to get wrong.
    //
    // Truncation is reported off the normalised length, not the raw one, so a row that merely had trailing
    // whitespace stripped is not announced as shortened.
    private static (string Text, bool Truncated) _Bounded(string? text)
    {
        var normalized = AgentMessageContent.Normalize(text, out _);
        return (BoundedText.Trim(normalized, MaxEntryTextLength), normalized.Length > MaxEntryTextLength);
    }

    // The gate, in one place so every tool on this server is covered by the same sentence rather than by its own
    // copy of it. Returns the refusal to hand straight back, or null when the caller really is the assistant.
    // A request with no verified pane is refused too, and not because it might be an impostor: it is the shared
    // app-lifetime key path (the in-process tool loop), which cannot be attributed to any session at all. There is
    // no identity to check, so there is no way to establish this one — and the safe answer to "I cannot tell who
    // this is" on a tool that reads every workspace is no.
    private static string? _RefuseIfNotTheAssistant() =>
        string.Equals(McpRequestContext.CurrentPaneId, AssistantIdentity.PaneId, StringComparison.Ordinal)
            ? null
            : _Serialize(new { ok = false, error = NotTheAssistant });

    private static string _Serialize(object value) => JsonSerializer.Serialize(value, SerializerOptions);
}
