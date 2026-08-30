namespace Cockpit.Core.Abstractions.Assistant;

/// <summary>
/// The host-side read path the assistant sees the whole cockpit through (AC-544): every AI session, on every
/// workspace, with the statusline it last set for itself. Separate from <c>list_agents</c>, which is
/// workspace-scoped and cannot answer for an assistant that has no workspace of its own.
/// </summary>
public interface IAssistantReadGateway
{
    /// <summary>
    /// Every AI session the cockpit is running right now, across every workspace, in no particular order. No
    /// search parameter — the caller is a model that reads the handful of rows anyway.
    /// </summary>
    Task<IReadOnlyList<AssistantSessionRow>> ListSessionsAsync();

    /// <summary>
    /// The tail of one named session's transcript, raw — in the order it happened, nothing rewritten. Null when no
    /// AI session runs on <paramref name="paneId"/>, which here is a lookup, not a scope: the caller is already the
    /// assistant, allowed every workspace (AC-544). <paramref name="count"/> bounds it here, not at the tool, so a ten-thousand-row session is never copied out to discard most of it.
    /// </summary>
    Task<AssistantTranscript?> ReadTranscriptAsync(string paneId, int count);

    /// <summary>
    /// The projects this cockpit knows — the operator's own list, not a folder scan. A project is not a workspace
    /// and not a session, and it was the one first-class thing the assistant could not see: asked "which projects
    /// do we have", it answered with the desks, the nearest thing it had a tool for, and wrong.
    /// </summary>
    Task<IReadOnlyList<AssistantProjectRow>> ListProjectsAsync();

    /// <summary>
    /// Every shared project this machine has not bound to a local project yet, grouped by the source that offers
    /// it (AC-797) — read fresh on every call, the same "no invalidation path" behaviour <c>ProjectsViewModel</c>'s
    /// own load already has.
    /// </summary>
    Task<IReadOnlyList<AssistantSharedProjectSourceRow>> ListSharedProjectsAsync();
}

// AC-1013: One project as the assistant is shown it. Links (AC-884) may be comma-separated per key
// (e.g. `"EWB, AT, EJ"`) — match every one, not just the first — turning "pick up AC-555" into a project lookup
// rather than a guess. Repositories[0].Path equals SourceDirectory; full per-field rationale was on AC-1013.
public sealed record AssistantProjectRow(
    string Id,
    string Name,
    string? Description,
    string? SourceDirectory,
    string? DefaultProfileLabel,
    IReadOnlyDictionary<string, string> Links,
    string? GitUrl,
    IReadOnlyList<AssistantProjectRepositoryRow> Repositories);

// One repository a project declares: `Path` is the repository's folder, `Label` is what the operator called
// it ("web", "android"), or null when they never named it.
public sealed record AssistantProjectRepositoryRow(string Path, string? Label);

// One source's shared projects, or why it failed (AC-797) — a source is expected to report a whole-connection
// failure through Error rather than throw, so one broken source never costs another's rows.
public sealed record AssistantSharedProjectSourceRow(
    string SourceName,
    bool Succeeded,
    string? Error,
    IReadOnlyList<AssistantSharedProjectRow> Projects);

// One shared project this machine has not bound to a local project yet (AC-797), as the assistant is shown it.
public sealed record AssistantSharedProjectRow(string Id, string Name, string? Description, string? Role);

// AC-1013: The tail of one session's transcript. TotalEntries is reported rather than inferred — a bounded
// read that doesn't say what it left behind reads as a short session, the same mistake read_inbox's remaining prevents.
public sealed record AssistantTranscript(
    string PaneId,
    string Name,
    int TotalEntries,
    IReadOnlyList<AssistantTranscriptEntry> Entries);

// AC-1013: One transcript row, deliberately three fields — summarising is the reading model's own work, not a
// silent host-side pass. ToolResult rides on its call row (matched by tool_use_id) rather than as its own row,
// so a reader on Text alone wouldn't see what any tool call returned.
public sealed record AssistantTranscriptEntry(string Kind, string Text, string? ToolResult);

// AC-1013: One running session as the assistant is shown it — "who is working on this, and where", nothing
// more. Statusline empty is ordinary, not "no work"; NeedsYou/Ready tell a never-started pane from a finished
// one; AC-1096's process figures say whether anything is still running, which no status here can.
public sealed record AssistantSessionRow(
    string PaneId,
    string Name,
    string Profile,
    string Statusline,
    string? WorkspaceId,
    string? WorkspaceName,
    string Status = "",
    bool NeedsYou = false,
    bool Ready = false,
    int ProcessCount = 0,
    double CpuPercent = 0,
    long MemoryBytes = 0,
    int AbandonedProcessCount = 0);
