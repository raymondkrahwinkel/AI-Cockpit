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
    /// Raised when the selected session changes, or when the selected session's working directory first
    /// becomes known — the cue to re-read <see cref="ActiveSessionWorkingDirectory"/> and re-scope.
    /// </summary>
    event EventHandler? ActiveSessionChanged;

    /// <summary>
    /// Raised for each chunk of text a session produces (assistant text and tool output), carrying the
    /// text and the producing session's working directory. Substring-scan it for an output signal — the
    /// text is surfaced verbatim so a match works regardless of which underlying field it came from.
    /// </summary>
    event EventHandler<SessionOutputText>? OutputProduced;
}

/// <summary>
/// One chunk of text a session produced, delivered on <see cref="ICockpitSessionObserver.OutputProduced"/>.
/// </summary>
/// <param name="Text">The produced text (assistant prose or tool output), verbatim.</param>
/// <param name="WorkingDirectory">Working directory of the session that produced it, or null when unknown.</param>
/// <param name="IsFromActiveSession">True when this came from the currently selected session, so a watcher can ignore background sessions if it only cares about the one in view.</param>
public sealed record SessionOutputText(string Text, string? WorkingDirectory, bool IsFromActiveSession);

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
