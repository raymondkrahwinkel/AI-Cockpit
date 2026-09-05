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
// workspace. Reading only. Its own server rather than more tools on `cockpit-agents`, since that server derives
// the caller's desk from the pane and the assistant sits on none. Gated by an `Internal` mount (AC-204) and a per-tool check that the verified pane is `AssistantIdentity.PaneId`.
internal sealed class AssistantReadMcpTools(IAssistantReadGateway gateway, IDelegationService delegation)
{
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = false };

    // What a caller that is not the assistant is told. One sentence, and no detail about what it would have got:
    // the refusal is the whole answer, and there is nothing here for an ordinary session to learn from.
    private const string NotTheAssistant =
        "This tool is the cockpit assistant's own. It is not available to an agent session.";

    [McpServerTool(Name = "list_sessions", ReadOnly = true)]
    [Description("Lists every AI session the cockpit is running right now, across all workspaces — not just one desk. Each entry has the pane id, the session's name, the profile it runs under, the workspace it sits on (id and the tab label the operator sees), its statusline (whatever that session last set for itself with cockpit-session__set_status), its status — Idle, Busy, WorkingBackground, Done or NeedsAttention — needsYou, and ready. Use it to answer questions like \"what is the status of AC-223\" or \"what is everyone working on\". NEEDSYOU IS THE ONE TO VOLUNTEER: it means that session is stopped on a permission nobody has answered, so it is not working and will not start again until the operator clicks. If any session has it, say so out loud and name it, even when the question was about something else — a stalled session reads exactly like a finished one from a statusline, and nobody goes looking for a question they were never told about. Status and statusline answer different things: the statusline is what a session chose to write down, the status is whether it is doing anything at all. READY IS A THIRD THING AGAIN: status Idle covers both a session that never came up and one that finished cleanly and is sitting there able to take a prompt — ready is what tells those two apart, true only for the second one. THE PROCESS FIGURES ARE WHAT THE STATUS CANNOT SAY: status comes from the agent's own event stream, so it reports that the agent stopped talking, never that the test run it started stopped running. processCount, cpuPercent and memoryBytes cover that session's process and everything it has spawned; an Idle session holding hundreds of megabytes has left something running and reads as finished without you saying so. abandonedProcessCount is the sharper one — those are processes of that session whose parent is gone, so nothing will ever collect them; if it is above zero, volunteer it and name the session. cpuPercent is also how a session that is WAITING differs from one that is COMPUTING: Busy at nearly zero percent is stuck on something, not working. All four are zero for a session with no local process, such as one served over HTTP — that is 'not measurable here', never 'idle'. IMPORTANT about what a statusline is and is not: it is a convention, not a record. A session says what it is working on because it was asked to, so a statusline mentioning a ticket is good evidence that session is on it — but a ticket appearing nowhere means only that no running session has written that ticket into its own status line. It does NOT mean nobody is working on it: a session may never have set a status, may have set a stale one, or may be doing the work under a different description. There is also one whole class of worker this list cannot see at all — a delegated task (delegate_task) runs without a pane and therefore without a statusline, so it never appears here however busy it is. Report the difference rather than turning an absence of evidence into an answer.")]
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
                    // AC-1096: what that session's processes are still doing, which its status cannot say.
                    processCount = session.ProcessCount,
                    cpuPercent = Math.Round(session.CpuPercent, 1),
                    memoryBytes = session.MemoryBytes,
                    abandonedProcessCount = session.AbandonedProcessCount,
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

    // How much of a transcript one `read_transcript` hands over when the caller does not say. Thirty rows because
    // a row is not a turn (roughly the last few turns), which is the span a spoken question usually wants — the
    // bound exists because the alternative is a whole session pulled into context, priced per token.
    internal const int DefaultEntryCount = 30;

    // The ceiling `count` cannot be raised past. Clamped rather than refused: a caller asking for a thousand
    // wants as much as it can have, and `omitted` in the reply already says what it did not get.
    internal const int MaxEntryCount = 100;

    // The most of any single transcript row repeated into the assistant's context — bounding row count does not
    // bound byte count (a build log or `git diff` can dwarf every other row combined). Same limit as
    // `AgentMessageContent.MaxBodyLength`. Truncated rather than refused: nobody to hand a refusal to.
    internal const int MaxEntryTextLength = 2000;

    [McpServerTool(Name = "list_projects", ReadOnly = true)]
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

    [McpServerTool(Name = "list_shared_projects", ReadOnly = true)]
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

    [McpServerTool(Name = "list_delegated_tasks", ReadOnly = true)]
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

    [McpServerTool(Name = "read_transcript", ReadOnly = true)]
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
                // A pane id matching nothing is either a closed session or a plain terminal with no transcript.
                // Neither is worth a search over session names — list_sessions is right there for that.
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

    // One transcript row as the assistant may be shown it: terminal control sequences stripped (via
    // `AgentMessageContent.Normalize`, since ANSI could reposition the cockpit's own output) and cut to
    // `MaxEntryTextLength`. Truncation is reported off the normalised length, not the raw one.
    private static (string Text, bool Truncated) _Bounded(string? text)
    {
        var normalized = AgentMessageContent.Normalize(text, out _);
        return (BoundedText.Trim(normalized, MaxEntryTextLength), normalized.Length > MaxEntryTextLength);
    }

    // The gate, in one place so every tool on this server shares it. A request with no verified pane is refused
    // too (the shared app-lifetime key path can't be attributed to any session), not because it might be an
    // impostor — there is simply no identity to check, and the safe answer to that is no.
    private static string? _RefuseIfNotTheAssistant() =>
        string.Equals(McpRequestContext.CurrentPaneId, AssistantIdentity.PaneId, StringComparison.Ordinal)
            ? null
            : _Serialize(new { ok = false, error = NotTheAssistant });

    private static string _Serialize(object value) => JsonSerializer.Serialize(value, SerializerOptions);
}
