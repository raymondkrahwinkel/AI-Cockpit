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

        for (var index = 0; index < servers.Count; index++)
        {
            var server = servers[index];
            if (_InlineTable(server, index, environmentVariables) is not { } inlineTable)
            {
                // No usable transport target (neither url nor command) — nothing Codex could connect to.
                continue;
            }

            configArgs.Add("-c");
            configArgs.Add($"mcp_servers.{_TomlKey(server.Name)}={inlineTable}");
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

    /// <summary>A bare TOML key when the name is a bare-key-safe identifier, otherwise a quoted key.</summary>
    private static string _TomlKey(string name) =>
        name.Length > 0 && name.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-')
            ? name
            : _TomlString(name);

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
