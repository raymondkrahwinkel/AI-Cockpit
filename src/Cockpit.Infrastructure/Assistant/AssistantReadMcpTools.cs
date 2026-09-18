using System.Collections.Concurrent;
using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using Cockpit.Core.Abstractions.Assistant;
using Cockpit.Core.Abstractions.Delegation;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Assistant;
using Cockpit.Core.Delegation;
using Cockpit.Infrastructure.Agents;
using Cockpit.Infrastructure.Formatting;
using Cockpit.Infrastructure.Mcp;

namespace Cockpit.Infrastructure.Assistant;

// The `cockpit-assistant` MCP tools (AC-544): the voice assistant's read path over every session in every
// workspace. Reading only. Its own server rather than more tools on `cockpit-agents`, since that server derives
// the caller's desk from the pane and the assistant sits on none. Gated by an `Internal` mount (AC-204) and a per-tool check that the verified pane is `AssistantIdentity.PaneId`.
internal sealed class AssistantReadMcpTools(
    IAssistantReadGateway gateway,
    IDelegationService delegation,
    INodeSessionsClient nodes,
    NodeDiscoveryId self)
{
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = false };

    // AC-1320: what a paired node gets to answer inside one list_sessions call. Its own, shorter limit than
    // `NodeSessionsClient`'s 10s, because the assistant calls this tool dozens of times an hour and a laptop that
    // went to the office is the ordinary case, not the exception. The local list never waits on a node.
    internal static readonly TimeSpan NodeBudget = TimeSpan.FromSeconds(2);

    // How long a node that did not answer is taken at its word before it is asked again, so the calls in between
    // cost nothing. The first read that succeeds clears it; there is no poller behind this, only the last attempt.
    internal static readonly TimeSpan UnreachableMemory = TimeSpan.FromMinutes(1);

    private readonly ConcurrentDictionary<string, _Unreachable> _unreachable = new(StringComparer.Ordinal);

    // What a caller that is not the assistant is told. One sentence, and no detail about what it would have got:
    // the refusal is the whole answer, and there is nothing here for an ordinary session to learn from.
    private const string NotTheAssistant =
        "This tool is the cockpit assistant's own. It is not available to an agent session.";

    [McpServerTool(Name = "list_sessions", ReadOnly = true)]
    [Description("Lists every AI session the cockpit is running right now, across all workspaces — not just one desk. Each entry has the pane id, the session's name, the profile it runs under, the workspace it sits on (id and the tab label the operator sees), its statusline (whatever that session last set for itself with cockpit-session__set_status), its status — Idle, Busy, WorkingBackground, Done, Failed or NeedsAttention — needsYou, ready and hasOutstandingWork. Use it to answer questions like \"what is the status of AC-223\" or \"what is everyone working on\". NEEDSYOU IS THE ONE TO VOLUNTEER: it means that session is stopped on a permission nobody has answered, so it is not working and will not start again until the operator clicks — it is exactly status == NeedsAttention, never a second opinion computed some other way. If any session has it, say so out loud and name it, even when the question was about something else — a stalled session reads exactly like a finished one from a statusline, and nobody goes looking for a question they were never told about. FAILED MEANS THE LAST TURN CRASHED: not \"stopped\", not \"finished\" — the agent's own turn ended in an error, and unlike NeedsAttention there is no pending question, so mention it but don't expect an answer to unblock it. Status and statusline answer different things: the statusline is what a session chose to write down, the status is whether it is doing anything at all. READY IS A THIRD THING AGAIN: status Idle covers both a session that never came up and one that finished cleanly and is sitting there able to take a prompt — ready is what tells those two apart, true only for the second one. HASOUTSTANDINGWORK IS A FOURTH THING, AND IT CAN BE TRUE UNDER ANY STATUS: a session can stop talking (Idle or Done) while something of its own — a backgrounded shell such as a build or a test run — is still going, because that work deliberately does not hold the status (a never-ending dev server would pin the session on \"working\" forever). A session reading Idle with hasOutstandingWork true is not finished; say so rather than reporting it as done. THE PROCESS FIGURES ARE WHAT THE STATUS CANNOT SAY EITHER: status comes from the agent's own event stream, so it reports that the agent stopped talking, never that the test run it started stopped running. processCount, cpuPercent and memoryBytes cover that session's process and everything it has spawned; an Idle session holding hundreds of megabytes has left something running and reads as finished without you saying so. abandonedProcessCount is the sharper one — those are processes of that session whose parent is gone, so nothing will ever collect them; if it is above zero, volunteer it and name the session. cpuPercent is also how a session that is WAITING differs from one that is COMPUTING: Busy at nearly zero percent is stuck on something, not working. All four are zero for a session with no local process, such as one served over HTTP — that is 'not measurable here', never 'idle'. IMPORTANT about what a statusline is and is not: it is a convention, not a record. A session says what it is working on because it was asked to, so a statusline mentioning a ticket is good evidence that session is on it — but a ticket appearing nowhere means only that no running session has written that ticket into its own status line. It does NOT mean nobody is working on it: a session may never have set a status, may have set a stale one, or may be doing the work under a different description. There is also one whole class of worker this list cannot see at all — a delegated task (delegate_task) runs without a pane and therefore without a statusline, so it never appears here however busy it is. Report the difference rather than turning an absence of evidence into an answer. THE LIST SPANS EVERY PAIRED NODE AS WELL AS THIS MACHINE (AC-1320): every row carries machine — its name, its discoveryId (the stable id of that machine, which two machines never share) and local, true only for a session on this cockpit. A PANE ID ALONE IS NO LONGER A UNIQUE REFERENCE: two machines can hold a session under the same pane id and the same name, so always say which machine a session is on. A node session's paneId is written as \"<node> · <paneId>\" and IS THE ADDRESS YOU CONTROL IT BY (AC-1323): stop_agent, send_prompt, send_message, rename_session and read_transcript take it as they take a local pane id and act on that machine; only watch_session cannot, since no event stream crosses between machines. To start something on a node, give start_agent the node's name — each entry under nodes lists the profiles and projects that node's operator allowed this cockpit, and those are the only ones a start there accepts. A node row has no workspace, no ready and no process figures: those are not measurable across machines, and they are absent rather than zero. nodes lists every paired node and whether it answered: reachable false with error and unreachableSince means the node is off, asleep or away, NOT that nothing runs there — a node that does not answer is asked again after a minute at the earliest, so unreachableSince can be older than this call. You only see the sessions a node's operator allowed this cockpit to see, so never report a node as idle: say what you can see.")]
    public async Task<string> ListSessionsAsync()
    {
        try
        {
            if (_RefuseIfNotTheAssistant() is { } refusal)
            {
                return refusal;
            }

            // The local read and every node read go out together; the local list never waits on a node.
            var localRead = gateway.ListSessionsAsync();
            var nodeReads = await _ReadNodesAsync().ConfigureAwait(false);
            var sessions = await localRead.ConfigureAwait(false);

            var here = new { name = Environment.MachineName, discoveryId = self.Value, local = true };
            var localRows = sessions.Select(session => new
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
                // AC-1309: something of this session's own is still running even though status does not say
                // so — a backgrounded shell, deliberately not status-pinning (AC-276).
                hasOutstandingWork = session.HasOutstandingWork,
                // AC-1096: what that session's processes are still doing, which its status cannot say.
                processCount = session.ProcessCount,
                cpuPercent = Math.Round(session.CpuPercent, 1),
                memoryBytes = session.MemoryBytes,
                abandonedProcessCount = session.AbandonedProcessCount,
                machine = here,
            });

            // AC-1320: a node's rows, under the node's own address shape. Deliberately fewer fields than a local
            // row — a desk, readiness and process figures are not measurable across machines, and absent is
            // honest where zero would read as idle.
            var nodeRows = nodeReads
                .Where(read => read.Snapshot is not null)
                .SelectMany(read => read.Snapshot!.Sessions.Select(session => new
                {
                    paneId = NodeSessionAddress.For(read.Name, session.PaneId),
                    name = session.Name,
                    profile = session.Profile,
                    statusline = session.Statusline,
                    hasStatusline = session.Statusline.Length > 0,
                    status = session.Status,
                    needsYou = session.NeedsYou,
                    hasOutstandingWork = session.HasOutstandingWork,
                    machine = new { name = read.Name, discoveryId = read.Snapshot.DiscoveryId, local = false },
                }));

            var rows = localRows.Cast<object>().Concat(nodeRows).ToList();
            return _Serialize(new
            {
                ok = true,
                count = rows.Count,
                sessions = rows,
                nodes = nodeReads.Select(read => new
                {
                    name = read.Name,
                    discoveryId = read.Snapshot?.DiscoveryId ?? "",
                    reachable = read.Snapshot is not null,
                    sessionCount = read.Snapshot?.Sessions.Count,
                    // AC-1323: what a start on this node may name — read in the same snapshot, so a start needs no
                    // second look at the node.
                    profiles = read.Snapshot?.Profiles.Select(profile => profile.Label),
                    projects = read.Snapshot?.Projects.Select(project => new { id = project.Id, name = project.Name }),
                    error = read.Unreachable?.Error,
                    unreachableSince = read.Unreachable?.Since,
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

    private async Task<_NodeRead[]> _ReadNodesAsync()
    {
        var names = await nodes.ListNodesAsync().ConfigureAwait(false);
        return await Task.WhenAll(names.Select(_ReadNodeAsync)).ConfigureAwait(false);
    }

    // One node under `NodeBudget`, or what the last attempt inside `UnreachableMemory` already said. `Since` is
    // the first failed attempt in this run, not the latest — the node has been away at least that long.
    private async Task<_NodeRead> _ReadNodeAsync(string name)
    {
        var now = DateTimeOffset.UtcNow;
        if (_unreachable.TryGetValue(name, out var remembered) && now - remembered.LastTried < UnreachableMemory)
        {
            return new _NodeRead(name, null, remembered);
        }

        NodeSessionsSnapshot snapshot;
        try
        {
            using var budget = new CancellationTokenSource(NodeBudget);
            snapshot = await nodes.ReadAsync(name, budget.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            snapshot = new NodeSessionsSnapshot(name, [], [], [], $"{name} did not answer within {NodeBudget.TotalSeconds:0}s.");
        }

        if (snapshot.Error is { Length: > 0 } error)
        {
            var since = _unreachable.TryGetValue(name, out var earlier) ? earlier.Since : now;
            var state = new _Unreachable(since, now, error);
            _unreachable[name] = state;
            return new _NodeRead(name, null, state);
        }

        _unreachable.TryRemove(name, out _);
        return new _NodeRead(name, snapshot, null);
    }

    private sealed record _Unreachable(DateTimeOffset Since, DateTimeOffset LastTried, string Error);

    private sealed record _NodeRead(string Name, NodeSessionsSnapshot? Snapshot, _Unreachable? Unreachable);

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
    [Description("Lists the projects this cockpit knows: name, what the operator wrote about each, the folder its work lives in, the profile its sessions default to, and links — what a plugin calls this project elsewhere, keyed by field, e.g. {\"youtrack.project\": \"AC\"}. That key is the ticket prefix: an issue named AC-555 belongs to whichever project links \"youtrack.project\" to \"AC\", which is how \"pick up AC-555\" is assembled from list_projects, YouTrack's own get_issue, list_workspaces and start_agent rather than needing a tool of its own. A LINK'S VALUE CAN NAME SEVERAL PREFIXES, comma-separated, e.g. {\"youtrack.project\": \"EWB, AT, EJ\"} for one Cockpit project tracked under several YouTrack projects at once — an issue named AT-42 belongs to that project exactly as EWB-1 does; check every comma-separated item, not just the first. A PROJECT IS NOT A DESK AND NOT A SESSION — it is the operator's own idea of a body of work, it outlives every session, and asking \"which projects do we have\" is this tool and never list_workspaces. A project with no folder is an ordinary project, not a broken one: administrative work is work. The folder is also the honest answer to \"start something for that project\": it is where that project's sessions are meant to run, so pass it as the working directory rather than guessing a path. A PROJECT CAN DECLARE MORE THAN ONE REPOSITORY (AC-938) — a web repo and an android repo, say, neither nested in the other, kept as one project: repositories lists all of them, each with its path and an optional label the operator gave it (\"web\", \"android\"); sourceDirectory is always repositories[0].path. A session runs in exactly one repository at a time — pick the one you mean and pass its path as the working directory, rather than assuming the first is the one wanted. NEVER GUESS A LINK: if two projects' comma-separated lists under the same key share a prefix, or a prefix matches no project's list at all, that is a question for the operator, not a coin flip — say what you found (or that two projects claim it) and ask which one, rather than picking either. THIS ALSO COVERS EVERY PAIRED NODE'S PROJECTS (AC-1326), same as list_sessions: each row carries machine and runsOn — every machine name that project is known to exist on, this one included. A shared project bound on both machines (the same Project.Id on each) is ONE row with both names in runsOn; a project that exists only on a node is a row with just that node's name, and the fields this cockpit cannot know about it (description, folder, links) are absent rather than guessed. Never merge two rows by name — only a shared Project.Id makes them the same project. This is what start_agent's refusal (a project on more than one machine needs node said explicitly) is reading when it says which other machine has it. AN UNREACHABLE NODE NEVER JUST DROPS ITS PROJECTS SILENTLY (AC-1333): it shows up in nodes[], the same shape list_sessions gives it (name, reachable, error, unreachableSince), so a project that lives only there reads as 'that node did not answer' rather than 'that project does not exist'.")]
    public async Task<string> ListProjectsAsync()
    {
        try
        {
            if (_RefuseIfNotTheAssistant() is { } refusal)
            {
                return refusal;
            }

            var localTask = gateway.ListProjectsAsync();
            var nodeReads = await _ReadNodesAsync().ConfigureAwait(false);
            var localProjects = await localTask.ConfigureAwait(false);

            var here = new { name = Environment.MachineName, discoveryId = self.Value, local = true };
            var runsOn = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (var project in localProjects)
            {
                runsOn[project.Id] = [Environment.MachineName];
            }

            foreach (var read in nodeReads)
            {
                if (read.Snapshot is null)
                {
                    continue;
                }

                foreach (var project in read.Snapshot.Projects)
                {
                    if (!runsOn.TryGetValue(project.Id, out var machines))
                    {
                        runsOn[project.Id] = machines = [];
                    }

                    if (!machines.Contains(read.Name))
                    {
                        machines.Add(read.Name);
                    }
                }
            }

            var localRows = localProjects.Select(project => new
            {
                project.Id,
                project.Name,
                project.Description,
                project.SourceDirectory,
                project.DefaultProfileLabel,
                project.Links,
                project.GitUrl,
                project.Repositories,
                machine = here,
                runsOn = runsOn[project.Id],
            });

            // Node-only projects (AC-1326): no local record exists, so only the id and name a node reported are
            // known — the fields a local project carries (folder, links, profile) are absent, not guessed.
            var localIds = localProjects.Select(project => project.Id).ToHashSet(StringComparer.Ordinal);
            var seenNodeOnly = new HashSet<string>(StringComparer.Ordinal);
            var nodeOnlyRows = nodeReads
                .Where(read => read.Snapshot is not null)
                .SelectMany(read => read.Snapshot!.Projects
                    .Where(project => !localIds.Contains(project.Id) && seenNodeOnly.Add(project.Id))
                    .Select(project => new
                    {
                        project.Id,
                        project.Name,
                        Description = (string?)null,
                        SourceDirectory = (string?)null,
                        DefaultProfileLabel = (string?)null,
                        Links = (IReadOnlyDictionary<string, string>)new Dictionary<string, string>(),
                        GitUrl = (string?)null,
                        Repositories = (IReadOnlyList<AssistantProjectRepositoryRow>)[],
                        machine = new { name = read.Name, discoveryId = read.Snapshot!.DiscoveryId, local = false },
                        runsOn = runsOn[project.Id],
                    }));

            var projects = localRows.Cast<object>().Concat(nodeOnlyRows).ToList();
            return _Serialize(new
            {
                ok = true,
                projects,
                // AC-1333: same shape as list_sessions' nodes[] — an unreachable node's projects are missing from
                // `projects` above, and this is where that absence gets a name instead of staying silent.
                nodes = nodeReads.Select(read => new
                {
                    name = read.Name,
                    discoveryId = read.Snapshot?.DiscoveryId ?? "",
                    reachable = read.Snapshot is not null,
                    sessionCount = read.Snapshot?.Sessions.Count,
                    profiles = read.Snapshot?.Profiles.Select(profile => profile.Label),
                    projects = read.Snapshot?.Projects.Select(project => new { id = project.Id, name = project.Name }),
                    error = read.Unreachable?.Error,
                    unreachableSince = read.Unreachable?.Since,
                }),
            });
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
    [Description("Lists the delegated tasks THIS MACHINE is running — the background work a session started with delegate_task, which always runs here even for a session on a paired node (AC-1326: a node has no delegation of its own) — newest first, across every owner pane. THIS IS THE HALF list_sessions CANNOT SEE: a delegated task runs without a pane, so it has no row there and no statusline however busy it is; a session that fanned its work out further looks idle in one list and is doing five things in the other. Each entry has the task id, the profile it runs under, its label and task type, its status (Queued, Running, Completed, Failed or Stopped), when it was created/started/finished, how many turns it has taken, its result or its error, and ownerPaneId — the session that started it, which is how you attribute background work to the agent you spawned. A null ownerPaneId means the task was started off the verified path (the operator or the cockpit itself), not that nobody owns it. Each entry also carries permission — what the task was allowed to do, read-only unless its caller asked for more — and changedPaths, the paths the cockpit itself found changed in its working directory, which is what answers 'who wrote that' about work no pane did. A null changedPaths means the cockpit could not establish it (no working directory, or not a git checkout), never that nothing changed. Reading only: starting, stopping or following up on a task is not available here. Turn count is progress, not success — a task with turns and no result is still working, and one that is Failed says why in error.")]
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
    [Description("Reads the raw transcript of one AI session, named by its pane id — any session in any workspace, not just one desk, and a session on a paired node when the pane id is a node address (\"<node> · <paneId>\"): the rows then come from that machine, in the same shape, and the reply's machine says so. Take the pane id from list_sessions. Returns the entries as they happened, oldest first: each has a kind (UserText, AssistantText, ToolUse, ToolResult, Thinking, Question, Error, TurnCompleted), the text of the row, and — on a tool call — the result that call returned. It is passed through raw and unedited, exactly as the operator's own screen shows it; reading it, making sense of it and saying what it means in a sentence is your job, not the cockpit's. BOUNDED: by default you get the last 30 entries, not the whole session, which is the recent end where nearly every spoken question is actually pointed. The reply always says totalEntries and omitted, so you can tell a short session from a long one you only saw the tail of — never report a session as having started with what is simply the first line you were given. Ask for more with count (up to 100) only when the question really is about earlier on, e.g. \"what did it try before that\". A single very long entry is cut to 2000 characters and marked truncated: that is a shortened tool result, not a complete one.")]
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

            var on = NodeSessionAddress.Split(paneId);
            var bounded = Math.Clamp(count, 1, MaxEntryCount);
            var read = on is null
                ? new NodeTranscriptRead(await gateway.ReadTranscriptAsync(paneId, bounded).ConfigureAwait(false))
                : await nodes.ReadTranscriptAsync(on.Value.NodeName, on.Value.PaneId, bounded).ConfigureAwait(false);
            if (read.Error is { } unreachable)
            {
                return _Serialize(new { ok = false, error = $"On {on!.Value.NodeName}: {unreachable}", machine = new { name = on.Value.NodeName, local = false } });
            }

            var transcript = read.Transcript;
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
                var (text, textTruncated) = Bounded(entry.Text);
                var (result, resultTruncated) = Bounded(entry.ToolResult);
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
                paneId,
                machine = on is null
                    ? new { name = Environment.MachineName, local = true }
                    : new { name = on.Value.NodeName, local = false },
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
    internal static (string Text, bool Truncated) Bounded(string? text)
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
