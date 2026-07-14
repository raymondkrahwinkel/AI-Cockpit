using Cockpit.Core.Sessions.Tty;

namespace Cockpit.Core.Mcp;

/// <summary>
/// The environment handed to a stdio MCP server the cockpit starts (an <c>npx</c>/<c>uv</c> tool server).
/// <para>
/// Such a server inherits the cockpit's environment by default, which is mostly what you want — it needs PATH,
/// HOME and a node runtime. What it has no business receiving is the operator's Anthropic credential: an MCP
/// tool server is a third party, often someone else's package, and a key that reaches it is a key it could
/// spend or ship. So the inherited environment passes through minus that family.
/// </para>
/// <para>
/// Deliberately a deny-list of one thing rather than an allowlist: an allowlist here would have to guess at
/// every variable a tool server legitimately needs (PATH, NODE_*, nvm's shims, a proxy, a server's own token)
/// and would break them by omission — a guard that breaks working setups gets turned off, and then it guards
/// nothing.
/// </para>
/// </summary>
public static class StdioServerEnvironment
{
    /// <summary>The current process environment, minus the Anthropic credentials.</summary>
    public static Dictionary<string, string?> Build() =>
        Build(Environment.GetEnvironmentVariables()
            .Cast<System.Collections.DictionaryEntry>()
            .ToDictionary(entry => (string)entry.Key, entry => entry.Value as string));

    /// <summary>Pure overload: the composition rule, testable without touching the real process environment.</summary>
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
