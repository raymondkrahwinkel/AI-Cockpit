namespace Cockpit.Plugins.Abstractions.Sessions;

/// <summary>
/// The read/observe surface over the cockpit's sessions, complementing the write-only
/// <see cref="ICockpitActions"/> — it lets a plugin see which sessions are open and what
/// sessions produce, so a contribution can react to a session instead of only pushing into it. Obtained via
/// <see cref="ICockpitHost.Sessions"/>; events are raised on the UI thread.
/// </summary>
public interface ICockpitSessionObserver
{
    /// <summary>
    /// Every open session — pane id and the operator-visible name shown on its header — so a contribution can
    /// name a specific session rather than only ever "the active one" (AC-833). Empty on a host that predates
    /// this member.
    /// </summary>
    /// <remarks>
    /// Includes the cockpit-assistant under its own fixed pane id (<c>AssistantIdentity.PaneId</c>,
    /// "cockpit-assistant"): it is a session like any other from this surface, with no separate mechanism for
    /// naming it.
    /// </remarks>
    IReadOnlyList<OpenCockpitSession> OpenSessions => [];

    /// <summary>Whether the session mounted the named MCP server; older hosts default to true for compatibility.</summary>
    bool HasMcpServer(string paneId, string serverName) => true;

    /// <summary>
    /// Raised for each chunk of text a session produces (assistant text and tool output), carrying the
    /// text and the producing session's working directory. Substring-scan it for an output signal — the
    /// text is surfaced verbatim so a match works regardless of which underlying field it came from.
    /// </summary>
    event EventHandler<SessionOutputText>? OutputProduced;

    /// <summary>
    /// Raised when a session's agent finishes a tool call, coupling the tool's name and input with its result
    /// (AC-116) — richer than <see cref="OutputProduced"/>, which carries only the result text. Only structured
    /// (SDK) sessions raise it; a raw terminal session never does.
    /// </summary>
    /// <remarks>
    /// Lets a plugin react to a specific tool completing (e.g. a YouTrack tracker attaching the message's images
    /// to an issue the agent just created) instead of scanning prose for a signal. Null on a host that predates
    /// this member.
    /// </remarks>
    event EventHandler<SessionToolActivity>? ToolActivityObserved
    {
        add { }
        remove { }
    }

    /// <summary>
    /// Raised when a session closes — its pane leaves the cockpit — carrying that pane's
    /// <see cref="IPluginSessionContext.PaneId"/>. The cue to drop anything a plugin kept keyed to that session,
    /// so a closed session leaves nothing behind to hold it (and its whole transcript) alive.
    /// </summary>
    /// <remarks>
    /// Covers a per-session subscription on a plugin-lived object, a cache entry, a header-item's registration.
    /// Raised on the UI thread, once per close. A no-op add/remove on a host that predates this member, so a
    /// subscribing plugin keeps compiling and simply never hears from it.
    /// </remarks>
    event EventHandler<string>? SessionClosed
    {
        add { }
        remove { }
    }

    /// <summary>
    /// The images the user message that started the pane's current turn carried (AC-116), or empty when it
    /// carried none / the turn is over / <paramref name="paneId"/> is unknown. Host-managed and turn-scoped:
    /// set when an image-bearing message is sent and cleared when that turn completes, so a consumer reacting
    /// to a tool call mid-turn (an attach) gets exactly this turn's images and never a stale earlier set.
    /// Default empty for a host that predates this member.
    /// </summary>
    IReadOnlyList<SessionImageAttachment> GetCurrentTurnImages(string paneId) => [];

    /// <summary>
    /// The directory the session in <paramref name="paneId"/> works in (AC-1397), or null when that pane is unknown
    /// or its directory is not known yet. How a backend part, which has no active session, reads the directory of
    /// the session it acts for — say the caller of its MCP tool. Default null for a host that predates this member.
    /// </summary>
    string? GetWorkingDirectory(string paneId) => null;

    /// <summary>
    /// The last <paramref name="count"/> transcript rows of <paramref name="paneId"/>, oldest first, with the total
    /// (AC-2256); SDK and readable TTY sessions alike. The host's own markers (turn end, divider) are left out, so
    /// <see cref="PluginTranscriptSlice.Entries"/> can hold fewer than asked. Null for an unknown pane, one without a
    /// readable transcript (<see cref="OpenCockpitSession.HasReadableTranscript"/>), or an older host.
    /// </summary>
    Task<PluginTranscriptSlice?> ReadTranscriptAsync(string paneId, int count) => Task.FromResult<PluginTranscriptSlice?>(null);

    /// <summary>
    /// Every Allow/Deny question the session in <paramref name="paneId"/> is stopped on right now (AC-2256); empty once
    /// they are answered, for an unknown pane, for a session whose questions are its own CLI's, and on a host that
    /// predates this member. Read-only.
    /// </summary>
    Task<IReadOnlyList<PluginPendingPermission>> ReadPendingPermissionsAsync(string paneId) =>
        Task.FromResult<IReadOnlyList<PluginPendingPermission>>([]);
}

