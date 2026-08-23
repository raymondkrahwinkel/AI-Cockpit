using Cockpit.Core.Sessions.Tty;

namespace Cockpit.Core.Mcp;

// The environment handed to a stdio MCP server the cockpit starts. It inherits the cockpit's environment
// (a third-party tool server has no business receiving the operator's Anthropic credential) minus that one
// family — deliberately a deny-list, since an allowlist would have to guess every variable a server needs.
public static class StdioServerEnvironment
{
    // The current process environment, minus the Anthropic credentials.
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
            if (TtyEnvironment.IsAnthropicCredentialMarker(key))
            {
                continue;
            }

            environment[key] = value;
        }

        return environment;
    }
}
