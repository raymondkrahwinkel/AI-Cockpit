using Cockpit.Core.Sessions.Tty;

namespace Cockpit.Core.Mcp;

// The environment handed to a stdio MCP server: the cockpit's own, minus what `TtyEnvironment.IsHostControlled`
// already treats as host-owned — the Anthropic credential and COCKPIT_MCP_KEY, the latter per AC-1148 the
// bearer for every loopback endpoint. Deliberately a deny-list; an allowlist would have to guess every variable.
public static class StdioServerEnvironment
{
    // The current process environment, minus everything the host owns.
    public static Dictionary<string, string?> Build() =>
        Build(Environment.GetEnvironmentVariables()
            .Cast<System.Collections.DictionaryEntry>()
            .ToDictionary(entry => (string)entry.Key, entry => entry.Value as string));

    // Pure overload: the composition rule, testable without touching the real process environment.
    public static Dictionary<string, string?> Build(IReadOnlyDictionary<string, string?> baseEnvironment)
    {
        var environment = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in baseEnvironment)
        {
            if (TtyEnvironment.IsHostControlled(key))
            {
                continue;
            }

            environment[key] = value;
        }

        return environment;
    }
}
