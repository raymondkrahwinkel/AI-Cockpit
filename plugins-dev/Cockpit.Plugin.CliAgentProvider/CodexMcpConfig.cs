using System.Text;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Plugin.CliAgentProvider;

// Turns the MCP servers the host resolved for a session (#26/#44) into what `codex app-server` reads:
// one `-c 'mcp_servers.&lt;name&gt;={…}'` config override per server, plus the environment the process
// needs.
//
// A server's bearer token is never written into the `-c` value — a process argument is visible in
// `/proc/&lt;pid&gt;/cmdline` to every local account. The value instead carries only Codex's
// `bearer_token_env_var` pointing at an environment variable this builder also emits, so the token reaches
// the child through its environment (the same route `CliAgentConfig.BuildEnvironmentVariables` uses
// for the API key) and never the command line.
internal static class CodexMcpConfig
{
    // Prefix for the per-server env var a bearer token is passed through, indexed so two servers never collide.
    private const string TokenEnvVarPrefix = "COCKPIT_MCP_TOKEN_";

    // Prefix for the env var one custom header's value is passed through, indexed per server and per header.
    private const string HeaderEnvVarPrefix = "COCKPIT_MCP_HEADER_";

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

            // Custom headers (AC-354) go through Codex's env_http_headers — header name mapped to the *name* of an
            // environment variable — rather than http_headers, which would take the value literally and so put it in
            // a -c argument. That is the same rule the bearer token above follows, and for the same reason: a process
            // argument is readable by every local account, and a custom header is where the credential goes for a
            // server that does not take a bearer.
            if (server.Headers.Count > 0)
            {
                var mappings = new List<string>();
                var headerIndex = 0;
                foreach (var (name, value) in server.Headers)
                {
                    var headerEnvVar = $"{HeaderEnvVarPrefix}{index}_{headerIndex++}";
                    environmentVariables[headerEnvVar] = value;
                    mappings.Add($"{_TomlString(name)} = {_TomlString(headerEnvVar)}");
                }

                fields.Add($"env_http_headers = {{ {string.Join(", ", mappings)} }}");
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

    // A server name Codex will accept. Codex validates every MCP server name against `^[a-zA-Z0-9_-]+$` and
    // refuses to start a server whose name carries anything else (AC-77 test finding: `"YouTrack: Personal"`,
    // `"SQL Explorer"` were rejected with "Invalid MCP server name"). A quoted TOML key parses fine but does
    // not change the name Codex then validates, so the display name is folded to the charset here: every
    // out-of-set character becomes `_`. The result is also a valid TOML bare key, so no quoting is needed.
    // Claude's `--mcp-config` route keeps the verbatim name (its JSON keys tolerate spaces), so the two
    // providers can differ on this without the Cockpit-side name changing. Names are made unique per launch (a
    // `_2`, `_3`, … suffix) so two display names that fold to the same identifier — `"a b"` and
    // `"a:b"` — do not collapse into one server. A name with no letter or digit at all (empty, or only
    // symbols that would fold to a bare run of `_`) falls back to `server_{index}`.
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

    // A TOML basic string with the escapes the spec requires, so a url/name with a quote or backslash cannot break the value.
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

// The `codex app-server` spawn's MCP-derived config args and the environment the tokens ride in.
internal sealed record CodexMcpLaunch(IReadOnlyList<string> ConfigArgs, IReadOnlyDictionary<string, string?> EnvironmentVariables)
{
    public static CodexMcpLaunch Empty { get; } = new([], new Dictionary<string, string?>());
}
