namespace Cockpit.Core.Abstractions.Assistant;

/// <summary>
/// The host-side read path the assistant sees the whole cockpit through (AC-544): every AI session, on every
/// workspace, with the statusline it last set for itself.
/// </summary>
/// <remarks>
/// <b>Why this is not <c>list_agents</c>.</b> <c>cockpit-agents</c>' roster is workspace-scoped on purpose, and the
/// scoping is not a filter it applies — it is the shape of the thing: the workspace is derived host-side from the
/// transport-verified pane the request came from, so there is nothing an agent could declare to reach another
/// workspace's roster. The assistant has no workspace at all (<c>SessionWorkspacePlacement</c> places it nowhere,
/// deliberately), which means that tool cannot answer for it — and the short way to make it answer would be to
/// loosen the derivation, which removes the protection for every agent in the cockpit, not just for this one.
/// <para>
/// So this is a second, separate read path that stands above the workspaces instead of inside one. Same source —
/// the running session panels — same fields, no new store and no new index: the statuslines are already kept, and
/// "who is on AC-223" is a question about data the cockpit already holds. What is new is only a reader that is not
/// standing on a desk.
/// </para>
/// <para>
/// <b>And it is not reachable from an ordinary session.</b> The tools over this interface are mounted only into the
/// assistant's own launch, and refuse any caller whose verified pane is not
/// <see cref="Core.Assistant.AssistantIdentity.PaneId"/>. Exclusion by construction, twice over: not handed out,
/// and not answered even when it is. See <c>AssistantReadMcpTools</c>.
/// </para>
/// </remarks>
public interface IAssistantReadGateway
{
    /// <summary>
    /// Every AI session the cockpit is running right now, across every workspace, in no particular order.
    /// <para>
    /// Whole rather than searched: the answer is a handful of rows, the caller is a model that reads them anyway,
    /// and a query parameter here would be a second place where "does this session match AC-223" is decided — one
    /// that would match on exact text while the assistant is the half that can read through a statusline saying
    /// "ac223 tests" and see the same ticket.
    /// </para>
    /// </summary>
    Task<IReadOnlyList<AssistantSessionRow>> ListSessionsAsync();

    /// <summary>
    /// The tail of one named session's transcript, raw — the rows the operator is looking at, in the order they
    /// happened, with nothing rewritten. Null when no AI session is running on that pane.
    /// </summary>
    /// <remarks>
    /// <b>Why this one takes an argument when <see cref="ListSessionsAsync"/> takes none.</b> Everywhere else in the
    /// cockpit a pane id on a tool would be the hole — <c>read_inbox</c> deliberately has no such parameter, because
    /// "whose inbox" must be the pane the transport verified rather than a pane the caller typed. Here it is not a
    /// hole, and the reason is that it decides nothing about <em>authority</em>: the caller has already been
    /// established as the assistant by the pane guard before this is reached, and the assistant is allowed every
    /// workspace by design — that is the whole of AC-544. So the argument selects among things the caller may
    /// already read, which is a lookup, not a scope. Do not "fix" it by deriving the pane from the request: the
    /// assistant is asking about somebody else's session, always, and deriving it would leave the tool able only to
    /// read the assistant's own.
    /// <para>
    /// <b>Bounded here rather than at the tool</b> so a session with ten thousand rows is never copied out of the UI
    /// thread's collection to have all but thirty of them thrown away.
    /// </para>
    /// </remarks>
    /// <param name="paneId">The session to read, as <see cref="AssistantSessionRow.PaneId"/> reports it.</param>
    /// <param name="count">How many of the most recent entries to return; already clamped by the caller.</param>
    Task<AssistantTranscript?> ReadTranscriptAsync(string paneId, int count);

    /// <summary>
    /// The projects this cockpit knows — the operator's own list, not a folder scan.
    /// </summary>
    /// <remarks>
    /// A project is not a workspace and not a session, and it was the one first-class thing in the cockpit the
    /// assistant could not see. Asked "which projects do we have", it answered with the desks — the nearest thing
    /// it had a tool for, and wrong. Added from the live test rather than designed in: what a model reaches for
    /// when it has no tool for the question is the most reliable way to find out which tool is missing.
    /// </remarks>
    Task<IReadOnlyList<AssistantProjectRow>> ListProjectsAsync();
}

/// <summary>
/// One project as the assistant is shown it.
/// </summary>
/// <param name="Id">The project's id, so a later answer can name the same one unambiguously.</param>
/// <param name="Name">What the operator calls it, and what they will say out loud.</param>
/// <param name="Description">Their own note on what it is, or null. Often the only thing that distinguishes two similarly named projects.</param>
/// <param name="SourceDirectory">
/// The folder its sessions start in, or null for a project that has none — an administrative project is a project
/// too. Worth having here rather than only in the manager: it is exactly the working directory a session started
/// "for that project" ought to run in.
/// </param>
/// <param name="DefaultProfileLabel">The profile its sessions default to, by label, or null when it names none.</param>
/// <param name="Links">
/// What this project is called elsewhere, keyed by the plugin field that named it —
/// <see cref="Cockpit.Core.Projects.Project.PluginFields"/> verbatim, e.g. <c>{"youtrack.project": "AC"}</c>.
/// Empty for a project no plugin has linked. This is what turns
/// "pick up AC-555" into a lookup rather than a guess: match the ticket's prefix against a project's
/// <c>youtrack.project</c> value here before ever calling into YouTrack for the issue itself.
/// </param>
/// <param name="GitUrl">The repository <see cref="SourceDirectory"/> was cloned from, or null when the folder was picked rather than cloned.</param>
public sealed record AssistantProjectRow(
    string Id,
    string Name,
    string? Description,
    string? SourceDirectory,
    string? DefaultProfileLabel,
    IReadOnlyDictionary<string, string> Links,
    string? GitUrl);

