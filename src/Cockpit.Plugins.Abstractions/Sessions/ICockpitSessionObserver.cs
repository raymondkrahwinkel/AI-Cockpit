namespace Cockpit.Plugins.Abstractions.Sessions;

/// <summary>
/// The read/observe surface over the cockpit's sessions — the contract's first "read-as" capability,
/// complementing the write-only <see cref="ICockpitActions"/>. It lets a plugin see <em>what the active
/// session is working on</em> (its working directory) and <em>what sessions produce</em> (a stream of their
/// output text), so a contribution can react to a session rather than only pushing into it: e.g. a
/// directory-scoped view that follows the selected session, or a watcher that refreshes when a signal —
/// like a freshly created pull-request url — shows up in the output. Obtained via
/// <see cref="ICockpitHost.Sessions"/>; events are raised on the UI thread.
/// </summary>
public interface ICockpitSessionObserver
{
    /// <summary>
    /// Working directory of the currently selected session, or <see langword="null"/> when there is no
    /// selection or the directory is not yet known (e.g. an SDK session before its init event arrives).
    /// </summary>
    string? ActiveSessionWorkingDirectory { get; }

    /// <summary>
    /// <see cref="IPluginSessionContext.PaneId"/> of the currently selected session, or <see langword="null"/>
    /// when nothing is selected — how a contribution outside a session (a dialog, say) names the session an
    /// action applies to, so the matching header item can pick it up. Null on a host that predates this member.
    /// </summary>
    string? ActivePaneId => null;

    /// <summary>
    /// The selected session's current usage — how full its context window is, the windows it reports, and its
    /// profile label (AC-54) — or <see langword="null"/> when nothing is selected, or on a host that predates this
    /// member. The plugin-facing mirror of the header's usage pill: a widget reads it (and re-reads it on
    /// <see cref="ActiveSessionUsageChanged"/>) to chart usage over time without the host knowing what the widget
    /// shows. A snapshot with all-null figures (<see cref="SessionUsageSnapshot.HasAny"/> false) means the session
    /// has not reported usage yet — a silence to skip, not a zero to record.
    /// </summary>
    SessionUsageSnapshot? ActiveSessionUsage => null;

    /// <summary>
    /// Raised when the selected session changes, or when the selected session's working directory first
    /// becomes known — the cue to re-read <see cref="ActiveSessionWorkingDirectory"/> and re-scope.
    /// </summary>
    event EventHandler? ActiveSessionChanged;

    /// <summary>
    /// Raised when <see cref="ActiveSessionUsage"/> moves — the selection changed, or the selected session's
    /// context/rate-limit figures updated — so a usage widget re-reads and samples. A no-op add/remove on a host
    /// that predates this member, so a subscribing plugin keeps compiling and simply never hears from it.
    /// </summary>
    event EventHandler? ActiveSessionUsageChanged
    {
        add { }
        remove { }
    }

    /// <summary>
    /// Raised for each chunk of text a session produces (assistant text and tool output), carrying the
    /// text and the producing session's working directory. Substring-scan it for an output signal — the
    /// text is surfaced verbatim so a match works regardless of which underlying field it came from.
    /// </summary>
    event EventHandler<SessionOutputText>? OutputProduced;

    /// <summary>
    /// Raised when a session's agent finishes a tool call, coupling the tool's name and input with its
    /// result (AC-116) — richer than <see cref="OutputProduced"/>, which carries only the result text and
    /// cannot say which tool produced it. Lets a plugin react to a specific tool completing (e.g. a YouTrack
    /// tracker attaching the message's images to an issue the agent just created) instead of scanning prose
    /// for a signal. Only structured (SDK) sessions raise it; a raw terminal session, whose tool calls the
    /// cockpit does not parse, never does. Null on a host that predates this member.
    /// </summary>
    event EventHandler<SessionToolActivity>? ToolActivityObserved
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
}

/// <summary>
/// One chunk of text a session produced, delivered on <see cref="ICockpitSessionObserver.OutputProduced"/>.
/// </summary>
/// <param name="Text">The produced text (assistant prose or tool output), verbatim.</param>
/// <param name="WorkingDirectory">Working directory of the session that produced it, or null when unknown.</param>
/// <param name="IsFromActiveSession">True when this came from the currently selected session, so a watcher can ignore background sessions if it only cares about the one in view.</param>
public sealed record SessionOutputText(string Text, string? WorkingDirectory, bool IsFromActiveSession);

/// <summary>
/// One tool call a session's agent completed (AC-116), delivered on
/// <see cref="ICockpitSessionObserver.ToolActivityObserved"/>: which session (its pane id), which tool, the
/// arguments it was called with, and the result it produced. The name and input come from the tool-use event,
/// the content and error flag from the matching tool-result — coupled by the host so a consumer sees the whole
/// call in one place.
/// </summary>
/// <param name="PaneId">Pane id of the session whose agent made the call — how a consumer names the session to act on (e.g. to fetch that turn's images via <see cref="ICockpitSessionObserver.GetCurrentTurnImages"/>).</param>
/// <param name="ToolName">The tool's name as the agent called it, e.g. <c>mcp__youtrack_personal__create_issue</c>.</param>
/// <param name="InputJson">The call's arguments as a JSON string, verbatim.</param>
/// <param name="ResultContent">The tool result's content, verbatim (often JSON the tool returned).</param>
/// <param name="IsError">True when the tool reported the call as an error.</param>
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

    public string? ActiveSessionWorkingDirectory => null;

    public event EventHandler? ActiveSessionChanged
    {
        add { }
        remove { }
    }

    public event EventHandler<SessionOutputText>? OutputProduced
    {
        add { }
        remove { }
    }
}
