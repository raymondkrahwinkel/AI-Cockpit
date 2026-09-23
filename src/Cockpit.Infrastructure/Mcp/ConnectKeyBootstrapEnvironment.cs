using System.Collections.Concurrent;

namespace Cockpit.Infrastructure.Mcp;

// AC-1351: the bootstrap connect key, taken out of the process environment just before startup scrubs it
// (`TtyEnvironment.IsCockpitConnectKeyMarker`), so no session inherits an admin key. The verifier reads it once.
public static class ConnectKeyBootstrapEnvironment
{
    private static readonly ConcurrentDictionary<string, string> Captured = new(StringComparer.Ordinal);

    public static void Capture()
    {
        foreach (var name in new[] { ConnectKeyVerifier.BootstrapFileVariable, ConnectKeyVerifier.BootstrapVariable })
        {
            if (Environment.GetEnvironmentVariable(name) is { Length: > 0 } value)
            {
                Captured[name] = value;
            }
        }
    }

    internal static string? Get(string name) => Captured.TryGetValue(name, out var value) ? value : null;

    internal static void Forget(string name) => Captured.TryRemove(name, out _);
}
