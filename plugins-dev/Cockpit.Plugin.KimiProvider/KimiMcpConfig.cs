using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Plugin.KimiProvider;

// Builds the ACP `mcpServers` array for `session/new`/`session/resume` from the host's shared
// MCP registry (AC-269 sub [b], protocol §9) — in its own file so the stdio-vs-http shape is testable without
// spinning up a driver.
//
// D6, the epic's costliest trap: a stdio server's wire object carries *no* `type` field at all — the
// adapter recognises stdio by that field's *absence*, the "bare branch" of the ACP union. Sending
// `"type":"stdio"` lands in the adapter's `default` arm and the server is dropped silently (only a
// warn in kimi's own log). So the stdio and http shapes below are two entirely distinct anonymous types, not
// one type with a nullable `type` — there is then no field that could ever accidentally serialize as null.
//
// Assumption: `PluginMcpServer` carries no signal that a URL server is SSE rather than plain HTTP
// (both ride `PluginMcpServer.Url`), so every URL server maps to `"type":"http"` here — there
// is no data on the plugin contract to ever pick `"sse"` from.
//
// Assumption: a cockpit-hosted server's bearer key has no env-var-reference mechanism on this wire (unlike
// Codex's `bearer_token_env_var` or Claude's own `${VAR}`-expanding config file) — ACP's
// `headers` is a literal array of `{name,value}`, so the header carries the resolved key value
// itself, read from the same environment the driver builds for the spawn. This does not repeat the
// world-readable-file risk the Claude plugin guards against: nothing here is written to disk, only sent once
// over the already-private stdio pipe to the child process.
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

        // The operator's own headers (AC-354) for a server that wants X-Api-Key or another scheme, then the bearer
        // the auth setting produced. The host has already removed Authorization from that set when a credential
        // covers it, so the two cannot both be sent.
        var headers = new List<object>();
        foreach (var (name, value) in server.Headers)
        {
            headers.Add(new { name, value });
        }

        if (!string.IsNullOrEmpty(token))
        {
            headers.Add(new { name = "Authorization", value = $"Bearer {token}" });
        }

        return [.. headers];
    }
}
