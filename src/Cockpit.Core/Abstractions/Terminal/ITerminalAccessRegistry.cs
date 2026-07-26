namespace Cockpit.Core.Abstractions.Terminal;

/// <summary>An open terminal pane the agent could ask to drive: its stable pane id and the name the operator sees (e.g. <c>zsh-5</c>).</summary>
public sealed record TerminalPane(string PaneId, string Name);

/// <summary>
/// How much of a pane an agent was granted. The operator approves the narrower thing the agent actually asked for:
/// reading a pane and typing into it are separate questions, so watching a build finish never quietly comes with the
/// keyboard. An agent that then wants to type asks again, and the coupling widens to <see cref="Drive"/>.
/// </summary>
public enum TerminalCouplingMode
{
    /// <summary>Read the pane's output only — <c>send_terminal</c> is refused until the operator widens it.</summary>
    Watch,

    /// <summary>Read and type, including Ctrl-C.</summary>
    Drive,
}

/// <summary>A pane as <c>list_terminals</c> reports it to one agent session — the pane plus what that session already holds on it, or null when it holds nothing.</summary>
public sealed record TerminalPaneView(string PaneId, string Name, TerminalCouplingMode? Coupling);

/// <summary>
/// What a coupled pane's shell is telling us about itself through its shell-integration marks (OSC 133/633), which
/// is what lets <c>run_in_terminal</c> know a command finished instead of guessing at a quiet stream.
/// </summary>
/// <param name="ShellIntegrationSeen">Whether any mark has arrived. Without one there is no way to tell a finished command from a slow one, and the tool says so rather than guessing.</param>
/// <param name="AtPrompt">Whether the shell is idle at a prompt. False while a command runs — which is also true while a full-screen program like <c>vim</c> is open, so this is what keeps a command from being typed into one.</param>
/// <param name="CommandsStarted">How many commands have begun. Paired with <paramref name="CommandsFinished"/> so a caller can tell that the finish it sees belongs to a command that started after it sent, not to one already in flight.</param>
/// <param name="CommandsFinished">A counter a caller snapshots before sending, then waits to move.</param>
/// <param name="LastExitCode">What the last finished command exited with, or null when the shell reported no code.</param>
/// <param name="CapturedSoFar">Everything captured up to now, counted across the whole coupling, so a caller can read back only what its own command produced. Not a buffer position: the buffer is capped and drops its oldest text.</param>
public sealed record TerminalShellState(bool ShellIntegrationSeen, bool AtPrompt, int CommandsStarted, int CommandsFinished, int? LastExitCode, long CapturedSoFar);

/// <summary>Captured pane output, and whether the buffer cap dropped part of what was asked for — worth passing on, since an agent must not read a build as clean when the errors scrolled out of reach.</summary>
public sealed record TerminalCapturedOutput(string Text, bool Truncated);

/// <summary>Raised when a pane's coupling changes, so the pane's UI can show, reword or hide its "agent connected" bar. <paramref name="Coupling"/> is null when it just decoupled.</summary>
public sealed record TerminalCouplingChange(string PaneId, TerminalCouplingMode? Coupling, string? AgentSession);

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

    /// <summary>The operator's Disconnect on a pane: breaks the coupling at once. A <see cref="TerminalCouplingMode.Drive"/> coupling is interrupted (Ctrl-C) first so whatever the agent started stops; a watching agent never typed, so nothing is interrupted — sending Ctrl-C there would kill the operator's own running command.</summary>
    void Disconnect(string paneId);

    // ---- Consumer side (the cockpit-terminal MCP tools) ----

    /// <summary>The open plain-shell panes as this agent session sees them, each flagged with whether this session is coupled to it. Agent-session panes are left out entirely.</summary>
    IReadOnlyList<TerminalPaneView> ListPanes(string sessionId);

    /// <summary>Finds an open plain-shell pane by its id or its operator-facing name, or null if there is no such pane. Naming an agent-session pane directly resolves to null, so leaving it out of <see cref="ListPanes"/> is a real gate and not just a hidden entry.</summary>
    TerminalPane? Resolve(string paneRef);

    /// <summary>What this session holds on the pane, or null when it holds nothing — so a caller can tell "no consent yet" from "consent to watch, but not to type".</summary>
    TerminalCouplingMode? CouplingOf(string sessionId, string paneId);

    /// <summary>Whether a <em>different</em> agent session holds the coupling — exclusivity: a second agent is refused.</summary>
    bool IsCoupledByAnother(string sessionId, string paneId);

    /// <summary>
    /// Commits the coupling of a pane to a session (after the operator approved) and starts its output capture.
    /// Idempotent for the same session, and widening from <see cref="TerminalCouplingMode.Watch"/> to
    /// <see cref="TerminalCouplingMode.Drive"/> keeps the captured output; a coupling never narrows on its own.
    /// Throws for a pane that is not an open plain shell — reading and typing both need a coupling, so this is where
    /// the plain-shell rule is enforced rather than trusted to each caller.
    /// </summary>
    void Couple(string sessionId, string paneId, TerminalCouplingMode mode);

    /// <summary>The output captured since this session coupled to the pane, or null when this session does not hold the coupling. <paramref name="fromOffset"/> is a <see cref="TerminalShellState.CapturedSoFar"/> taken earlier, so a caller reads back only what arrived after it.</summary>
    TerminalCapturedOutput? ReadCoupled(string sessionId, string paneId, long fromOffset = 0);

    /// <summary>What the coupled pane's shell reports about itself, or null when this session does not hold the coupling.</summary>
    TerminalShellState? ShellStateOf(string sessionId, string paneId);

    /// <summary>Writes bytes into a coupled pane's pty stdin (a keystroke, Ctrl-C, a command). Returns false when this session does not hold a <see cref="TerminalCouplingMode.Drive"/> coupling on it, or the pane has no input sink.</summary>
    bool SendInput(string sessionId, string paneId, ReadOnlyMemory<byte> data);

    /// <summary>Breaks every coupling this agent session held (its session ended or crashed).</summary>
    void SessionEnded(string sessionId);
}
