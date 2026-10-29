using System.Collections.Concurrent;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Infrastructure.Configuration;

namespace Cockpit.Infrastructure.Mcp;

// AC-1943: paused nodes are remembered in `paused-nodes.txt` (a pause must survive a restart); disconnected ones are
// in memory only, as the operator's disconnect has always been "for this run".
internal sealed class NodeLinkGate : INodeLinkGate, ISingletonService
{
    private readonly string _path;
    private readonly ConcurrentDictionary<string, byte> _paused = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _disconnected = new(StringComparer.Ordinal);
    private readonly object _writeLock = new();

    public NodeLinkGate()
        : this(CockpitConfigPath.PausedNodes)
    {
    }

    // Test seam: point the gate at an arbitrary file.
    internal NodeLinkGate(string path)
    {
        _path = path;
        try
        {
            foreach (var line in File.Exists(path) ? File.ReadAllLines(path) : [])
            {
                if (line.Trim() is { Length: > 0 } name)
                {
                    _paused[name] = 0;
                }
            }
        }
        catch (IOException)
        {
            // An unreadable list reads as "nothing paused" — the node card still offers Pause.
        }
    }

    public bool IsPaused(string nodeName) => _paused.ContainsKey(nodeName);

    public string? RefusalFor(string nodeName) =>
        IsPaused(nodeName) ? $"Node {nodeName} is paused here. Resume it in Options → Nodes to use it again."
        : _disconnected.ContainsKey(nodeName) ? $"Node {nodeName} is disconnected here. Connect it again in Options → Nodes to use it."
        : null;

    public void SetPaused(string nodeName, bool paused)
    {
        if (paused)
        {
            _paused[nodeName] = 0;
        }
        else
        {
            _paused.TryRemove(nodeName, out _);
        }

        // An empty path is a gate that keeps nothing across runs (a `--remote` window, which has no pauses of its own).
        if (_path.Length == 0)
        {
            return;
        }

        lock (_writeLock)
        {
            try
            {
                CockpitConfigPath.WriteAllTextPrivate(_path, string.Join('\n', _paused.Keys.Order(StringComparer.Ordinal)));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Held for this run; the next change tries the file again.
            }
        }
    }

    public void SetDisconnected(string nodeName, bool disconnected)
    {
        if (disconnected)
        {
            _disconnected[nodeName] = 0;
        }
        else
        {
            _disconnected.TryRemove(nodeName, out _);
        }
    }
}
