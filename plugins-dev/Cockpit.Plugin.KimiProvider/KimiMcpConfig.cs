using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Plugin.KimiProvider;

/// <summary>
/// Builds the ACP <c>mcpServers</c> array for <c>session/new</c>/<c>session/resume</c> from the host's shared
/// MCP registry (AC-269 sub [b], protocol §9) — in its own file so the stdio-vs-http shape is testable without
/// spinning up a driver.
/// </summary>
/// <remarks>
/// <para>
/// D6, the epic's costliest trap: a stdio server's wire object carries <b>no</b> <c>type</c> field at all — the
/// adapter recognises stdio by that field's <em>absence</em>, the "bare branch" of the ACP union. Sending
/// <c>"type":"stdio"</c> lands in the adapter's <c>default</c> arm and the server is dropped silently (only a
/// warn in kimi's own log). So the stdio and http shapes below are two entirely distinct anonymous types, not
/// one type with a nullable <c>type</c> — there is then no field that could ever accidentally serialize as null.
/// </para>
/// <para>
/// Assumption: <see cref="PluginMcpServer"/> carries no signal that a URL server is SSE rather than plain HTTP
/// (both ride <see cref="PluginMcpServer.Url"/>), so every URL server maps to <c>"type":"http"</c> here — there
/// is no data on the plugin contract to ever pick <c>"sse"</c> from.
/// </para>
/// <para>
/// Assumption: a cockpit-hosted server's bearer key has no env-var-reference mechanism on this wire (unlike
/// Codex's <c>bearer_token_env_var</c> or Claude's own <c>${VAR}</c>-expanding config file) — ACP's
/// <c>headers</c> is a literal array of <c>{name,value}</c>, so the header carries the resolved key value
/// itself, read from the same environment the driver builds for the spawn. This does not repeat the
/// world-readable-file risk the Claude plugin guards against: nothing here is written to disk, only sent once
/// over the already-private stdio pipe to the child process.
/// </para>
/// </remarks>
internal static class KimiMcpConfig
{
    public static IReadOnlyList<object> Build(IReadOnlyList<PluginMcpServer>? servers, IReadOnlyDictionary<string, string?> environmentVariables)
    {
        if (servers is not { Count: > 0 })
        {
            return [];
        }

        var wire = new List<object>(servers.Count);
        foreach (var server in servers)
        {
            if (_BuildStdio(server) is { } stdio)
            {
                wire.Add(stdio);
            }
            else if (_BuildHttp(server, environmentVariables) is { } http)
            {
                wire.Add(http);
            }
        }

        return wire;
    }

    private static object? _BuildStdio(PluginMcpServer server)
    {
        if (string.IsNullOrWhiteSpace(server.Command))
        {
            return null;
        }

        // No "type" property at all (D6) — see the remarks above.
        return new
        {
            name = server.Name,
            command = server.Command,
            args = server.Args,
            env = Array.Empty<object>(),
        };
    }

    private static object? _BuildHttp(PluginMcpServer server, IReadOnlyDictionary<string, string?> environmentVariables)
    {
        if (string.IsNullOrWhiteSpace(server.Url))
        {
            return null;
        }

        return new
        {
            type = "http",
            name = server.Name,
            url = server.Url,
            headers = _BuildHeaders(server, environmentVariables),
        };
    }

    private static object[] _BuildHeaders(PluginMcpServer server, IReadOnlyDictionary<string, string?> environmentVariables)
    {
        var token = server.CockpitHosted
            ? environmentVariables.GetValueOrDefault(WellKnownSessionEnvironment.CockpitMcpKey)
            : server.BearerToken;

        return string.IsNullOrEmpty(token) ? [] : [new { name = "Authorization", value = $"Bearer {token}" }];
    }
}
