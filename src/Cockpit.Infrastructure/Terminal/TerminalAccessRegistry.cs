using System.Text;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Terminal;

namespace Cockpit.Infrastructure.Terminal;

// The live coupling state behind the terminal-access MCP (AC-34). Producer calls come from the UI thread (a pane
// opens, output flushes); consumer calls come from MCP request threads (list, couple, read). All of it is behind one
// lock — the state is small and the calls are short, so a lock is simpler and safer here than a lock-free scheme.
//
// Read-scope starts at the coupling: `CaptureOutput` is a no-op until a pane is coupled, so nothing that
// scrolled by before an agent connected — an earlier secret echo included — is ever in the buffer it can read. The
// buffer is capped so a long-lived coupling on a chatty pane cannot grow without bound.
//
// What an agent may reach is narrowed on the way out, not on the way in: every pane registers, but only the
// plain-shell ones are listed or resolvable, so the agent-session panes stay out of reach of both.
internal sealed class TerminalAccessRegistry : ITerminalAccessRegistry, ISingletonService
{
    // Cap on a coupling's captured text — enough to be useful, bounded so a streaming pane cannot exhaust memory. Oldest output is dropped first.
    private const int MaxCaptureChars = 256 * 1024;

    // The interrupt a Disconnect sends before breaking the coupling, so a running command stops at once (ETX / Ctrl-C).
    private static readonly byte[] Interrupt = [0x03];

    private readonly object _lock = new();
    private readonly Dictionary<string, Pane> _panes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Coupling> _couplings = new(StringComparer.Ordinal); // paneId -> coupling
    private readonly Dictionary<string, Action<ReadOnlyMemory<byte>>> _inputSinks = new(StringComparer.Ordinal); // paneId -> pty writer

    public event Action<TerminalCouplingChange>? CouplingChanged;

    public void PaneOpened(string paneId, string name, bool plainShell)
    {
        lock (_lock)
        {
            _panes[paneId] = new Pane(name, plainShell);
        }
    }

    public void PaneClosed(string paneId)
    {
        bool wasCoupled;
        lock (_lock)
        {
            _panes.Remove(paneId);
            _inputSinks.Remove(paneId);
            wasCoupled = _couplings.Remove(paneId);
        }

        if (wasCoupled)
        {
            CouplingChanged?.Invoke(new TerminalCouplingChange(paneId, Coupling: null, AgentSession: null));
        }
    }

    public void RegisterInput(string paneId, Action<ReadOnlyMemory<byte>> writeToPty)
    {
        lock (_lock)
        {
            _inputSinks[paneId] = writeToPty;
        }
    }

    public bool SendInput(string sessionId, string paneId, ReadOnlyMemory<byte> data)
    {
        Action<ReadOnlyMemory<byte>>? sink;
        lock (_lock)
        {
            if (!(_couplings.TryGetValue(paneId, out var coupling) && coupling.SessionId == sessionId && coupling.Mode == TerminalCouplingMode.Drive)
                || !_inputSinks.TryGetValue(paneId, out sink))
            {
                return false;
            }
        }

        // Invoked outside the lock: the sink writes to the pty, which must never run under the registry lock.
        sink(data);
        return true;
    }

    public void Disconnect(string paneId)
    {
        Action<ReadOnlyMemory<byte>>? sink;
        Coupling? dropped;
        lock (_lock)
        {
            _inputSinks.TryGetValue(paneId, out sink);
            _couplings.Remove(paneId, out dropped);
        }

        if (dropped is null)
        {
            return;
        }

        // Interrupt first, then drop the coupling: a Disconnect must stop what the agent started, not just deny the
        // next thing. Only for a driving agent — a watching one never typed, so an interrupt here would land on
        // whatever the operator themselves is running. Best-effort: a pane whose pty is already gone still decouples.
        if (dropped.Mode == TerminalCouplingMode.Drive)
        {
            try
            {
                sink?.Invoke(Interrupt);
            }
            catch (Exception)
            {
                // The pty may have exited; breaking the coupling is the part that has to land.
            }
        }

        CouplingChanged?.Invoke(new TerminalCouplingChange(paneId, Coupling: null, AgentSession: null));
    }

    public void CaptureOutput(string paneId, string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        lock (_lock)
        {
            if (!_couplings.TryGetValue(paneId, out var coupling))
            {
                return; // Not coupled — read-scope has not started, so this output is not the agent's to see.
            }

            coupling.Shell.Feed(text);
            coupling.Buffer.Append(text);
            coupling.TotalCaptured += text.Length;
            if (coupling.Buffer.Length > MaxCaptureChars)
            {
                coupling.Buffer.Remove(0, coupling.Buffer.Length - MaxCaptureChars);
            }
        }
    }

    public bool IsCoupled(string paneId)
    {
        lock (_lock)
        {
            return _couplings.ContainsKey(paneId);
        }
    }

    public IReadOnlyList<TerminalPaneView> ListPanes(string sessionId)
    {
        lock (_lock)
        {
            return _panes
                .Where(pane => pane.Value.PlainShell)
                .Select(pane => new TerminalPaneView(
                    pane.Key,
                    pane.Value.Name,
                    _couplings.TryGetValue(pane.Key, out var coupling) && coupling.SessionId == sessionId
                        ? coupling.Mode
                        : null))
                .ToList();
        }
    }

