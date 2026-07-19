using System.Text;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Plugin.CliAgentProvider;

/// <summary>
/// Turns the MCP servers the host resolved for a session (#26/#44) into what <c>codex app-server</c> reads:
/// one <c>-c 'mcp_servers.&lt;name&gt;={…}'</c> config override per server, plus the environment the process
/// needs.
/// <para>
/// A server's bearer token is never written into the <c>-c</c> value — a process argument is visible in
/// <c>/proc/&lt;pid&gt;/cmdline</c> to every local account. The value instead carries only Codex's
/// <c>bearer_token_env_var</c> pointing at an environment variable this builder also emits, so the token reaches
/// the child through its environment (the same route <see cref="CliAgentConfig.BuildEnvironmentVariables"/> uses
/// for the API key) and never the command line.
/// </para>
/// </summary>
internal static class CodexMcpConfig
{
    /// <summary>Prefix for the per-server env var a bearer token is passed through, indexed so two servers never collide.</summary>
    private const string TokenEnvVarPrefix = "COCKPIT_MCP_TOKEN_";

    public static CodexMcpLaunch Build(IReadOnlyList<PluginMcpServer>? servers)
    {
        if (servers is null || servers.Count == 0)
        {
            return CodexMcpLaunch.Empty;
        }

        var configArgs = new List<string>();
        var environmentVariables = new Dictionary<string, string?>();
        var usedNames = new HashSet<string>(StringComparer.Ordinal);

        for (var index = 0; index < servers.Count; index++)
        {
            var server = servers[index];
            if (_InlineTable(server, index, environmentVariables) is not { } inlineTable)
            {
                // No usable transport target (neither url nor command) — nothing Codex could connect to.
                continue;
            }

            configArgs.Add("-c");
            configArgs.Add($"mcp_servers.{_CodexServerName(server.Name, index, usedNames)}={inlineTable}");
        }

        return new CodexMcpLaunch(configArgs, environmentVariables);
    }

    private static string? _InlineTable(PluginMcpServer server, int index, Dictionary<string, string?> environmentVariables)
    {
        if (!string.IsNullOrWhiteSpace(server.Url))
        {
            var fields = new List<string> { $"url = {_TomlString(server.Url)}" };

            if (server.CockpitHosted)
            {
                // A cockpit-hosted endpoint's auth is the host-set COCKPIT_MCP_KEY env var (AC-40): point Codex
                // straight at it, so nothing is added to the environment this builder emits and no literal is written.
                fields.Add($"bearer_token_env_var = {_TomlString(WellKnownSessionEnvironment.CockpitMcpKey)}");
            }
            else if (!string.IsNullOrWhiteSpace(server.BearerToken))
            {
                var tokenEnvVar = $"{TokenEnvVarPrefix}{index}";
                environmentVariables[tokenEnvVar] = server.BearerToken;
                fields.Add($"bearer_token_env_var = {_TomlString(tokenEnvVar)}");
            }

            return $"{{ {string.Join(", ", fields)} }}";
        }

        if (!string.IsNullOrWhiteSpace(server.Command))
        {
            var fields = new List<string> { $"command = {_TomlString(server.Command)}" };

            if (server.Args.Count > 0)
            {
                fields.Add($"args = [{string.Join(", ", server.Args.Select(_TomlString))}]");
            }

            return $"{{ {string.Join(", ", fields)} }}";
        }

        return null;
    }

    /// <summary>
    /// A server name Codex will accept. Codex validates every MCP server name against <c>^[a-zA-Z0-9_-]+$</c> and
    /// refuses to start a server whose name carries anything else (AC-77 test finding: <c>"YouTrack: Personal"</c>,
    /// <c>"SQL Explorer"</c> were rejected with "Invalid MCP server name"). A quoted TOML key parses fine but does
    /// not change the name Codex then validates, so the display name is folded to the charset here: every
    /// out-of-set character becomes <c>_</c>. The result is also a valid TOML bare key, so no quoting is needed.
    /// Claude's <c>--mcp-config</c> route keeps the verbatim name (its JSON keys tolerate spaces), so the two
    /// providers can differ on this without the Cockpit-side name changing. Names are made unique per launch (a
    /// <c>_2</c>, <c>_3</c>, … suffix) so two display names that fold to the same identifier — <c>"a b"</c> and
    /// <c>"a:b"</c> — do not collapse into one server. A name with no letter or digit at all (empty, or only
    /// symbols that would fold to a bare run of <c>_</c>) falls back to <c>server_{index}</c>.
    /// </summary>
    private static string _CodexServerName(string name, int index, HashSet<string> usedNames)
    {
        var builder = new StringBuilder(name.Length);
        var hasAlphanumeric = false;
        foreach (var character in name)
        {
            if (char.IsAsciiLetterOrDigit(character))
            {
                hasAlphanumeric = true;
                builder.Append(character);
            }
            else
            {
                builder.Append(character is '_' or '-' ? character : '_');
            }
        }

        var sanitized = hasAlphanumeric ? builder.ToString() : $"server_{index}";

        var unique = sanitized;
        for (var suffix = 2; !usedNames.Add(unique); suffix++)
        {
            unique = $"{sanitized}_{suffix}";
        }

        return unique;
    }

    /// <summary>A TOML basic string with the escapes the spec requires, so a url/name with a quote or backslash cannot break the value.</summary>
    private static string _TomlString(string value)
    {
        var builder = new StringBuilder(value.Length + 2);
        builder.Append('"');
        foreach (var character in value)
        {
            switch (character)
            {
                case '"':
                    builder.Append("\\\"");
                    break;
                case '\\':
                    builder.Append("\\\\");
                    break;
                case '\n':
                    builder.Append("\\n");
                    break;
                case '\r':
                    builder.Append("\\r");
                    break;
                case '\t':
                    builder.Append("\\t");
                    break;
                default:
                    builder.Append(character);
                    break;
            }
        }

        builder.Append('"');
        return builder.ToString();
    }
}

/// <summary>The <c>codex app-server</c> spawn's MCP-derived config args and the environment the tokens ride in.</summary>
internal sealed record CodexMcpLaunch(IReadOnlyList<string> ConfigArgs, IReadOnlyDictionary<string, string?> EnvironmentVariables)
{
    public static CodexMcpLaunch Empty { get; } = new([], new Dictionary<string, string?>());
}
