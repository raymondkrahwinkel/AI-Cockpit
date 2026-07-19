namespace Cockpit.Core.Abstractions.Terminal;

/// <summary>An open terminal pane the agent could ask to drive: its stable pane id and the name the operator sees (e.g. <c>zsh-5</c>).</summary>
public sealed record TerminalPane(string PaneId, string Name);

/// <summary>A pane as <c>list_terminals</c> reports it to one agent session — the pane plus whether that session is already coupled to it.</summary>
public sealed record TerminalPaneView(string PaneId, string Name, bool Coupled);

/// <summary>
/// The source of truth for terminal-pane access (AC-34). The TTY layer registers the panes that exist and feeds their
/// rendered output; the <c>cockpit-terminal</c> MCP tools read through the consumer side. Coupling is exclusive (one
/// agent per pane) and its captured output starts at the coupling, never before — so an earlier <c>cat .env</c> in the
/// scrollback cannot leak into an agent's context (Iron Law #8). A pane closing or a session ending decouples
/// automatically. The concrete registry is a singleton so both sides see the same live state.
/// </summary>
public interface ITerminalAccessRegistry
{
    // ---- Producer side (the TTY/UI layer) ----

    /// <summary>Records that a terminal pane is open and drivable. Idempotent — re-registering updates the name.</summary>
    void PaneOpened(string paneId, string name);

    /// <summary>Records that a pane closed (tab closed, shell exit, SSH dropped): any coupling on it is broken automatically.</summary>
    void PaneClosed(string paneId);

    /// <summary>Feeds a pane's freshly rendered output. Buffered only while the pane is coupled, so capture begins at the coupling, not before.</summary>
    void CaptureOutput(string paneId, string text);

    /// <summary>Whether the pane is coupled to any agent — the cheap gate the producer uses so it only decodes/pushes output that will actually be read.</summary>
    bool IsCoupled(string paneId);

    // ---- Consumer side (the cockpit-terminal MCP tools) ----

    /// <summary>The open panes as this agent session sees them, each flagged with whether this session is coupled to it.</summary>
    IReadOnlyList<TerminalPaneView> ListPanes(string sessionId);

    /// <summary>Finds an open pane by its id or its operator-facing name, or null if there is no such pane.</summary>
    TerminalPane? Resolve(string paneRef);

    /// <summary>Whether this session already holds the coupling on the pane (so no fresh consent is needed to read it).</summary>
    bool IsCoupledBy(string sessionId, string paneId);

    /// <summary>Whether a <em>different</em> agent session holds the coupling — exclusivity: a second agent is refused.</summary>
    bool IsCoupledByAnother(string sessionId, string paneId);

    /// <summary>Commits the coupling of a pane to a session (after the operator approved) and starts its output capture. Idempotent for the same session.</summary>
    void Couple(string sessionId, string paneId);

    /// <summary>The output captured since this session coupled to the pane, or null when this session does not hold the coupling.</summary>
    string? ReadCoupled(string sessionId, string paneId);

    /// <summary>Breaks every coupling this agent session held (its session ended or crashed).</summary>
    void SessionEnded(string sessionId);
}