    public TerminalPane? Resolve(string paneRef)
    {
        lock (_lock)
        {
            if (_panes.TryGetValue(paneRef, out var byId))
            {
                return byId.PlainShell ? new TerminalPane(paneRef, byId.Name) : null;
            }

            // Fall back to the operator-facing name, so an agent told "use zsh-5" can name it directly. First match wins.
            var byName = _panes.FirstOrDefault(pane => pane.Value.PlainShell && string.Equals(pane.Value.Name, paneRef, StringComparison.Ordinal));
            return byName.Key is null ? null : new TerminalPane(byName.Key, byName.Value.Name);
        }
    }

    public TerminalCouplingMode? CouplingOf(string sessionId, string paneId)
    {
        lock (_lock)
        {
            return _couplings.TryGetValue(paneId, out var coupling) && coupling.SessionId == sessionId
                ? coupling.Mode
                : null;
        }
    }

    public bool IsCoupledByAnother(string sessionId, string paneId)
    {
        lock (_lock)
        {
            return _couplings.TryGetValue(paneId, out var coupling) && coupling.SessionId != sessionId;
        }
    }

    public void Couple(string sessionId, string paneId, TerminalCouplingMode mode)
    {
        lock (_lock)
        {
            // The one place a coupling comes into being, so the plain-shell rule is enforced here rather than trusted
            // to every caller: reading and typing both need a coupling, so a pane that cannot be coupled cannot be
            // reached at all — including by a future caller that skips Resolve and passes a pane id it got elsewhere.
            if (!_panes.TryGetValue(paneId, out var pane) || !pane.PlainShell)
            {
                throw new InvalidOperationException($"Terminal pane '{paneId}' is not a plain shell an agent may be coupled to.");
            }

            if (_couplings.TryGetValue(paneId, out var existing))
            {
                if (existing.SessionId != sessionId)
                {
                    throw new InvalidOperationException($"Terminal pane '{paneId}' is already coupled to another agent.");
                }

                // Same session again: keep the capture either way. Widening to Drive is a real change the pane has to
                // hear about (its bar stops saying "watching"); re-asking for what it already holds is a no-op, and a
                // Watch request never narrows an existing Drive — consent is not withdrawn by asking for less.
                if (existing.Mode == mode || mode == TerminalCouplingMode.Watch)
                {
                    return;
                }

                existing.Mode = mode;
            }
            else
            {
                _couplings[paneId] = new Coupling(sessionId) { Mode = mode };
            }
        }

        CouplingChanged?.Invoke(new TerminalCouplingChange(paneId, mode, AgentSession: sessionId));
    }

    public TerminalCapturedOutput? ReadCoupled(string sessionId, string paneId, long fromOffset = 0)
    {
        lock (_lock)
        {
            if (!(_couplings.TryGetValue(paneId, out var coupling) && coupling.SessionId == sessionId))
            {
                return null;
            }

            // The offset counts everything ever captured, not a position in the buffer: the buffer is capped and drops
            // its oldest text, so a buffer position taken a while ago no longer means what it did. Work back from the
            // end instead — "how much arrived since then" — and clamp to what survived the cap.
            var wanted = coupling.TotalCaptured - fromOffset;
            var since = (int)Math.Clamp(wanted, 0, coupling.Buffer.Length);
            return new TerminalCapturedOutput(
                coupling.Buffer.ToString(coupling.Buffer.Length - since, since),
                Truncated: wanted > since);
        }
    }

    public TerminalShellState? ShellStateOf(string sessionId, string paneId)
    {
        lock (_lock)
        {
            return _couplings.TryGetValue(paneId, out var coupling) && coupling.SessionId == sessionId
                ? new TerminalShellState(
                    coupling.Shell.ShellIntegrationSeen,
                    coupling.Shell.AtPrompt,
                    coupling.Shell.CommandsStarted,
                    coupling.Shell.CommandsFinished,
                    coupling.Shell.LastExitCode,
                    coupling.TotalCaptured)
                : null;
        }
    }

    public void SessionEnded(string sessionId)
    {
        List<string> dropped;
        lock (_lock)
        {
            dropped = _couplings.Where(entry => entry.Value.SessionId == sessionId).Select(entry => entry.Key).ToList();
            foreach (var paneId in dropped)
            {
                _couplings.Remove(paneId);
            }
        }

        foreach (var paneId in dropped)
        {
            CouplingChanged?.Invoke(new TerminalCouplingChange(paneId, Coupling: null, AgentSession: null));
        }
    }

    // A pane running an agent CLI is stored like any other — PaneClosed and the coupling teardown still have to work
    // on it — but PlainShell is what decides whether an agent is ever offered it.
    private sealed record Pane(string Name, bool PlainShell);

    private sealed class Coupling(string sessionId)
    {
        public string SessionId { get; } = sessionId;

        // Settable so widening from Watch to Drive keeps the same buffer: the operator approving the keyboard should
        // not cost the agent the output it was already reading. Only ever mutated under the registry lock.
        public required TerminalCouplingMode Mode { get; set; }

        public StringBuilder Buffer { get; } = new();

        // Everything ever captured, buffer cap included — the stable ruler a caller measures "since I sent" against,
        // which a buffer position cannot be once the cap starts dropping the oldest text.
        public long TotalCaptured { get; set; }

        // Fed the same bytes as the buffer, so what the shell says about itself and what the agent can read stay in
        // step. Per coupling rather than per pane: capture starts at the coupling, so the marks do too.
        public TerminalShellIntegrationTracker Shell { get; } = new();
    }
}