/// <summary>
/// The tail of one session's transcript, plus what it takes to know that it <em>is</em> a tail.
/// </summary>
/// <param name="PaneId">The pane that was read — echoed back, so a reply is never ambiguous about which session it describes.</param>
/// <param name="Name">That session's title, as the operator sees it.</param>
/// <param name="TotalEntries">
/// How many entries the transcript holds in total. Reported rather than inferred: <see cref="Entries"/> is the last
/// slice of it, and a bounded read that does not say what it left behind is indistinguishable from a short session —
/// the same mistake <c>read_inbox</c>'s <c>remaining</c> exists to prevent, one read path along.
/// </param>
/// <param name="Entries">The most recent entries, oldest first, so the tail reads in the order it happened.</param>
public sealed record AssistantTranscript(
    string PaneId,
    string Name,
    int TotalEntries,
    IReadOnlyList<AssistantTranscriptEntry> Entries);

/// <summary>
/// One transcript row, as raw as it is on screen.
/// </summary>
/// <remarks>
/// Deliberately three fields and no more. Turning a transcript into prose is the assistant's own work, done by the
/// model that reads this against its system prompt — not a host-side cleanup pass. A summariser here would be a
/// second, silent opinion about what a session did, running on every read, that nobody could see or correct.
/// </remarks>
/// <param name="Kind">The row's kind — <c>UserText</c>, <c>AssistantText</c>, <c>ToolUse</c>, <c>Thinking</c>, and so on — as its own name.</param>
/// <param name="Text">The row's text. For a tool call this is the call as the panel shows it, tool name and input together.</param>
/// <param name="ToolResult">
/// The result coupled to a tool-call row, or null on a row that has none. Carried because a tool call's result is
/// held <em>on</em> its call row rather than as a row of its own (they are matched by tool_use_id when the result
/// arrives), so a reader that reported only <see cref="Text"/> would show every tool this session ran and nothing
/// any of them returned.
/// </param>
public sealed record AssistantTranscriptEntry(string Kind, string Text, string? ToolResult);

/// <summary>
/// One running session as the assistant is shown it: enough to answer "who is working on this, and where", and
/// nothing more.
/// </summary>
/// <param name="PaneId">The session's pane id — its handle, and what a later phase would act on.</param>
/// <param name="Name">The session's title, as the operator sees it in the sidebar.</param>
/// <param name="Profile">The profile it runs under, or empty when it has not reported one yet.</param>
/// <param name="Statusline">
/// What the session last said it is working on with <c>cockpit-session__set_status</c>, or empty when it has never
/// said anything. Empty is the ordinary case for a session nobody instructed, and it is exactly why the assistant
/// is told never to read an absence here as an absence of work — see <c>AssistantSystemPrompt.Default</c>.
/// </param>
/// <param name="WorkspaceId">The workspace it sits on, or null for a session the cockpit places on no desk.</param>
/// <param name="WorkspaceName">
/// That workspace's tab label — the name the operator would actually recognise, since the id is never shown
/// anywhere. Null when there is no workspace, and null rather than the id when the workspace has since gone.
/// </param>
/// <param name="Status">
/// The coarse state the sidebar draws — Idle, Busy, WorkingBackground, Done. The statusline says what a session
/// chose to write down; this says whether it is doing anything, which is the other half of the question and the one
/// no session has to opt into.
/// </param>
/// <param name="NeedsYou">
/// Whether this session is stopped on a permission nobody has answered.
/// </param>
/// <param name="Ready">
/// Whether a prompt handed to this session right now would actually reach the agent — the same
/// <c>SessionPanelViewModel.CanTakeAPrompt</c> every other waker in the cockpit already reads (AC-395's wake,
/// AC-234's scheduled resume, <c>WorkspaceAgentGateway</c>), not a second opinion computed here. Worth its own
/// field because <paramref name="Status"/> cannot carry it: a pane that never came up and a pane that finished a
/// turn are both <c>Idle</c>, and that is exactly the pair this field is for telling apart.
/// </param>
/// <remarks>
/// <b>Why <paramref name="NeedsYou"/> is worth its own field.</b> "Who is waiting for me" is the most ordinary
/// question to ask a cockpit out loud, and until this existed the assistant could only answer it by reading
/// transcripts one at a time and inferring. A stalled session looks exactly like an idle one from a statusline, and
/// the difference is the whole point: one is finished, the other is burning the afternoon waiting for a click.
/// </remarks>
public sealed record AssistantSessionRow(
    string PaneId,
    string Name,
    string Profile,
    string Statusline,
    string? WorkspaceId,
    string? WorkspaceName,
    string Status = "",
    bool NeedsYou = false,
    bool Ready = false);
