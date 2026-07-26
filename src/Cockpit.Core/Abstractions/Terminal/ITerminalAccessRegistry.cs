namespace Cockpit.Core.Abstractions.Terminal;

/// <summary>An open terminal pane the agent could ask to drive: its stable pane id and the name the operator sees (e.g. <c>zsh-5</c>).</summary>
public sealed record TerminalPane(string PaneId, string Name);

/// <summary>A pane as <c>list_terminals</c> reports it to one agent session — the pane plus whether that session is already coupled to it.</summary>
public sealed record TerminalPaneView(string PaneId, string Name, bool Coupled);

/// <summary>Raised when a pane's coupling changes, so the pane's UI can show or hide its "agent connected" bar. <paramref name="AgentSession"/> is the coupled session, or null when it just decoupled.</summary>
public sealed record TerminalCouplingChange(string PaneId, bool Coupled, string? AgentSession);

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

    /// <summary>
    /// Records that a terminal pane is open. Idempotent — re-registering updates the name.
    /// <paramref name="plainShell"/> says the cockpit started this pane as a shell, not as an agent session. Only those
    /// are offered to an agent, so an agent can neither list nor name another agent's session pane: driving one would
    /// be agent-to-agent puppeting behind the operator's terminal consent, and reading one would pull that session's
    /// whole transcript into this agent's context. It describes how the pane was launched, not what is running in the
    /// pty now — an operator who starts an agent CLI by hand inside a shell they opened still has a shell they opened,
    /// and still has to approve each agent that asks for it.
    /// </summary>
    void PaneOpened(string paneId, string name, bool plainShell);

    /// <summary>Records that a pane closed (tab closed, shell exit, SSH dropped): any coupling on it is broken automatically.</summary>
    void PaneClosed(string paneId);

    /// <summary>Feeds a pane's freshly rendered output. Buffered only while the pane is coupled, so capture begins at the coupling, not before.</summary>
    void CaptureOutput(string paneId, string text);

    /// <summary>Whether the pane is coupled to any agent — the cheap gate the producer uses so it only decodes/pushes output that will actually be read.</summary>
    bool IsCoupled(string paneId);

    /// <summary>Registers the sink that writes bytes into this pane's pty stdin (its keystroke channel), so a coupled agent's <c>send_terminal</c> reaches the shell. Cleared when the pane closes.</summary>
    void RegisterInput(string paneId, Action<ReadOnlyMemory<byte>> writeToPty);

    /// <summary>Raised on the coupling changing (coupled, or decoupled by close/session-end/operator Disconnect) so the pane can show or hide its "agent connected" bar.</summary>
    event Action<TerminalCouplingChange>? CouplingChanged;

    /// <summary>The operator's Disconnect on a pane: sends the shell an interrupt (Ctrl-C) so a running command stops, then breaks the coupling — "no more access" is immediate and hard.</summary>
    void Disconnect(string paneId);

    // ---- Consumer side (the cockpit-terminal MCP tools) ----

    /// <summary>The open plain-shell panes as this agent session sees them, each flagged with whether this session is coupled to it. Agent-session panes are left out entirely.</summary>
    IReadOnlyList<TerminalPaneView> ListPanes(string sessionId);

    /// <summary>Finds an open plain-shell pane by its id or its operator-facing name, or null if there is no such pane. Naming an agent-session pane directly resolves to null, so leaving it out of <see cref="ListPanes"/> is a real gate and not just a hidden entry.</summary>
    TerminalPane? Resolve(string paneRef);

    /// <summary>Whether this session already holds the coupling on the pane (so no fresh consent is needed to read it).</summary>
    bool IsCoupledBy(string sessionId, string paneId);

    /// <summary>Whether a <em>different</em> agent session holds the coupling — exclusivity: a second agent is refused.</summary>
    bool IsCoupledByAnother(string sessionId, string paneId);

    /// <summary>Commits the coupling of a pane to a session (after the operator approved) and starts its output capture. Idempotent for the same session. Throws for a pane that is not an open plain shell — reading and typing both need a coupling, so this is where the plain-shell rule is enforced rather than trusted to each caller.</summary>
    void Couple(string sessionId, string paneId);

    /// <summary>The output captured since this session coupled to the pane, or null when this session does not hold the coupling.</summary>
    string? ReadCoupled(string sessionId, string paneId);

    /// <summary>Writes bytes into a coupled pane's pty stdin (a keystroke, Ctrl-C, a command). Returns false when this session does not hold the coupling or the pane has no input sink.</summary>
    bool SendInput(string sessionId, string paneId, ReadOnlyMemory<byte> data);

    /// <summary>Breaks every coupling this agent session held (its session ended or crashed).</summary>
    void SessionEnded(string sessionId);
}
