using System.Text;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Terminal;

namespace Cockpit.Infrastructure.Terminal;

/// <summary>
/// The live coupling state behind the terminal-access MCP (AC-34). Producer calls come from the UI thread (a pane
/// opens, output flushes); consumer calls come from MCP request threads (list, couple, read). All of it is behind one
/// lock — the state is small and the calls are short, so a lock is simpler and safer here than a lock-free scheme.
/// <para>
/// Read-scope starts at the coupling: <see cref="CaptureOutput"/> is a no-op until a pane is coupled, so nothing that
/// scrolled by before an agent connected — an earlier secret echo included — is ever in the buffer it can read. The
/// buffer is capped so a long-lived coupling on a chatty pane cannot grow without bound.
/// </para>
/// </summary>
internal sealed class TerminalAccessRegistry : ITerminalAccessRegistry, ISingletonService
{
    /// <summary>Cap on a coupling's captured text — enough to be useful, bounded so a streaming pane cannot exhaust memory. Oldest output is dropped first.</summary>
    private const int MaxCaptureChars = 256 * 1024;

    private readonly object _lock = new();
    private readonly Dictionary<string, string> _panes = new(StringComparer.Ordinal); // paneId -> name
    private readonly Dictionary<string, Coupling> _couplings = new(StringComparer.Ordinal); // paneId -> coupling

    public void PaneOpened(string paneId, string name)
    {
        lock (_lock)
        {
            _panes[paneId] = name;
        }
    }

    public void PaneClosed(string paneId)
    {
        lock (_lock)
        {
            _panes.Remove(paneId);
            _couplings.Remove(paneId);
        }
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

            coupling.Buffer.Append(text);
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
                .Select(pane => new TerminalPaneView(
                    pane.Key,
                    pane.Value,
                    _couplings.TryGetValue(pane.Key, out var coupling) && coupling.SessionId == sessionId))
                .ToList();
        }
    }

    public TerminalPane? Resolve(string paneRef)
    {
        lock (_lock)
        {
            if (_panes.TryGetValue(paneRef, out var byId))
            {
                return new TerminalPane(paneRef, byId);
            }

            // Fall back to the operator-facing name, so an agent told "use zsh-5" can name it directly. First match wins.
            var byName = _panes.FirstOrDefault(pane => string.Equals(pane.Value, paneRef, StringComparison.Ordinal));
            return byName.Key is null ? null : new TerminalPane(byName.Key, byName.Value);
        }
    }

    public bool IsCoupledBy(string sessionId, string paneId)
    {
        lock (_lock)
        {
            return _couplings.TryGetValue(paneId, out var coupling) && coupling.SessionId == sessionId;
        }
    }

    public bool IsCoupledByAnother(string sessionId, string paneId)
    {
        lock (_lock)
        {
            return _couplings.TryGetValue(paneId, out var coupling) && coupling.SessionId != sessionId;
        }
    }

    public void Couple(string sessionId, string paneId)
    {
        lock (_lock)
        {
            if (_couplings.TryGetValue(paneId, out var existing))
            {
                if (existing.SessionId != sessionId)
                {
                    throw new InvalidOperationException($"Terminal pane '{paneId}' is already coupled to another agent.");
                }

                return; // Same session re-couples: idempotent, keep the existing capture.
            }

            _couplings[paneId] = new Coupling(sessionId);
        }
    }

    public string? ReadCoupled(string sessionId, string paneId)
    {
        lock (_lock)
        {
            return _couplings.TryGetValue(paneId, out var coupling) && coupling.SessionId == sessionId
                ? coupling.Buffer.ToString()
                : null;
        }
    }

    public void SessionEnded(string sessionId)
    {
        lock (_lock)
        {
            foreach (var paneId in _couplings.Where(entry => entry.Value.SessionId == sessionId).Select(entry => entry.Key).ToList())
            {
                _couplings.Remove(paneId);
            }
        }
    }

    private sealed class Coupling(string sessionId)
    {
        public string SessionId { get; } = sessionId;

        public StringBuilder Buffer { get; } = new();
    }
}