/// <summary>
/// One entry in <see cref="ICockpitSessionObserver.OpenSessions"/> — a session's pane id and the operator-visible
/// name shown on its header, how a contribution outside any session names the one it means to act on.
/// </summary>
/// <param name="PaneId">
/// The session's <see cref="IPluginSessionContext.PaneId"/>.
/// </param>
/// <param name="Name">
/// The name the operator sees on that session's header.
/// </param>
/// <param name="ProfileLabel">
/// The profile the session runs under, or null when the host does not expose it.
/// </param>
/// <param name="Status">
/// The host's coarse lifecycle status, or null when the host does not expose it.
/// </param>
public sealed record OpenCockpitSession(
    string PaneId,
    string Name,
    string? ProfileLabel = null,
    string? Status = null)
{
    /// <summary>
    /// What the session last said it is working on (AC-2256), or null when it set nothing or the host predates this member.
    /// </summary>
    public string? Statusline { get; init; }

    /// <summary>
    /// True for a plain shell pane, which has no agent behind it (AC-2256).
    /// </summary>
    public bool IsTerminal { get; init; }

    /// <summary>
    /// Whether <see cref="ICockpitSessionObserver.ReadTranscriptAsync"/> can read this session back at all (AC-2256): a
    /// route, not content. False for a plain terminal, a provider that records nothing readable, and on a host that
    /// predates this member.
    /// </summary>
    public bool HasReadableTranscript { get; init; }
}

/// <summary>
/// One Allow/Deny question a session is stopped on (AC-2256), as <see cref="ICockpitSessionObserver.ReadPendingPermissionsAsync"/>
/// reports it. Read-only: answering it is not part of this surface.
/// </summary>
/// <param name="ToolUseId">
/// The id of the tool call the question is about.
/// </param>
/// <param name="ToolName">
/// The tool's name as the agent called it.
/// </param>
/// <param name="InputJson">
/// The call's arguments as a JSON string, verbatim.
/// </param>
/// <param name="SinceUtc">
/// When the question was asked.
/// </param>
public sealed record PluginPendingPermission(string ToolUseId, string ToolName, string InputJson, DateTimeOffset SinceUtc);

/// <summary>
/// One chunk of text a session produced, delivered on <see cref="ICockpitSessionObserver.OutputProduced"/>.
/// </summary>
/// <param name="Text">
/// The produced text (assistant prose or tool output), verbatim.
/// </param>
/// <param name="WorkingDirectory">
/// Working directory of the session that produced it, or null when unknown.
/// </param>
/// <param name="IsFromActiveSession">
/// True when this came from the currently selected session, so a watcher can ignore background sessions if it only cares about the one in view.
/// </param>
public sealed record SessionOutputText(string Text, string? WorkingDirectory, bool IsFromActiveSession);

/// <summary>
/// One tool call a session's agent completed (AC-116), delivered on
/// <see cref="ICockpitSessionObserver.ToolActivityObserved"/>: which session (its pane id), which tool, the
/// arguments it was called with, and the result it produced. The name and input come from the tool-use event,
/// the content and error flag from the matching tool-result — coupled by the host so a consumer sees the whole
/// call in one place.
/// </summary>
/// <param name="PaneId">
/// Pane id of the session whose agent made the call — how a consumer names the session to act on (e.g. to fetch that turn's images via <see cref="ICockpitSessionObserver.GetCurrentTurnImages"/>).
/// </param>
/// <param name="ToolName">
/// The tool's name as the agent called it, e.g. <c>mcp__youtrack_personal__create_issue</c>.
/// </param>
/// <param name="InputJson">
/// The call's arguments as a JSON string, verbatim.
/// </param>
/// <param name="ResultContent">
/// The tool result's content, verbatim (often JSON the tool returned).
/// </param>
/// <param name="IsError">
/// True when the tool reported the call as an error.
/// </param>
public sealed record SessionToolActivity(string PaneId, string ToolName, string InputJson, string ResultContent, bool IsError);

/// <summary>
/// A no-op <see cref="ICockpitSessionObserver"/> used as the default from <see cref="ICockpitHost.Sessions"/>
/// so hosts that predate the read surface (test fakes, older embeddings) keep compiling and observing plugins
/// simply see no sessions and no output. The app supplies a live observer.
/// </summary>
public sealed class NullCockpitSessionObserver : ICockpitSessionObserver
{
    public static readonly NullCockpitSessionObserver Instance = new();

    private NullCockpitSessionObserver()
    {
    }

    public event EventHandler<SessionOutputText>? OutputProduced
    {
        add { }
        remove { }
    }
}
